// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - phase 1: the window, the look, the fonts and the disk numbers. Scanning and cleaning come next.

mod engine;
mod storage;
mod ui;

use gtk::prelude::*;
use gtk::{gdk, glib};
use std::ffi::CString;
use std::path::PathBuf;

const VERSION: &str = include_str!("../../VERSION");

// the same fonts as the Windows and Mac apps, built into the program (SIL Open Font License, see fonts/OFL.txt)
const FONTS: &[(&str, &[u8])] = &[
    ("BigShouldersDisplay-Black.ttf", include_bytes!("../../fonts/BigShouldersDisplay-Black.ttf")),
    ("SchibstedGrotesk-Regular.ttf", include_bytes!("../../fonts/SchibstedGrotesk-Regular.ttf")),
    ("SchibstedGrotesk-SemiBold.ttf", include_bytes!("../../fonts/SchibstedGrotesk-SemiBold.ttf")),
    ("SchibstedGrotesk-Bold.ttf", include_bytes!("../../fonts/SchibstedGrotesk-Bold.ttf")),
    ("IBMPlexMono-Regular.ttf", include_bytes!("../../fonts/IBMPlexMono-Regular.ttf")),
    ("IBMPlexMono-SemiBold.ttf", include_bytes!("../../fonts/IBMPlexMono-SemiBold.ttf")),
];

#[link(name = "fontconfig")]
extern "C" {
    fn FcConfigAppFontAddFile(config: *mut libc::c_void, file: *const u8) -> libc::c_int;
}

// Make the built-in fonts available to this program only (nothing is installed for the rest of the system).
fn load_fonts() {
    let dir: PathBuf = glib::user_cache_dir().join("space-hunter").join("fonts");
    if std::fs::create_dir_all(&dir).is_err() {
        return;
    }
    for (name, data) in FONTS {
        let path = dir.join(name);
        let same = std::fs::metadata(&path).map(|m| m.len() == data.len() as u64).unwrap_or(false);
        if !same && std::fs::write(&path, data).is_err() {
            continue;
        }
        if let Ok(c) = CString::new(path.to_string_lossy().as_bytes()) {
            unsafe { FcConfigAppFontAddFile(std::ptr::null_mut(), c.as_ptr() as *const u8) };
        }
    }
}

// Free and total bytes of the disk that holds `path`.
fn disk(path: &str) -> (u64, u64) {
    let c = match CString::new(path) { Ok(c) => c, Err(_) => return (0, 0) };
    let mut s: libc::statvfs = unsafe { std::mem::zeroed() };
    if unsafe { libc::statvfs(c.as_ptr(), &mut s) } != 0 {
        return (0, 0);
    }
    let unit = s.f_frsize as u64;
    (s.f_bavail as u64 * unit, s.f_blocks as u64 * unit)
}

#[allow(unused_imports)]
use engine::fmt;

const CSS: &str = r#"
window, .chassis { background: #D8D5CE; }
* { font-family: "Schibsted Grotesk"; }
.display { background: #121212; border-radius: 7px; padding: 14px 22px 18px 22px; }
.display-label { color: #B4B1AA; font-weight: 600; font-size: 11pt; }
.hero { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 60pt; }
.hero-unit { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 24pt; }
.beside { color: #E9E6DF; font-weight: 600; font-size: 10.5pt; }
.beside-muted { color: #A8A49C; font-weight: 600; font-size: 10.5pt; }
.disk { color: #B4B1AA; font-size: 11pt; }
.legend { color: #A8A49C; font-weight: 600; font-size: 10.5pt; }
button.key { background: #ECEAE5; background-image: none; color: #1B1B1A; border: none; border-radius: 4px;
             box-shadow: 0 2px 0 #A9A59C; font-weight: 600; font-size: 9.75pt; padding: 8px 14px; min-height: 0; }
button.key:hover { background: #F3F1ED; }
button.key:active { box-shadow: none; margin-top: 2px; margin-bottom: -2px; }
button.key.accent { box-shadow: inset 0 0 0 2px #FF5B1F, 0 2px 0 #A9A59C; }
button.key.primary { background: #FF5B1F; box-shadow: 0 2px 0 #B5400F; font-size: 11.5pt; padding: 12px 28px; }
button.key.primary:disabled { background: #D0CDC6; color: #9A968F; box-shadow: 0 2px 0 #C3BFB6; }
.tile { background: #ECEAE5; border-radius: 6px; box-shadow: 0 3px 0 #A9A59C; padding: 10px 16px 12px 16px; }
.tile.on { background: #1B1B1A; box-shadow: 0 3px 0 #000000; }
.tile-name { color: #1B1B1A; font-weight: 700; font-size: 11.5pt; }
.tile.on .tile-name { color: #E9E6DF; }
.tile-desc { color: #7D7972; font-size: 9pt; }
.tile.on .tile-desc { color: #8A8A86; }
.section-title { color: #1B1B1A; font-weight: 700; font-size: 10pt; }
.explain { color: #5F5C56; font-size: 9pt; }
.note { color: #7D7972; font-size: 9.5pt; }
.status { color: #5F5C56; font-size: 9pt; padding: 10px 22px; border-top: 1px solid #C3BFB6; }
.legend-ink { color: #E9E6DF; font-weight: 600; font-size: 10.5pt; }
button.key.on { background: #1B1B1A; color: #E9E6DF; box-shadow: 0 2px 0 #000000; }
button.key.small { font-size: 8.5pt; padding: 5px 10px; }
button.key:disabled { background: #D0CDC6; color: #9A968F; box-shadow: 0 2px 0 #C3BFB6; }
button.tile { background: #ECEAE5; background-image: none; border: none; border-radius: 6px; box-shadow: 0 3px 0 #A9A59C; padding: 10px 16px 12px 16px; }
button.tile:hover { background: #F3F1ED; }
button.tile.on { background: #1B1B1A; box-shadow: 0 3px 0 #000000; }
button.tile label.tile-name { color: #1B1B1A; font-weight: 700; font-size: 11.5pt; }
button.tile.on label.tile-name { color: #E9E6DF; }
button.tile label.tile-size { color: #1B1B1A; font-weight: 600; font-size: 12.5pt; }
button.tile.on label.tile-size { color: #E9E6DF; }
button.tile label.tile-desc { color: #7D7972; font-size: 9pt; }
button.tile.on label.tile-desc { color: #8A8A86; }
button.tile label.tile-count { color: #5F5C56; font-weight: 600; font-size: 9pt; }
button.tile.on label.tile-count { color: #E9E6DF; }
button.link-btn { background: none; border: none; box-shadow: none; padding: 2px 4px; color: #1B1B1A; font-weight: 600; font-size: 9pt; border-bottom: 1px solid #1B1B1A; border-radius: 0; }
.rowline { border-bottom: 1px solid #C3BFB6; }
button.row-btn { background: none; background-image: none; border: none; box-shadow: none; border-radius: 0; padding: 8px 6px; }
button.row-btn:hover { background: #E0DDD6; }
button.row-btn.open { background: #E9E6E0; }
.row-name { color: #1B1B1A; font-weight: 600; font-size: 10.5pt; }
.row-name-dim { color: #7D7972; font-size: 10pt; }
.row-sub { color: #7D7972; font-size: 9pt; }
.row-size { color: #1B1B1A; font-weight: 600; font-size: 11pt; }
.chevron { color: #7D7972; font-size: 16pt; }
.chevron-open { color: #FF5B1F; font-size: 16pt; font-weight: 700; }
.group-note { color: #7D7972; font-size: 9pt; }
checkbutton check { min-width: 17px; min-height: 17px; border-radius: 2px; border: 2px solid #1B1B1A; background: transparent; background-image: none; box-shadow: none; }
checkbutton check:checked { background: #FF5B1F; border-color: #FF5B1F; color: #1B1B1A; }
checkbutton check:disabled { border-color: #C3BFB6; background: transparent; }
.panel { background: #E4E1DA; border-left: 1px solid #C3BFB6; }
.panel-tag { color: #5F5C56; font-weight: 600; font-size: 9.5pt; }
.panel-title { color: #1B1B1A; font-weight: 700; font-size: 14pt; }
.panel-stats { color: #1B1B1A; font-size: 10.5pt; margin-bottom: 6px; }
.panel-head { color: #5F5C56; font-weight: 600; font-size: 10pt; }
.panel-body { color: #1B1B1A; font-size: 10.25pt; }
.panel-path { color: #1B1B1A; font-size: 9.5pt; }
.age-value { color: #1B1B1A; font-weight: 600; font-size: 13pt; }
.confirm-title { color: #1B1B1A; font-weight: 700; font-size: 17pt; margin-bottom: 4px; }
.confirm-head { color: #5F5C56; font-weight: 700; font-size: 9.5pt; }
.confirm-how { color: #5F5C56; font-weight: 600; font-size: 10pt; }
.hero-mid { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 40pt; }
.panel-title-dark { color: #E9E6DF; font-size: 11pt; }
button.key.drive { padding: 8px 14px; }
.drive-text { font-weight: 600; font-size: 9.75pt; color: #1B1B1A; }
button.place-row { background: none; border: none; box-shadow: none; padding: 8px 12px; border-radius: 3px; }
button.place-row:hover { background: #E0DDD6; }
button.crumb { background: none; border: none; box-shadow: none; padding: 4px 6px; color: #5F5C56; font-size: 10pt; border-radius: 3px; }
button.crumb:hover { background: #ECEAE5; color: #1B1B1A; }
button.crumb-now { background: none; border: none; box-shadow: none; padding: 4px 6px; color: #1B1B1A; font-weight: 700; font-size: 10pt; }
.crumb-sep { color: #7D7972; font-size: 11pt; }
.chart-caption { color: #5F5C56; font-size: 8.75pt; }
.included { color: #E9E6DF; background: #1B1B1A; font-weight: 600; font-size: 8.25pt; padding: 6px 12px; border-radius: 4px; }
button.key.primary-small { background: #FF5B1F; box-shadow: 0 2px 0 #B5400F; }
textview.log, textview.log text { background: #E4E1DA; color: #1B1B1A; font-family: "IBM Plex Mono"; font-size: 8.5pt; }
"#;

fn main() -> glib::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--version") {
        println!("Space Hunter {}", VERSION.trim());
        return glib::ExitCode::SUCCESS;
    }
    // the password helper (started by pkexec as root): cleans the named system items, then exits
    if let Some(i) = args.iter().position(|a| a == "--clean-system") {
        return glib::ExitCode::from(engine::clean_system(args.get(i + 1).map(|s| s.as_str()).unwrap_or("")));
    }
    // test hook: list every item found on this PC with its size
    if args.iter().any(|a| a == "--report") {
        for c in engine::build() {
            let mut cnt = engine::Counter::default();
            engine::process(&c, false, false, &mut cnt);
            println!("{:<12} {:<8} {:>10} {:>8} files  {}", c.id, format!("{:?}", c.level), fmt(cnt.bytes), cnt.files,
                     c.roots.iter().map(|r| r.display().to_string()).collect::<Vec<_>>().join(", "));
        }
        return glib::ExitCode::SUCCESS;
    }
    // test hook: measure, then clean, one folder as a test item (files older than a day); 3 = the folder was refused
    if let Some(i) = args.iter().position(|a| a == "--test-engine") {
        let dir = std::path::PathBuf::from(args.get(i + 1).cloned().unwrap_or_default());
        let sub = args.iter().any(|a| a == "--subfolders");
        if !engine::is_safe_root(&dir, false, false) { println!("REFUSED {}", dir.display()); return glib::ExitCode::from(3i32); }
        let c = engine::Cat {
            id: "test".into(), name: "Test folder".into(), level: engine::Level::Safe,
            kind: if sub { engine::Kind::Subfolders } else { engine::Kind::Contents }, admin: false,
            roots: vec![dir], exclude: ["memory".to_string()].into_iter().collect(), extensions: None, top_level_only: false,
            is_custom: false, default_age: engine::DAY, age_minutes: engine::DAY, last_age: engine::DAY, checked: true, files: 0, bytes: 0,
            scanned: false, blocked: false, short: String::new(), what: String::new(), deletes: String::new(),
            safe_text: String::new(), after: String::new(),
        };
        let mut m = engine::Counter::default();
        engine::process(&c, false, false, &mut m);
        println!("MEASURE {} files {} bytes", m.files, m.bytes);
        let mut k = engine::Counter::default();
        engine::process(&c, true, args.iter().any(|a| a == "--trash"), &mut k);
        println!("CLEAN {} files {} bytes {} trashed {} skipped", k.files, k.bytes, k.trashed, k.skipped);
        return glib::ExitCode::SUCCESS;
    }
    // test hook: close by itself after N seconds (for screenshots on the build machine)
    let quit_after: Option<u32> = args.iter().position(|a| a == "--quit-after").and_then(|i| args.get(i + 1)).and_then(|s| s.parse().ok());

    load_fonts();
    let app = gtk::Application::builder().application_id("io.github.knox101.SpaceHunter").build();
    app.connect_startup(|_| {
        let css = gtk::CssProvider::new();
        css.load_from_data(CSS);
        if let Some(d) = gdk::Display::default() {
            gtk::style_context_add_provider_for_display(&d, &css, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
        }
    });
    let opts = ui::Opts {
        open: args.iter().position(|a| a == "--open").and_then(|i| args.get(i + 1)).cloned(),
        confirm: args.iter().any(|a| a == "--confirm"),
        progress_dir: args.iter().position(|a| a == "--test-progress").and_then(|i| args.get(i + 1)).cloned(),
        storage: args.iter().position(|a| a == "--storage").and_then(|i| args.get(i + 1)).cloned(),
        drill: args.iter().position(|a| a == "--drill").and_then(|i| args.get(i + 1)).and_then(|s| s.parse().ok()).unwrap_or(0),
        protect: args.iter().any(|a| a == "--protect"),
    };
    app.connect_activate(move |app| {
        let _ui = ui::build(app, opts.clone());
        if let Some(s) = quit_after {
            let a = app.clone();
            glib::timeout_add_seconds_local_once(s, move || a.quit());
        }
    });
    app.run_with_args::<&str>(&[])
}
