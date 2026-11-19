// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - phase 1: the window, the look, the fonts and the disk numbers. Scanning and cleaning come next.

mod engine;

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

use engine::fmt;

const CSS: &str = r#"
window, .chassis { background: #D8D5CE; }
* { font-family: "Schibsted Grotesk"; }
.display { background: #121212; border-radius: 7px; padding: 14px 22px 18px 22px; }
.display-label { color: #B4B1AA; font-weight: 600; font-size: 11pt; }
.hero { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 50pt; }
.hero-unit { color: #FF5B1F; font-family: "Big Shoulders Display", "Big Shoulders Display Black"; font-weight: 900; font-size: 20pt; }
.beside { color: #E9E6DF; font-weight: 600; font-size: 10.5pt; }
.beside-muted { color: #8A8A86; font-weight: 600; font-size: 10.5pt; }
.disk { color: #B4B1AA; font-size: 11pt; }
.legend { color: #8A8A86; font-weight: 600; font-size: 10.5pt; }
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
"#;

fn label(text: &str, class: &str) -> gtk::Label {
    let l = gtk::Label::new(Some(text));
    l.add_css_class(class);
    l.set_xalign(0.0);
    l
}

fn key(text: &str, classes: &[&str]) -> gtk::Button {
    let b = gtk::Button::with_label(&text.to_uppercase());
    b.add_css_class("key");
    for c in classes { b.add_css_class(c); }
    b.set_valign(gtk::Align::Center);
    b
}

// four blocks of different sizes: a tiny picture of the Space map, for its key
fn map_icon() -> gtk::DrawingArea {
    let d = gtk::DrawingArea::new();
    d.set_content_width(14);
    d.set_content_height(12);
    d.set_draw_func(|_, cr, _, _| {
        cr.set_source_rgb(0.106, 0.106, 0.102);
        cr.rectangle(0.0, 0.0, 7.0, 12.0);
        cr.rectangle(8.0, 0.0, 6.0, 7.0);
        cr.rectangle(8.0, 8.0, 3.0, 4.0);
        let _ = cr.fill();
        cr.set_source_rgb(1.0, 0.357, 0.122);
        cr.rectangle(12.0, 8.0, 2.0, 4.0);
        let _ = cr.fill();
    });
    d
}

fn tile(name: &str, desc: &str, on: bool) -> gtk::Box {
    let b = gtk::Box::new(gtk::Orientation::Vertical, 6);
    b.add_css_class("tile");
    if on { b.add_css_class("on"); }
    b.set_hexpand(true);
    b.append(&label(name, "tile-name"));
    b.append(&label(desc, "tile-desc"));
    b
}

fn build(app: &gtk::Application) {
    let (free, total) = disk("/");

    let win = gtk::ApplicationWindow::builder()
        .application(app)
        .title("Space Hunter")
        .default_width(1180)
        .default_height(760)
        .build();

    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    root.add_css_class("chassis");

    // the dark display: the number, the disk, one bar
    let display = gtk::Box::new(gtk::Orientation::Vertical, 0);
    display.add_css_class("display");
    display.set_margin_top(18);
    display.set_margin_start(18);
    display.set_margin_end(18);
    let top = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    let left = gtk::Box::new(gtk::Orientation::Vertical, 0);
    left.append(&label("Space to free up", "display-label"));
    let num = gtk::Box::new(gtk::Orientation::Horizontal, 14);
    let hero = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let n = label("0", "hero");
    let u = label("MB", "hero-unit");
    u.set_valign(gtk::Align::End);
    u.set_margin_bottom(12);
    hero.append(&n);
    hero.append(&u);
    num.append(&hero);
    let beside = gtk::Box::new(gtk::Orientation::Vertical, 4);
    beside.set_valign(gtk::Align::Center);
    beside.append(&label("Linux preview", "beside"));
    beside.append(&label("Scanning and cleaning come in the next step", "beside-muted"));
    num.append(&beside);
    left.append(&num);
    top.append(&left);
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    top.append(&spacer);
    let right = gtk::Box::new(gtk::Orientation::Vertical, 6);
    let dl = label("Disk /", "display-label");
    dl.set_xalign(1.0);
    let df = label(&format!("{} free of {}", fmt(free), fmt(total)), "disk");
    df.set_xalign(1.0);
    right.append(&dl);
    right.append(&df);
    top.append(&right);
    display.append(&top);

    let bar = gtk::DrawingArea::new();
    bar.set_content_height(16);
    bar.set_margin_top(14);
    let used = if total > 0 { (total - free) as f64 / total as f64 } else { 0.0 };
    bar.set_draw_func(move |_, cr, w, h| {
        cr.set_source_rgb(0.227, 0.227, 0.220);   // free: the track
        cr.rectangle(0.0, 0.0, w as f64, h as f64);
        let _ = cr.fill();
        cr.set_source_rgb(0.435, 0.424, 0.400);   // used
        cr.rectangle(0.0, 0.0, w as f64 * used, h as f64);
        let _ = cr.fill();
    });
    display.append(&bar);
    let legend = gtk::Box::new(gtk::Orientation::Horizontal, 24);
    legend.set_margin_top(10);
    legend.append(&label("■ Used", "legend"));
    legend.append(&label("□ Free", "legend"));
    display.append(&legend);
    root.append(&display);

    // the keys: the Space map alone at the left, the cleaning keys together at the right
    let keys = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    keys.set_margin_start(18);
    keys.set_margin_end(18);
    keys.set_margin_top(12);
    keys.set_margin_bottom(12);
    let map = gtk::Button::new();
    map.add_css_class("key");
    map.add_css_class("accent");
    map.set_valign(gtk::Align::Center);
    let mc = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    mc.append(&map_icon());
    mc.append(&gtk::Label::new(Some("SPACE MAP")));
    map.set_child(Some(&mc));
    keys.append(&map);
    let gap = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    gap.set_hexpand(true);
    keys.append(&gap);
    keys.append(&key("scan again", &[]));
    keys.append(&key("protected folders", &[]));
    let reset = key("reset to default", &[]);
    reset.set_margin_end(4);
    keys.append(&reset);
    let clean = key("clean", &["primary"]);
    clean.set_sensitive(false);
    keys.append(&clean);
    root.append(&keys);

    // the three sections
    let tiles = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    tiles.set_homogeneous(true);
    tiles.set_margin_start(18);
    tiles.set_margin_end(18);
    tiles.append(&tile("Safe to clean", "Rebuilds or isn't needed", true));
    tiles.append(&tile("Review first", "Re-downloads or history", false));
    tiles.append(&tile("Caution  !", "Can hold your history or work", false));
    root.append(&tiles);

    let section = gtk::Box::new(gtk::Orientation::Vertical, 6);
    section.set_margin_start(22);
    section.set_margin_end(22);
    section.set_margin_top(20);
    section.set_vexpand(true);
    section.append(&label("SAFE TO CLEAN  ·  Rebuilds or isn't needed", "section-title"));
    let ex = label("Caches, temp files and leftovers that apps and Linux rebuild or no longer need. Your files, logins and passwords aren't touched. Some things load a little slower once.", "explain");
    ex.set_wrap(true);
    ex.set_max_width_chars(110);
    section.append(&ex);
    let note = label("The cleanup list for Linux arrives in the next step.", "note");
    note.set_margin_top(18);
    section.append(&note);
    root.append(&section);

    root.append(&label(&format!("Space Hunter {} for Linux  ·  preview build", VERSION.trim()), "status"));
    win.set_child(Some(&root));
    win.present();
}

fn main() -> glib::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--version") {
        println!("Space Hunter {}", VERSION.trim());
        return glib::ExitCode::SUCCESS;
    }
    // the password helper (started by pkexec as root): cleans the named system items, then exits
    if let Some(i) = args.iter().position(|a| a == "--clean-system") {
        return glib::ExitCode::from(engine::clean_system(args.get(i + 1).map(|s| s.as_str()).unwrap_or("")) as u8);
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
        if !engine::is_safe_root(&dir, false, false) { println!("REFUSED {}", dir.display()); return glib::ExitCode::from(3); }
        let c = engine::Cat {
            id: "test".into(), name: "Test folder".into(), level: engine::Level::Safe,
            kind: if sub { engine::Kind::Subfolders } else { engine::Kind::Contents }, admin: false,
            roots: vec![dir], exclude: ["memory".to_string()].into_iter().collect(), extensions: None, top_level_only: false,
            is_custom: false, default_age: engine::DAY, age_minutes: engine::DAY, checked: true, files: 0, bytes: 0,
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
    app.connect_activate(move |app| {
        build(app);
        if let Some(s) = quit_after {
            let a = app.clone();
            glib::timeout_add_seconds_local_once(s, move || a.quit());
        }
    });
    app.run_with_args::<&str>(&[])
}
