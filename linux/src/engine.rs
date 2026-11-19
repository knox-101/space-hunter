// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - the engine: the cleanup list, measuring, cleaning, ages, protected folders, the Trash,
// and the password helper for system folders. No interface code here.

use gtk::gio;
use gtk::gio::prelude::FileExt;
use gtk::glib;
use serde::Deserialize;
use std::collections::{HashMap, HashSet};
use std::ffi::CString;
use std::fs;
use std::os::unix::fs::MetadataExt;
use std::path::{Component, Path, PathBuf};
use std::process::Command;
use std::time::{Duration, SystemTime};

const CLEANERS: &str = include_str!("../cleaners.json");

pub const HOUR: u32 = 60;
pub const DAY: u32 = 1440;

// System folders the password helper may clean. Running as root, it cleans nothing outside this list, whatever
// cleaners.json says.
pub const ADMIN_ROOTS: &[&str] = &[
    "/var/cache/apt/archives",
    "/var/cache/dnf",
    "/var/cache/libdnf5",
    "/var/cache/zypp/packages",
    "/var/cache/pacman/pkg",
    "/var/lib/systemd/coredump",
    "/var/crash",
    "/var/log/journal",
];

#[derive(Clone, Copy, PartialEq, Eq, Debug, PartialOrd, Ord, Hash)]
pub enum Level { Safe, Review, Caution }

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Kind { Contents, Subfolders, Trash, Journal }

#[derive(Deserialize)]
struct CleanersFile { items: Vec<ItemDef> }

#[derive(Deserialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
struct ItemDef {
    id: String,
    name: String,
    level: String,
    #[serde(default)] kind: Option<String>,
    #[serde(default)] age: Option<String>,
    #[serde(default)] admin: bool,
    #[serde(default)] paths: Vec<String>,
    #[serde(default)] exclude: Vec<String>,
    #[serde(default)] extensions: Option<Vec<String>>,
    #[serde(default)] top_level_only: bool,
    #[serde(default)] short: String,
    #[serde(default)] what: String,
    #[serde(default)] deletes: String,
    #[serde(default)] safe: String,
    #[serde(default)] after: String,
}

#[derive(Clone, Debug)]
pub struct Cat {
    pub id: String,
    pub name: String,
    pub level: Level,
    pub kind: Kind,
    pub admin: bool,                         // a system folder: cleaned by the password helper
    pub roots: Vec<PathBuf>,
    pub exclude: HashSet<String>,
    pub extensions: Option<HashSet<String>>, // only files with these extensions (Downloads, apt)
    pub top_level_only: bool,                // only files directly in the folder
    pub is_custom: bool,                     // a folder added from the Space map
    pub default_age: u32,
    pub age_minutes: u32,                    // only files untouched for this long (0 = everything)
    pub last_age: u32,                       // the age to go back to when switching from "Everything"
    pub checked: bool,
    pub files: u64,
    pub bytes: u64,
    pub scanned: bool,
    pub blocked: bool,                       // couldn't be measured without the password
    pub short: String,
    pub what: String,
    pub deletes: String,
    pub safe_text: String,
    pub after: String,
}

impl Cat {
    pub fn is_safe(&self) -> bool { self.level == Level::Safe }
}

#[derive(Default)]
pub struct Counter {
    pub files: u64,
    pub bytes: u64,
    pub skipped: u64,
    pub trashed: u64,   // bytes moved to the Trash (they come back only when the Trash is emptied)
    pub cancel: Option<std::sync::Arc<std::sync::atomic::AtomicBool>>,              // Stop
    pub on_item: Option<Box<dyn FnMut(&Path, u64, bool, bool) + Send>>,            // path, size, ok, to the Trash
}

impl Counter {
    pub fn cancelled(&self) -> bool { self.cancel.as_ref().map(|c| c.load(std::sync::atomic::Ordering::Relaxed)).unwrap_or(false) }
}

pub fn is_root() -> bool { unsafe { libc::geteuid() == 0 } }
fn uid() -> u32 { unsafe { libc::geteuid() } }

pub fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).filter(|p| p.is_absolute()).unwrap_or_else(|| PathBuf::from("/nonexistent"))
}

fn config_dir() -> PathBuf { glib::user_config_dir().join("space-hunter") }

fn lines(name: &str) -> Vec<String> {
    fs::read_to_string(config_dir().join(name)).unwrap_or_default()
        .lines().map(|l| l.trim().to_string()).filter(|l| l.starts_with('/')).collect()
}

fn write_lines(name: &str, list: &[String]) {
    let _ = fs::create_dir_all(config_dir());
    let _ = fs::write(config_dir().join(name), list.join("\n"));
}

// Protected folders: never measured or cleaned, whatever is ticked.
pub mod keep {
    use super::*;
    pub fn all() -> Vec<String> { lines("keep.txt") }
    pub fn add(p: &str) { let mut a = all(); if !a.iter().any(|x| x == p) { a.push(p.to_string()); } write_lines("keep.txt", &a); }
    pub fn remove(p: &str) { let a: Vec<String> = all().into_iter().filter(|x| x != p).collect(); write_lines("keep.txt", &a); }
    pub fn is_kept(path: &Path, list: &[String]) -> bool { list.iter().any(|k| path.starts_with(k)) }
}

// Folders added to the cleanup list from the Space map.
pub mod custom {
    use super::*;
    pub fn all() -> Vec<String> { lines("your-folders.txt") }
    pub fn add(p: &str) { let mut a = all(); if !a.iter().any(|x| x == p) { a.push(p.to_string()); } write_lines("your-folders.txt", &a); }
    pub fn remove(p: &str) { let a: Vec<String> = all().into_iter().filter(|x| x != p).collect(); write_lines("your-folders.txt", &a); }
}

// "0" = everything; "30m", "1h", "14d"
pub fn parse_age(s: &str) -> u32 {
    if s == "0" || s.len() < 2 { return 0; }
    let (n, unit) = s.split_at(s.len() - 1);
    let n: u32 = n.parse().unwrap_or(0);
    match unit { "m" => n, "h" => n * HOUR, "d" => n * DAY, _ => 0 }
}

// The folders named in ~/.config/user-dirs.dirs (Documents, Downloads... in the system's language).
pub fn user_dirs() -> HashMap<String, PathBuf> {
    let mut m = HashMap::new();
    let h = home();
    let text = fs::read_to_string(glib::user_config_dir().join("user-dirs.dirs")).unwrap_or_default();
    for line in text.lines() {
        let line = line.trim();
        if line.starts_with('#') { continue; }
        if let Some((k, v)) = line.split_once('=') {
            let v = v.trim().trim_matches('"');
            let p = if let Some(rest) = v.strip_prefix("$HOME") { h.join(rest.trim_start_matches('/')) } else { PathBuf::from(v) };
            if p.is_absolute() && p != h { m.insert(k.trim().to_string(), p); }
        }
    }
    for (k, d) in [("XDG_DOCUMENTS_DIR", "Documents"), ("XDG_DOWNLOAD_DIR", "Downloads"), ("XDG_DESKTOP_DIR", "Desktop"),
                   ("XDG_PICTURES_DIR", "Pictures"), ("XDG_VIDEOS_DIR", "Videos"), ("XDG_MUSIC_DIR", "Music"),
                   ("XDG_PUBLICSHARE_DIR", "Public"), ("XDG_TEMPLATES_DIR", "Templates")] {
        m.entry(k.to_string()).or_insert_with(|| h.join(d));
    }
    m
}

// Your personal folders: nothing inside them is ever cleaned (except old installers in Downloads, by filter).
pub fn personal() -> Vec<PathBuf> {
    let h = home();
    let mut v: Vec<PathBuf> = user_dirs().into_values().collect();
    for n in ["Projects", "projects", "src", "code", "Code", "dev", "Dropbox", "Nextcloud", "ownCloud", "OneDrive",
              ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".password-store", ".local/share/keyrings"] {
        v.push(h.join(n));
    }
    v.sort(); v.dedup();
    v
}

pub fn personal_folder_of(p: &Path) -> Option<PathBuf> { personal().into_iter().find(|k| p.starts_with(k)) }

fn has_dots(p: &Path) -> bool { p.components().any(|c| matches!(c, Component::ParentDir | Component::CurDir)) }

// Never operate on these, whatever an item says.
pub fn is_safe_root(p: &Path, admin: bool, allow_downloads: bool) -> bool {
    if !p.is_absolute() || has_dots(p) { return false; }
    if admin { return ADMIN_ROOTS.iter().any(|a| p.starts_with(a)); }
    let h = home();
    let banned = [PathBuf::from("/"), PathBuf::from("/home"), h.clone(), h.join(".config"), h.join(".local"),
                  h.join(".local/share"), h.join(".cache")];
    if banned.iter().any(|b| b == p) { return false; }
    if let Some(k) = personal_folder_of(p) {
        let downloads = user_dirs().get("XDG_DOWNLOAD_DIR").cloned();
        if !(allow_downloads && Some(&k) == downloads.as_ref()) { return false; }
    }
    p.starts_with(&h) || p.starts_with("/tmp") || p.starts_with("/var/tmp")
}

fn fnmatch(pattern: &str, name: &str) -> bool {
    match (CString::new(pattern), CString::new(name)) {
        (Ok(p), Ok(n)) => unsafe { libc::fnmatch(p.as_ptr(), n.as_ptr(), 0) == 0 },
        _ => false,
    }
}

// Real sub-folders (links are never followed).
pub fn dirs(parent: &Path) -> Vec<PathBuf> {
    let mut v = Vec::new();
    if let Ok(rd) = fs::read_dir(parent) {
        for e in rd.flatten() {
            if let Ok(m) = fs::symlink_metadata(e.path()) { if m.is_dir() { v.push(e.path()); } }
        }
    }
    v
}

// ~/.config/Claude*/Cache  ->  every matching folder that exists. A path without wildcards is returned as it is.
pub fn expand(pattern: &str) -> Vec<PathBuf> {
    let mut p = pattern.to_string();
    if let Some(rest) = p.strip_prefix("~/") { p = format!("{}/{}", home().display(), rest); }
    if p.starts_with("$XDG_") {
        let (var, rest) = p.split_at(p.find('/').unwrap_or(p.len()));
        match user_dirs().get(var) { Some(d) => p = format!("{}{}", d.display(), rest), None => return vec![] }
    }
    if !p.starts_with('/') { return vec![]; }   // an unknown variable: never guess
    if !p.contains('*') && !p.contains('?') { return vec![PathBuf::from(p)]; }
    let mut current = vec![PathBuf::from("/")];
    for part in p.split('/').filter(|s| !s.is_empty()) {
        if part.contains('*') || part.contains('?') {
            current = current.iter().flat_map(|d| dirs(d).into_iter()
                .filter(|s| s.file_name().map(|n| fnmatch(part, &n.to_string_lossy())).unwrap_or(false))).collect();
        } else {
            current = current.iter().map(|d| d.join(part)).collect();
        }
        if current.is_empty() { break; }
    }
    current
}

fn is_dir(p: &Path) -> bool { fs::symlink_metadata(p).map(|m| m.is_dir()).unwrap_or(false) }

fn journal_installed() -> bool { is_dir(Path::new("/var/log/journal")) || is_dir(Path::new("/run/log/journal")) }

// "Archived and active journals take up 1.2G in the file system."
fn journal_usage() -> Option<u64> {
    let out = Command::new("journalctl").arg("--disk-usage").output().ok()?;
    if !out.status.success() { return None; }
    let text = String::from_utf8_lossy(&out.stdout);
    let word = text.split_whitespace().find(|w| w.chars().next().map(|c| c.is_ascii_digit()).unwrap_or(false))?;
    let num: String = word.chars().take_while(|c| c.is_ascii_digit() || *c == '.').collect();
    let unit = word[num.len()..].chars().next().unwrap_or('B');
    let n: f64 = num.parse().ok()?;
    let mul = match unit { 'K' => 1024.0, 'M' => 1048576.0, 'G' => 1073741824.0, 'T' => 1099511627776.0, _ => 1.0 };
    Some((n * mul) as u64)
}

// The items live in cleaners.json (built into the program); only what's on this PC gets a row.
pub fn build() -> Vec<Cat> {
    let file: CleanersFile = serde_json::from_str(CLEANERS).expect("cleaners.json is valid (checked when it's built)");
    let mut list = Vec::new();
    for d in file.items {
        let age = parse_age(d.age.as_deref().unwrap_or("0"));
        let kind = match d.kind.as_deref() { Some("subfolders") => Kind::Subfolders, Some("trash") => Kind::Trash, Some("journal") => Kind::Journal, _ => Kind::Contents };
        let level = match d.level.as_str() { "review" => Level::Review, "caution" => Level::Caution, _ => Level::Safe };
        let mut roots: Vec<PathBuf> = Vec::new();
        for p in &d.paths { for u in expand(p) { if !roots.contains(&u) { roots.push(u); } } }
        let c = Cat {
            id: d.id, name: d.name, level, kind, admin: d.admin, roots,
            exclude: d.exclude.into_iter().collect(),
            extensions: d.extensions.map(|v| v.into_iter().map(|e| e.to_lowercase()).collect()),
            top_level_only: d.top_level_only, is_custom: false,
            default_age: age, age_minutes: age, last_age: if age > 0 { age } else { 14 * DAY }, checked: level == Level::Safe,
            files: 0, bytes: 0, scanned: false, blocked: false,
            short: d.short, what: d.what, deletes: d.deletes, safe_text: d.safe, after: d.after,
        };
        let installed = match c.kind {
            Kind::Trash => true,
            Kind::Journal => journal_installed(),
            _ => c.roots.iter().any(|r| is_dir(r)),
        };
        if installed { list.push(c); }
    }
    for p in custom::all() {
        let path = PathBuf::from(&p);
        let name = path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or(p.clone());
        list.push(Cat {
            id: format!("custom:{}", p), name: format!("{}  (your folder)", name), level: Level::Caution, kind: Kind::Contents,
            admin: false, roots: vec![path], exclude: HashSet::new(), extensions: None, top_level_only: false, is_custom: true,
            default_age: 30 * DAY, age_minutes: 30 * DAY, last_age: 30 * DAY, checked: false, files: 0, bytes: 0, scanned: false, blocked: false,
            short: "Everything inside goes; the folder itself stays".into(),
            what: format!("A folder you added from the Space map: {}", p),
            deletes: "Every file inside it (older than the age you set) and the sub-folders that end up empty. The folder itself stays.".into(),
            safe_text: "Only you know. Open it and check before ticking. Protected folders inside it are skipped.".into(),
            after: "The files are gone, unless you choose Trash in the confirm window.".into(),
        });
    }
    list
}

fn size_of(m: &fs::Metadata) -> u64 { m.blocks() * 512 }   // space actually taken on disk

// Every regular file under root, on the same disk, skipping excluded and protected folders. Links are never followed.
fn walk(root: &Path, exclude: &HashSet<String>, kept: &[String], f: &mut dyn FnMut(&Path, &fs::Metadata)) {
    let dev = match fs::symlink_metadata(root) { Ok(m) => m.dev(), Err(_) => return };
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let rd = match fs::read_dir(&dir) { Ok(r) => r, Err(_) => continue };
        for e in rd.flatten() {
            let path = e.path();
            let m = match fs::symlink_metadata(&path) { Ok(m) => m, Err(_) => continue };
            let ft = m.file_type();
            if ft.is_symlink() { continue; }
            if ft.is_dir() {
                let name = e.file_name().to_string_lossy().to_string();
                if exclude.contains(&name) || keep::is_kept(&path, kept) || m.dev() != dev { continue; }
                stack.push(path);
            } else if ft.is_file() {
                f(&path, &m);
            }
        }
    }
}

fn newest(dir: &Path) -> SystemTime {
    let mut n = fs::symlink_metadata(dir).and_then(|m| m.modified()).unwrap_or(SystemTime::UNIX_EPOCH);
    walk(dir, &HashSet::new(), &[], &mut |_, m| { if let Ok(t) = m.modified() { if t > n { n = t; } } });
    n
}

fn remove_empty_dirs(root: &Path, keep_root: bool, exclude: &HashSet<String>, kept: &[String]) {
    let mut all = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for d in dirs(&dir) {
            let name = d.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
            if exclude.contains(&name) || keep::is_kept(&d, kept) { continue; }
            stack.push(d.clone());
            all.push(d);
        }
    }
    if !keep_root { all.push(root.to_path_buf()); }
    all.sort_by_key(|p| std::cmp::Reverse(p.components().count()));
    for d in all { let _ = fs::remove_dir(&d); }   // only removes folders that are empty
}

fn trash(path: &Path) -> bool { gio::File::for_path(path).trash(gio::Cancellable::NONE).is_ok() }

// Measure (delete = false) or clean (delete = true) one item.
pub fn process(c: &Cat, delete: bool, to_trash: bool, cnt: &mut Counter) {
    if c.kind == Kind::Journal {
        let before = journal_usage();
        if delete && is_root() {
            let days = (c.age_minutes / DAY).max(1);
            let _ = Command::new("journalctl").arg(format!("--vacuum-time={}d", days)).output();
            if let (Some(b), Some(a)) = (before, journal_usage()) { cnt.bytes += b.saturating_sub(a); cnt.files += 1; }
        } else if !delete {
            if let Some(b) = before { cnt.bytes += b; cnt.files += 1; }
        }
        return;
    }
    let cutoff = SystemTime::now() - Duration::from_secs(c.age_minutes as u64 * 60);
    let kept = keep::all();
    let me = uid();
    let root_user = is_root();
    let trash_it = to_trash && c.kind != Kind::Trash && !c.admin;   // emptying the Trash can't go to the Trash
    let handle = |path: &Path, m: &fs::Metadata, cut: SystemTime, cnt: &mut Counter| {
        if cnt.cancelled() { return; }
        if let Some(ext) = &c.extensions {
            let e = path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
            if !ext.contains(&e) { return; }
        }
        if keep::is_kept(path, &kept) { return; }
        if !root_user && m.uid() != me { return; }   // other people's and the system's files are never touched
        let t = m.modified().unwrap_or(SystemTime::UNIX_EPOCH);
        if t >= cut { if delete { cnt.skipped += 1; } return; }
        let size = size_of(m);
        if !delete { cnt.files += 1; cnt.bytes += size; return; }
        let ok = if trash_it { trash(path) } else { fs::remove_file(path).is_ok() };
        if ok { cnt.files += 1; if trash_it { cnt.trashed += size } else { cnt.bytes += size } } else { cnt.skipped += 1; }
        if let Some(f) = cnt.on_item.as_mut() { f(path, size, ok, trash_it); }
    };
    for root in &c.roots {
        if cnt.cancelled() { break; }
        let m = match fs::symlink_metadata(root) { Ok(m) => m, Err(_) => continue };
        if !m.is_dir() { continue; }   // a linked root is never followed
        if !is_safe_root(root, c.admin, c.extensions.is_some() && c.top_level_only) { continue; }
        if keep::is_kept(root, &kept) { continue; }
        match c.kind {
            Kind::Contents | Kind::Trash => {
                if c.top_level_only {
                    if let Ok(rd) = fs::read_dir(root) {
                        for e in rd.flatten() {
                            if let Ok(fm) = fs::symlink_metadata(e.path()) { if fm.is_file() { handle(&e.path(), &fm, cutoff, cnt); } }
                        }
                    }
                } else {
                    walk(root, &c.exclude, &kept, &mut |p, fm| handle(p, fm, cutoff, cnt));
                    if delete { remove_empty_dirs(root, true, &c.exclude, &kept); }
                }
            }
            Kind::Subfolders => {
                for sub in dirs(root) {
                    if keep::is_kept(&sub, &kept) || newest(&sub) >= cutoff { continue; }
                    let far = SystemTime::now() + Duration::from_secs(3600);
                    walk(&sub, &c.exclude, &kept, &mut |p, fm| handle(p, fm, far, cnt));
                    if delete { remove_empty_dirs(&sub, false, &c.exclude, &kept); }
                }
            }
            Kind::Journal => {}
        }
    }
}

// Where to run the helper from: inside an AppImage the program sits in a private mount root can't open, so use the
// AppImage file itself.
fn helper_exe() -> Option<PathBuf> {
    if let Some(a) = std::env::var_os("APPIMAGE") { return Some(PathBuf::from(a)); }
    std::env::current_exe().ok()
}

pub struct SystemResult { pub id: String, pub files: u64, pub bytes: u64, pub skipped: u64 }

// Clean the ticked system items: Linux shows its own password box once (pkexec), then the helper below runs as root.
// None = the password was declined or pkexec isn't there.
pub fn run_system_helper(items: &[&Cat]) -> Option<Vec<SystemResult>> {
    let exe = helper_exe()?;
    let arg: Vec<String> = items.iter().filter(|c| c.admin).map(|c| format!("{}:{}", c.id, c.age_minutes)).collect();
    if arg.is_empty() { return Some(vec![]); }
    let out = Command::new("pkexec").arg(exe).arg("--clean-system").arg(arg.join(",")).output().ok()?;
    if !out.status.success() { return None; }
    Some(String::from_utf8_lossy(&out.stdout).lines().filter_map(|l| {
        let p: Vec<&str> = l.split_whitespace().collect();
        if p.len() == 5 && p[0] == "RESULT" {
            Some(SystemResult { id: p[1].into(), files: p[2].parse().ok()?, bytes: p[3].parse().ok()?, skipped: p[4].parse().ok()? })
        } else { None }
    }).collect())
}

// The helper itself (started by pkexec as root): cleans only system items, only inside ADMIN_ROOTS.
pub fn clean_system(spec: &str) -> i32 {
    if !is_root() { eprintln!("--clean-system only runs through the password prompt"); return 2; }
    let wanted: HashMap<String, u32> = spec.split(',').filter_map(|s| {
        let (id, age) = s.split_once(':')?; Some((id.to_string(), age.parse().ok()?))
    }).collect();
    for mut c in build().into_iter().filter(|c| c.admin && wanted.contains_key(&c.id)) {
        c.age_minutes = wanted[&c.id];
        let mut cnt = Counter::default();
        process(&c, true, false, &mut cnt);
        println!("RESULT {} {} {} {}", c.id, cnt.files, cnt.bytes, cnt.skipped);
    }
    0
}

pub fn fmt(b: u64) -> String {
    let b = b as f64;
    let (k, m, g, t) = (1024.0, 1048576.0, 1073741824.0, 1099511627776.0);
    if b >= t { format!("{:.1} TB", b / t) }
    else if b >= g { format!("{:.1} GB", b / g) }
    else if b >= m { format!("{:.0} MB", b / m) }
    else if b >= k { format!("{:.0} KB", b / k) }
    else { "0 MB".to_string() }
}

// Your ticks and ages, kept between runs (~/.config/space-hunter/settings.tsv: id, ticked, age, last age).
pub mod settings {
    use super::*;
    fn path() -> PathBuf { config_dir().join("settings.tsv") }
    pub fn load(cats: &mut [Cat]) {
        let text = fs::read_to_string(path()).unwrap_or_default();
        for line in text.lines() {
            let p: Vec<&str> = line.split('\t').collect();
            if p.len() < 4 { continue; }
            if let Some(c) = cats.iter_mut().find(|c| c.id == p[0]) {
                if c.refused().is_none() { c.checked = p[1] == "1"; }
                if let Ok(a) = p[2].parse() { c.age_minutes = a; }
                if let Ok(a) = p[3].parse::<u32>() { if a > 0 { c.last_age = a; } }
            }
        }
    }
    pub fn save(cats: &[Cat]) {
        let _ = fs::create_dir_all(config_dir());
        let text: Vec<String> = cats.iter().map(|c| format!("{}\t{}\t{}\t{}", c.id, if c.checked { 1 } else { 0 }, c.age_minutes, c.last_age)).collect();
        let _ = fs::write(path(), text.join("\n"));
    }
}

// What's been freed so far, one line per clean (~/.local/share/space-hunter/cleans.tsv).
pub mod history {
    use super::*;
    fn path() -> PathBuf { glib::user_data_dir().join("space-hunter").join("cleans.tsv") }
    pub fn record(bytes: u64, trashed: u64) {
        let _ = fs::create_dir_all(glib::user_data_dir().join("space-hunter"));
        let now = SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let mut text = fs::read_to_string(path()).unwrap_or_default();
        text.push_str(&format!("{}\t{}\t{}\n", now, bytes, trashed));
        let _ = fs::write(path(), text);
    }
    // bytes sent to the Trash over all cleans
    pub fn trashed() -> u64 {
        fs::read_to_string(path()).unwrap_or_default().lines()
            .filter_map(|l| l.split('\t').nth(2).and_then(|v| v.parse::<u64>().ok())).sum()
    }
    // (bytes freed, number of cleans)
    pub fn total() -> (u64, u32) {
        let text = fs::read_to_string(path()).unwrap_or_default();
        let mut b = 0u64; let mut n = 0u32;
        for l in text.lines() { let p: Vec<&str> = l.split('\t').collect(); if p.len() >= 2 { b += p[1].parse::<u64>().unwrap_or(0); n += 1; } }
        (b, n)
    }
}

impl Cat {
    // something you added that the safety rules refuse: listed so it can be removed, never cleaned
    pub fn refused(&self) -> Option<&str> { if self.is_custom && !is_safe_root(&self.roots[0], false, false) { Some("likely your own files") } else { None } }
}

pub fn fmt_age(m: u32) -> String {
    if m == 0 { return "any age".into(); }
    if m % DAY == 0 { let n = m / DAY; return format!("{} day{}", n, if n == 1 { "" } else { "s" }); }
    if m % HOUR == 0 { let n = m / HOUR; return format!("{} hour{}", n, if n == 1 { "" } else { "s" }); }
    format!("{} min", m)
}

// The names a newcomer can act on without opening anything (the same as on Windows and Mac).
pub fn tier_name(l: Level) -> &'static str { match l { Level::Safe => "Quick clean", Level::Review => "Big caches & history", Level::Caution => "Personal & work items" } }
pub fn tier_desc(l: Level) -> &'static str { match l { Level::Safe => "Caches and leftovers that rebuild", Level::Review => "Big caches that download again, or old history", Level::Caution => "Can hold your work or history. Check before cleaning" } }
pub fn tier_explain(l: Level) -> &'static str {
    match l {
        Level::Safe => "Caches, temp files and leftovers that apps and Linux rebuild or no longer need. Your files, logins and passwords aren't touched. Clean these without worry; some things load a little slower once.",
        Level::Review => "Things that come back by downloading them again, or history you might still want. Read each one before you tick it.",
        Level::Caution => "These can hold your own work or history. Nothing here is ticked for you; open an item to see exactly what goes.",
    }
}
