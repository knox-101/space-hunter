// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - phase 1: the window, the look, the fonts and the disk numbers. Scanning and cleaning come next.

mod engine;
mod storage;
mod stats;
mod updater;
mod ui;

use gtk::prelude::*;
use gtk::glib;
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

// Every window's content sits in a "sheet": a 1 px hairline in the theme's line colour just inside the window's edge,
// so overlapping Space Hunter windows read as separate sheets even without shadows.
pub fn sheet(child: &gtk::Widget) -> gtk::Box {
    let b = gtk::Box::new(gtk::Orientation::Vertical, 0);
    b.add_css_class("sheet");
    child.set_hexpand(true);
    child.set_vexpand(true);
    b.append(child);
    b
}

const CSS: &str = r#"
window, .chassis { background: $CHASSIS; }
* { font-family: "Schibsted Grotesk"; }
.display { background: $SCREEN; border-radius: 7px; padding: 14px 22px 18px 22px; }
.display-label { color: #B4B1AA; font-weight: 600; font-size: 11pt; }
.hero { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 60pt; }
.hero-unit { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 24pt; }
.beside { color: #E9E6DF; font-weight: 600; font-size: 10.5pt; }
.beside-muted { color: #A8A49C; font-weight: 600; font-size: 10.5pt; }
.disk { color: #B4B1AA; font-size: 11pt; }
.legend { color: #A8A49C; font-weight: 600; font-size: 10.5pt; }
button.key { background: $KFACE; background-image: none; color: $INK; border: none; border-radius: 4px;
             box-shadow: 0 2px 0 $KSHADOW; font-weight: 600; font-size: 9.75pt; padding: 8px 14px; min-height: 0; }
button.key:hover { background: $KHOVER; }
button.key:active { box-shadow: none; margin-top: 2px; margin-bottom: -2px; }
button.key.accent { box-shadow: inset 0 0 0 2px #FF5B1F, 0 2px 0 $KSHADOW; }
button.key.primary { background: #FF5B1F; color: #1B1B1A; box-shadow: 0 2px 0 #B5400F; font-size: 11.5pt; padding: 12px 28px; }
button.key.primary:disabled { background: $KOFF; color: $DISABLED; box-shadow: 0 2px 0 $LINE; }
.section-title { color: $INK; font-weight: 700; font-size: 10pt; }
.explain { color: $MUTED; font-size: 9pt; }
.note { color: $SOFT; font-size: 9.5pt; }
.status { color: $MUTED; font-size: 9pt; padding: 10px 22px; border-top: 1px solid $LINE; }
.legend-ink { color: #E9E6DF; font-weight: 600; font-size: 10.5pt; }
button.key.on { background: $INK; color: $PANE; box-shadow: 0 2px 0 #000000; }
button.key.small { font-size: 8.5pt; padding: 5px 10px; }
button.key:disabled { background: $KOFF; color: $DISABLED; box-shadow: 0 2px 0 $LINE; }
button.link-btn { background: none; border: none; box-shadow: none; padding: 2px 4px; color: $INK; font-weight: 600; font-size: 9pt; border-bottom: 1px solid $INK; border-radius: 0; min-height: 0; }
button.link-btn:hover { color: $ODEEP; border-bottom-color: $ODEEP; }
.rowline { border-bottom: 1px solid $LINE; }
button.row-btn { background: none; background-image: none; border: none; box-shadow: none; border-radius: 0; padding: 8px 6px; }
button.row-btn:hover { background: $HOVER; }
button.row-btn.open { background: $SELECTED; }
.row-name { color: $INK; font-weight: 600; font-size: 10.5pt; }
.row-name-dim { color: $SOFT; font-size: 10pt; }
.row-sub { color: $SOFT; font-size: 9pt; }
.pick-chip { background: $SCREEN; color: #E9E6DF; font-weight: 600; font-size: 8.25pt; padding: 0 6px; border-radius: 3px; }
.row-size { color: $INK; font-weight: 600; font-size: 11pt; }
.chevron { color: $SOFT; font-size: 16pt; }
.chevron-open { color: #FF5B1F; font-size: 16pt; font-weight: 700; }
.group-note { color: $SOFT; font-size: 9pt; }
checkbutton check { min-width: 17px; min-height: 17px; border-radius: 2px; border: 2px solid $INK; background: transparent; background-image: none; box-shadow: none; }
checkbutton check:checked, checkbutton check:indeterminate { background: #FF5B1F; border-color: #FF5B1F; color: #1B1B1A; }
checkbutton.dim check:checked { background: $LINE; border-color: $LINE; }
checkbutton check:disabled { border-color: $LINE; background: transparent; }
checkbutton check:checked:disabled { background: $LINE; border-color: $LINE; color: $PANE; }
.panel { background: $PANE; border-left: 1px solid $LINE; }
.panel-tag { color: $MUTED; font-weight: 600; font-size: 9.5pt; }
.panel-title { color: $INK; font-weight: 700; font-size: 14pt; }
.panel-stats { color: $INK; font-size: 10.5pt; margin-bottom: 6px; }
.panel-head { color: $MUTED; font-weight: 600; font-size: 10pt; }
.panel-body { color: $INK; font-size: 10.25pt; }
.panel-path { color: $INK; font-size: 9.5pt; }
.sum-key { color: $MUTED; font-size: 9.5pt; }
.sum-val { color: $INK; font-size: 9.75pt; }
.inside-state { color: $MUTED; font-weight: 600; font-size: 9pt; }
.inside-state.picked { color: $ODEEP; }
.age-value { color: $INK; font-weight: 600; font-size: 13pt; }
.confirm-title { color: $INK; font-weight: 700; font-size: 17pt; margin-bottom: 4px; }
.confirm-head { color: $MUTED; font-weight: 700; font-size: 9.5pt; }
.confirm-how { color: $MUTED; font-weight: 600; font-size: 10pt; }
.hero-mid { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 40pt; }
.panel-title-dark { color: #E9E6DF; font-size: 11pt; }
button.key.drive { padding: 8px 14px; }
.drive-text { font-weight: 600; font-size: 9.75pt; color: $INK; }
button.place-row { background: none; border: none; box-shadow: none; padding: 8px 12px; border-radius: 3px; }
button.place-row:hover { background: $HOVER; }
button.crumb { background: none; border: none; box-shadow: none; padding: 4px 6px; color: $MUTED; font-size: 10pt; border-radius: 3px; }
button.crumb:hover { background: $KFACE; color: $INK; }
button.crumb-now { background: none; border: none; box-shadow: none; padding: 4px 6px; color: $INK; font-weight: 700; font-size: 10pt; }
.crumb-sep { color: $SOFT; font-size: 11pt; }
.chart-caption { color: $MUTED; font-size: 8.75pt; }
.included { color: #E9E6DF; background: $SCREEN; font-weight: 600; font-size: 8.25pt; padding: 6px 12px; border-radius: 4px; }
button.key.primary-small { background: #FF5B1F; color: #1B1B1A; box-shadow: 0 2px 0 #B5400F; }
.update-card { background: $SCREEN; border-radius: 7px; padding: 12px 18px; margin: 8px 18px 10px 18px; }
.update-title { color: #E9E6DF; font-weight: 600; font-size: 11pt; }
.update-sub { color: #A8A49C; font-size: 9pt; }
.update-dot { color: #FF5B1F; font-size: 14pt; }
textview.log, textview.log text { background: $PANE; color: $INK; font-family: "IBM Plex Mono"; font-size: 8.5pt; }
popover contents { background: $PANE; color: $INK; }
.sheet { background: $CHASSIS; border: 1px solid $LINE; }
button.link-btn.flush { padding-left: 0; padding-right: 0; }
.cta-bar { background: #FF5B1F; }
.cta { color: $ODEEP; font-weight: 600; font-size: 9.75pt; }
"#;

// Light, Dark or System (follows the desktop). Two style sheets are made from the one above, and the one wanted now is
// the one in use; drawings read the same colours from pal().
pub mod theme {
    use gtk::prelude::*;
    use gtk::{gdk, gio, glib};
    use std::cell::RefCell;
    use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};

    #[derive(Clone, Copy, PartialEq, Eq, Debug)]
    pub enum Mode { Light, Dark, System }

    #[derive(Clone, Copy)]
    pub struct Pal {
        pub chassis: u32, pub pane: u32, pub selected: u32, pub hover: u32, pub screen: u32, pub ink: u32, pub muted: u32,
        pub soft: u32, pub line: u32, pub orange_deep: u32, pub key: u32, pub key_shadow: u32, pub disabled: u32,
        pub key_off: u32, pub key_hover: u32,
    }
    const LIGHT: Pal = Pal { chassis: 0xD8D5CE, pane: 0xE4E1DA, selected: 0xE9E6E0, hover: 0xE0DDD6, screen: 0x121212, ink: 0x1B1B1A,
        muted: 0x55524C, soft: 0x57534D, line: 0xC3BFB6, orange_deep: 0x9A3409, key: 0xECEAE5, key_shadow: 0xA9A59C,
        disabled: 0x9A968F, key_off: 0xD0CDC6, key_hover: 0xF3F1ED };
    const DARK: Pal = Pal { chassis: 0x1E1E1C, pane: 0x262624, selected: 0x30302D, hover: 0x2A2A27, screen: 0x0B0B0A, ink: 0xECE9E3,
        muted: 0xABA79F, soft: 0xA6A29A, line: 0x3B3A37, orange_deep: 0xFF7A45, key: 0x34332F, key_shadow: 0x0E0E0D,
        disabled: 0x6E6B65, key_off: 0x2C2B28, key_hover: 0x3E3D38 };
    // the display's own colours, the same in both
    pub const DISPLAY_INK: u32 = 0xE9E6DF;
    pub const DISPLAY_MUTED: u32 = 0x8A8A86;

    static DARK_NOW: AtomicBool = AtomicBool::new(false);
    static MODE: AtomicU8 = AtomicU8::new(2);                     // 0 light, 1 dark, 2 system
    static DESKTOP_PREFER: AtomicBool = AtomicBool::new(false);   // GTK's own "prefer dark" before the app sets it
    static PORTAL: AtomicU8 = AtomicU8::new(0);                   // the desktop's colour scheme: 0 not known, 1 dark, 2 light
    static FORCED: AtomicBool = AtomicBool::new(false);           // test hook: --theme, not saved

    thread_local! {
        // light, dark, and which one is in use (None: neither yet)
        static PROVIDERS: RefCell<Option<(gtk::CssProvider, gtk::CssProvider, Option<bool>)>> = RefCell::new(None);
    }

    pub fn pal() -> Pal { if DARK_NOW.load(Ordering::Relaxed) { DARK } else { LIGHT } }
    pub fn rgb(cr: &gtk::cairo::Context, c: u32) {
        cr.set_source_rgb(((c >> 16) & 255) as f64 / 255.0, ((c >> 8) & 255) as f64 / 255.0, (c & 255) as f64 / 255.0);
    }

    pub fn mode() -> Mode { match MODE.load(Ordering::Relaxed) { 0 => Mode::Light, 1 => Mode::Dark, _ => Mode::System } }
    fn store(m: Mode) { MODE.store(match m { Mode::Light => 0, Mode::Dark => 1, Mode::System => 2 }, Ordering::Relaxed); }
    fn file() -> std::path::PathBuf { crate::engine::config_dir().join("theme.txt") }

    // the test hook for screenshots: this run only
    pub fn force(dark: bool) { FORCED.store(true, Ordering::Relaxed); store(if dark { Mode::Dark } else { Mode::Light }); }

    pub fn set_mode(m: Mode) {
        store(m);
        if !FORCED.load(Ordering::Relaxed) {
            let _ = std::fs::create_dir_all(crate::engine::config_dir());
            let _ = std::fs::write(file(), match m { Mode::Light => "light", Mode::Dark => "dark", Mode::System => "system" });
        }
        apply();
    }

    fn css(p: &Pal) -> String {
        let h = |c: u32| format!("#{:06X}", c);
        super::CSS.replace("$CHASSIS", &h(p.chassis)).replace("$PANE", &h(p.pane)).replace("$SELECTED", &h(p.selected))
            .replace("$HOVER", &h(p.hover)).replace("$SCREEN", &h(p.screen)).replace("$INK", &h(p.ink))
            .replace("$MUTED", &h(p.muted)).replace("$SOFT", &h(p.soft)).replace("$LINE", &h(p.line))
            .replace("$ODEEP", &h(p.orange_deep)).replace("$KFACE", &h(p.key)).replace("$KSHADOW", &h(p.key_shadow))
            .replace("$DISABLED", &h(p.disabled)).replace("$KOFF", &h(p.key_off)).replace("$KHOVER", &h(p.key_hover))
    }

    // The desktop's light/dark choice from the freedesktop settings portal (GNOME, KDE and others). None: not known.
    fn portal_scheme(timeout_ms: i32) -> Option<u8> {
        let conn = gio::bus_get_sync(gio::BusType::Session, gio::Cancellable::NONE).ok()?;
        let args = ("org.freedesktop.appearance".to_string(), "color-scheme".to_string()).to_variant();
        let reply = conn.call_sync(Some("org.freedesktop.portal.Desktop"), "/org/freedesktop/portal/desktop",
                                   "org.freedesktop.portal.Settings", "Read", Some(&args),
                                   None, gio::DBusCallFlags::NONE, timeout_ms, gio::Cancellable::NONE).ok()?;
        if reply.n_children() == 0 { return None; }
        let mut v = reply.child_value(0);
        for _ in 0..3 { match v.as_variant() { Some(inner) => v = inner, None => break } }   // the value comes wrapped once or twice
        v.get::<u32>().map(|n| n.min(2) as u8)
    }

    fn desktop_dark() -> bool {
        match PORTAL.load(Ordering::Relaxed) {
            1 => true,
            2 => false,
            _ => {
                if DESKTOP_PREFER.load(Ordering::Relaxed) { return true; }
                let name = gtk::Settings::default().and_then(|s| s.gtk_theme_name()).map(|n| n.to_lowercase()).unwrap_or_default();
                name.ends_with("-dark") || name.ends_with(":dark")
            }
        }
    }

    fn want_dark() -> bool { match mode() { Mode::Light => false, Mode::Dark => true, Mode::System => desktop_dark() } }

    // Once, at startup: read the saved choice and the desktop's, make both style sheets and use the right one.
    pub fn init() {
        if !FORCED.load(Ordering::Relaxed) {
            let t = std::fs::read_to_string(file()).unwrap_or_default();
            store(match t.trim() { "light" => Mode::Light, "dark" => Mode::Dark, _ => Mode::System });
        }
        if let Some(s) = gtk::Settings::default() {
            DESKTOP_PREFER.store(s.is_gtk_application_prefer_dark_theme(), Ordering::Relaxed);
            s.connect_gtk_theme_name_notify(|_| { if mode() == Mode::System { apply(); } });
        }
        PORTAL.store(portal_scheme(300).unwrap_or(0), Ordering::Relaxed);
        let light = gtk::CssProvider::new();
        light.load_from_data(&css(&LIGHT));
        let dark = gtk::CssProvider::new();
        dark.load_from_data(&css(&DARK));
        PROVIDERS.with(|p| *p.borrow_mut() = Some((light, dark, None)));
        apply();
        // the desktop can switch while the app is open: ask again now and then, away from the window
        std::thread::spawn(|| loop {
            std::thread::sleep(std::time::Duration::from_secs(4));
            if let Some(v) = portal_scheme(2000) { PORTAL.store(v, Ordering::Relaxed); }
        });
        glib::timeout_add_seconds_local(2, || {
            if mode() == Mode::System && want_dark() != DARK_NOW.load(Ordering::Relaxed) { apply(); }
            glib::ControlFlow::Continue
        });
    }

    // Use the style sheet wanted now, and redraw everything drawn by hand.
    pub fn apply() {
        let dark = want_dark();
        DARK_NOW.store(dark, Ordering::Relaxed);
        if let Some(s) = gtk::Settings::default() {   // GTK's own parts (title bars, scroll bars) follow too
            if s.is_gtk_application_prefer_dark_theme() != dark { s.set_gtk_application_prefer_dark_theme(dark); }
        }
        if let Some(d) = gdk::Display::default() {
            PROVIDERS.with(|p| {
                let mut p = p.borrow_mut();
                if let Some((light, darkp, current)) = p.as_mut() {
                    if *current != Some(dark) {
                        if current.is_some() {
                            let old: &gtk::CssProvider = if dark { &*light } else { &*darkp };
                            gtk::style_context_remove_provider_for_display(&d, old);
                        }
                        let new: &gtk::CssProvider = if dark { &*darkp } else { &*light };
                        gtk::style_context_add_provider_for_display(&d, new, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
                        *current = Some(dark);
                    }
                }
            });
        }
        for w in gtk::Window::list_toplevels() { redraw(&w); }
    }

    fn redraw(w: &gtk::Widget) {
        w.queue_draw();
        let mut c = w.first_child();
        while let Some(x) = c { redraw(&x); c = x.next_sibling(); }
    }
}

fn main() -> glib::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--version") {
        println!("Space Hunter {}", VERSION.trim());
        return glib::ExitCode::SUCCESS;
    }
    // test hook for CI: check a signature with a given public key
    if let Some(i) = args.iter().position(|a| a == "--test-verify") {
        let ok = updater::test_verify(args.get(i + 1).map(|s| s.as_str()).unwrap_or(""), args.get(i + 2).map(|s| s.as_str()).unwrap_or(""), args.get(i + 3).map(|s| s.as_str()).unwrap_or(""));
        println!("{}", if ok { "SIGNATURE OK" } else { "SIGNATURE BAD" });
        return glib::ExitCode::from(if ok { 0 } else { 1 });
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
        if args.iter().any(|a| a == "--by-changed") { engine::BY_CHANGED_ONLY.store(true, std::sync::atomic::Ordering::Relaxed); }
        if !engine::is_safe_root(&dir, false, false) { println!("REFUSED {}", dir.display()); return glib::ExitCode::from(3i32); }
        // --pick <path> (repeatable, or several joined with '|'): the item is unticked and only these are cleaned
        let picks: Vec<PathBuf> = args.iter().enumerate().filter(|(_, a)| a.as_str() == "--pick")
            .filter_map(|(j, _)| args.get(j + 1))
            .flat_map(|s| s.split('|').filter(|x| !x.is_empty()).map(PathBuf::from).collect::<Vec<PathBuf>>())
            .collect();
        let mut c = engine::Cat {
            id: "test".into(), name: "Test folder".into(), level: engine::Level::Safe,
            kind: if sub { engine::Kind::Subfolders } else { engine::Kind::Contents }, admin: false,
            roots: vec![dir], exclude: ["memory".to_string()].into_iter().collect(), extensions: None, top_level_only: false,
            is_custom: false, default_age: engine::DAY, age_minutes: engine::DAY, last_age: engine::DAY, checked: true, files: 0, bytes: 0,
            scanned: false, blocked: false, short: String::new(), what: String::new(), deletes: String::new(),
            safe_text: String::new(), after: String::new(),
            picks: Default::default(), pick_files: 0, pick_bytes: None,
        };
        if !picks.is_empty() { c.checked = false; c.picks = picks.into_iter().collect(); }
        let mut m = engine::Counter::default();
        if c.partial() { engine::process_picks(&c, false, false, &mut m); } else { engine::process(&c, false, false, &mut m); }
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
    // test hook for screenshots: --theme light|dark (this run only)
    if let Some(t) = args.iter().position(|a| a == "--theme").and_then(|i| args.get(i + 1)) { theme::force(t == "dark"); }
    app.connect_startup(|_| theme::init());
    let opts = ui::Opts {
        open: args.iter().position(|a| a == "--open").and_then(|i| args.get(i + 1)).cloned(),
        confirm: args.iter().any(|a| a == "--confirm"),
        progress_dir: args.iter().position(|a| a == "--test-progress").and_then(|i| args.get(i + 1)).cloned(),
        storage: args.iter().position(|a| a == "--storage").and_then(|i| args.get(i + 1)).cloned(),
        drill: args.iter().position(|a| a == "--drill").and_then(|i| args.get(i + 1)).and_then(|s| s.parse().ok()).unwrap_or(0),
        protect: args.iter().any(|a| a == "--protect"),
        guide: args.iter().position(|a| a == "--guide").and_then(|i| args.get(i + 1)).and_then(|s| s.parse().ok()),
        quiet: quit_after.is_some(),
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
