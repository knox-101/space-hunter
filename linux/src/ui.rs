// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - the main window: the display, the keys, the three section tiles, the list, the item panel,
// the confirm window and the cleaning window. Same layout and wording as the Windows and Mac apps.

use crate::engine::{self, fmt, Cat, Counter, Kind, Level};
use gtk::glib;
use gtk::prelude::*;
use std::cell::{Cell, RefCell};
use std::collections::VecDeque;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;

const ORDER: [Level; 3] = [Level::Safe, Level::Review, Level::Caution];

#[derive(Default, Clone)]
pub struct Opts {
    pub open: Option<String>,          // test hook: open this item's panel once measured
    pub confirm: bool,                 // test hook: open the confirm window once measured
    pub progress_dir: Option<String>,  // test hook: clean this folder (to the Trash) in the cleaning window
    pub storage: Option<String>,       // test hook: open the Storage map on this folder
    pub drill: usize,                  // test hook: open the biggest folder this many levels down
    pub protect: bool,                 // test hook: open Protected folders
}

struct St {
    cats: Vec<Cat>,
    tier: Level,
    open: Option<String>,
    pending: usize,
    cleaning: bool,
    first_scan_done: bool,
}

pub struct Ui {
    st: RefCell<St>,
    win: gtk::ApplicationWindow,
    hero: gtk::Label,
    unit: gtk::Label,
    beside1: gtk::Label,
    beside2: gtk::Label,
    disk_text: gtk::Label,
    freed_text: gtk::Label,
    bar: gtk::DrawingArea,
    bar_vals: Cell<(f64, f64, f64)>,   // used by everything else, ticked, not ticked (shares of the disk)
    legend_ticked: gtk::Label,
    legend_not: gtk::Label,
    clean: gtk::Button,
    scan_key: gtk::Button,
    tiles: Vec<(gtk::Button, gtk::Label, gtk::Label)>,
    title: gtk::Label,
    explain: gtk::Label,
    rows: gtk::Box,
    panel_rev: gtk::Revealer,
    panel: gtk::Box,
    status: gtk::Label,
    prog_win: RefCell<Option<gtk::Window>>,
    opts: Opts,
}

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

fn on(b: bool) -> &'static [&'static str] { if b { &["on"] } else { &[] } }

fn clear(b: &gtk::Box) { while let Some(c) = b.first_child() { b.remove(&c); } }

fn hspacer() -> gtk::Box { let s = gtk::Box::new(gtk::Orientation::Horizontal, 0); s.set_hexpand(true); s }

fn split(b: u64) -> (String, String) {
    let s = fmt(b);
    match s.split_once(' ') { Some((n, u)) => (n.to_string(), u.to_string()), None => (s, String::new()) }
}

fn home_short(p: &std::path::Path) -> String {
    let h = engine::home();
    match p.strip_prefix(&h) { Ok(r) => format!("~/{}", r.display()), Err(_) => p.display().to_string() }
}

fn is_empty(c: &Cat) -> bool { c.refused().is_none() && c.scanned && c.bytes == 0 && c.files == 0 }

// four blocks of different sizes: a tiny picture of the Storage map, for its key
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

// a 13px legend box drawn like the bar: 0 ticked (orange), 1 not ticked (hatched), 2 used (grey), 3 free (outlined)
fn swatch(kind: u8) -> gtk::DrawingArea {
    let d = gtk::DrawingArea::new();
    d.set_content_width(13); d.set_content_height(13); d.set_valign(gtk::Align::Center);
    d.set_draw_func(move |_, cr, w, h| {
        let (w, h) = (w as f64, h as f64);
        match kind {
            0 => { cr.set_source_rgb(1.0, 0.357, 0.122); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill(); }
            1 => hatch(cr, 0.0, 0.0, w, h),
            2 => { cr.set_source_rgb(0.435, 0.424, 0.400); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill(); }
            _ => { cr.set_source_rgb(0.227, 0.227, 0.220); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill();
                   cr.set_source_rgb(0.541, 0.541, 0.525); cr.set_line_width(1.0); cr.rectangle(0.5, 0.5, w - 1.0, h - 1.0); let _ = cr.stroke(); }
        }
    });
    d
}

fn legend_item(kind: u8, l: &gtk::Label) -> gtk::Box {
    let b = gtk::Box::new(gtk::Orientation::Horizontal, 7);
    b.append(&swatch(kind)); b.append(l);
    b
}

fn hatch(cr: &gtk::cairo::Context, x: f64, y: f64, w: f64, h: f64) {
    if w <= 0.0 { return; }
    let _ = cr.save();
    cr.rectangle(x, y, w, h);
    cr.clip();
    cr.set_source_rgb(0.227, 0.227, 0.220);
    cr.rectangle(x, y, w, h);
    let _ = cr.fill();
    cr.set_source_rgb(1.0, 0.357, 0.122);
    cr.set_line_width(1.5);
    let mut lx = x - h;
    while lx < x + w { cr.move_to(lx, y + h); cr.line_to(lx + h, y); lx += 5.0; }
    let _ = cr.stroke();
    let _ = cr.restore();
}

pub fn build(app: &gtk::Application, opts: Opts) -> Rc<Ui> {
    let win = gtk::ApplicationWindow::builder().application(app).title("Space Hunter").default_width(1180).default_height(760).build();
    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    root.add_css_class("chassis");

    // the dark display: the number you get back, the disk as one bar with a clear legend
    let display = gtk::Box::new(gtk::Orientation::Vertical, 0);
    display.add_css_class("display");
    display.set_margin_top(18); display.set_margin_start(18); display.set_margin_end(18);
    let top = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    let left = gtk::Box::new(gtk::Orientation::Vertical, 0);
    left.append(&label("Space to free up", "display-label"));
    let num = gtk::Box::new(gtk::Orientation::Horizontal, 16);
    let heroes = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let hero = label("0", "hero");
    let unit = label("MB", "hero-unit");
    unit.set_valign(gtk::Align::End); unit.set_margin_bottom(12);
    heroes.append(&hero); heroes.append(&unit);
    num.append(&heroes);
    let beside = gtk::Box::new(gtk::Orientation::Vertical, 4);
    beside.set_valign(gtk::Align::Center);
    let beside1 = label("measuring\u{2026}", "beside");
    let beside2 = label("", "beside-muted");
    beside.append(&beside1); beside.append(&beside2);
    num.append(&beside);
    left.append(&num);
    top.append(&left);
    top.append(&hspacer());
    let right = gtk::Box::new(gtk::Orientation::Vertical, 6);
    let dl = label("Disk /", "display-label"); dl.set_xalign(1.0);
    let disk_text = label("", "disk"); disk_text.set_xalign(1.0);
    let freed_text = label("", "legend"); freed_text.set_xalign(1.0);
    right.append(&dl); right.append(&disk_text); right.append(&freed_text);
    top.append(&right);
    display.append(&top);
    let bar = gtk::DrawingArea::new();
    bar.set_content_height(27); bar.set_margin_top(4);
    display.append(&bar);
    let legend = gtk::Box::new(gtk::Orientation::Horizontal, 26);
    legend.set_margin_top(10);
    let legend_ticked = label("", "legend-ink");
    let legend_not = label("", "legend-ink");
    legend.append(&legend_item(0, &legend_ticked)); legend.append(&legend_item(1, &legend_not));
    legend.append(&legend_item(2, &label("Used", "legend"))); legend.append(&legend_item(3, &label("Free", "legend")));
    display.append(&legend);
    root.append(&display);

    // the keys: the Storage map alone at the left, the cleaning keys together at the right
    let keys = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    keys.set_margin_start(18); keys.set_margin_end(18); keys.set_margin_top(12); keys.set_margin_bottom(12);
    let map = gtk::Button::new();
    map.add_css_class("key"); map.add_css_class("accent"); map.set_valign(gtk::Align::Center);
    let mc = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    mc.append(&map_icon()); mc.append(&gtk::Label::new(Some("STORAGE MAP")));
    map.set_child(Some(&mc));
    keys.append(&map);
    keys.append(&hspacer());
    let scan_key = key("scan again", &[]);
    let protect = key("protected folders", &[]);
    let reset = key("reset to default", &[]);
    reset.set_margin_end(4);
    let clean = key("clean", &["primary"]);
    keys.append(&scan_key); keys.append(&protect); keys.append(&reset); keys.append(&clean);
    root.append(&keys);

    // the three sections
    let tile_box = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    tile_box.set_homogeneous(true);
    tile_box.set_margin_start(18); tile_box.set_margin_end(18);
    let mut tiles = Vec::new();
    for lvl in ORDER {
        let b = gtk::Button::new();
        b.add_css_class("tile");
        let v = gtk::Box::new(gtk::Orientation::Vertical, 4);
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        line.append(&label(engine::tier_name(lvl), "tile-name"));
        line.append(&hspacer());
        let size = label("", "tile-size");
        line.append(&size);
        v.append(&line);
        v.append(&label(engine::tier_desc(lvl), "tile-desc"));
        let count = label("", "tile-count");
        let cl = gtk::Box::new(gtk::Orientation::Horizontal, 7);
        let mark = gtk::DrawingArea::new();
        mark.set_content_width(11); mark.set_content_height(11); mark.set_valign(gtk::Align::Center);
        let cc = count.clone();
        mark.set_draw_func(move |_, cr, w, h| {   // filled orange with a tick when anything is ticked, else an outline
            let (w, h) = (w as f64, h as f64);
            if cc.has_css_class("some") {
                cr.set_source_rgb(1.0, 0.357, 0.122); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill();
                cr.set_source_rgb(0.106, 0.106, 0.102); cr.set_line_width(1.6);
                cr.move_to(2.5, 5.5); cr.line_to(4.8, 8.0); cr.line_to(8.8, 3.0); let _ = cr.stroke();
            } else if cc.has_css_class("none") {
                cr.set_source_rgb(0.49, 0.475, 0.447); cr.set_line_width(1.3); cr.rectangle(0.5, 0.5, w - 1.0, h - 1.0); let _ = cr.stroke();
            }
        });
        cl.append(&mark); cl.append(&count);
        v.append(&cl);
        b.set_child(Some(&v));
        tile_box.append(&b);
        tiles.push((b, size, count));
    }
    root.append(&tile_box);

    // the list of the chosen section, and the item panel that slides in at the right
    let body = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    body.set_vexpand(true);
    let listside = gtk::Box::new(gtk::Orientation::Vertical, 4);
    listside.set_hexpand(true);
    listside.set_margin_start(22); listside.set_margin_end(14); listside.set_margin_top(18);
    let head = gtk::Box::new(gtk::Orientation::Horizontal, 14);
    let title = label("", "section-title");
    head.append(&title);
    head.append(&hspacer());
    let tick_all = gtk::Button::with_label("Tick all"); tick_all.add_css_class("link-btn");
    let untick_all = gtk::Button::with_label("Untick all"); untick_all.add_css_class("link-btn");
    head.append(&tick_all); head.append(&untick_all);
    listside.append(&head);
    let explain = label("", "explain");
    explain.set_wrap(true); explain.set_max_width_chars(110);
    listside.append(&explain);
    let rows = gtk::Box::new(gtk::Orientation::Vertical, 0);
    rows.set_margin_top(10);
    let sw = gtk::ScrolledWindow::builder().child(&rows).vexpand(true).hscrollbar_policy(gtk::PolicyType::Never).build();
    listside.append(&sw);
    body.append(&listside);
    let panel = gtk::Box::new(gtk::Orientation::Vertical, 6);
    panel.set_margin_start(26); panel.set_margin_end(22); panel.set_margin_top(20); panel.set_margin_bottom(20);
    let psw = gtk::ScrolledWindow::builder().child(&panel).hscrollbar_policy(gtk::PolicyType::Never).build();
    psw.add_css_class("panel");
    psw.set_size_request(440, -1);
    let panel_rev = gtk::Revealer::builder().transition_type(gtk::RevealerTransitionType::SlideLeft).transition_duration(260).child(&psw).build();
    body.append(&panel_rev);
    root.append(&body);

    let status = label("", "status");
    root.append(&status);
    win.set_child(Some(&root));

    let ui = Rc::new(Ui {
        st: RefCell::new(St { cats: Vec::new(), tier: Level::Safe, open: None, pending: 0, cleaning: false, first_scan_done: false }),
        win, hero, unit, beside1, beside2, disk_text, freed_text, bar, bar_vals: Cell::new((0.0, 0.0, 0.0)),
        legend_ticked, legend_not, clean, scan_key, tiles, title, explain, rows, panel_rev, panel, status,
        prog_win: RefCell::new(None), opts,
    });

    {
        let u = ui.clone();
        ui.bar.set_draw_func(move |_, cr, w, h| {
            let w = w as f64;
            let (y, h) = (11.0, (h as f64 - 11.0).max(1.0));   // room for the caret above the bar
            let (other, sel, rev) = u.bar_vals.get();
            cr.set_source_rgb(0.227, 0.227, 0.220);
            cr.rectangle(0.0, y, w, h);
            let _ = cr.fill();
            let (mut w0, mut w1, mut w2) = (w * other, w * sel, w * rev);
            if sel > 0.0 && w1 < 4.0 { w1 = 4.0; w0 = (w0 - 4.0).max(0.0); }
            if rev > 0.0 && w2 < 4.0 { w2 = 4.0; w0 = (w0 - 4.0).max(0.0); }
            cr.set_source_rgb(0.435, 0.424, 0.400);
            cr.rectangle(0.0, y, w0, h);
            let _ = cr.fill();
            cr.set_source_rgb(1.0, 0.357, 0.122);
            cr.rectangle(w0, y, w1, h);
            let _ = cr.fill();
            hatch(cr, w0 + w1, y, w2, h);
            if sel > 0.0 {   // a caret marks the part you get back, even when it's thin
                let cx = w0 + w1 / 2.0;
                cr.set_source_rgb(1.0, 0.357, 0.122);
                cr.move_to(cx - 6.0, 0.0); cr.line_to(cx + 6.0, 0.0); cr.line_to(cx, 8.0); cr.close_path();
                let _ = cr.fill();
            }
        });
    }
    for (i, (b, _, _)) in ui.tiles.iter().enumerate() {
        let u = ui.clone();
        b.connect_clicked(move |_| {
            {
                let mut st = u.st.borrow_mut();
                st.tier = ORDER[i];
                let open_level = st.open.as_ref().and_then(|id| st.cats.iter().find(|c| &c.id == id).map(|c| c.level));
                if open_level.is_some() && open_level != Some(ORDER[i]) { st.open = None; }
            }
            if u.st.borrow().open.is_none() { u.panel_rev.set_reveal_child(false); }
            refresh(&u);
        });
    }
    {
        let u = ui.clone();
        tick_all.connect_clicked(move |_| set_all(&u, true));
        let u = ui.clone();
        untick_all.connect_clicked(move |_| set_all(&u, false));
        let u = ui.clone();
        ui.scan_key.connect_clicked(move |_| scan(&u, None));
        let u = ui.clone();
        reset.connect_clicked(move |_| {
            {
                let mut st = u.st.borrow_mut();
                for c in st.cats.iter_mut() {
                    c.checked = c.is_safe() && c.refused().is_none();
                    c.age_minutes = c.default_age;
                    c.last_age = if c.default_age > 0 { c.default_age } else { 14 * engine::DAY };
                }
                engine::settings::save(&st.cats);
            }
            u.status.set_text("Back to default: Quick clean items ticked, default ages. Protected folders stay protected.");
            scan(&u, None);
        });
        let u = ui.clone();
        map.connect_clicked(move |_| open_storage(&u, None, 0));
        let u = ui.clone();
        protect.connect_clicked(move |_| { let u2 = u.clone(); crate::storage::open_protect(&u.win, Rc::new(move || reload(&u2))); });
        let u = ui.clone();
        ui.clean.connect_clicked(move |_| {
            if u.st.borrow().cleaning {
                if let Some(w) = u.prog_win.borrow().as_ref() { w.present(); }
                return;
            }
            confirm(&u);
        });
    }

    {
        let mut cats = engine::build();
        engine::settings::load(&mut cats);
        ui.st.borrow_mut().cats = cats;
    }
    refresh(&ui);
    ui.win.present();
    if let Some(p) = ui.opts.storage.clone() { let d = ui.opts.drill; open_storage(&ui, Some(PathBuf::from(p)), d); }
    if ui.opts.protect { let u2 = ui.clone(); crate::storage::open_protect(&ui.win, Rc::new(move || reload(&u2))); }
    if let Some(dir) = ui.opts.progress_dir.clone() {
        let c = test_cat(PathBuf::from(dir));
        start_clean(&ui, vec![c], true);
    } else {
        scan(&ui, None);
    }
    ui
}

// The Storage map; when it closes after you've added or removed something, the list is built and measured again.
fn open_storage(u: &Rc<Ui>, start: Option<PathBuf>, drill: usize) {
    let cats = u.st.borrow().cats.clone();
    let u2 = u.clone();
    crate::storage::open(&u.win, cats, Rc::new(move || { reload(&u2); u2.status.set_text("Cleanup list updated. What you added is in Personal & work items, unticked."); }), start, drill);
}

// Build the list again (after the cleanup list or protected folders changed) and measure it.
fn reload(u: &Rc<Ui>) {
    {
        let mut st = u.st.borrow_mut();
        let mut cats = engine::build();
        engine::settings::load(&mut cats);
        st.cats = cats;
        st.open = None;
    }
    u.panel_rev.set_reveal_child(false);
    scan(u, None);
}

fn test_cat(dir: PathBuf) -> Cat {
    Cat {
        id: "test".into(), name: "Test folder".into(), level: Level::Safe, kind: Kind::Contents, admin: false,
        roots: vec![dir], exclude: Default::default(), extensions: None, top_level_only: false, is_custom: false,
        default_age: 0, age_minutes: 0, last_age: engine::DAY, checked: true, files: 0, bytes: 0, scanned: true, blocked: false,
        short: String::new(), what: String::new(), deletes: String::new(), safe_text: String::new(), after: String::new(),
    }
}

fn set_all(u: &Rc<Ui>, on: bool) {
    {
        let mut st = u.st.borrow_mut();
        let tier = st.tier;
        for c in st.cats.iter_mut().filter(|c| c.level == tier) {
            if !is_empty(c) && c.refused().is_none() { c.checked = on; }
        }
        engine::settings::save(&st.cats);
    }
    refresh(u);
}

// Everything on screen from the current state.
fn refresh(u: &Rc<Ui>) { refresh_top(u); rebuild_rows(u); }

// The display, keys and tiles (not the list, so a tick box isn't replaced while it's being clicked).
fn refresh_top(u: &Rc<Ui>) {
    let st = u.st.borrow();
    let selected: u64 = st.cats.iter().filter(|c| c.checked).map(|c| c.bytes).sum();
    let not_ticked: u64 = st.cats.iter().filter(|c| !c.checked && !c.is_safe() && c.kind != Kind::Trash).map(|c| c.bytes).sum();
    let only_safe = !st.cats.iter().any(|c| c.checked && !c.is_safe() && (c.bytes > 0 || c.files > 0));
    let (num, unit) = split(selected);
    u.hero.set_text(&num);
    u.unit.set_text(&unit);
    let freed = engine::history::total();
    if st.pending > 0 { u.beside1.set_text("measuring\u{2026}"); }
    else if only_safe { u.beside1.set_text(if freed.1 == 0 { "all safe to clean  \u{00B7}  one click on Clean" } else { "all safe to clean" }); }
    else { u.beside1.set_text("includes items to check first"); }
    u.beside2.set_text(&if not_ticked > 0 { format!("+ {} more you can check yourself", fmt(not_ticked)) } else { String::new() });
    let (free, total) = crate::disk("/");
    u.disk_text.set_text(&format!("{} free of {}", fmt(free), fmt(total)));
    let binned = engine::history::trashed();
    u.freed_text.set_text(&if freed.1 > 0 { format!("Freed so far: {}{} in {} clean{}", fmt(freed.0), if binned > 0 { format!(" + {} to the Trash", fmt(binned)) } else { String::new() }, freed.1, if freed.1 == 1 { "" } else { "s" }) } else { String::new() });
    if total > 0 {
        let t = total as f64;
        let used = (total - free) as f64 / t;
        let sel = (selected as f64 / t).min(used);
        let rev = (not_ticked as f64 / t).min(used - sel);
        u.bar_vals.set(((used - sel - rev).max(0.0), sel, rev));
        u.bar.queue_draw();
    }
    u.legend_ticked.set_text(&format!("Ticked: {}", fmt(selected)));
    u.legend_not.set_text(&format!("Not ticked: {}", fmt(not_ticked)));
    let can_clean = st.pending == 0 && st.cats.iter().any(|c| c.checked && (c.bytes > 0 || c.files > 0));
    u.clean.set_label(&if st.cleaning { "SHOW CLEANING".to_string() } else { format!("CLEAN {}", fmt(selected)) });
    u.clean.set_sensitive(st.cleaning || can_clean);
    u.scan_key.set_sensitive(st.pending == 0 && !st.cleaning);
    for (i, (b, size, count)) in u.tiles.iter().enumerate() {
        let lvl = ORDER[i];
        let items: Vec<&Cat> = st.cats.iter().filter(|c| c.level == lvl).collect();
        let found: u64 = items.iter().map(|c| c.bytes).sum();
        let ticked: u64 = items.iter().filter(|c| c.checked).map(|c| c.bytes).sum();
        let n_items = items.iter().filter(|c| c.bytes > 0 || c.files > 0).count();
        let n_ticked = items.iter().filter(|c| c.checked && (c.bytes > 0 || c.files > 0)).count();
        size.set_text(&format!("{} found", fmt(found)));
        count.set_text(&if n_items == 0 { "Nothing to clean here right now".to_string() }
                        else if n_ticked == 0 { format!("None of {} ticked", n_items) }
                        else { format!("{} of {} ticked{}", n_ticked, n_items, if ticked > 0 { format!("  \u{00B7}  {}", fmt(ticked)) } else { String::new() }) });
        count.remove_css_class("some"); count.remove_css_class("none");
        if n_items > 0 { count.add_css_class(if n_ticked > 0 { "some" } else { "none" }); }
        if let Some(m) = count.prev_sibling() { m.set_visible(n_items > 0); m.queue_draw(); }
        if st.tier == lvl { b.add_css_class("on"); } else { b.remove_css_class("on"); }
    }
    u.title.set_text(&engine::tier_name(st.tier).to_uppercase());
    u.explain.set_text(engine::tier_explain(st.tier));
}

fn rebuild_rows(u: &Rc<Ui>) {
    clear(&u.rows);
    let st = u.st.borrow();
    let mut full: Vec<&Cat> = st.cats.iter().filter(|c| c.level == st.tier && !is_empty(c)).collect();
    full.sort_by(|a, b| b.bytes.cmp(&a.bytes));
    let empty: Vec<&Cat> = st.cats.iter().filter(|c| c.level == st.tier && is_empty(c)).collect();
    for c in full { u.rows.append(&row(u, c, false, st.open.as_deref() == Some(&c.id))); }
    if !empty.is_empty() {
        let n = label(&format!("Nothing to clean right now ({})", empty.len()), "group-note");
        n.set_margin_top(14); n.set_margin_bottom(4);
        u.rows.append(&n);
        for c in empty { u.rows.append(&row(u, c, true, st.open.as_deref() == Some(&c.id))); }
    }
}

// One item: a tick box, its name and what happens, its size; the rest of the row opens its panel.
fn row(u: &Rc<Ui>, c: &Cat, empty: bool, open: bool) -> gtk::Box {
    let h = gtk::Box::new(gtk::Orientation::Horizontal, 12);
    h.add_css_class("rowline");
    let chk = gtk::CheckButton::new();
    chk.set_active(c.checked && !empty);   // nothing to clean: an empty grey box, as on Windows and Mac
    chk.set_sensitive(!empty && c.refused().is_none());
    chk.set_valign(gtk::Align::Center);
    chk.set_margin_start(4);
    let id = c.id.clone();
    let uu = u.clone();
    chk.connect_toggled(move |b| {
        {
            let mut st = uu.st.borrow_mut();
            if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) { c.checked = b.is_active(); }
            engine::settings::save(&st.cats);
        }
        refresh_top(&uu);
    });
    h.append(&chk);
    let btn = gtk::Button::new();
    btn.add_css_class("row-btn");
    if open { btn.add_css_class("open"); }
    btn.set_hexpand(true);
    let inner = gtk::Box::new(gtk::Orientation::Horizontal, 12);
    let names = gtk::Box::new(gtk::Orientation::Vertical, 3);
    names.set_hexpand(true);
    names.set_valign(gtk::Align::Center);
    let name = label(&c.name, if empty { "row-name-dim" } else { "row-name" });
    name.set_ellipsize(gtk::pango::EllipsizeMode::End);
    names.append(&name);
    if !empty {
        let age = match c.kind {
            Kind::Contents | Kind::Subfolders => if c.age_minutes > 0 { format!("  \u{00B7}  older than {}", engine::fmt_age(c.age_minutes)) } else { "  \u{00B7}  all files, any age".to_string() },
            _ => String::new(),
        };
        let sub = match c.refused() { Some(why) => format!("Not cleaned: {}. Remove it from the list", why), None => format!("{}{}", c.short, age) };
        let s = label(&sub, "row-sub");
        s.set_ellipsize(gtk::pango::EllipsizeMode::End);
        names.append(&s);
    }
    inner.append(&names);
    let sizes = gtk::Box::new(gtk::Orientation::Vertical, 3);
    let size = label(&if c.scanned { fmt(c.bytes) } else { "\u{2026}".to_string() }, "row-size");
    size.set_xalign(1.0);
    sizes.append(&size);
    if !empty {
        let f = label(&format!("{} file{}", c.files, if c.files == 1 { "" } else { "s" }), "row-sub");
        f.set_xalign(1.0);
        sizes.append(&f);
    }
    sizes.set_valign(gtk::Align::Center);
    inner.append(&sizes);
    inner.append(&label("\u{203A}", if open { "chevron-open" } else { "chevron" }));
    btn.set_child(Some(&inner));
    let id = c.id.clone();
    let uu = u.clone();
    btn.connect_clicked(move |_| {
        let same = uu.st.borrow().open.as_deref() == Some(&id);
        if same { close_panel(&uu); } else { open_panel(&uu, &id); }
    });
    h.append(&btn);
    h
}

fn open_panel(u: &Rc<Ui>, id: &str) {
    u.st.borrow_mut().open = Some(id.to_string());
    fill_panel(u);
    u.panel_rev.set_reveal_child(true);
    rebuild_rows(u);
}

fn close_panel(u: &Rc<Ui>) {
    u.st.borrow_mut().open = None;
    u.panel_rev.set_reveal_child(false);
    rebuild_rows(u);
}

fn head(t: &str) -> gtk::Label { let l = label(t, "panel-head"); l.set_margin_top(10); l }
fn body_text(t: &str) -> gtk::Label { let l = label(t, "panel-body"); l.set_wrap(true); l.set_max_width_chars(46); l }

fn set_age(u: &Rc<Ui>, id: &str, minutes: u32) {
    {
        let mut st = u.st.borrow_mut();
        if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) { c.age_minutes = minutes; if minutes > 0 { c.last_age = minutes; } }
        engine::settings::save(&st.cats);
    }
    fill_panel(u);
    scan(u, Some(vec![id.to_string()]));
}

// The item panel: what it is, what goes, whether it's safe, the age rule and its folders.
fn fill_panel(u: &Rc<Ui>) {
    clear(&u.panel);
    let c = {
        let st = u.st.borrow();
        match st.open.as_ref().and_then(|id| st.cats.iter().find(|c| &c.id == id)) { Some(c) => c.clone(), None => return }
    };
    let top = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    top.append(&label(engine::tier_name(c.level), "panel-tag"));
    top.append(&hspacer());
    let close = key("close", &[]);
    let uu = u.clone();
    close.connect_clicked(move |_| close_panel(&uu));
    top.append(&close);
    u.panel.append(&top);
    let t = label(&c.name, "panel-title"); t.set_wrap(true);
    u.panel.append(&t);
    let stats = match c.refused() {
        Some(why) => format!("Space Hunter won't clean this: {}. Use \u{201C}Remove from cleanup list\u{201D} below.", why),
        None => if c.scanned { format!("{}   \u{00B7}   {} file{}", fmt(c.bytes), c.files, if c.files == 1 { "" } else { "s" }) } else { "Measuring\u{2026}".to_string() },
    };
    let s = label(&stats, "panel-stats"); s.set_wrap(true); s.set_max_width_chars(46);
    u.panel.append(&s);

    if matches!(c.kind, Kind::Contents | Kind::Subfolders) && c.refused().is_none() {
        u.panel.append(&head("What to clean"));
        let older = c.age_minutes > 0;
        let mode = gtk::Box::new(gtk::Orientation::Horizontal, 4);
        let ob = key("only older than", on(older));
        let ab = key("everything", on(!older));
        let (uu, id, last) = (u.clone(), c.id.clone(), c.last_age);
        ob.connect_clicked(move |_| { if !older { set_age(&uu, &id, last.max(1)); } });
        let (uu, id) = (u.clone(), c.id.clone());
        ab.connect_clicked(move |_| { if older { set_age(&uu, &id, 0); } });
        mode.append(&ob); mode.append(&ab);
        u.panel.append(&mode);
        let m = if older { c.age_minutes } else { c.last_age };
        let (unit, value) = if m % engine::DAY == 0 { (engine::DAY, m / engine::DAY) } else if m % engine::HOUR == 0 { (engine::HOUR, m / engine::HOUR) } else { (1, m) };
        let ages = gtk::Box::new(gtk::Orientation::Horizontal, 4);
        let minus = key("\u{2212}", &[]);
        let val = label(&value.to_string(), "age-value"); val.set_xalign(0.5); val.set_width_chars(4);
        let plus = key("+", &[]);
        ages.append(&minus); ages.append(&val); ages.append(&plus);
        let gap = gtk::Box::new(gtk::Orientation::Horizontal, 0); gap.set_size_request(8, -1); ages.append(&gap);
        for (txt, u_m) in [("min", 1u32), ("hours", engine::HOUR), ("days", engine::DAY)] {
            let k = key(txt, on(older && unit == u_m));
            let (uu, id) = (u.clone(), c.id.clone());
            k.connect_clicked(move |_| set_age(&uu, &id, value.max(1) * u_m));
            k.set_sensitive(older);
            ages.append(&k);
        }
        let (uu, id) = (u.clone(), c.id.clone());
        minus.connect_clicked(move |_| { if value > 1 { set_age(&uu, &id, (value - 1) * unit); } });
        let (uu, id) = (u.clone(), c.id.clone());
        plus.connect_clicked(move |_| { if value < 999 { set_age(&uu, &id, (value + 1) * unit); } });
        minus.set_sensitive(older); plus.set_sensitive(older);
        u.panel.append(&ages);
        let hint = if older { format!("Files untouched for {} or more.", engine::fmt_age(c.age_minutes)) } else { "Every file, whatever its age.".to_string() };
        u.panel.append(&body_text(&format!("{} Delete or Trash is chosen when you press Clean.", hint)));
    }
    for (h, b) in [("What is it?", &c.what), ("What gets cleaned", &c.deletes), ("Is it safe?", &c.safe_text), ("What happens after", &c.after)] {
        if b.is_empty() { continue; }
        u.panel.append(&head(h));
        u.panel.append(&body_text(b));
    }
    // the item's folders, each with a keep switch (kept folders are protected)
    let roots: Vec<PathBuf> = c.roots.iter().filter(|r| r.is_dir()).cloned().collect();
    if !roots.is_empty() && !c.admin && c.kind != Kind::Journal {
        u.panel.append(&head("Folders  \u{00B7}  keep one to never clean it"));
        let kept = engine::keep::all();
        for r in roots.iter().take(14) {
            let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
            let is_kept = engine::keep::is_kept(r, &kept);
            let cls: &[&str] = if is_kept { &["on", "small"] } else { &["small"] };
            let k = key(if is_kept { "kept" } else { "keep" }, cls);
            let (uu, id, p) = (u.clone(), c.id.clone(), r.to_string_lossy().to_string());
            k.connect_clicked(move |_| {
                if engine::keep::is_kept(std::path::Path::new(&p), &engine::keep::all()) { engine::keep::remove(&p); } else { engine::keep::add(&p); }
                fill_panel(&uu);
                scan(&uu, Some(vec![id.clone()]));
            });
            line.append(&k);
            let pl = label(&home_short(r), "panel-path");
            pl.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
            line.append(&pl);
            u.panel.append(&line);
        }
    }
    if c.is_custom {
        let rm = key("remove from cleanup list", &[]);
        rm.set_halign(gtk::Align::Start); rm.set_margin_top(12);
        let (uu, p, name) = (u.clone(), c.roots[0].to_string_lossy().to_string(), c.name.clone());
        rm.connect_clicked(move |_| {
            engine::custom::remove(&p);
            {
                let mut st = uu.st.borrow_mut();
                st.open = None;
                let mut cats = engine::build();
                engine::settings::load(&mut cats);
                st.cats = cats;
            }
            uu.panel_rev.set_reveal_child(false);
            uu.status.set_text(&format!("Removed from the cleanup list: {}", name));
            scan(&uu, None);
        });
        u.panel.append(&rm);
    }
}

// Measure items in the background, a few at once, and fill the numbers in as they arrive.
fn scan(u: &Rc<Ui>, ids: Option<Vec<String>>) {
    let targets: Vec<Cat> = {
        let mut st = u.st.borrow_mut();
        if st.cleaning { return; }
        let mut t: Vec<Cat> = Vec::new();
        for c in st.cats.iter_mut() {
            if ids.as_ref().map(|v| v.contains(&c.id)).unwrap_or(true) { c.scanned = false; t.push(c.clone()); }
        }
        t.sort_by_key(|c| c.level);
        st.pending += t.len();
        t
    };
    refresh(u);
    if targets.is_empty() { return; }
    let n = targets.len();
    let queue = Arc::new(Mutex::new(VecDeque::from(targets)));
    let (tx, rx) = mpsc::channel::<(String, String, u64, u64)>();
    for _ in 0..n.min(4) {
        let (q, tx) = (queue.clone(), tx.clone());
        std::thread::spawn(move || loop {
            let c = match q.lock().ok().and_then(|mut g| g.pop_front()) { Some(c) => c, None => break };
            let mut cnt = Counter::default();
            engine::process(&c, false, false, &mut cnt);
            if tx.send((c.id.clone(), c.name.clone(), cnt.files, cnt.bytes)).is_err() { break; }
        });
    }
    let got = Rc::new(Cell::new(0usize));
    let uu = u.clone();
    glib::timeout_add_local(Duration::from_millis(120), move || {
        let mut any = false;
        let mut last = String::new();
        while let Ok((id, name, files, bytes)) = rx.try_recv() {
            any = true; got.set(got.get() + 1); last = name;
            let mut st = uu.st.borrow_mut();
            if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) { c.files = files; c.bytes = bytes; c.scanned = true; }
            st.pending = st.pending.saturating_sub(1);
        }
        if any {
            let pending = uu.st.borrow().pending;
            let freed = engine::history::total();
            uu.status.set_text(&if pending > 0 { format!("Measuring {}\u{2026}", last) }
                               else if freed.1 == 0 { "Quick clean is ticked for you, so one click on Clean is enough. You see the list before anything goes.".to_string() }
                               else { "Scan done.  Click a row to see what it is.  Tick the box to include it.".to_string() });
            refresh(&uu);
            if uu.st.borrow().open.is_some() { fill_panel(&uu); }
        }
        if got.get() >= n {
            let first = { let mut st = uu.st.borrow_mut(); let f = !st.first_scan_done && st.pending == 0; if f { st.first_scan_done = true; } f };
            if first { after_first_scan(&uu); }
            return glib::ControlFlow::Break;
        }
        glib::ControlFlow::Continue
    });
}

// test hooks for screenshots: open a panel or the confirm window once everything is measured
fn after_first_scan(u: &Rc<Ui>) {
    if let Some(id) = u.opts.open.clone() {
        let lvl = u.st.borrow().cats.iter().find(|c| c.id == id).map(|c| c.level);
        if let Some(l) = lvl { u.st.borrow_mut().tier = l; open_panel(u, &id); refresh(u); }
    }
    if u.opts.confirm { confirm(u); }
}

// Shown every time Clean is pressed: what will go, grouped by risk, and how (Trash or permanently).
fn confirm(u: &Rc<Ui>) {
    let sel: Vec<Cat> = u.st.borrow().cats.iter().filter(|c| c.checked && c.refused().is_none() && (c.bytes > 0 || c.files > 0)).cloned().collect();
    if sel.is_empty() { return; }
    let total: u64 = sel.iter().map(|c| c.bytes).sum();
    let w = gtk::Window::builder().transient_for(&u.win).modal(true).title("Space Hunter").default_width(560).resizable(false).build();
    let v = gtk::Box::new(gtk::Orientation::Vertical, 4);
    v.add_css_class("chassis");
    v.set_margin_start(26); v.set_margin_end(26); v.set_margin_top(22); v.set_margin_bottom(22);
    v.append(&label(&format!("Clean {}?", fmt(total)), "confirm-title"));
    for lvl in ORDER {
        let mut items: Vec<&Cat> = sel.iter().filter(|c| c.level == lvl).collect();
        if items.is_empty() { continue; }
        items.sort_by(|a, b| b.bytes.cmp(&a.bytes));
        let h = label(&engine::tier_name(lvl).to_uppercase(), "confirm-head"); h.set_margin_top(10);
        v.append(&h);
        for c in items.iter().take(4) { v.append(&confirm_row(&c.name, &fmt(c.bytes))); }
        if items.len() > 4 {
            let rest: u64 = items.iter().skip(4).map(|c| c.bytes).sum();
            v.append(&confirm_row(&format!("+ {} more", items.len() - 4), &fmt(rest)));
        }
    }
    let to_trash = Rc::new(Cell::new(sel.iter().any(|c| !c.is_safe())));   // the safe choice first for anything beyond Quick clean
    let how = label("How to clean", "confirm-how"); how.set_margin_top(14);
    v.append(&how);
    let toggles = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let del = key("permanently delete", &[]);
    let bin = key("trash", &[]);
    toggles.append(&del); toggles.append(&bin);
    v.append(&toggles);
    let hint = body_text(""); hint.set_max_width_chars(64); hint.set_margin_top(6);
    v.append(&hint);
    if sel.iter().any(|c| c.admin) {
        let a = body_text("System folders (such as downloaded updates) need your password once; Linux asks in its own window. They are deleted, not moved to the Trash.");
        a.set_max_width_chars(64);
        v.append(&a);
    }
    let bottom = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    bottom.set_margin_top(16);
    bottom.append(&hspacer());
    let cancel = key("cancel", &[]);
    let ok = key("clean", &["primary"]);
    bottom.append(&cancel); bottom.append(&ok);
    v.append(&bottom);
    w.set_child(Some(&v));
    let show: Rc<dyn Fn()> = {
        let (del, bin, hint, ok, t) = (del.clone(), bin.clone(), hint.clone(), ok.clone(), to_trash.clone());
        Rc::new(move || {
            let trash = t.get();
            if trash { bin.add_css_class("on"); del.remove_css_class("on"); } else { del.add_css_class("on"); bin.remove_css_class("on"); }
            hint.set_text(if trash { "Everything goes to the Trash, so you can restore it. The space comes back only when you empty the Trash. If the Trash item is ticked, the old Trash is emptied first." }
                          else { "Everything is deleted permanently and can't be restored. The space comes back straight away." });
            ok.set_label(&if trash { format!("MOVE {} TO TRASH", fmt(total)) } else { format!("PERMANENTLY DELETE {}", fmt(total)) });
        })
    };
    (*show)();
    let (t, s) = (to_trash.clone(), show.clone());
    del.connect_clicked(move |_| { t.set(false); (*s)(); });
    let (t, s) = (to_trash.clone(), show.clone());
    bin.connect_clicked(move |_| { t.set(true); (*s)(); });
    let ww = w.clone();
    cancel.connect_clicked(move |_| ww.close());
    let (ww, uu, t) = (w.clone(), u.clone(), to_trash.clone());
    ok.connect_clicked(move |_| {
        ww.close();
        // the Trash item always runs first, so files moved to the Trash in this clean are never emptied by it
        let mut s2 = sel.clone();
        s2.sort_by_key(|c| if c.kind == Kind::Trash { 0 } else { 1 });
        start_clean(&uu, s2, t.get());
    });
    w.present();
}

fn confirm_row(name: &str, size: &str) -> gtk::Box {
    let r = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    r.add_css_class("rowline");
    let n = label(name, "row-name"); n.set_hexpand(true); n.set_ellipsize(gtk::pango::EllipsizeMode::End);
    n.set_margin_top(6); n.set_margin_bottom(6);
    r.append(&n);
    r.append(&label(size, "row-name"));
    r
}

enum Msg { Line(String), Totals(u64, u64, u64, u64), Current(String), Done(bool) }

// The cleaning window: it doesn't block the main window, and it can be minimised while it works.
fn start_clean(u: &Rc<Ui>, sel: Vec<Cat>, to_trash: bool) {
    u.st.borrow_mut().cleaning = true;
    refresh_top(u);
    let expected: u64 = sel.iter().map(|c| c.bytes).sum::<u64>().max(1);
    let w = gtk::Window::builder().transient_for(&u.win).title("Space Hunter \u{00B7} cleaning").default_width(780).default_height(560).build();
    let v = gtk::Box::new(gtk::Orientation::Vertical, 10);
    v.add_css_class("chassis");
    v.set_margin_start(18); v.set_margin_end(18); v.set_margin_top(18); v.set_margin_bottom(18);
    let disp = gtk::Box::new(gtk::Orientation::Vertical, 6);
    disp.add_css_class("display");
    let headl = label("Cleaning", "display-label");
    disp.append(&headl);
    let hl = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    let big = label("0", "hero");
    let bigu = label("MB FREED", "hero-unit"); bigu.set_valign(gtk::Align::End); bigu.set_margin_bottom(12);
    hl.append(&big); hl.append(&bigu); hl.append(&hspacer());
    let stat = label("", "beside"); stat.set_valign(gtk::Align::Center);
    hl.append(&stat);
    disp.append(&hl);
    let pbar = gtk::DrawingArea::new(); pbar.set_content_height(12);
    let frac = Rc::new(Cell::new(0.0f64));
    let fin = Rc::new(Cell::new(-1.0f64));   // the finish, 0..1 (below 0: not finishing)
    let (f2, fin2) = (frac.clone(), fin.clone());
    pbar.set_content_height(28);
    pbar.set_draw_func(move |_, cr, w, _| {
        let (w, h) = (w as f64, 12.0);
        cr.set_source_rgb(0.227, 0.227, 0.220); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill();
        let t = fin2.get();
        if t >= 0.0 {
            // the full bar breaks into pieces that drop away left to right: the space is cleared
            let n = 28; let sw = w / n as f64;
            for k in 0..n {
                let local = ((t - 0.55 * k as f64 / n as f64) / 0.4).clamp(0.0, 1.0);
                if local >= 1.0 { continue; }
                let fall = local * local;
                cr.set_source_rgba(1.0, 0.357, 0.122, 1.0 - fall);
                cr.rectangle(k as f64 * sw + 1.0, fall * 16.0, sw - 2.0, h * (1.0 - fall * 0.6));
                let _ = cr.fill();
            }
        } else {
            cr.set_source_rgb(1.0, 0.357, 0.122); cr.rectangle(0.0, 0.0, w * f2.get(), h); let _ = cr.fill();
        }
    });
    let totals = Rc::new(Cell::new((0u64, 0u64, 0u64, 0u64)));
    disp.append(&pbar);
    v.append(&disp);
    let tv = gtk::TextView::new();
    tv.set_editable(false); tv.set_cursor_visible(false); tv.add_css_class("log");
    tv.set_left_margin(12); tv.set_top_margin(12);
    let lsw = gtk::ScrolledWindow::builder().child(&tv).vexpand(true).build();
    v.append(&lsw);
    let keys = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    keys.append(&hspacer());
    let minimise = key("minimise", &[]);
    let stop = key("stop", &[]);
    keys.append(&minimise); keys.append(&stop);
    v.append(&keys);
    w.set_child(Some(&v));
    *u.prog_win.borrow_mut() = Some(w.clone());

    let cancel = Arc::new(AtomicBool::new(false));
    let done = Rc::new(Cell::new(false));
    let ww = w.clone();
    minimise.connect_clicked(move |_| ww.minimize());
    let (c2, d2, uu) = (cancel.clone(), done.clone(), u.clone());
    stop.connect_clicked(move |b| {
        if d2.get() { if let Some(w) = uu.prog_win.borrow_mut().take() { w.close(); } return; }
        c2.store(true, Ordering::Relaxed); b.set_label("STOPPING"); b.set_sensitive(false);
    });
    let d3 = done.clone();
    w.connect_close_request(move |w| {
        if d3.get() { glib::Propagation::Proceed } else { w.minimize(); glib::Propagation::Stop }   // closing mid-clean just tucks it away
    });

    let (tx, rx) = mpsc::channel::<Msg>();
    let c3 = cancel.clone();
    std::thread::spawn(move || {
        let (mut bytes, mut trashed, mut files, mut skipped) = (0u64, 0u64, 0u64, 0u64);
        let user: Vec<&Cat> = sel.iter().filter(|c| !c.admin).collect();
        let admin: Vec<&Cat> = sel.iter().filter(|c| c.admin).collect();
        let n = sel.len();
        for (i, c) in user.iter().enumerate() {
            if c3.load(Ordering::Relaxed) { break; }
            let _ = tx.send(Msg::Current(format!("Cleaning  \u{00B7}  item {} of {}  \u{00B7}  {}", i + 1, n, c.name)));
            let t2 = tx.clone();
            let mut cnt = Counter { cancel: Some(c3.clone()), ..Default::default() };
            cnt.on_item = Some(Box::new(move |p: &std::path::Path, size: u64, ok: bool, bin: bool| {
                let head = if !ok { "skip".to_string() } else if bin { format!("trash {:>8}", fmt(size)) } else { format!("del  {:>8}", fmt(size)) };
                let _ = t2.send(Msg::Line(format!("{}  {}", head, home_short(p))));
            }));
            engine::process(c, true, to_trash, &mut cnt);
            bytes += cnt.bytes; trashed += cnt.trashed; files += cnt.files; skipped += cnt.skipped;
            let _ = tx.send(Msg::Totals(bytes, trashed, files, skipped));
            let _ = tx.send(Msg::Line(format!("\u{2014} {}: freed {}{}, {} files", c.name, fmt(cnt.bytes), if cnt.trashed > 0 { format!(" + {} to the Trash", fmt(cnt.trashed)) } else { String::new() }, cnt.files)));
        }
        if !admin.is_empty() && !c3.load(Ordering::Relaxed) {
            let _ = tx.send(Msg::Current("Waiting for your password to clean system folders\u{2026}".into()));
            match engine::run_system_helper(&admin) {
                Some(res) => for r in res {
                    bytes += r.bytes; files += r.files; skipped += r.skipped;
                    let _ = tx.send(Msg::Line(format!("\u{2014} {}: freed {}, {} files", r.id, fmt(r.bytes), r.files)));
                },
                None => { let _ = tx.send(Msg::Line("\u{2014} System folders skipped: the password wasn't given.".into())); }
            }
            let _ = tx.send(Msg::Totals(bytes, trashed, files, skipped));
        }
        engine::history::record(bytes, trashed);
        let _ = tx.send(Msg::Done(c3.load(Ordering::Relaxed)));
    });

    let buf = tv.buffer();
    let uu = u.clone();
    let mut lines = 0usize;
    glib::timeout_add_local(Duration::from_millis(120), move || {
        let mut finished = None;
        let mut batch = String::new();
        while let Ok(m) = rx.try_recv() {
            match m {
                Msg::Line(l) => { if lines < 3000 { batch.push_str(&l); batch.push('\n'); lines += 1; } }
                Msg::Totals(b, t, f, s) => {
                    totals.set((b, t, f, s));
                    let bin_only = b == 0 && t > 0;
                    let (num, unit) = split(if bin_only { t } else { b });
                    big.set_text(&num);
                    bigu.set_text(&format!("{} {}", unit, if bin_only { "TO TRASH" } else { "FREED" }));
                    stat.set_text(&format!("{} files  \u{00B7}  {} skipped{}", f, s, if t > 0 && !bin_only { format!("  \u{00B7}  + {} to the Trash", fmt(t)) } else { String::new() }));
                    frac.set(((b + t) as f64 / expected as f64).min(1.0));
                    pbar.queue_draw();
                    uu.status.set_text(&format!("Cleaning  \u{00B7}  {} freed{} so far.  You can keep looking around.", fmt(b), if t > 0 { format!(" + {} to the Trash", fmt(t)) } else { String::new() }));
                }
                Msg::Current(c) => headl.set_text(&c),
                Msg::Done(stopped) => finished = Some(stopped),
            }
        }
        if !batch.is_empty() {
            let mut end = buf.end_iter();
            buf.insert(&mut end, &batch);
            let mark = buf.create_mark(None, &buf.end_iter(), false);
            tv.scroll_mark_onscreen(&mark);
        }
        if let Some(stopped) = finished {
            done.set(true);
            headl.set_text(if stopped { "Stopped  \u{00B7}  nothing else will be cleaned" } else { "Done  \u{00B7}  all ticked items cleaned" });
            // Done closes the window; it's the one orange key once a clean has finished
            stop.set_label(if stopped { "CLOSE" } else { "DONE" }); stop.set_sensitive(true);
            if !stopped { stop.add_css_class("primary"); }
            let animate = gtk::Settings::default().map(|st| st.is_gtk_enable_animations()).unwrap_or(true);
            if !stopped && animate {   // a moment of "clearing up": the number counts up while the bar clears
                headl.set_text("Clearing up\u{2026}");
                let (b, t, f, s) = totals.get();
                let bin_only = b == 0 && t > 0;
                let start = std::time::Instant::now();
                let (big, bigu, stat, headl, fin, pbar) = (big.clone(), bigu.clone(), stat.clone(), headl.clone(), fin.clone(), pbar.clone());
                glib::timeout_add_local(Duration::from_millis(16), move || {
                    let x = (start.elapsed().as_secs_f64() / 1.2).min(1.0);
                    let ease = 1.0 - (1.0 - x).powi(3);
                    let (num, unit) = split(((if bin_only { t } else { b }) as f64 * ease) as u64);
                    big.set_text(&num);
                    bigu.set_text(&format!("{} {}", unit, if bin_only { "TO TRASH" } else { "FREED" }));
                    stat.set_text(&format!("{} files  \u{00B7}  {} skipped", (f as f64 * ease) as u64, s));
                    fin.set(x); pbar.queue_draw();
                    if x >= 1.0 { headl.set_text("Done  \u{00B7}  all ticked items cleaned"); return glib::ControlFlow::Break; }
                    glib::ControlFlow::Continue
                });
            } else if !stopped { fin.set(1.0); pbar.queue_draw(); }
            minimise.set_visible(false);
            uu.st.borrow_mut().cleaning = false;
            uu.status.set_text("Cleaning finished. The list is being measured again.");
            scan(&uu, None);
            return glib::ControlFlow::Break;
        }
        glib::ControlFlow::Continue
    });
    w.present();
}
