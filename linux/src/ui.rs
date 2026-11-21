// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - the main window: the display, the keys, the section rail, the list, the item panel,
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
    pub progress_dir: Option<String>,  // test hook: clean this folder (deleted) in the cleaning window
    pub storage: Option<String>,       // test hook: open the Storage map on this folder
    pub drill: usize,                  // test hook: open the biggest folder this many levels down
    pub protect: bool,                 // test hook: open Protected folders
    pub guide: Option<usize>,          // test hook: open the first-run guide at this step once measured
    pub quiet: bool,                   // a test run (--quit-after): the guide never starts by itself
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
    spin: gtk::DrawingArea,                   // one turning ring beside the number while the items are measured
    beside2: gtk::Label,
    disk_text: gtk::Label,
    freed_text: gtk::Label,
    bar: gtk::DrawingArea,
    bar_vals: Cell<(f64, f64, f64)>,   // used by everything else, ticked, not ticked (shares of the disk)
    legend_ticked: gtk::Label,
    legend_not: gtk::Label,
    clean: gtk::Button,
    map_key: gtk::Button,
    protect_key: gtk::Button,
    reset_key: gtk::Button,
    top_keys: gtk::DrawingArea,               // the guide key and the Light / Dark / System keys
    rail_box_hover: Cell<i32>,                // the column whose tick box (and its words) is under the pointer
    display: gtk::Box,
    overlay: gtk::Overlay,                    // over everything: the first-run guide
    rail: gtk::DrawingArea,                   // the three sections on one rail
    rail_hover: Cell<i32>,                    // the column under the pointer (-1: none)
    rail_boxes: Cell<[R4; 3]>,                // each column's tick-all box, as last drawn (width 0: none)
    mark: Cell<f64>,                          // the orange marker's place, in columns (0, 1, 2)
    sliding: Cell<bool>,
    slide_gen: Cell<u64>,
    title: gtk::Label,
    explain: gtk::DrawingArea,                // the section's explain line, balanced
    glyph_peers: Vec<gtk::Widget>,            // lined up with the big number's first glyph
    cta: gtk::Label,                          // the section's call to action, under the explain line
    list_sw: gtk::ScrolledWindow,
    guide: RefCell<Option<Rc<Guide>>>,
    guide_pending: Cell<bool>,                // start the guide when the window is next active
    rows: gtk::Box,
    panel_rev: gtk::Revealer,
    panel: gtk::Box,
    psw: gtk::ScrolledWindow,                 // the item panel's scroller
    panel_for: RefCell<Option<String>>,       // which item the panel was last filled for (the same one keeps its place)
    scroll_gen: Cell<u64>,                    // the newest "stay where you were"; older ones stop
    status: gtk::Label,
    prog_win: RefCell<Option<gtk::Window>>,
    opts: Opts,
    bottom: gtk::Box,
    update_on: Cell<bool>,                    // the update card is showing (it wins over the public-totals question)
    ask_card: RefCell<Option<gtk::Box>>,      // the public-totals question, when it's showing
    ask_shown: Cell<bool>,                    // the question came up by itself this launch
    guide_seen_at_start: bool,                // the first-run guide had been seen before this launch
    scan_gen: RefCell<std::collections::HashMap<String, u64>>,     // per item: which measurement is the newest
    dir_sizes: RefCell<std::collections::HashMap<PathBuf, u64>>,   // folder sizes for "What's inside", measured once
    dir_times: RefCell<std::collections::HashMap<PathBuf, (std::time::SystemTime, std::time::SystemTime, Option<std::time::SystemTime>)>>,   // newest (with the folder's own date), newest file, oldest file (none: no files)
    inside_here: RefCell<Option<(String, PathBuf)>>,                       // a folder you added: where its panel list is looking
    described: RefCell<std::collections::HashMap<String, String>>,       // what's inside folders you added, worked out once
    more: Cell<bool>,                                                    // the item panel shows its full notes
}

type R4 = (f64, f64, f64, f64);   // x, y, width, height

const ORANGE: u32 = 0xFF5B1F;
const ON_ORANGE: u32 = 0x1B1B1A;    // text and ticks on orange, in both themes
const TRACK: u32 = 0x3A3A38;        // the display's empty track
const USED: u32 = 0x6F6C66;         // the display's used space
const SC: f64 = gtk::pango::SCALE as f64;

fn ease(t: f64) -> f64 { let t = t.clamp(0.0, 1.0); 1.0 - (1.0 - t).powi(3) }

fn animations_on() -> bool { gtk::Settings::default().map(|s| s.is_gtk_enable_animations()).unwrap_or(true) }

fn rgba(cr: &gtk::cairo::Context, c: u32, a: f64) {
    cr.set_source_rgba(((c >> 16) & 255) as f64 / 255.0, ((c >> 8) & 255) as f64 / 255.0, (c & 255) as f64 / 255.0, a.clamp(0.0, 1.0));
}

fn round_rect(cr: &gtk::cairo::Context, x: f64, y: f64, w: f64, h: f64, r: f64) {
    use std::f64::consts::PI;
    let r = r.min(w / 2.0).min(h / 2.0).max(0.0);
    cr.new_sub_path();
    cr.arc(x + w - r, y + r, r, -PI / 2.0, 0.0);
    cr.arc(x + w - r, y + h - r, r, 0.0, PI / 2.0);
    cr.arc(x + r, y + h - r, r, PI / 2.0, PI);
    cr.arc(x + r, y + r, r, PI, 1.5 * PI);
    cr.close_path();
}

// text drawn by hand, in the app's own type ("Schibsted Grotesk Semi-Bold 10", "Big Shoulders Display Black 24", ...)
fn layout(w: &gtk::DrawingArea, font: &str) -> gtk::pango::Layout {
    let l = w.create_pango_layout(None);
    l.set_font_description(Some(&gtk::pango::FontDescription::from_string(font)));
    l
}

fn show_at(cr: &gtk::cairo::Context, l: &gtk::pango::Layout, text: &str, x: f64, y: f64) {
    l.set_text(text);
    cr.move_to(x, y);
    pangocairo::functions::show_layout(cr, l);
}

// Narrow a wrapped layout as far as it goes without adding a line, so its lines come out even (no orphaned word).
fn balance(l: &gtk::pango::Layout, max_w: f64) {
    let max_w = max_w.max(20.0);
    l.set_width((max_w * SC) as i32);
    let n = l.line_count();
    if n < 2 { return; }
    let (mut lo, mut hi) = (max_w * 0.45, max_w);
    for _ in 0..12 {
        let mid = (lo + hi) / 2.0;
        l.set_width((mid * SC) as i32);
        if l.line_count() > n { lo = mid; } else { hi = mid; }
    }
    l.set_width((hi * SC).ceil() as i32);
}

// The section's explain line: muted, wrapped at 820 px at most, balanced.
fn explain_layout(da: &gtk::DrawingArea, tier: Level, w: f64) -> gtk::pango::Layout {
    let l = layout(da, "Schibsted Grotesk 9");
    l.set_wrap(gtk::pango::WrapMode::WordChar);
    l.set_text(engine::tier_explain(tier));
    balance(&l, w.min(820.0));
    l
}

// its height follows the text at the width it has now
fn fit_explain(u: &Ui) {
    let w = u.explain.width();
    if w <= 0 { return; }
    let tier = u.st.borrow().tier;
    let h = explain_layout(&u.explain, tier, w as f64).pixel_size().1.max(1);
    if u.explain.content_height() != h { u.explain.set_content_height(h); }
    u.explain.queue_draw();
}

// Start the label above a big number (and the bar, the legend) where the number's first glyph really starts.
pub fn align_to_glyph(hero: &gtk::Label, peers: &[&gtk::Widget]) {
    let ix = hero.layout().pixel_extents().0.x().max(0);
    for w in peers { if w.margin_start() != ix { w.set_margin_start(ix); } }
}

fn inside(r: R4, x: f64, y: f64) -> bool { r.2 > 0.0 && x >= r.0 && x <= r.0 + r.2 && y >= r.1 && y <= r.1 + r.3 }

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
        crate::theme::rgb(cr, crate::theme::pal().ink);
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

// Top right: the guide key, then Light / Dark / System as three icon keys in one strip, the chosen one lit. Hovering a
// key names it beside them, at the left. All drawn by hand; the guide key's click is wired up in build().
const TK_C: f64 = 26.0;     // one key
const TK_GAP: f64 = 10.0;   // between the guide key and the strip
const TK_H: f64 = 26.0;

// 0..2 the theme keys, 3 the guide key
fn top_cells(w: f64) -> [R4; 4] {
    let x0 = w - TK_C * 3.0;
    [(x0, 1.0, TK_C, TK_H - 2.0), (x0 + TK_C, 1.0, TK_C, TK_H - 2.0), (x0 + 2.0 * TK_C, 1.0, TK_C, TK_H - 2.0), (x0 - TK_GAP - TK_C, 1.0, TK_C, TK_H - 2.0)]
}
fn top_hit(w: f64, x: f64, y: f64) -> i32 { top_cells(w).iter().position(|r| inside(*r, x, y)).map(|i| i as i32).unwrap_or(-1) }

fn top_icon(cr: &gtk::cairo::Context, da: &gtk::DrawingArea, i: usize, r: R4, ink: u32) {
    use std::f64::consts::PI;
    let (cx, cy) = (r.0 + r.2 / 2.0, r.1 + r.3 / 2.0);
    crate::theme::rgb(cr, ink);
    cr.set_line_width(1.6);
    cr.set_line_cap(gtk::cairo::LineCap::Round);
    cr.new_path();
    match i {
        0 => {   // sun
            cr.arc(cx, cy, 3.6, 0.0, 2.0 * PI);
            let _ = cr.fill();
            for k in 0..8 {
                let a = k as f64 * PI / 4.0;
                cr.move_to(cx + a.cos() * 6.0, cy + a.sin() * 6.0);
                cr.line_to(cx + a.cos() * 8.2, cy + a.sin() * 8.2);
            }
            let _ = cr.stroke();
        }
        1 => {   // crescent moon: a disc with a bite out of it
            let _ = cr.save();
            cr.set_fill_rule(gtk::cairo::FillRule::EvenOdd);
            cr.rectangle(r.0, r.1, r.2, r.3);
            cr.arc(cx + 3.8, cy - 3.2, 6.0, 0.0, 2.0 * PI);
            cr.clip();
            cr.set_fill_rule(gtk::cairo::FillRule::Winding);
            cr.new_path();
            cr.arc(cx, cy, 6.5, 0.0, 2.0 * PI);
            let _ = cr.fill();
            let _ = cr.restore();
        }
        2 => {   // system: half light, half dark
            cr.arc(cx, cy, 6.5, 0.0, 2.0 * PI);
            let _ = cr.stroke();
            cr.new_path();
            cr.move_to(cx, cy);
            cr.arc(cx, cy, 6.5, PI / 2.0, 1.5 * PI);
            cr.close_path();
            let _ = cr.fill();
        }
        _ => {   // the guide: a "?" in a circle
            cr.arc(cx, cy, 7.5, 0.0, 2.0 * PI);
            let _ = cr.stroke();
            let q = layout(da, "Schibsted Grotesk Bold 8.5");
            q.set_text("?");
            let (qw, qh) = q.pixel_size();
            cr.move_to(cx - qw as f64 / 2.0, cy - qh as f64 / 2.0);
            pangocairo::functions::show_layout(cr, &q);
        }
    }
    cr.set_line_cap(gtk::cairo::LineCap::Butt);
}

fn draw_top_keys(da: &gtk::DrawingArea, cr: &gtk::cairo::Context, w: f64, hover: i32) {
    use crate::theme::Mode;
    let pal = crate::theme::pal();
    let cells = top_cells(w);
    let modes = [Mode::Light, Mode::Dark, Mode::System];
    // the guide key: a small key of its own, with a keycap shadow
    let g = cells[3];
    round_rect(cr, g.0, g.1 + 2.0, g.2, g.3, 4.0);
    crate::theme::rgb(cr, pal.key_shadow);
    let _ = cr.fill();
    round_rect(cr, g.0, g.1, g.2, g.3, 4.0);
    crate::theme::rgb(cr, if hover == 3 { pal.key_hover } else { pal.key });
    let _ = cr.fill();
    top_icon(cr, da, 3, g, pal.ink);
    // the theme strip
    let (sx, sy, sw, sh) = (cells[0].0, 1.0, TK_C * 3.0, TK_H - 2.0);
    round_rect(cr, sx, sy + 2.0, sw, sh, 4.0);
    crate::theme::rgb(cr, pal.key_shadow);
    let _ = cr.fill();
    round_rect(cr, sx, sy, sw, sh, 4.0);
    crate::theme::rgb(cr, pal.key);
    let _ = cr.fill();
    let now = crate::theme::mode();
    for i in 0..3 {
        let r = cells[i];
        let on = now == modes[i];
        if on || hover == i as i32 {
            round_rect(cr, r.0 + 2.5, r.1 + 2.5, r.2 - 5.0, r.3 - 5.0, 3.0);
            crate::theme::rgb(cr, if on { pal.ink } else { pal.hover });
            let _ = cr.fill();
        }
        top_icon(cr, da, i, r, if on { pal.pane } else { pal.muted });
    }
    // the name of what's under the pointer, at the left of the keys
    if hover >= 0 {
        let names = ["Light", "Dark", "System default"];
        let t = if hover == 3 { "Guide: how Space Hunter works".to_string() }
                else { format!("{}{}", names[hover as usize], if now == modes[hover as usize] { "  (on)" } else { "" }) };
        let l = layout(da, "Schibsted Grotesk 8.75");
        l.set_text(&t);
        let (tw, th) = l.pixel_size();
        crate::theme::rgb(cr, pal.ink);
        cr.move_to(g.0 - 10.0 - tw as f64, (TK_H - th as f64) / 2.0 + 1.0);
        pangocairo::functions::show_layout(cr, &l);
    }
}

fn theme_switch() -> (gtk::Box, gtk::DrawingArea) {
    use crate::theme::Mode;
    let strip = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    strip.set_margin_top(8); strip.set_margin_start(18); strip.set_margin_end(18);
    strip.append(&hspacer());
    let da = gtk::DrawingArea::new();
    // room for the keys and, at their left, the longest hover name in full (never cut off)
    let tl = layout(&da, "Schibsted Grotesk 8.75");
    tl.set_text("Guide: how Space Hunter works");
    let need = TK_C * 4.0 + TK_GAP + 10.0 + tl.pixel_size().0 as f64 + 12.0;
    da.set_content_width(need.max(340.0).ceil() as i32);
    da.set_content_height(TK_H as i32 + 3);
    let hover = Rc::new(Cell::new(-1i32));
    {
        let hv = hover.clone();
        da.set_draw_func(move |da, cr, w, _| draw_top_keys(da, cr, w as f64, hv.get()));
    }
    let motion = gtk::EventControllerMotion::new();
    {
        let (hv, dw) = (hover.clone(), da.downgrade());
        motion.connect_motion(move |_, x, y| {
            if let Some(d) = dw.upgrade() {
                let h = top_hit(d.width() as f64, x, y);
                d.set_cursor_from_name(Some(if h >= 0 { "pointer" } else { "default" }));
                if h != hv.get() { hv.set(h); d.queue_draw(); }
            }
        });
        let (hv, dw) = (hover.clone(), da.downgrade());
        motion.connect_leave(move |_| { hv.set(-1); if let Some(d) = dw.upgrade() { d.queue_draw(); } });
    }
    da.add_controller(motion);
    let click = gtk::GestureClick::new();
    {
        let dw = da.downgrade();
        click.connect_pressed(move |_, _, x, y| {
            if let Some(d) = dw.upgrade() {
                let i = top_hit(d.width() as f64, x, y);
                if (0..3).contains(&i) {
                    crate::theme::set_mode([Mode::Light, Mode::Dark, Mode::System][i as usize]);
                    d.queue_draw();
                }
            }
        });
    }
    da.add_controller(click);
    strip.append(&da);
    (strip, da)
}

pub fn build(app: &gtk::Application, opts: Opts) -> Rc<Ui> {
    let win = gtk::ApplicationWindow::builder().application(app).title("Space Hunter").default_width(1180).default_height(760).build();
    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    root.add_css_class("chassis");
    let (strip, top_keys) = theme_switch();
    root.append(&strip);

    // the dark display: the number you get back, the disk as one bar with a clear legend
    let display = gtk::Box::new(gtk::Orientation::Vertical, 0);
    display.add_css_class("display");
    display.set_margin_top(6); display.set_margin_start(18); display.set_margin_end(18);
    let top = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    let left = gtk::Box::new(gtk::Orientation::Vertical, 0);
    let space_lbl = label("Space to free up", "display-label");
    left.append(&space_lbl);
    let num = gtk::Box::new(gtk::Orientation::Horizontal, 16);
    let heroes = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let hero = label("0", "hero");
    let unit = label("MB", "hero-unit");
    unit.set_valign(gtk::Align::End); unit.set_margin_bottom(12);
    heroes.append(&hero); heroes.append(&unit);
    num.append(&heroes);
    // while the items are measured: one turning ring beside the number, moved by the clock (shown only then)
    let spin = gtk::DrawingArea::new();
    spin.set_content_width(26); spin.set_content_height(26); spin.set_valign(gtk::Align::Center); spin.set_margin_end(-6);
    let spin_t0 = std::time::Instant::now();
    spin.set_draw_func(move |_, cr, w, h| {
        let (cx, cy, r) = (w as f64 / 2.0, h as f64 / 2.0, 10.5);
        cr.set_line_width(3.5);
        cr.set_source_rgb(0.227, 0.227, 0.220); cr.arc(cx, cy, r, 0.0, 2.0 * std::f64::consts::PI); let _ = cr.stroke();
        let a = (spin_t0.elapsed().as_secs_f64() * 300f64.to_radians()) % (2.0 * std::f64::consts::PI);
        cr.set_line_cap(gtk::cairo::LineCap::Round);
        cr.set_source_rgb(1.0, 0.357, 0.122); cr.arc(cx, cy, r, a, a + 1.9); let _ = cr.stroke();
    });
    spin.add_tick_callback(|w, _| { w.queue_draw(); glib::ControlFlow::Continue });   // only runs while it's shown
    num.append(&spin);
    let beside = gtk::Box::new(gtk::Orientation::Vertical, 4);
    beside.set_valign(gtk::Align::Center);
    let beside1 = label("measuring every item", "beside");
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
    let protect = key("protected folders", &[]);
    let reset = key("default cleanup", &[]);
    reset.set_margin_end(4);
    let clean = key("clean", &["primary"]);
    keys.append(&protect); keys.append(&reset); keys.append(&clean);
    root.append(&keys);

    // the three sections: three columns on one rail, an orange marker under the open one
    let rail = gtk::DrawingArea::new();
    rail.set_content_height(88);
    rail.set_margin_start(18); rail.set_margin_end(18); rail.set_margin_top(4);
    rail.set_cursor_from_name(Some("pointer"));
    root.append(&rail);

    // the list of the chosen section, and the item panel that slides in at the right
    let body = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    body.set_vexpand(true);
    // the list shares the display's, the keys' and the rail's left and right edges (18 px in)
    let listside = gtk::Box::new(gtk::Orientation::Vertical, 0);
    listside.set_hexpand(true);
    listside.set_margin_start(18); listside.set_margin_end(18); listside.set_margin_top(18);
    let head = gtk::Box::new(gtk::Orientation::Horizontal, 14);
    let title = label("", "section-title");
    head.append(&title);
    head.append(&hspacer());
    let tick_all = gtk::Button::with_label("Tick all"); tick_all.add_css_class("link-btn");
    let untick_all = gtk::Button::with_label("Untick all"); untick_all.add_css_class("link-btn");
    head.append(&tick_all); head.append(&untick_all);
    listside.append(&head);
    // what this section is: drawn by hand so its lines can be balanced (no word left alone on the last line);
    // its height follows the text, so the gaps title / explain / call to action / first row stay even
    let explain = gtk::DrawingArea::new();
    explain.set_margin_top(8);
    explain.set_content_height(18);
    listside.append(&explain);
    // the one thing to do in this section: its own line, a 3 px orange bar and orange-deep words
    let cta_row = gtk::Box::new(gtk::Orientation::Horizontal, 7);
    cta_row.set_margin_top(8);
    let cbar = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    cbar.add_css_class("cta-bar");
    cbar.set_size_request(3, 17); cbar.set_valign(gtk::Align::Center);
    cta_row.append(&cbar);
    let cta = label("", "cta");
    cta.set_ellipsize(gtk::pango::EllipsizeMode::End);
    cta.set_hexpand(true);
    cta_row.append(&cta);
    listside.append(&cta_row);
    let rows = gtk::Box::new(gtk::Orientation::Vertical, 0);
    rows.set_margin_top(8);
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
    status.set_hexpand(true);
    let bottom = gtk::Box::new(gtk::Orientation::Vertical, 0);   // the status line, or the update card in its place
    let status_row = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    status_row.append(&status);
    let ver = label(&format!("Space Hunter {}", crate::VERSION.trim()), "status");   // which version this is
    ver.set_xalign(1.0);
    status_row.append(&ver);
    bottom.append(&status_row);
    root.append(&bottom);
    let overlay = gtk::Overlay::new();   // the guide lies over all of it
    overlay.set_child(Some(&root));
    win.set_child(Some(&crate::sheet(overlay.upcast_ref::<gtk::Widget>())));

    // the label above the big number, the bar and the legend start where its first glyph's ink starts
    let glyph_peers: Vec<gtk::Widget> = vec![space_lbl.clone().upcast(), bar.clone().upcast(), legend.clone().upcast()];
    let ui = Rc::new(Ui {
        st: RefCell::new(St { cats: Vec::new(), tier: Level::Safe, open: None, pending: 0, cleaning: false, first_scan_done: false }),
        win, hero, unit, beside1, spin, beside2, disk_text, freed_text, bar, bar_vals: Cell::new((0.0, 0.0, 0.0)),
        legend_ticked, legend_not, clean, map_key: map.clone(), protect_key: protect.clone(), reset_key: reset.clone(), top_keys, display, overlay, rail, rail_box_hover: Cell::new(-1),
        rail_hover: Cell::new(-1), rail_boxes: Cell::new([(0.0, 0.0, 0.0, 0.0); 3]), mark: Cell::new(0.0), sliding: Cell::new(false), slide_gen: Cell::new(0),
        title, explain, cta, list_sw: sw.clone(), guide: RefCell::new(None), guide_pending: Cell::new(false), rows, panel_rev, panel,
        psw: psw.clone(), panel_for: RefCell::new(None), scroll_gen: Cell::new(0), status,
        prog_win: RefCell::new(None), opts,
        bottom, update_on: Cell::new(false), ask_card: RefCell::new(None), ask_shown: Cell::new(false), guide_seen_at_start: guide_seen(),
        scan_gen: RefCell::new(Default::default()), dir_sizes: RefCell::new(Default::default()), dir_times: RefCell::new(Default::default()), inside_here: RefCell::new(None), described: RefCell::new(Default::default()),
        more: Cell::new(false),
        glyph_peers,
    });

    {   // the version number, bottom right: click it to change your answer about the public totals
        ver.set_cursor_from_name(Some("pointer"));
        let click = gtk::GestureClick::new();
        let u = ui.clone();
        click.connect_released(move |_, _, _, _| { if !u.update_on.get() { show_ask(&u); } });
        ver.add_controller(click);
    }

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
    {   // the explain line
        let u = ui.clone();
        ui.explain.set_draw_func(move |da, cr, w, _| {
            let tier = u.st.borrow().tier;
            let l = explain_layout(da, tier, w as f64);
            crate::theme::rgb(cr, crate::theme::pal().muted);
            cr.move_to(0.0, 0.0);
            pangocairo::functions::show_layout(cr, &l);
        });
        let u = ui.clone();
        ui.explain.connect_resize(move |_, _, _| { let u2 = u.clone(); glib::idle_add_local_once(move || fit_explain(&u2)); });
    }
    {   // the rail: drawn by hand; a column switches the section, its small box ticks or unticks all of it
        let u = ui.clone();
        ui.rail.set_draw_func(move |da, cr, w, h| draw_rail(&u, da, cr, w as f64, h as f64));
        let motion = gtk::EventControllerMotion::new();
        let u = ui.clone();
        motion.connect_motion(move |_, x, y| {
            let w = u.rail.width() as f64;
            let i = if w > 0.0 { ((x / (w / 3.0)) as i32).clamp(0, 2) } else { -1 };
            let b = u.rail_boxes.get().iter().position(|b| b.2 > 0.0 && inside((b.0 - 5.0, b.1 - 5.0, b.2 + 10.0, b.3 + 10.0), x, y)).map(|k| k as i32).unwrap_or(-1);
            if i != u.rail_hover.get() || b != u.rail_box_hover.get() { u.rail_hover.set(i); u.rail_box_hover.set(b); u.rail.queue_draw(); }
        });
        let u = ui.clone();
        motion.connect_leave(move |_| { u.rail_hover.set(-1); u.rail_box_hover.set(-1); u.rail.queue_draw(); });
        ui.rail.add_controller(motion);
        let click = gtk::GestureClick::new();
        let u = ui.clone();
        click.connect_pressed(move |_, _, x, y| rail_click(&u, x, y));
        ui.rail.add_controller(click);
    }
    {   // the guide: "?" plays it again; while it's up it takes the keys (Esc skips, Enter / Right / Space next, Left back)
        let u = ui.clone();
        let gclick = gtk::GestureClick::new();
        gclick.connect_pressed(move |_, _, x, y| {
            if top_hit(u.top_keys.width() as f64, x, y) == 3 { start_guide(&u, 0); }
        });
        ui.top_keys.add_controller(gclick);
        let keys = gtk::EventControllerKey::new();
        keys.set_propagation_phase(gtk::PropagationPhase::Capture);
        let u = ui.clone();
        keys.connect_key_pressed(move |_, k, _, _| {
            let g = u.guide.borrow().clone();
            let g = match g { Some(g) => g, None => return glib::Propagation::Proceed };
            use gtk::gdk::Key;
            if k == Key::Escape { guide_finish(&u, &g); }
            else if k == Key::Return || k == Key::KP_Enter || k == Key::Right || k == Key::space { guide_go(&u, &g, g.step.get() + 1); }
            else if k == Key::Left && g.step.get() > 0 { guide_go(&u, &g, g.step.get() - 1); }
            glib::Propagation::Stop
        });
        ui.win.add_controller(keys);
        // it starts by itself only while you're looking at the window
        let u = ui.clone();
        ui.win.connect_is_active_notify(move |w| {
            if !w.is_active() || !u.guide_pending.get() { return; }
            u.guide_pending.set(false);
            if u.guide.borrow().is_none() && !u.st.borrow().cleaning && !guide_seen() { start_guide(&u, 0); }
        });
    }
    {
        let u = ui.clone();
        tick_all.connect_clicked(move |_| set_all(&u, true));
        let u = ui.clone();
        untick_all.connect_clicked(move |_| set_all(&u, false));
        let u = ui.clone();
        reset.connect_clicked(move |_| {
            {
                let mut st = u.st.borrow_mut();
                for c in st.cats.iter_mut() {
                    c.checked = c.is_safe() && c.refused().is_none();
                    c.picks.clear(); c.pick_bytes = None;
                    c.age_minutes = c.default_age;
                    c.last_age = if c.default_age > 0 { c.default_age } else { 14 * engine::DAY };
                }
                engine::settings::save(&st.cats);
            }
            u.status.set_text("Default cleanup: Quick clean ticked, the usual ages. Protected folders stay protected.");
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
    {   // closing the main window mid-clean would cut the clean off: show the cleaning window instead
        let u = ui.clone();
        ui.win.connect_close_request(move |_| {
            if !u.st.borrow().cleaning { return glib::Propagation::Proceed; }
            u.status.set_text("Cleaning is still running. Press Stop in its window first, or wait for it to finish.");
            if let Some(w) = u.prog_win.borrow().as_ref() { w.present(); }
            glib::Propagation::Stop
        });
    }
    ui.win.present();
    let hooks = ui.opts.open.is_some() || ui.opts.confirm || ui.opts.progress_dir.is_some() || ui.opts.storage.is_some() || ui.opts.protect || ui.opts.guide.is_some();
    if !hooks { check_update(&ui); }
    if let Some(p) = ui.opts.storage.clone() { let d = ui.opts.drill; open_storage(&ui, Some(PathBuf::from(p)), d); }
    if ui.opts.protect { let u2 = ui.clone(); crate::storage::open_protect(&ui.win, Rc::new(move || reload(&u2))); }
    if let Some(dir) = ui.opts.progress_dir.clone() {
        let c = test_cat(PathBuf::from(dir));
        start_clean(&ui, vec![c], false);   // deleted, as the screenshot has always shown
    } else {
        scan(&ui, None);
    }
    ui
}

// The Storage map; when it closes after you've added or removed something, the list is built and measured again.
fn open_storage(u: &Rc<Ui>, start: Option<PathBuf>, drill: usize) {
    let cats = u.st.borrow().cats.clone();
    let u2 = u.clone();
    crate::storage::open(&u.win, cats, Rc::new(move || { reload(&u2); u2.status.set_text("Cleanup list updated. What you added is in Deep clean, unticked."); }), start, drill);
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

// One quiet check for a newer version; offline or up to date, nothing happens.
fn check_update(u: &Rc<Ui>) {
    let (tx, rx) = mpsc::channel::<crate::updater::Release>();
    std::thread::spawn(move || { if let Some(r) = crate::updater::check() { let _ = tx.send(r); } });
    let uu = u.clone();
    glib::timeout_add_local(Duration::from_millis(500), move || match rx.try_recv() {
        Ok(r) => { show_update(&uu, Rc::new(r)); glib::ControlFlow::Break }
        Err(mpsc::TryRecvError::Empty) => glib::ControlFlow::Continue,
        Err(_) => glib::ControlFlow::Break,
    });
}

// The update card: at the bottom, in place of the status line.
fn show_update(u: &Rc<Ui>, r: Rc<crate::updater::Release>) {
    hide_ask(u); u.ask_shown.set(false);   // an update wins over the public-totals question (which can come up again later)
    u.update_on.set(true);
    let can_install = crate::updater::self_path().is_some();
    let card = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    card.add_css_class("update-card");
    let dot = label("\u{25CF}", "update-dot"); dot.set_valign(gtk::Align::Center);
    card.append(&dot);
    let text = gtk::Box::new(gtk::Orientation::Vertical, 2); text.set_valign(gtk::Align::Center);
    text.append(&label(&format!("Space Hunter {} is ready", r.version), "update-title"));
    let sub = label(&if can_install { format!("You have {}. Updating takes a few seconds and keeps your settings.", crate::updater::current()) }
                    else { format!("You have {}. Update it the way you installed it (for example with Nix).", crate::updater::current()) }, "update-sub");
    text.append(&sub);
    card.append(&text);
    card.append(&hspacer());
    let news = key("what's new", &[]);
    let later = key("later", &[]);
    card.append(&news); card.append(&later);
    let page = r.page.clone();
    news.connect_clicked(move |_| { if let Some(p) = &page { let _ = gtk::gio::AppInfo::launch_default_for_uri(p, None::<&gtk::gio::AppLaunchContext>); } });
    let (uu, c2) = (u.clone(), card.clone());
    later.connect_clicked(move |_| { uu.update_on.set(false); uu.bottom.remove(&c2); if let Some(p) = uu.status.parent() { p.set_visible(true); } });
    if can_install {
        let go = key("update & relaunch", &["primary"]);
        card.append(&go);
        let (uu, r2, sub2, later2, news2) = (u.clone(), r.clone(), sub.clone(), later.clone(), news.clone());
        go.connect_clicked(move |b| {
            b.set_sensitive(false); later2.set_sensitive(false); news2.set_sensitive(false);
            let (tx, rx) = mpsc::channel::<Result<String, String>>();
            let r3 = r2.clone();
            let rel = crate::updater::Release { version: r3.version.clone(), page: r3.page.clone(), url: r3.url.clone(), sig_url: r3.sig_url.clone() };
            std::thread::spawn(move || {
                let t2 = tx.clone();
                let res = crate::updater::install(&rel, &move |m: &str| { let _ = t2.send(Ok(m.to_string())); });
                let _ = tx.send(match res { Ok(()) => Err(String::new()), Err(why) => Err(why) });
            });
            let (uu2, sub3, b2, l3, n3) = (uu.clone(), sub2.clone(), b.clone(), later2.clone(), news2.clone());
            glib::timeout_add_local(Duration::from_millis(100), move || {
                while let Ok(m) = rx.try_recv() {
                    match m {
                        Ok(msg) => sub3.set_text(&msg),
                        Err(why) if why.is_empty() => { uu2.win.close(); std::process::exit(0); }   // the new version is opening
                        Err(why) => { sub3.set_text(&why); b2.set_label("TRY AGAIN"); b2.set_sensitive(true); l3.set_sensitive(true); n3.set_sensitive(true); return glib::ControlFlow::Break; }
                    }
                }
                glib::ControlFlow::Continue
            });
        });
    }
    if let Some(p) = u.status.parent() { p.set_visible(false); }
    u.bottom.append(&card);
}

// A run started with a test hook: nothing opens by itself and nothing is sent.
fn test_run(u: &Ui) -> bool {
    let o = &u.opts;
    o.quiet || o.open.is_some() || o.confirm || o.progress_dir.is_some() || o.storage.is_some() || o.protect || o.guide.is_some()
}

// The public-totals question, asked once (after the first-run guide, on a later launch) and again only when you click
// the version number. The same card as an update, in place of the status line.
fn show_ask(u: &Rc<Ui>) {
    if u.update_on.get() { return; }
    hide_ask(u);
    let on = crate::stats::is_on();
    let card = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    card.add_css_class("update-card");
    let dot = label("\u{25CF}", "update-dot"); dot.set_valign(gtk::Align::Center);
    card.append(&dot);
    let text = gtk::Box::new(gtk::Orientation::Vertical, 2); text.set_valign(gtk::Align::Center);
    text.append(&label(if on { "You're counted in Space Hunter's public totals" } else { "Count yourself in Space Hunter's public totals?" }, "update-title"));
    text.append(&label("Shares only how much you've freed and your version. Never file names or what was cleaned.", "update-sub"));
    card.append(&text);
    card.append(&hspacer());
    let sent = key("what's sent", &[]);
    let no = key(if on { "stop" } else { "no thanks" }, &[]);
    let yes = key(if on { "keep counting me" } else { "count me in" }, &["primary"]);
    card.append(&sent); card.append(&no); card.append(&yes);
    sent.connect_clicked(|_| { let _ = gtk::gio::AppInfo::launch_default_for_uri(crate::stats::EXPLAINED, None::<&gtk::gio::AppLaunchContext>); });
    let uu = u.clone();
    no.connect_clicked(move |_| end_ask(&uu, false));
    let uu = u.clone();
    yes.connect_clicked(move |_| end_ask(&uu, true));
    if let Some(p) = u.status.parent() { p.set_visible(false); }
    u.bottom.append(&card);
    *u.ask_card.borrow_mut() = Some(card);
}

fn hide_ask(u: &Ui) {
    let c = u.ask_card.borrow_mut().take();
    if let Some(c) = c { u.bottom.remove(&c); }
}

fn end_ask(u: &Rc<Ui>, on: bool) {
    crate::stats::set(on);
    if on { crate::stats::maybe_send(true); }
    hide_ask(u);
    if let Some(p) = u.status.parent() { p.set_visible(true); }
    u.status.set_text(if on { "Thanks: you're counted in the public totals. Click the version number to change it." }
                      else { "Not counted. Click the version number at the bottom right if you change your mind." });
}

// Every time a scan finishes: send the totals (at most once in 20 hours, only if you said yes), or ask once.
fn scan_finished(u: &Rc<Ui>) {
    if test_run(u) || u.st.borrow().pending > 0 { return; }
    if crate::stats::asked() { crate::stats::maybe_send(false); }
    else if u.guide_seen_at_start && !u.ask_shown.get() && u.guide.borrow().is_none() && !u.st.borrow().cleaning && !u.update_on.get() {
        u.ask_shown.set(true);
        show_ask(u);
    }
}

fn test_cat(dir: PathBuf) -> Cat {
    Cat {
        id: "test".into(), name: "Test folder".into(), level: Level::Safe, kind: Kind::Contents, admin: false,
        roots: vec![dir], exclude: Default::default(), extensions: None, top_level_only: false, is_custom: false,
        default_age: 0, age_minutes: 0, last_age: engine::DAY, checked: true, files: 0, bytes: 0, scanned: true, blocked: false,
        short: String::new(), what: String::new(), deletes: String::new(), safe_text: String::new(), after: String::new(),
        picks: Default::default(), pick_files: 0, pick_bytes: None,
    }
}

fn set_all(u: &Rc<Ui>, on: bool) {
    {
        let mut st = u.st.borrow_mut();
        let tier = st.tier;
        for c in st.cats.iter_mut().filter(|c| c.level == tier) {
            if !is_empty(c) && c.refused().is_none() { c.checked = on; c.picks.clear(); c.pick_bytes = None; }
        }
        engine::settings::save(&st.cats);
    }
    refresh(u);
}

// Everything on screen from the current state.
fn refresh(u: &Rc<Ui>) { refresh_top(u); rebuild_rows(u); }

// The display, keys and section rail (not the list, so a tick box isn't replaced while it's being clicked).
fn refresh_top(u: &Rc<Ui>) {
    let st = u.st.borrow();
    let selected: u64 = st.cats.iter().filter(|c| c.included()).map(|c| c.clean_bytes()).sum();
    let not_ticked: u64 = st.cats.iter().filter(|c| !c.included() && !c.is_safe() && c.kind != Kind::Trash).map(|c| c.bytes).sum();
    let only_safe = !st.cats.iter().any(|c| c.included() && !c.is_safe() && (c.bytes > 0 || c.files > 0));
    let (num, unit) = split(selected);
    u.hero.set_text(&num);
    let peers: Vec<&gtk::Widget> = u.glyph_peers.iter().collect();
    align_to_glyph(&u.hero, &peers);
    u.unit.set_text(&unit);
    let freed = engine::history::total();
    u.spin.set_visible(st.pending > 0);
    if st.pending > 0 { u.beside1.set_text("measuring every item"); }
    else if only_safe { u.beside1.set_text(if freed.1 == 0 { "all safe to clean  \u{00B7}  press Clean to start" } else { "all safe to clean" }); }
    else { u.beside1.set_text("includes items to check first"); }
    u.beside2.set_text(&if not_ticked > 0 { format!("+ {} more you can check yourself", fmt(not_ticked)) } else { String::new() });
    let (free, total) = crate::disk("/");
    u.disk_text.set_text(&format!("{} free of {}", fmt(free), fmt(total)));
    let binned = engine::history::trashed();
    // no "0 MB + ..." : say only what happened
    let parts: Vec<String> = [(freed.0, "freed".to_string()), (binned, "moved to the Trash".to_string())].iter().filter(|(b, _)| *b > 0).map(|(b, w)| format!("{} {}", fmt(*b), w)).collect();
    u.freed_text.set_text(&if freed.1 > 0 && !parts.is_empty() { format!("Freed so far: {} in {} clean{}", parts.join(" + "), freed.1, if freed.1 == 1 { "" } else { "s" }) } else { String::new() });
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
    let can_clean = st.pending == 0 && st.cats.iter().any(|c| c.checked && (c.bytes > 0 || c.files > 0) || c.partial() && c.pick_bytes.unwrap_or(0) > 0);
    u.clean.set_label(&if st.cleaning { "SHOW CLEANING".to_string() } else { format!("CLEAN {}", fmt(selected)) });
    u.clean.set_sensitive(st.cleaning || can_clean);
    // the rail redraws from the state; the marker sits under the open section unless it's sliding there
    if !u.sliding.get() { u.mark.set(ORDER.iter().position(|l| *l == st.tier).unwrap_or(0) as f64); }
    u.rail.queue_draw();
    u.title.set_text(&engine::tier_name(st.tier).to_uppercase());
    fit_explain(u);
    u.cta.set_text(engine::tier_cta(st.tier));
}

// "14 of 17 MB" when both share a unit, else "900 MB of 2.1 GB"
fn size_of(a: u64, b: u64) -> String {
    let (x, y) = (fmt(a), fmt(b));
    let ux = x.rsplit(' ').next().unwrap_or("");
    let uy = y.rsplit(' ').next().unwrap_or("");
    if ux == uy { format!("{} of {}", x.rsplit_once(' ').map(|p| p.0).unwrap_or(x.as_str()), y) } else { format!("{} of {}", x, y) }
}

// The rail: per section its name, a small box (all / some / none ticked) with how much of it is ticked, the big size and
// what's in it; a 2 px line along the bottom with the orange marker under the open section.
fn draw_rail(u: &Ui, da: &gtk::DrawingArea, cr: &gtk::cairo::Context, w: f64, h: f64) {
    let pal = crate::theme::pal();
    let st = u.st.borrow();
    let rail_y = h - 3.0;
    crate::theme::rgb(cr, pal.line);
    cr.rectangle(0.0, rail_y, w, 2.0);
    let _ = cr.fill();
    const GAP: f64 = 6.0;   // between the tabs
    const PAD: f64 = 12.0;  // text inset inside a tab
    let tw = (w - 2.0 * GAP) / 3.0;
    let hover = u.rail_hover.get();
    let box_hover = u.rail_box_hover.get();
    let mut boxes = [(0.0, 0.0, 0.0, 0.0); 3];
    for (i, lvl) in ORDER.iter().enumerate() {
        let lvl = *lvl;
        let on = st.tier == lvl;
        let hot = hover == i as i32 && !on;
        let items: Vec<&Cat> = st.cats.iter().filter(|c| c.level == lvl).collect();
        let total: u64 = items.iter().map(|c| c.bytes).sum();
        let ticked: u64 = items.iter().filter(|c| c.included()).map(|c| c.clean_bytes()).sum();
        let n_items = items.iter().filter(|c| c.bytes > 0 || c.files > 0).count();
        let n_ticked = items.iter().filter(|c| c.included() && (c.bytes > 0 || c.files > 0)).count();
        let x0 = i as f64 * (tw + GAP);
        // the open section is a filled tab standing on the rail; a hovered one gets a faint fill
        if on || hot {
            crate::theme::rgb(cr, if on { pal.selected } else { pal.hover });
            let r = 6.0;
            cr.new_sub_path();
            cr.move_to(x0, rail_y);
            cr.arc(x0 + r, r, r, std::f64::consts::PI, 1.5 * std::f64::consts::PI);
            cr.arc(x0 + tw - r, r, r, 1.5 * std::f64::consts::PI, 2.0 * std::f64::consts::PI);
            cr.line_to(x0 + tw, rail_y);
            cr.close_path();
            let _ = cr.fill();
        }
        let x = x0 + PAD;
        let ty = 8.0;
        let text_ink = if on || hot { pal.ink } else { pal.muted };
        // the box sits after the name as set in semibold, so it never shifts when the open section changes
        let semi = layout(da, "Schibsted Grotesk Semi-Bold 9.75");
        semi.set_text(engine::tier_name(lvl));
        let (nw, nh) = semi.pixel_size();
        let nh = nh as f64;
        let hl = if on { semi } else { let l = layout(da, "Schibsted Grotesk 9.75"); l.set_text(engine::tier_name(lvl)); l };
        crate::theme::rgb(cr, text_ink);
        cr.move_to(x, ty);
        pangocairo::functions::show_layout(cr, &hl);
        let mut bx = x + nw as f64 + 10.0;
        let box_x = bx;
        let by = (ty + nh / 2.0 - 7.0).round();
        if n_items > 0 {   // the box, centred on the line: filled with a tick (all), a dash (some), or empty (none)
            if n_ticked == 0 {
                crate::theme::rgb(cr, if on || hot { pal.ink } else { pal.soft });
                cr.set_line_width(1.4);
                round_rect(cr, bx + 0.7, by + 0.7, 12.6, 12.6, 2.0);   // the rows' tick boxes' corners
                let _ = cr.stroke();
            } else {
                rgba(cr, ORANGE, 1.0);
                round_rect(cr, bx, by, 14.0, 14.0, 2.0);
                let _ = cr.fill();
                rgba(cr, ON_ORANGE, 1.0);
                if n_ticked == n_items {
                    cr.set_line_width(1.7);
                    cr.move_to(bx + 3.0, by + 7.2); cr.line_to(bx + 6.0, by + 10.2); cr.line_to(bx + 11.2, by + 3.8);
                    let _ = cr.stroke();
                } else {
                    cr.rectangle(bx + 3.5, by + 6.0, 7.0, 2.0);
                    let _ = cr.fill();
                }
            }
            bx += 14.0 + 6.0;
        }
        // how much of it is ticked, in space
        let words = if n_items == 0 { "nothing to clean".to_string() }
                    else if n_ticked == 0 { "none ticked".to_string() }
                    else if n_ticked == n_items && ticked >= total { format!("all {} ticked", fmt(total)) }
                    else { format!("{} ticked", size_of(ticked, total)) };
        let room = (x0 + tw - bx - 10.0).max(10.0);
        hl.set_width((room * SC) as i32);
        hl.set_ellipsize(gtk::pango::EllipsizeMode::End);
        crate::theme::rgb(cr, text_ink);
        show_at(cr, &hl, &words, bx, ty);
        if n_items > 0 {   // the box and its words are one target, underlined on hover
            let cw = (hl.pixel_size().0 as f64).min(room);
            boxes[i] = (box_x, by, bx + cw - box_x, 14.0);
            if box_hover == i as i32 {
                crate::theme::rgb(cr, text_ink);
                cr.set_line_width(1.0);
                cr.move_to(bx, ty + nh + 0.5);
                cr.line_to(bx + cw, ty + nh + 0.5);
                let _ = cr.stroke();
            }
        }
        // the big size, and what's in it beside the number (at most two lines)
        let bl = layout(da, "Big Shoulders Display Black 24");
        bl.set_text(&fmt(total));
        let (sw, sh) = bl.pixel_size();
        let ny = 30.0;
        crate::theme::rgb(cr, if on || hot { pal.ink } else { pal.muted });
        cr.move_to(x - bl.pixel_extents().0.x() as f64, ny);   // the first glyph's ink on the name's edge
        pangocairo::functions::show_layout(cr, &bl);
        let dx = x + sw as f64 + 10.0;
        let dw = x0 + tw - dx - 12.0;
        if dw > 60.0 {
            let dl = layout(da, "Schibsted Grotesk 9");
            dl.set_wrap(gtk::pango::WrapMode::WordChar);
            dl.set_text(engine::tier_desc(lvl));
            balance(&dl, dw);   // two even lines, never one word alone
            if dl.line_count() > 2 {
                dl.set_width((dw * SC) as i32);
                dl.set_height(-2);
                dl.set_ellipsize(gtk::pango::EllipsizeMode::End);
            }
            let (_, dh) = dl.pixel_size();
            crate::theme::rgb(cr, if on { pal.muted } else { pal.soft });
            cr.move_to(dx, ny + sh as f64 / 2.0 - dh as f64 / 2.0 + 1.0);
            pangocairo::functions::show_layout(cr, &dl);
        }
    }
    u.rail_boxes.set(boxes);
    rgba(cr, ORANGE, 1.0);
    cr.rectangle(u.mark.get() * (tw + GAP), rail_y - 2.0, tw, 5.0);
    let _ = cr.fill();
}

fn rail_click(u: &Rc<Ui>, x: f64, y: f64) {
    for (i, b) in u.rail_boxes.get().iter().enumerate() {
        if b.2 > 0.0 && inside((b.0 - 5.0, b.1 - 5.0, b.2 + 10.0, b.3 + 10.0), x, y) { tick_section(u, ORDER[i]); return; }
    }
    let w = u.rail.width() as f64;
    if w <= 0.0 { return; }
    let i = ((x.max(0.0) / (w / 3.0)) as usize).min(2);
    if u.st.borrow().tier == ORDER[i] { return; }
    slide_to(u, i);
    {
        let mut st = u.st.borrow_mut();
        st.tier = ORDER[i];
        let open_level = st.open.as_ref().and_then(|id| st.cats.iter().find(|c| &c.id == id).map(|c| c.level));
        if open_level.is_some() && open_level != Some(ORDER[i]) { st.open = None; }
    }
    if u.st.borrow().open.is_none() { u.panel_rev.set_reveal_child(false); }
    refresh(u);
}

// The small box: tick the whole section if anything in it isn't ticked, else untick it all (picks go either way).
fn tick_section(u: &Rc<Ui>, lvl: Level) {
    let open_now = {
        let mut st = u.st.borrow_mut();
        let all = st.cats.iter().filter(|c| c.level == lvl && c.refused().is_none() && (c.bytes > 0 || c.files > 0)).all(|c| c.checked);
        let mut any = false;
        for c in st.cats.iter_mut().filter(|c| c.level == lvl && c.refused().is_none() && (c.bytes > 0 || c.files > 0)) {
            c.checked = !all; c.picks.clear(); c.pick_bytes = None; any = true;
        }
        if !any { return; }
        engine::settings::save(&st.cats);
        st.open.is_some()
    };
    refresh(u);
    if open_now { fill_panel(u); }
}

// The marker slides to another column (ease-out, 0.32 s, by the frame clock).
fn slide_to(u: &Rc<Ui>, to: usize) {
    let from = u.mark.get();
    let gen = u.slide_gen.get() + 1;
    u.slide_gen.set(gen);
    if !animations_on() { u.sliding.set(false); u.mark.set(to as f64); u.rail.queue_draw(); return; }
    u.sliding.set(true);
    let start = Cell::new(None::<i64>);
    let uu = Rc::downgrade(u);
    u.rail.add_tick_callback(move |w, clock| {
        let u = match uu.upgrade() { Some(u) => u, None => return glib::ControlFlow::Break };
        if u.slide_gen.get() != gen { return glib::ControlFlow::Break; }
        let ft = clock.frame_time();
        let s = start.get().unwrap_or(ft);
        start.set(Some(s));
        let t = ((ft - s) as f64 / 320_000.0).min(1.0);
        u.mark.set(from + (to as f64 - from) * ease(t));
        w.queue_draw();
        if t >= 1.0 { u.sliding.set(false); glib::ControlFlow::Break } else { glib::ControlFlow::Continue }
    });
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

// what you picked, against the whole item: files and space
fn pick_chip(c: &Cat) -> String {
    let pb = match c.pick_bytes { Some(b) => b, None => return "measuring your picks\u{2026}".to_string() };
    let files = if c.pick_files <= c.files { format!("{} of {} files", c.pick_files, c.files) }
                else { format!("{} file{}", c.pick_files, if c.pick_files == 1 { "" } else { "s" }) };
    let size = if pb <= c.bytes { format!("{} of {}", fmt(pb), fmt(c.bytes)) } else { fmt(pb) };
    format!("{}  \u{00B7}  {}", files, size)
}

// One item: a tick box, its name and what happens, its size; the rest of the row opens its panel.
fn row(u: &Rc<Ui>, c: &Cat, empty: bool, open: bool) -> gtk::Box {
    let h = gtk::Box::new(gtk::Orientation::Horizontal, 12);
    h.add_css_class("rowline");
    let chk = gtk::CheckButton::new();
    // nothing to clean: an empty grey box, as on Windows and Mac; files picked by hand: ticked, like the whole item
    chk.set_active((c.checked || c.partial()) && !empty);
    chk.set_sensitive(!empty && c.refused().is_none());
    chk.set_valign(gtk::Align::Center);
    let id = c.id.clone();
    let uu = u.clone();
    // picked -> none (the picks go), none -> all, all -> none
    chk.connect_toggled(move |b| {
        let was_partial = {
            let mut st = uu.st.borrow_mut();
            let mut was = false;
            if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) {
                was = c.partial();
                c.checked = !was && b.is_active();
                c.picks.clear(); c.pick_bytes = None;
            }
            engine::settings::save(&st.cats);
            was
        };
        refresh_top(&uu);
        // the row's lines and the open panel follow once the click is over (the box isn't replaced while it's clicked)
        let open_here = uu.st.borrow().open.as_deref() == Some(id.as_str());
        if was_partial || open_here {
            let u3 = uu.clone();
            glib::idle_add_local_once(move || {
                if was_partial { rebuild_rows(&u3); }
                if open_here { fill_panel(&u3); }
            });
        }
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
    let partial = c.partial() && !empty && c.refused().is_none();
    if !empty {
        let age = if c.age_choice() {
            if c.age_minutes > 0 { format!("  \u{00B7}  older than {}", engine::fmt_age(c.age_minutes)) } else { "  \u{00B7}  everything".to_string() }
        } else if matches!(c.kind, Kind::Contents | Kind::Subfolders) { "  \u{00B7}  all of it".to_string() } else { String::new() };
        if partial {   // the usual description, then a small chip with what you picked; the chip always fits whole
            let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
            if !c.short.is_empty() {
                let s = label(&c.short, "row-sub");
                s.set_ellipsize(gtk::pango::EllipsizeMode::End);
                s.set_valign(gtk::Align::Center);
                line.append(&s);
            }
            let chip = label(&pick_chip(c), "pick-chip");
            chip.set_valign(gtk::Align::Center);
            line.append(&chip);
            names.append(&line);
        } else {
            let sub = match c.refused() {
                Some(why) => format!("Not cleaned: {}. Remove it from the list", why),
                None => format!("{}{}", c.short, age),
            };
            let s = label(&sub, "row-sub");
            s.set_ellipsize(gtk::pango::EllipsizeMode::End);
            names.append(&s);
        }
    }
    inner.append(&names);
    let sizes = gtk::Box::new(gtk::Orientation::Vertical, 3);
    let size_text = if !c.scanned { "\u{2026}".to_string() }
                    else if partial { c.pick_bytes.map(fmt).unwrap_or_else(|| "\u{2026}".to_string()) }
                    else { fmt(c.bytes) };
    let size = label(&size_text, "row-size");
    size.set_xalign(1.0);
    sizes.append(&size);
    if !empty {
        let second = if partial { "picked".to_string() } else { format!("{} file{}", c.files, if c.files == 1 { "" } else { "s" }) };
        let f = label(&second, "row-sub");
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
    *u.panel_for.borrow_mut() = None;   // opened again later, it starts at the top
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

// the first sentence of a note, for the short summary
fn first(t: &str) -> String {
    match t.find(". ") { Some(i) if i > 0 && i < 160 => t[..=i].to_string(), _ => t.to_string() }
}

// Every item that cleans folders shows what's in them (a single file you added has nothing inside).
fn has_inside(c: &Cat) -> bool { matches!(c.kind, Kind::Contents | Kind::Subfolders) && c.refused().is_none() && c.roots.iter().any(|r| r.is_dir()) }

// Select all / Clear in the item panel: the whole item, or nothing. Hand-picked files go either way.
fn set_item(u: &Rc<Ui>, id: &str, on: bool) {
    {
        let mut st = u.st.borrow_mut();
        if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) { c.checked = on; c.picks.clear(); c.pick_bytes = None; }
        engine::settings::save(&st.cats);
    }
    u.described.borrow_mut().clear();
    refresh(u);
    fill_panel(u);
    scan(u, Some(vec![id.to_string()]));
}

// The ticks under "What's inside" changed: measure the item (and what's picked in it) again.
fn remeasure(u: &Rc<Ui>, id: &str) {
    if u.st.borrow().cleaning {   // nothing is measured during a clean; show the change, measure afterwards
        let uu = u.clone();
        glib::idle_add_local_once(move || { refresh(&uu); fill_panel(&uu); });
        return;
    }
    scan(u, Some(vec![id.to_string()]));
}

// The item panel, top to bottom: its name and size, a short summary, what to clean, the files; the full notes (behind
// "More about this item") and the Trash's folders after.
// The panel is built again whenever its item changes (a tick, an age, a keep, a new measurement). The same item keeps
// its place: the scroll position is put back once the new contents are laid out (removing the clicked box would
// otherwise let the scroller fall back to the top). Another item starts at the top.
fn fill_panel(u: &Rc<Ui>) {
    let now = u.st.borrow().open.clone();
    let same = now.is_some() && u.panel_rev.reveals_child() && *u.panel_for.borrow() == now;   // hidden: opening afresh
    let adj = u.psw.vadjustment();
    let y = if same { adj.value() } else { 0.0 };
    *u.panel_for.borrow_mut() = now;
    fill_panel_now(u);
    hold_scroll(u, y);
}

// Put the panel's scroll back to y now and for the next few frames (until the new contents are laid out and any
// scroll-to-focus has settled). A newer call takes over from an older one.
fn hold_scroll(u: &Rc<Ui>, y: f64) {
    let adj = u.psw.vadjustment();
    adj.set_value(y);
    let gen = u.scroll_gen.get() + 1;
    u.scroll_gen.set(gen);
    let start = std::time::Instant::now();
    let uu = Rc::downgrade(u);
    u.psw.add_tick_callback(move |_, _| {
        let u = match uu.upgrade() { Some(u) => u, None => return glib::ControlFlow::Break };
        if u.scroll_gen.get() != gen { return glib::ControlFlow::Break; }
        if (adj.value() - y).abs() > 0.5 { adj.set_value(y); }
        if start.elapsed() < Duration::from_millis(300) { glib::ControlFlow::Continue } else { glib::ControlFlow::Break }
    });
}

fn fill_panel_now(u: &Rc<Ui>) {
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
    let t = label(&c.name, "panel-title");   // two lines at most, never cut off after one
    t.set_wrap(true); t.set_wrap_mode(gtk::pango::WrapMode::WordChar); t.set_lines(2); t.set_ellipsize(gtk::pango::EllipsizeMode::End);
    u.panel.append(&t);
    let stats = match c.refused() {
        Some(why) => format!("Space Hunter won't clean this: {}. Use \u{201C}Remove from cleanup list\u{201D} below.", why),
        None => if c.scanned { format!("{}   \u{00B7}   {} file{}", fmt(c.bytes), c.files, if c.files == 1 { "" } else { "s" }) } else { "Measuring\u{2026}".to_string() },
    };
    let s = label(&stats, "panel-stats"); s.set_wrap(true); s.set_max_width_chars(46);
    u.panel.append(&s);

    // three short lines, always in view
    let inside = has_inside(&c);
    let ages = c.age_choice() && c.refused().is_none();
    let goes = if c.partial() { "Only the files you tick below, whatever their age, in the next clean.".to_string() }
               else if !c.checked && inside && !c.admin { "Nothing yet: tick the item, or tick files below to clean just those.".to_string() }
               else if ages { if c.age_minutes > 0 { format!("Files untouched for {} or more.", engine::fmt_age(c.age_minutes)) } else { "Everything in it, whatever its age.".to_string() } }
               else { first(&c.deletes) };
    let grid = gtk::Grid::new();
    grid.set_column_spacing(14); grid.set_row_spacing(5); grid.set_margin_top(4);
    let mut r = 0;
    for (k, v) in [("What it is", first(&c.what)), ("What goes", goes), ("After", first(&c.after))] {
        if v.is_empty() { continue; }
        let kl = label(k, "sum-key"); kl.set_valign(gtk::Align::Start); kl.set_size_request(84, -1);
        let vl = label(&v, "sum-val"); vl.set_wrap(true); vl.set_max_width_chars(38); vl.set_hexpand(true);
        grid.attach(&kl, 0, r, 1, 1);
        grid.attach(&vl, 1, r, 1, 1);
        r += 1;
    }
    u.panel.append(&grid);
    let more = gtk::Button::with_label(if u.more.get() { "Less about this item" } else { "More about this item" });
    more.add_css_class("link-btn"); more.set_halign(gtk::Align::Start); more.set_margin_top(4); more.set_margin_bottom(4);
    u.panel.append(&more);

    // the age rule: only on items that fill up over time, and not while only hand-picked files are cleaned
    if ages && !c.partial() {
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
        let agebox = gtk::Box::new(gtk::Orientation::Horizontal, 4);
        let minus = key("\u{2212}", &[]);
        let val = label(&value.to_string(), "age-value"); val.set_xalign(0.5); val.set_width_chars(4);
        let plus = key("+", &[]);
        agebox.append(&minus); agebox.append(&val); agebox.append(&plus);
        let gap = gtk::Box::new(gtk::Orientation::Horizontal, 0); gap.set_size_request(8, -1); agebox.append(&gap);
        for (txt, u_m) in [("min", 1u32), ("hours", engine::HOUR), ("days", engine::DAY)] {
            let k = key(txt, on(older && unit == u_m));
            let (uu, id) = (u.clone(), c.id.clone());
            k.connect_clicked(move |_| set_age(&uu, &id, value.max(1) * u_m));
            k.set_sensitive(older);
            agebox.append(&k);
        }
        let (uu, id) = (u.clone(), c.id.clone());
        minus.connect_clicked(move |_| { if value > 1 { set_age(&uu, &id, (value - 1) * unit); } });
        let (uu, id) = (u.clone(), c.id.clone());
        plus.connect_clicked(move |_| { if value < 999 { set_age(&uu, &id, (value + 1) * unit); } });
        minus.set_sensitive(older); plus.set_sensitive(older);
        u.panel.append(&agebox);
        u.panel.append(&body_text(&if older { format!("Files untouched or added within {} stay.", engine::fmt_age(c.age_minutes)) } else { "Every file, whatever its age.".to_string() }));
    }
    if inside { inside_list(u, &c); }

    // the full notes, behind "More about this item"
    let details = gtk::Box::new(gtk::Orientation::Vertical, 6);
    details.set_visible(u.more.get());
    // a folder or file you added: say what's actually in it (worked out here, nothing is sent anywhere)
    let what = if c.is_custom {
        let root = c.roots[0].to_string_lossy().to_string();
        let known = u.described.borrow().get(&root).cloned();
        match known {
            Some(d) => d,
            None => {
                let looking = format!("Looking inside {}\u{2026}", c.roots[0].file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default());
                u.described.borrow_mut().insert(root.clone(), looking.clone());   // one look at a time, however often the panel redraws
                let (tx, rx) = mpsc::channel::<String>();
                let r2 = c.roots[0].clone();
                std::thread::spawn(move || { let _ = tx.send(engine::describe(&r2)); });
                let (uu, key) = (u.clone(), root.clone());
                glib::timeout_add_local(Duration::from_millis(150), move || match rx.try_recv() {
                    Ok(d) => { uu.described.borrow_mut().insert(key.clone(), d); fill_panel(&uu); glib::ControlFlow::Break }
                    Err(mpsc::TryRecvError::Empty) => glib::ControlFlow::Continue,
                    Err(_) => glib::ControlFlow::Break,
                });
                looking
            }
        }
    } else { c.what.clone() };
    for (h, b) in [("What is it?", &what), ("What gets cleaned", &c.deletes), ("Is it safe?", &c.safe_text), ("What happens after", &c.after)] {
        if b.is_empty() { continue; }
        details.append(&head(h));
        details.append(&body_text(b));
    }
    u.panel.append(&details);
    u.panel.reorder_child_after(&details, Some(&more));   // the full notes open right under their link
    {
        let (uu, d2) = (u.clone(), details.clone());
        more.connect_clicked(move |b| {
            let m = !uu.more.get();
            uu.more.set(m);
            d2.set_visible(m);
            b.set_label(if m { "Less about this item" } else { "More about this item" });
        });
    }
    // the Trash's folders, each with a keep switch (kept folders are protected)
    let roots: Vec<PathBuf> = c.roots.iter().filter(|r| r.is_dir()).cloned().collect();
    if !roots.is_empty() && !c.admin && c.kind == Kind::Trash {   // other items show their folders in the list above
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

// What the age choice does to a row of What's inside in a ticked item: 2 it goes, 1 some of it goes (newer files stay),
// 0 it stays (too new). Follows the age as you change it. A folder not measured yet counts as going.
fn age_state(c: &Cat, roots: &[PathBuf], p: &std::path::Path, dir: bool, modified: std::time::SystemTime, cutoff: std::time::SystemTime,
             times: &std::collections::HashMap<PathBuf, (std::time::SystemTime, std::time::SystemTime, Option<std::time::SystemTime>)>) -> u8 {
    if c.kind == Kind::Subfolders {   // a whole subfolder goes only when nothing in it is newer (as the engine decides)
        let root = match roots.iter().find(|r| p.starts_with(r) && p != r.as_path()) { Some(r) => r, None => return 2 };
        let first = match p.strip_prefix(root).ok().and_then(|rel| rel.components().next()) { Some(f) => f, None => return 2 };
        let top = root.join(first.as_os_str());
        if top.as_path() == p && !dir { return if modified < cutoff { 2 } else { 0 }; }
        return match times.get(&top) { Some((newest, _, _)) => if *newest < cutoff { 2 } else { 0 }, None => 2 };
    }
    if !dir { return if modified < cutoff { 2 } else { 0 }; }
    match times.get(p) {
        None => 2,                                                              // not measured yet
        Some((own, _, None)) => if *own < cutoff { 2 } else { 0 },              // no files in it: its own date decides
        // files only: a folder's own date doesn't hold its old files back
        Some((_, newest_file, Some(oldest))) => if *newest_file < cutoff { 2 } else if *oldest >= cutoff { 0 } else { 1 },
    }
}

// What's inside an item: its files and sub-folders, each with a tick box. In a ticked item everything ticked is
// cleaned and unticking keeps a file or folder for good; in an unticked item, ticking picks just that, for the next
// clean. A folder's name opens it, and Back goes up (never above the item's own folders).
fn inside_list(u: &Rc<Ui>, c: &Cat) {
    let roots: Vec<PathBuf> = c.roots.iter().filter(|r| r.is_dir()).cloned().collect();
    // where the list is looking: empty = the top, all of the item's folders at once
    let here = match u.inside_here.borrow().as_ref() {
        Some((id, h)) if id == &c.id && (h.as_os_str().is_empty() || roots.iter().any(|r| h.starts_with(r))) => h.clone(),
        _ => PathBuf::new(),
    };
    *u.inside_here.borrow_mut() = Some((c.id.clone(), here.clone()));
    let top = here.as_os_str().is_empty();
    let partial = c.partial();
    u.panel.append(&head(if c.is_custom { "Inside this folder" } else { "What's inside" }));
    u.panel.append(&body_text(if c.admin { "A system folder: after your password it's cleaned as a whole, so this list is just to look at." }
                              else if c.checked { "Everything ticked is cleaned. Untick anything that should always stay. Click a folder to look inside it." }
                              else if partial { "Just the ticked files and folders are cleaned, this once. Select all cleans the whole item." }
                              else { "Tick files or folders to clean just those, this once, or Select all for the whole item. Click a folder to look inside it." }));
    // what is picked, with Select all / Clear at the right (system items are cleaned whole, so there's nothing to pick)
    if !c.admin {
        let bar = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        bar.add_css_class("rowline");
        bar.set_margin_top(4);
        let age_now = if c.age_choice() { c.age_minutes } else { 0 };
        let state = if c.checked { if age_now > 0 { format!("Only files older than {}", engine::fmt_age(age_now)) } else { "All of it, except anything unticked".to_string() } }
                    else if partial { format!("{} picked{}", c.picks.len(), c.pick_bytes.map(|b| format!("  \u{00B7}  {}", fmt(b))).unwrap_or_default()) }
                    else { "Nothing picked yet".to_string() };
        let sl = label(&state, "inside-state");
        if partial { sl.add_css_class("picked"); }
        sl.set_hexpand(true); sl.set_valign(gtk::Align::Center);
        sl.set_ellipsize(gtk::pango::EllipsizeMode::End);
        bar.append(&sl);
        let all = gtk::Button::with_label("Select all"); all.add_css_class("link-btn");
        let none = gtk::Button::with_label("Clear"); none.add_css_class("link-btn");
        for b in [&all, &none] { b.set_valign(gtk::Align::Center); b.set_margin_bottom(4); }
        let (uu, id, rs) = (u.clone(), c.id.clone(), roots.clone());
        all.connect_clicked(move |_| {
            // the whole item, with nothing held back under it
            for sp in engine::custom::skip() {
                if rs.iter().any(|r| std::path::Path::new(&sp).starts_with(r)) { engine::custom::set_skipped(&sp, false); }
            }
            set_item(&uu, &id, true);
        });
        let (uu, id) = (u.clone(), c.id.clone());
        none.connect_clicked(move |_| set_item(&uu, &id, false));
        bar.append(&all); bar.append(&none);
        u.panel.append(&bar);
    }
    if !top {
        let root = roots.iter().find(|r| here.starts_with(r)).cloned().unwrap_or(here.clone());
        let rel = here.strip_prefix(&root).map(|r| r.to_string_lossy().replace('/', "  \u{203A}  ")).unwrap_or_default();
        let rname = root.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let b = gtk::Button::with_label(&format!("\u{2190}  Back  \u{00B7}  {}{}", rname, if rel.is_empty() { String::new() } else { format!("  \u{203A}  {}", rel) }));
        b.add_css_class("crumb");
        b.set_hexpand(true);
        if let Some(l) = b.child().and_then(|w| w.downcast::<gtk::Label>().ok()) { l.set_ellipsize(gtk::pango::EllipsizeMode::Middle); l.set_xalign(0.0); }
        let up = if roots.contains(&here) { PathBuf::new() } else { here.parent().map(|p| p.to_path_buf()).unwrap_or_default() };
        let (uu, id) = (u.clone(), c.id.clone());
        b.connect_clicked(move |_| { *uu.inside_here.borrow_mut() = Some((id.clone(), up.clone())); fill_panel(&uu); });
        line.append(&b);
        let open = gtk::Button::with_label("Open in Files");
        open.add_css_class("link-btn"); open.set_valign(gtk::Align::Center);
        let hp = here.clone();
        open.connect_clicked(move |_| {
            let _ = gtk::gio::AppInfo::launch_default_for_uri(&gtk::gio::File::for_path(&hp).uri(), None::<&gtk::gio::AppLaunchContext>);
        });
        line.append(&open);
        u.panel.append(&line);
    }
    let skip = engine::custom::skip();
    // name, path, folder?, size, changed, which folder it comes from (at the top)
    let mut entries: Vec<(String, PathBuf, bool, u64, std::time::SystemTime, Option<String>)> = Vec::new();
    let mut list = |dir: &std::path::Path, sub: Option<String>| {
        let mut dirs = Vec::new(); let mut files = Vec::new();
        if let Ok(rd) = std::fs::read_dir(dir) {
            for e in rd.flatten() {
                let m = match std::fs::symlink_metadata(e.path()) { Ok(m) => m, Err(_) => continue };
                if m.file_type().is_symlink() { continue; }
                // other people's and the system's files are never cleaned, so they aren't listed as if they were
                if !engine::is_root() && std::os::unix::fs::MetadataExt::uid(&m) != unsafe { libc::geteuid() } && !c.admin { continue; }
                let n = e.file_name().to_string_lossy().to_string();
                let item = (n.clone(), e.path(), m.is_dir(), m.len(), engine::age_time(&m), sub.clone());
                if m.is_dir() {
                    if c.top_level_only || c.exclude.contains(&n) { continue; }   // the item's own rules
                    dirs.push(item);
                } else {
                    if let Some(x) = &c.extensions { if !x.contains(&e.path().extension().map(|v| v.to_string_lossy().to_lowercase()).unwrap_or_default()) { continue; } }
                    files.push(item);
                }
            }
        }
        dirs.sort_by(|a, b| a.0.to_lowercase().cmp(&b.0.to_lowercase()));
        files.sort_by(|a, b| b.3.cmp(&a.3));
        entries.extend(dirs); entries.extend(files);
    };
    if top { for r in &roots { list(r, Some(format!("in {}", home_short(r)))); } } else { list(&here, None); }
    if entries.is_empty() { u.panel.append(&label("Empty", "group-note")); }
    let whole = c.checked || c.admin;   // ticked: everything here goes unless unticked; unticked: only what's ticked here
    let age = if c.age_choice() { c.age_minutes } else { 0 };
    let cutoff = std::time::SystemTime::now() - Duration::from_secs(age as u64 * 60);
    for (n, p, dir, len, modified, sub) in entries.iter().take(60) {
        let parent = p.parent().map(|x| x.to_path_buf()).unwrap_or_default();
        let inherited = !roots.contains(&parent) && engine::custom::is_skipped(&parent, &skip);   // a folder around it is kept
        let skipped = inherited || engine::custom::is_skipped(p, &skip);
        let inherited_pick = c.picks.iter().any(|q| q != p && p.starts_with(q));   // a folder around it is picked
        let (mut active, mut locked) = if whole { (!skipped, inherited) }
                                       else { (c.picks.contains(p) || inherited_pick, inherited_pick || skipped) };
        // the age choice, in a ticked item: 2 it goes, 1 some of it goes (newer files stay), 0 too new, it stays.
        // Unticked by you, it stays whatever its age; picked by hand in an unticked item, it goes whatever its age.
        let ast = if c.checked && age > 0 && !skipped { age_state(c, &roots, p, *dir, *modified, cutoff, &u.dir_times.borrow()) } else { 2 };
        let too_new = active && ast == 0;
        if too_new { active = false; locked = true; }   // the age decides: change the age above to include it
        let row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        row.add_css_class("rowline");
        let chk = gtk::CheckButton::new();
        chk.set_active(active); chk.set_sensitive(!locked && !c.admin);
        if active && !locked && ast == 1 { chk.set_inconsistent(true); }   // some of it: an orange box with a dash
        chk.set_valign(gtk::Align::Center);
        let (uu, pp, id) = (u.clone(), p.clone(), c.id.clone());
        if whole {   // unticking in a ticked item: always keep it
            chk.connect_toggled(move |b| {
                engine::custom::set_skipped(&pp.to_string_lossy(), !b.is_active());
                uu.described.borrow_mut().clear();
                remeasure(&uu, &id);
            });
        } else {     // ticking in an unticked item: clean just this, in the next clean
            chk.connect_toggled(move |b| {
                {
                    let mut st = uu.st.borrow_mut();
                    if let Some(item) = st.cats.iter_mut().find(|x| x.id == id) {
                        if b.is_active() {
                            item.picks.retain(|q| !q.starts_with(&pp));   // anything picked inside it is now covered by it
                            item.picks.insert(pp.clone());
                        } else {
                            item.picks.remove(&pp);
                        }
                        item.pick_bytes = None;
                    }
                }
                remeasure(&uu, &id);
            });
        }
        row.append(&chk);
        let nb = gtk::Button::new(); nb.add_css_class("row-btn"); nb.set_hexpand(true);
        let col = gtk::Box::new(gtk::Orientation::Vertical, 1);
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let nl = label(n, "row-name"); nl.set_hexpand(true); nl.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
        line.append(&nl);
        let dsize = if *dir { u.dir_sizes.borrow().get(p).cloned() } else { None };
        let size = if too_new { "too new, stays".to_string() }
                   else if *dir { format!("{}{}", match dsize { Some(sz) if sz < u64::MAX => format!("{}  \u{00B7}  ", fmt(sz)), _ => String::new() }, if ast == 1 { "newer files stay" } else { "folder" }) }
                   else if *len < 1024 { "under 1 KB".to_string() } else { fmt(*len) };
        line.append(&label(&if *dir { format!("{}  \u{203A}", size) } else { size }, "row-sub"));
        col.append(&line);
        if let Some(sb) = sub { let sl = label(sb, "row-sub"); sl.set_ellipsize(gtk::pango::EllipsizeMode::Middle); col.append(&sl); }   // which folder it comes from
        nb.set_child(Some(&col));
        if *dir {
            let (uu, pp, id) = (u.clone(), p.clone(), c.id.clone());
            nb.connect_clicked(move |_| { *uu.inside_here.borrow_mut() = Some((id.clone(), pp.clone())); fill_panel(&uu); });
        }
        row.append(&nb);
        u.panel.append(&row);
    }
    if entries.len() > 60 { u.panel.append(&label(&format!("+ {} more (open the folder in Files to see them all)", entries.len() - 60), "group-note")); }
    // folder sizes not known yet: measure them once in the background, then show them
    let unknown: Vec<PathBuf> = entries.iter().take(60).filter(|e| e.2 && !u.dir_sizes.borrow().contains_key(&e.1)).map(|e| e.1.clone()).collect();
    if !unknown.is_empty() {
        for d in &unknown { u.dir_sizes.borrow_mut().insert(d.clone(), u64::MAX); }   // "being measured"
        // its size, and its newest and oldest file (the folder's own date starts as the newest), so the age choice can
        // tick it (all older), dash it (some) or leave it (all newer)
        let (tx, rx) = mpsc::channel::<(PathBuf, u64, std::time::SystemTime, std::time::SystemTime, Option<std::time::SystemTime>)>();
        std::thread::spawn(move || for d in unknown {
            use std::os::unix::fs::MetadataExt;
            let dm = std::fs::symlink_metadata(&d);
            let dev = dm.as_ref().map(|m| m.dev()).unwrap_or(0);
            let mut newest = dm.as_ref().map(|m| engine::age_time(m)).unwrap_or(std::time::SystemTime::UNIX_EPOCH);
            let mut oldest: Option<std::time::SystemTime> = None;
            let mut newest_file = std::time::SystemTime::UNIX_EPOCH;
            let mut sum = 0u64; let mut st = vec![d.clone()];
            while let Some(x) = st.pop() {
                if let Ok(rd) = std::fs::read_dir(&x) { for e in rd.flatten() { if let Ok(m) = std::fs::symlink_metadata(e.path()) {
                    if m.dev() != dev { continue; }   // never into another disk mounted inside
                    if m.is_dir() { st.push(e.path()); } else if m.is_file() {
                        sum += m.blocks() * 512;
                        let t = engine::age_time(&m);
                        if t > newest { newest = t; }
                        if t > newest_file { newest_file = t; }
                        if oldest.map_or(true, |o| t < o) { oldest = Some(t); }
                    } } } }
            }
            if tx.send((d, sum, newest, newest_file, oldest)).is_err() { break; }
        });
        let uu = u.clone();
        let asked = u.inside_here.borrow().clone();   // the list that asked; another one open by then isn't redrawn
        glib::timeout_add_local(Duration::from_millis(300), move || {
            let mut any = false;
            let refresh = |uu: &Rc<Ui>| if *uu.inside_here.borrow() == asked { fill_panel(uu); };
            loop { match rx.try_recv() {
                Ok((d, sz, newest, newest_file, oldest)) => { uu.dir_times.borrow_mut().insert(d.clone(), (newest, newest_file, oldest)); uu.dir_sizes.borrow_mut().insert(d, sz); any = true; }
                Err(mpsc::TryRecvError::Empty) => { if any { refresh(&uu); } return glib::ControlFlow::Continue; }
                Err(_) => { if any { refresh(&uu); } return glib::ControlFlow::Break; }
            } }
        });
    }
}

// Measure items in the background, a few at once, and fill the numbers in as they arrive.
fn scan(u: &Rc<Ui>, ids: Option<Vec<String>>) {
    if ids.is_none() && !u.st.borrow().cleaning { u.dir_sizes.borrow_mut().clear(); u.dir_times.borrow_mut().clear(); }   // folder sizes are measured again
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
    let gens: std::collections::HashMap<String, u64> = targets.iter().map(|c| {
        let mut g = u.scan_gen.borrow_mut(); let e = g.entry(c.id.clone()).or_insert(0); *e += 1; (c.id.clone(), *e)
    }).collect();
    let queue = Arc::new(Mutex::new(VecDeque::from(targets)));
    // id, name, files, bytes, and for a partly picked item what its picks hold (files, bytes)
    let (tx, rx) = mpsc::channel::<(String, String, u64, u64, Option<(u64, u64)>)>();
    for _ in 0..n.min(4) {
        let (q, tx) = (queue.clone(), tx.clone());
        std::thread::spawn(move || loop {
            let c = match q.lock().ok().and_then(|mut g| g.pop_front()) { Some(c) => c, None => break };
            let mut cnt = Counter::default();
            engine::process(&c, false, false, &mut cnt);
            let picked = if c.partial() {
                let mut pc = Counter::default();
                engine::process_picks(&c, false, false, &mut pc);
                Some((pc.files, pc.bytes))
            } else { None };
            if tx.send((c.id.clone(), c.name.clone(), cnt.files, cnt.bytes, picked)).is_err() { break; }
        });
    }
    let got = Rc::new(Cell::new(0usize));
    let uu = u.clone();
    glib::timeout_add_local(Duration::from_millis(120), move || {
        let mut any = false;
        let mut last = String::new();
        while let Ok((id, name, files, bytes, picked)) = rx.try_recv() {
            any = true; got.set(got.get() + 1); last = name;
            let newest = uu.scan_gen.borrow().get(&id) == gens.get(&id);
            let mut st = uu.st.borrow_mut();
            if newest {
                if let Some(c) = st.cats.iter_mut().find(|c| c.id == id) {
                    c.files = files; c.bytes = bytes; c.scanned = true;
                    match picked { Some((pf, pb)) => { c.pick_files = pf; c.pick_bytes = Some(pb); } None => { c.pick_files = 0; c.pick_bytes = None; } }
                }
            }
            st.pending = st.pending.saturating_sub(1);
        }
        if any {
            let pending = uu.st.borrow().pending;
            let freed = engine::history::total();
            uu.status.set_text(&if pending > 0 { format!("Measuring {}\u{2026}", last) }
                               else if freed.1 == 0 { "Quick clean is ticked for you: press Clean, check the list, and it's done.".to_string() }
                               else { "Scan done.  Click a row to see what it is.  Tick the box to include it.".to_string() });
            refresh(&uu);
            if uu.st.borrow().open.is_some() { fill_panel(&uu); }
        }
        if got.get() >= n {
            let first = { let mut st = uu.st.borrow_mut(); let f = !st.first_scan_done && st.pending == 0; if f { st.first_scan_done = true; } f };
            if first { after_first_scan(&uu); }
            scan_finished(&uu);
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
    // the first-run guide: once, the first time a scan finishes (never during a clean or a test run)
    let test_run = u.opts.quiet || u.opts.open.is_some() || u.opts.confirm || u.opts.progress_dir.is_some() || u.opts.storage.is_some() || u.opts.protect;
    if let Some(n) = u.opts.guide {   // test hook: let the new rows be laid out first
        let uu = u.clone();
        glib::timeout_add_local_once(Duration::from_millis(300), move || start_guide(&uu, n));
    } else if !test_run && !guide_seen() && u.guide.borrow().is_none() && !u.st.borrow().cleaning {
        if u.win.is_active() {
            let uu = u.clone();
            glib::idle_add_local_once(move || { if uu.guide.borrow().is_none() && !uu.st.borrow().cleaning { start_guide(&uu, 0); } });
        } else { u.guide_pending.set(true); }
    }
}

// Shown every time Clean is pressed: what will go, grouped by risk, and how (Trash or permanently).
fn confirm(u: &Rc<Ui>) {
    // frozen copies; a partly picked item counts only what you picked
    let sel: Vec<Cat> = u.st.borrow().cats.iter()
        .filter(|c| c.refused().is_none() && (c.checked && (c.bytes > 0 || c.files > 0) || c.partial() && c.pick_bytes.unwrap_or(0) > 0))
        .map(|c| {
            let mut s = c.clone();
            if c.partial() { s.bytes = c.pick_bytes.unwrap_or(0); s.files = c.pick_files; s.name = format!("{} (your picks)", c.name); }
            s
        })
        .collect();
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
    // how everything ticked goes, Quick clean included: deleted for good unless you choose the Trash
    let only_safe = sel.iter().all(|c| c.is_safe());
    let to_trash = Rc::new(Cell::new(false));
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
    w.set_child(Some(&crate::sheet(v.upcast_ref::<gtk::Widget>())));
    let show: Rc<dyn Fn()> = {
        let (del, bin, hint, ok, t) = (del.clone(), bin.clone(), hint.clone(), ok.clone(), to_trash.clone());
        Rc::new(move || {
            let trash = t.get();
            if trash { bin.add_css_class("on"); del.remove_css_class("on"); } else { del.add_css_class("on"); bin.remove_css_class("on"); }
            hint.set_text(&if trash { "Everything goes to the Trash, so you can restore it. That space comes back only when you empty the Trash.".to_string() }
                          else { format!("{}Everything is deleted permanently and can't be restored. The space comes back straight away.",
                                         if only_safe { "These are caches and leftovers that rebuild. " } else { "" }) });
            ok.set_label(&if !trash { format!("PERMANENTLY DELETE {}", fmt(total)) } else { format!("MOVE {} TO TRASH", fmt(total)) });
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

enum Msg { Line(String), Result(String), Freed(String, u64, u64, bool), Totals(u64, u64, u64, u64), Current(String), Done(bool, Option<u64>) }

// The finish of a clean, in ms from the moment it's done (as on Windows): the display grows over the log, the bar gives
// way to the drive drawn as a ring, the freed wedge lifts out and crumbles, free space counts up, one pulse, the list.
const GROW_MS: f64 = 420.0;
const RING_AT: f64 = 250.0;
const RING_MS: f64 = 520.0;
const LIFT_AT: f64 = 820.0;
const LIFT_MS: f64 = 300.0;
const CRUMBLE_AT: f64 = 1120.0;
const CRUMBLE_MS: f64 = 520.0;
const CLOSE_AT: f64 = 1250.0;
const CLOSE_MS: f64 = 520.0;
const DONE_AT: f64 = 1820.0;
const PULSE_MS: f64 = 650.0;
const LIST_AT: f64 = 2150.0;
const LIST_MS: f64 = 320.0;
const FINISH_MS: f64 = LIST_AT + LIST_MS;
const BASE_H: i32 = 150;   // the display's height while cleaning

struct Speck { a: f64, r: f64, vx: f64, vy: f64, s: f64, delay: f64 }

// the freed wedge's dust: always the same 90 specks
fn make_specks() -> Vec<Speck> {
    let mut seed: u64 = 0x9E37_79B9_7F4A_7C15 ^ 7;
    let mut rnd = move || { seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17; (seed >> 11) as f64 / (1u64 << 53) as f64 };
    let mut v = Vec::with_capacity(90);
    for _ in 0..90 {
        let a = rnd(); let r = rnd() - 0.5; let vx = rnd() * 1.6 + 0.4; let vy = -rnd() * 1.4 - 0.2; let s = rnd() * 3.0 + 2.0; let delay = rnd() * 180.0;
        v.push(Speck { a, r, vx, vy, s, delay });
    }
    v
}

// What the cleaning window's display shows; it's all drawn by hand from this.
struct Prog {
    label: String,                          // while cleaning: which item
    totals: (u64, u64, u64, u64),           // freed, to the Trash, files, skipped
    frac: f64,                              // the share done
    shown: f64,                             // what the bar shows: it glides toward frac (below 0: nothing yet)
    done: bool,
    stopped: bool,
    ms: f64,                                // where the finish is (below 0: cleaning, or stopped)
    details: bool,                          // "What was cleaned": the log back under a small display
    freed: Vec<(String, u64, u64, bool)>,   // per item: name, freed, to the Trash, cleaned by the password helper
    disk: String,                           // where the home folder is
    total: u64,
    free_before: u64,
    free_after: Option<u64>,
    started: std::time::Instant,
    secs: Option<f64>,
    specks: Vec<Speck>,
}

fn mmss(secs: f64) -> String { let s = secs.max(0.0) as u64; format!("{}:{:02}", s / 60, s % 60) }

// the mount point that holds a path ("/" or "/home", say), as the disk's name
fn mount_of(p: &std::path::Path) -> String {
    let mounts = std::fs::read_to_string("/proc/self/mounts").unwrap_or_default();
    let mut best = String::from("/");
    for l in mounts.lines() {
        let f: Vec<&str> = l.split_whitespace().collect();
        if f.len() < 2 { continue; }
        let mp = f[1].replace("\\040", " ");
        if mp.len() > best.len() && p.starts_with(&mp) { best = mp; }
    }
    best
}

// one line of the result list: the left text cut short with "..." so the right text always fits
fn slip_row(cr: &gtk::cairo::Context, lay: &gtk::pango::Layout, pad: f64, rx: f64, y: f64, left: &str, right: &str) {
    lay.set_width(-1);
    lay.set_ellipsize(gtk::pango::EllipsizeMode::None);
    lay.set_text(right);
    let (rw, _) = lay.pixel_size();
    cr.move_to(rx - rw as f64, y);
    pangocairo::functions::show_layout(cr, lay);
    lay.set_width(((rx - pad - rw as f64 - 12.0).max(10.0) * SC) as i32);
    lay.set_ellipsize(gtk::pango::EllipsizeMode::End);
    lay.set_text(left);
    cr.move_to(pad, y);
    pangocairo::functions::show_layout(cr, lay);
}

fn draw_prog(da: &gtk::DrawingArea, cr: &gtk::cairo::Context, w: f64, h: f64, p: &Prog) {
    use crate::theme::{DISPLAY_INK, DISPLAY_MUTED};
    use std::f64::consts::PI;
    let pad = 20.0;
    round_rect(cr, 0.0, 0.0, w, h, 7.0);
    crate::theme::rgb(cr, crate::theme::pal().screen);
    let _ = cr.fill();
    let ms = p.ms;
    let fin = ms >= 0.0;
    let beat = fin && ms >= DONE_AT;
    let (bytes, trashed, files, skipped) = p.totals;
    let bin_only = bytes == 0 && trashed > 0;
    let total = if bin_only { trashed } else { bytes };

    // the label: what's happening, then the plain result
    let text = if !p.done { p.label.clone() }
               else if p.stopped { "Stopped  \u{00B7}  nothing else will be cleaned".to_string() }
               else if !beat { "Finishing".to_string() }
               else if bin_only { "Done  \u{00B7}  moved to the Trash".to_string() }
               else { "Done  \u{00B7}  everything you ticked is cleaned".to_string() };
    // the top line sits 16 px from the display's top edge, on one baseline whether it says "DONE" or not; every left
    // edge (the line, the number, the bar, the list) is the ink of the first glyph, on pad
    let top_y = 16.0;
    let ll = layout(da, "Schibsted Grotesk Semi-Bold 10");
    let dl = layout(da, "Big Shoulders Display Black 17");
    dl.set_text("DONE");
    let (dw, dh) = dl.pixel_size();
    let line_y = top_y + (dl.baseline() - ll.baseline()) as f64 / SC;
    if beat {   // done: "DONE" in the display type, then the plain result, small and muted
        rgba(cr, DISPLAY_INK, 1.0);
        cr.move_to(pad - dl.pixel_extents().0.x() as f64, top_y);
        pangocairo::functions::show_layout(cr, &dl);
        ll.set_text(&text.replace("Done  \u{00B7}  ", "\u{00B7}  "));
        ll.set_width(((w - 2.0 * pad - dw as f64 - 8.0).max(10.0) * SC) as i32);
        ll.set_ellipsize(gtk::pango::EllipsizeMode::End);
        rgba(cr, DISPLAY_MUTED, 1.0);
        cr.move_to(pad + dw as f64 + 6.0, line_y);
        pangocairo::functions::show_layout(cr, &ll);
    } else {
        ll.set_width(((w - 2.0 * pad).max(10.0) * SC) as i32);
        ll.set_ellipsize(gtk::pango::EllipsizeMode::End);
        rgba(cr, 0xB4B1AA, 1.0);
        show_at(cr, &ll, &text, pad, line_y);
    }

    // the big number: what this clean freed (it stays put)
    let num_y = top_y + dh as f64 + 2.0;
    let (num, unit) = split(total);
    let bl = layout(da, "Big Shoulders Display Black 40");
    bl.set_text(&num);
    let (nw, _) = bl.pixel_size();
    let ink_x = bl.pixel_extents().0.x() as f64;
    let ul = layout(da, "Big Shoulders Display Black 17");
    ul.set_text(&format!("{} {}", unit, if bin_only { "TO TRASH" } else { "FREED" }));
    rgba(cr, ORANGE, 1.0);
    cr.move_to(pad - ink_x, num_y);
    pangocairo::functions::show_layout(cr, &bl);
    cr.move_to(pad - ink_x + nw as f64 + 8.0, num_y + (bl.baseline() - ul.baseline()) as f64 / SC);
    pangocairo::functions::show_layout(cr, &ul);

    let ring = fin && !p.details;
    if !ring {   // files, skipped and time at the right, as while cleaning
        let secs = p.secs.unwrap_or_else(|| p.started.elapsed().as_secs_f64());
        let sl = layout(da, "Schibsted Grotesk Semi-Bold 10.5");
        sl.set_text(&format!("{} files  \u{00B7}  {} skipped  \u{00B7}  {}", files, skipped, mmss(secs)));
        let (sw, _) = sl.pixel_size();
        rgba(cr, DISPLAY_INK, 1.0);
        cr.move_to(w - pad - sw as f64, num_y + 12.0);
        pangocairo::functions::show_layout(cr, &sl);
        let second = if trashed > 0 && !bin_only { format!("+ {} moved to the Trash", fmt(trashed)) }
                     else if bin_only && p.done { "Empty the Trash to get this space back".to_string() }
                     else { String::new() };
        if !second.is_empty() {
            let ml = layout(da, "Schibsted Grotesk 10");
            ml.set_text(&second);
            let (mw, _) = ml.pixel_size();
            rgba(cr, DISPLAY_MUTED, 1.0);
            cr.move_to(w - pad - mw as f64, num_y + 36.0);
            pangocairo::functions::show_layout(cr, &ml);
        }
    }

    // the bar while cleaning; at the end it fades and gives way to the drive, drawn as a ring
    let (bx, bw, by, bh) = (pad, w - 2.0 * pad, 118.0, 12.0);
    if !ring || ms < RING_AT + 200.0 {
        let f = if p.done && !p.stopped { 1.0 } else { p.shown.clamp(0.0, 1.0) };
        let ba = if ring { 1.0 - ease(ms / (RING_AT + 200.0)) } else { 1.0 };
        rgba(cr, TRACK, ba);
        cr.rectangle(bx, by, bw, bh);
        let _ = cr.fill();
        rgba(cr, ORANGE, ba);
        cr.rectangle(bx, by, bw * f, bh);
        let _ = cr.fill();
    }
    if !ring { return; }

    // the drive, measured before and after the clean (never a made-up number)
    let known = p.total > 0 && p.free_after.is_some();
    let t = p.total.max(1) as f64;
    let fa = p.free_after.unwrap_or(0) as f64;
    let fb = p.free_before as f64;
    let mut used_after = if known { (1.0 - fa / t).clamp(0.0, 1.0) } else { 0.0 };
    let user_freed: u64 = p.freed.iter().filter(|f| !f.3).map(|f| f.1).sum();   // the helper cleans system folders
    let mut slice = if known && !bin_only && user_freed > 0 { ((fa - fb) / t).max(0.0) } else { 0.0 };
    if slice > 0.0 && slice * 360.0 < 5.0 { slice = if fa - fb >= 1048576.0 { 5.0 / 360.0 } else { 0.0 }; }   // a small clean still shows; the numbers stay exact
    slice = slice.min(1.0);
    if used_after + slice > 1.0 { used_after = 1.0 - slice; }
    // centred while it plays; it slides to the right when the list arrives
    let (r, lw) = (104.0, 26.0);
    let cx_end = w - pad - r - lw / 2.0 - 40.0;
    let cx_mid = w / 2.0;
    let cx = cx_mid + (cx_end - cx_mid) * ease((ms - LIST_AT) / 380.0);
    let cy = 214.0;
    let sweep = ease((ms - RING_AT) / RING_MS);                                                 // the ring draws itself round
    let lift = if slice > 0.0 { ease((ms - LIFT_AT) / LIFT_MS) } else { 0.0 };                   // the freed wedge lifts out...
    let crumble = if slice > 0.0 { ((ms - CRUMBLE_AT) / CRUMBLE_MS).clamp(0.0, 1.0) } else { 1.0 };   // ...turns to dust...
    let close = if slice > 0.0 { ease((ms - CLOSE_AT) / CLOSE_MS) } else { 1.0 };                // ...and free space grows into the gap
    let gap = slice * (1.0 - close);
    let top = -PI / 2.0;
    let ang = |f: f64| top + 2.0 * PI * f;
    cr.set_line_width(lw);
    // free space: from the end of the gap round to the top
    let free_ext = 1.0 - used_after - gap;
    if free_ext > 0.0008 {
        rgba(cr, TRACK, (sweep * 2.0).min(1.0));
        cr.new_path();
        cr.arc(cx, cy, r, ang(used_after + gap), ang(1.0));
        let _ = cr.stroke();
    }
    // used space, sweeping round; the freed wedge sits at its end in orange until it lifts out
    let drawn = (used_after + slice) * sweep;
    let used = drawn.min(used_after);
    if used > 0.0008 {
        rgba(cr, USED, 1.0);
        cr.new_path();
        cr.arc(cx, cy, r, top, ang(used));
        let _ = cr.stroke();
    }
    if slice > 0.0 && crumble < 1.0 && drawn > used_after {
        let ext = slice.min(drawn - used_after);
        let (a0, a1) = (ang(used_after), ang(used_after + ext));
        let mid = (a0 + a1) / 2.0;
        let off = 16.0 * lift;
        let wa = 1.0 - ease(crumble * 1.4);
        if ext > 0.0008 && wa > 0.0 {
            rgba(cr, ORANGE, wa);
            cr.new_path();
            cr.arc(cx + mid.cos() * off, cy + mid.sin() * off, r, a0, a1);
            let _ = cr.stroke();
        }
    }
    if slice > 0.0 && ms >= CRUMBLE_AT {   // the wedge turns to dust and falls away
        let start = ang(used_after);
        for sp in &p.specks {
            let tt = (ms - CRUMBLE_AT - sp.delay) / 1000.0;
            if tt <= 0.0 { continue; }
            let pa = 1.0 - tt / 0.75;
            if pa <= 0.0 { continue; }
            let an = start + 2.0 * PI * slice * sp.a;
            let rr = r + sp.r * lw;
            let (mx, my) = (an.cos(), an.sin());
            let px = cx + mx * (rr + 16.0) + mx * sp.vx * 90.0 * tt;
            let py = cy + my * (rr + 16.0) + sp.vy * 90.0 * tt + 260.0 * tt * tt;
            rgba(cr, ORANGE, pa);
            cr.rectangle(px, py, sp.s, sp.s);
            let _ = cr.fill();
        }
    }
    if slice > 0.0 && close > 0.0 && slice - gap > 0.0008 {   // what you gained stays marked: an orange edge on the outer rim
        rgba(cr, ORANGE, 1.0);
        cr.set_line_width(4.0);
        cr.new_path();
        cr.arc(cx, cy, r + lw / 2.0 - 2.0, ang(used_after + gap), ang(used_after + slice));
        let _ = cr.stroke();
    }
    if beat {   // done: the whole ring flashes orange once and settles
        let pt = (ms - DONE_AT) / PULSE_MS;
        if pt < 1.0 {
            rgba(cr, ORANGE, 150.0 / 255.0 * (1.0 - pt) * (1.0 - pt));
            cr.set_line_width(lw + 2.0);
            cr.new_path();
            cr.arc(cx, cy, r, 0.0, 2.0 * PI);
            let _ = cr.stroke();
        }
    }
    if known && sweep > 0.0 {   // in the middle: free space, counting up as the ring closes, and what you gained
        let ca = (sweep * 1.5).min(1.0);
        let shown = (fb + (fa - fb) * close).max(0.0) as u64;
        let fl = layout(da, "Big Shoulders Display Black 22");
        fl.set_text(&fmt(shown));
        let (fw, _) = fl.pixel_size();
        rgba(cr, DISPLAY_INK, ca);
        cr.move_to(cx - fw as f64 / 2.0, cy - 34.0);
        pangocairo::functions::show_layout(cr, &fl);
        let sl = layout(da, "Schibsted Grotesk 10");
        sl.set_text(&format!("free of {} on {}", fmt(p.total), p.disk));
        let (sw, _) = sl.pixel_size();
        rgba(cr, DISPLAY_MUTED, ca);
        cr.move_to(cx - sw as f64 / 2.0, cy + 2.0);
        pangocairo::functions::show_layout(cr, &sl);
        if fa > fb && close > 0.0 {
            let gl = layout(da, "IBM Plex Mono Semi-Bold 10");
            gl.set_text(&format!("+ {}", fmt((fa - fb) as u64)));
            let (gw, _) = gl.pixel_size();
            rgba(cr, ORANGE, close);
            cr.move_to(cx - gw as f64 / 2.0, cy + 22.0);
            pangocairo::functions::show_layout(cr, &gl);
        }
    }
    if ms < LIST_AT { return; }

    // the list of what went, at the left: the biggest items, the rest, and the total at the bottom like on a slip
    let a = ease((ms - LIST_AT) / LIST_MS);
    let mut items: Vec<&(String, u64, u64, bool)> = p.freed.iter().filter(|f| f.1 + f.2 > 0).collect();
    items.sort_by(|x, y| (y.1 + y.2).cmp(&(x.1 + x.2)));
    let lh = 22.0;
    let rx = w - pad - 2.0 * r - lw - 80.0;
    let mono = layout(da, "IBM Plex Mono 9.5");
    let mono_b = layout(da, "IBM Plex Mono Semi-Bold 9.5");
    let mut y = by + 6.0;
    let show = if items.len() > 5 { 4 } else { items.len() };
    rgba(cr, DISPLAY_INK, a);
    for it in items.iter().take(show) {
        slip_row(cr, &mono, pad, rx, y, &it.0, &format!("{}{}", fmt(it.1 + it.2), if it.2 > 0 && it.1 == 0 { "  trash" } else { "" }));
        y += lh;
    }
    rgba(cr, DISPLAY_MUTED, a);
    if items.len() > show {
        let rest: u64 = items.iter().skip(show).map(|f| f.1 + f.2).sum();
        slip_row(cr, &mono, pad, rx, y, &format!("+ {} more", items.len() - show), &fmt(rest));
        y += lh;
    }
    if items.is_empty() {
        slip_row(cr, &mono, pad, rx, y, "Nothing needed cleaning", "");
        y += lh;
    }
    let after = if trashed > 0 { 3.0 } else { 2.0 };
    y = (y + 6.0).max(h - 18.0 - after * lh - 10.0);
    rgba(cr, TRACK, a);
    cr.set_line_width(1.0);
    cr.set_dash(&[1.0, 2.0], 0.0);
    cr.new_path();
    cr.move_to(pad, y - 2.5);
    cr.line_to(rx, y - 2.5);
    let _ = cr.stroke();
    cr.set_dash(&[], 0.0);
    y += 4.0;
    rgba(cr, ORANGE, a);
    slip_row(cr, &mono_b, pad, rx, y, if bin_only { "MOVED TO THE TRASH" } else { "FREED" }, &fmt(total));
    y += lh;
    rgba(cr, DISPLAY_MUTED, a);
    if trashed > 0 && !bin_only {
        slip_row(cr, &mono, pad, rx, y, "+ moved to the Trash (empty it to get this back)", &fmt(trashed));
        y += lh;
    }
    if bin_only {
        slip_row(cr, &mono, pad, rx, y, "Empty the Trash to get this space back", "");
        y += lh;
    }
    let secs = p.secs.unwrap_or(0.0).ceil().max(1.0);
    let kept = if skipped > 0 { format!("  \u{00B7}  {} kept (in use or too new)", skipped) } else { String::new() };
    slip_row(cr, &mono, pad, rx, y, &format!("{} file{}{}", files, if files == 1 { "" } else { "s" }, kept), &mmss(secs));
}

// The cleaning window: it doesn't block the main window, and it can be minimised while it works.
fn start_clean(u: &Rc<Ui>, sel: Vec<Cat>, to_trash: bool) {
    u.st.borrow_mut().cleaning = true;
    refresh_top(u);
    let expected: u64 = sel.iter().map(|c| c.bytes).sum::<u64>().max(1);
    let w = gtk::Window::builder().transient_for(&u.win).title("Space Hunter \u{00B7} cleaning").default_width(780).default_height(560).build();
    let v = gtk::Box::new(gtk::Orientation::Vertical, 10);
    v.add_css_class("chassis");
    v.set_margin_start(18); v.set_margin_end(18); v.set_margin_top(18); v.set_margin_bottom(18);
    // the disk the home folder is on, measured now and again when the clean is done
    let home = engine::home();
    let home_s = home.to_string_lossy().to_string();
    let (free_before, disk_total) = crate::disk(&home_s);
    let p = Rc::new(RefCell::new(Prog {
        label: "Cleaning".to_string(), totals: (0, 0, 0, 0), frac: 0.0, shown: -1.0, done: false, stopped: false, ms: -1.0,
        details: false, freed: Vec::new(), disk: mount_of(&home), total: disk_total, free_before, free_after: None,
        started: std::time::Instant::now(), secs: None, specks: Vec::new(),
    }));
    // the display lies over the top of the log; at the end it grows over all of it and the log fades away
    let stage = gtk::Overlay::new();
    stage.set_vexpand(true);
    let tv = gtk::TextView::new();
    tv.set_editable(false); tv.set_cursor_visible(false); tv.add_css_class("log");
    tv.set_left_margin(12); tv.set_top_margin(12);
    let lsw = gtk::ScrolledWindow::builder().child(&tv).vexpand(true).build();
    lsw.set_margin_top(BASE_H + 10);
    stage.set_child(Some(&lsw));
    let disp = gtk::DrawingArea::new();
    disp.set_content_height(BASE_H);
    disp.set_valign(gtk::Align::Start);
    {
        let p = p.clone();
        disp.set_draw_func(move |da, cr, w, h| draw_prog(da, cr, w as f64, h as f64, &p.borrow()));
    }
    stage.add_overlay(&disp);
    v.append(&stage);
    let keys = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    let what = gtk::Button::with_label("What was cleaned");
    // its left edge on the display's, centred on the Done key
    what.add_css_class("link-btn"); what.add_css_class("flush"); what.set_valign(gtk::Align::Center); what.set_visible(false);
    let log_key = key("open log file", &[]);
    log_key.set_visible(false); log_key.set_margin_start(8);
    keys.append(&what); keys.append(&log_key);
    keys.append(&hspacer());
    let minimise = key("minimise", &[]);
    let stop = key("stop", &[]);
    keys.append(&minimise); keys.append(&stop);
    v.append(&keys);
    w.set_child(Some(&crate::sheet(v.upcast_ref::<gtk::Widget>())));
    *u.prog_win.borrow_mut() = Some(w.clone());
    let log_path = glib::user_data_dir().join("space-hunter").join("last-clean.txt");

    let cancel = Arc::new(AtomicBool::new(false));
    let done = Rc::new(Cell::new(false));
    let close_win: Rc<dyn Fn()> = {
        let (uu, ww) = (u.clone(), w.clone());
        Rc::new(move || {
            {
                let mut pw = uu.prog_win.borrow_mut();
                if pw.as_ref() == Some(&ww) { *pw = None; }
            }
            ww.close();
        })
    };
    let ww = w.clone();
    minimise.connect_clicked(move |_| ww.minimize());
    let (c2, d2, cw) = (cancel.clone(), done.clone(), close_win.clone());
    stop.connect_clicked(move |b| {
        if d2.get() { (*cw)(); return; }
        c2.store(true, Ordering::Relaxed); b.set_label("STOPPING"); b.set_sensitive(false);
    });
    {   // once the clean is done, Enter or Esc closes the window
        let kc = gtk::EventControllerKey::new();
        kc.set_propagation_phase(gtk::PropagationPhase::Capture);
        let (d2, cw) = (done.clone(), close_win.clone());
        kc.connect_key_pressed(move |_, k, _, _| {
            use gtk::gdk::Key;
            if d2.get() && (k == Key::Return || k == Key::KP_Enter || k == Key::Escape) { (*cw)(); return glib::Propagation::Stop; }
            glib::Propagation::Proceed
        });
        w.add_controller(kc);
    }
    {
        let lp = log_path.clone();
        log_key.connect_clicked(move |_| {
            let _ = gtk::gio::AppInfo::launch_default_for_uri(&gtk::gio::File::for_path(&lp).uri(), None::<&gtk::gio::AppLaunchContext>);
        });
    }
    // 0 = the cleaning layout, 1 = the display fills the window and the log has gone
    let full = Rc::new(Cell::new(0i32));
    let grow: Rc<dyn Fn(f64)> = {
        let (disp, lsw, stage, full) = (disp.clone(), lsw.clone(), stage.clone(), full.clone());
        Rc::new(move |t: f64| {
            if full.get() <= BASE_H { full.set(stage.height()); }
            let f = full.get().max(BASE_H);
            disp.set_content_height(BASE_H + ((f - BASE_H) as f64 * t).round() as i32);
            disp.set_valign(if t >= 1.0 { gtk::Align::Fill } else { gtk::Align::Start });
            lsw.set_opacity(1.0 - t);
            lsw.set_visible(t < 1.0);
        })
    };
    // "What was cleaned": the old log view (small display, the log, Open log file); and back to the summary
    let show_details: Rc<dyn Fn(bool)> = {
        let (p, disp, lsw, log_key, what, grow) = (p.clone(), disp.clone(), lsw.clone(), log_key.clone(), what.clone(), grow.clone());
        Rc::new(move |on: bool| {
            p.borrow_mut().details = on;
            let stopped = p.borrow().stopped;
            if on {
                disp.set_valign(gtk::Align::Start);
                disp.set_content_height(BASE_H);
                lsw.set_visible(true); lsw.set_opacity(1.0);
                log_key.set_visible(true);
            } else {
                (*grow)(1.0);
                log_key.set_visible(false);
            }
            what.set_label(if on && !stopped { "Back to the summary" } else { "What was cleaned" });
            if stopped { what.set_visible(false); }
            disp.queue_draw();
        })
    };
    {
        let (p, sd) = (p.clone(), show_details.clone());
        what.connect_clicked(move |_| { let on = !p.borrow().details; (*sd)(on); });
    }
    let link_shown = Rc::new(Cell::new(false));
    let set_done_key: Rc<dyn Fn()> = {   // Done closes the window; it's the one orange key once the clean is over
        let (stop, w2) = (stop.clone(), w.clone());
        Rc::new(move || {
            stop.set_label("DONE"); stop.add_css_class("primary"); stop.set_sensitive(true);
            w2.set_title(Some("Space Hunter \u{00B7} done"));
        })
    };
    // straight to the end of the finish (reduce motion, or a screen whose frame clock doesn't run)
    let finish_now: Rc<dyn Fn()> = {
        let (p, grow, what, link_shown, set_done_key, disp) = (p.clone(), grow.clone(), what.clone(), link_shown.clone(), set_done_key.clone(), disp.clone());
        Rc::new(move || {
            let (ms, details) = { let pb = p.borrow(); (pb.ms, pb.details) };
            if ms >= FINISH_MS { return; }
            p.borrow_mut().ms = FINISH_MS;
            if !details { (*grow)(1.0); }
            link_shown.set(true);
            what.set_visible(true);
            (*set_done_key)();
            disp.queue_draw();
        })
    };
    {   // every frame: while cleaning the bar glides toward the real share (time-based, so a late frame never jumps; it
        // goes straight down if the share ever drops); once done, the finish plays by the frame clock
        let (p, grow, what, link_shown, set_done_key) = (p.clone(), grow.clone(), what.clone(), link_shown.clone(), set_done_key.clone());
        let last = Cell::new(None::<i64>);
        let fin_start = Cell::new(None::<i64>);
        let key_done = Cell::new(false);
        disp.add_tick_callback(move |da, clock| {
            let ft = clock.frame_time();
            let (is_done, stopped, ms_now, details) = { let pb = p.borrow(); (pb.done, pb.stopped, pb.ms, pb.details) };
            if !is_done {
                let dt = last.get().map(|l| (ft - l) as f64 / 1e6).unwrap_or(0.0).max(0.0);
                last.set(Some(ft));
                {
                    let mut pb = p.borrow_mut();
                    let (s, target) = (pb.shown, pb.frac);
                    pb.shown = if s < 0.0 || target < s { target } else { s + (target - s) * (1.0 - (-dt / 0.12).exp()) };
                }
                da.queue_draw();
                return glib::ControlFlow::Continue;
            }
            if stopped || ms_now >= FINISH_MS { da.queue_draw(); return glib::ControlFlow::Break; }
            let s = fin_start.get().unwrap_or(ft);
            fin_start.set(Some(s));
            let ms = ((ft - s) as f64 / 1000.0).min(FINISH_MS);
            p.borrow_mut().ms = ms;
            if !details { (*grow)(ease(ms / GROW_MS)); }
            if ms >= LIST_AT && !link_shown.get() { link_shown.set(true); what.set_visible(true); }
            if ms >= DONE_AT && !key_done.get() { key_done.set(true); (*set_done_key)(); }
            da.queue_draw();
            if ms >= FINISH_MS { glib::ControlFlow::Break } else { glib::ControlFlow::Continue }
        });
    }
    {
        let (d3, uu) = (done.clone(), u.clone());
        w.connect_close_request(move |w| {
            if !d3.get() { w.minimize(); return glib::Propagation::Stop; }   // closing mid-clean just tucks it away
            let mut pw = uu.prog_win.borrow_mut();
            if pw.as_ref() == Some(w) { *pw = None; }
            glib::Propagation::Proceed
        });
    }

    let cleaned_ids: Vec<String> = sel.iter().map(|c| c.id.clone()).collect();   // their picks are for this clean only
    let (tx, rx) = mpsc::channel::<Msg>();
    let c3 = cancel.clone();
    let home_t = home_s.clone();
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
            engine::process(c, true, to_trash, &mut cnt);   // Quick clean too; the engine keeps the Trash item and journal deleting
            bytes += cnt.bytes; trashed += cnt.trashed; files += cnt.files; skipped += cnt.skipped;
            let _ = tx.send(Msg::Totals(bytes, trashed, files, skipped));
            let _ = tx.send(Msg::Freed(c.name.clone(), cnt.bytes, cnt.trashed, false));
            let _ = tx.send(Msg::Line(format!("\u{2014} {}: freed {}{}, {} files", c.name, fmt(cnt.bytes), if cnt.trashed > 0 { format!(" + {} to the Trash", fmt(cnt.trashed)) } else { String::new() }, cnt.files)));
            let _ = tx.send(Msg::Result(format!("{}   \u{00B7}   {}{}", c.name, if cnt.bytes > 0 || cnt.trashed == 0 { format!("{} freed", fmt(cnt.bytes)) } else { String::new() },
                                                 if cnt.trashed > 0 { format!("{}{} to the Trash", if cnt.bytes > 0 { " + " } else { "" }, fmt(cnt.trashed)) } else { String::new() })));
        }
        if !admin.is_empty() && !c3.load(Ordering::Relaxed) {
            let _ = tx.send(Msg::Current("Waiting for your password to clean system folders\u{2026}".into()));
            match engine::run_system_helper(&admin) {
                Some(res) => for r in res {
                    bytes += r.bytes; files += r.files; skipped += r.skipped;
                    let _ = tx.send(Msg::Line(format!("\u{2014} {}: freed {}, {} files", r.id, fmt(r.bytes), r.files)));
                    let name = admin.iter().find(|c| c.id == r.id).map(|c| c.name.clone()).unwrap_or(r.id.clone());
                    let _ = tx.send(Msg::Freed(name.clone(), r.bytes, 0, true));
                    let _ = tx.send(Msg::Result(format!("{}   \u{00B7}   {} freed", name, fmt(r.bytes))));
                },
                None => { let _ = tx.send(Msg::Line("\u{2014} System folders skipped: the password wasn't given.".into())); }
            }
            let _ = tx.send(Msg::Totals(bytes, trashed, files, skipped));
        }
        engine::history::record(bytes, trashed);
        let (fa, ftotal) = crate::disk(&home_t);
        let _ = tx.send(Msg::Done(c3.load(Ordering::Relaxed), if ftotal > 0 { Some(fa) } else { None }));
    });

    let buf = tv.buffer();
    let uu = u.clone();
    let wt = w.clone();
    let mut lines = 0usize;
    let mut results: Vec<String> = Vec::new();
    glib::timeout_add_local(Duration::from_millis(120), move || {
        let mut finished = None;
        let mut batch = String::new();
        while let Ok(m) = rx.try_recv() {
            match m {
                Msg::Line(l) => { if lines < 3000 { batch.push_str(&l); batch.push('\n'); lines += 1; } }
                Msg::Totals(b, t, f, s) => {
                    {
                        let mut pb = p.borrow_mut();
                        pb.totals = (b, t, f, s);
                        pb.frac = ((b + t) as f64 / expected as f64).min(1.0);
                    }
                    uu.status.set_text(&format!("Cleaning  \u{00B7}  {} freed{} so far.  You can keep looking around.", fmt(b), if t > 0 { format!(" + {} to the Trash", fmt(t)) } else { String::new() }));
                }
                Msg::Current(c) => p.borrow_mut().label = c,
                Msg::Freed(n, b, t, admin) => p.borrow_mut().freed.push((n, b, t, admin)),
                Msg::Done(stopped, after) => finished = Some((stopped, after)),
                Msg::Result(r) => results.push(r),
            }
        }
        if !batch.is_empty() {
            let mut end = buf.end_iter();
            buf.insert(&mut end, &batch);
            let mark = buf.create_mark(None, &buf.end_iter(), false);
            tv.scroll_mark_onscreen(&mark);
        }
        if let Some((stopped, after)) = finished {
            done.set(true);
            if !results.is_empty() {   // what was cleaned, item by item, above every single file
                let mut start = buf.start_iter();
                buf.insert(&mut start, &format!("What was cleaned\n\n{}\n\nEvery file:\n", results.join("\n")));
                tv.scroll_to_iter(&mut buf.start_iter(), 0.0, false, 0.0, 0.0);
            }
            // the whole log as a file, for "Open log file"
            if let Some(dir) = log_path.parent() { let _ = std::fs::create_dir_all(dir); }
            let _ = std::fs::write(&log_path, buf.text(&buf.start_iter(), &buf.end_iter(), false).as_str());
            {
                let mut pb = p.borrow_mut();
                let secs = pb.started.elapsed().as_secs_f64();
                let fa = if pb.total > 0 { after } else { None };
                pb.done = true;
                pb.stopped = stopped;
                pb.secs = Some(secs);
                pb.free_after = fa;
                pb.specks = make_specks();
            }
            minimise.set_visible(false);
            if stopped {   // no finish: the log stays, so you can see where it stopped
                wt.set_title(Some("Space Hunter \u{00B7} stopped"));
                stop.set_label("CLOSE"); stop.set_sensitive(true);
                (*show_details)(true);
            } else if animations_on() {
                stop.set_label("FINISHING"); stop.set_sensitive(false);   // the finish plays in the display's frame callback
                let fin = finish_now.clone();   // and is certain to end, even if frames stop coming
                glib::timeout_add_local_once(Duration::from_millis((FINISH_MS as u64) + 1500), move || (*fin)());
            } else {   // reduce motion: straight to the result
                (*finish_now)();
            }
            disp.queue_draw();
            {
                let mut st = uu.st.borrow_mut();
                st.cleaning = false;
                for c in st.cats.iter_mut().filter(|c| cleaned_ids.contains(&c.id)) { c.picks.clear(); c.pick_bytes = None; }
            }
            uu.status.set_text("Cleaning finished. The list is being measured again.");
            if !test_run(&uu) { crate::stats::maybe_send(true); }   // the total now includes this clean (only if you said yes)
            scan(&uu, None);
            return glib::ControlFlow::Break;
        }
        glib::ControlFlow::Continue
    });
    w.present();
}

// ---- The first-run guide: the window dims, a lit cut-out sits on each real part in turn, a small card explains it.
// The cut-out glides from one part to the next. "?" plays it again.

const GUIDE: [(&str, &str); 9] = [
    ("This is what you can free", "The big number is what's ticked right now. The bar is your disk: orange is ticked, hatched is what you could still free. On the right, your disk's free space and what you've freed so far."),
    ("Three sections, by how careful to be", "Quick clean is safe and ticked for you. Big caches come back when they're needed. Deep clean may include your work. The small box ticks or unticks a whole section."),
    ("Each row is one thing to clean", "Tick a row to include it; the size is what it would free. Click the row itself to see what it is."),
    ("What it is, what goes, what happens after", "Every item explains itself. Some let you choose \u{201C}only older than\u{201D} or \u{201C}everything\u{201D}. Under What's inside, untick files to keep them, or tick just a few to clean only those."),
    ("See what fills your disk", "Storage map shows every folder by size, on any disk. Nothing there deletes anything; you can add a folder to your cleanup list."),
    ("Keep what must never go", "Protected folders are never cleaned, whatever is ticked. Space Hunter protects your own files and itself already."),
    ("Default cleanup", "Back to the first-time setup: Quick clean ticked, the usual ages, and every size measured afresh."),
    ("Clean always asks first", "You see the list of what goes and choose Permanently delete or Trash. When it's done, a ring shows the space you got back."),
    ("Your look, and this guide", "Light, dark or your computer's setting. The ? key shows this guide again whenever you want."),
];
// what each step does before it points: 0 nothing, 1 close the item panel, 2 open the biggest item of the open section
const GUIDE_DO: [u8; 9] = [0, 1, 1, 2, 1, 0, 0, 0, 0];
pub struct Guide {
    da: gtk::DrawingArea,
    step: Cell<usize>,
    from: Cell<R4>,
    to: Cell<R4>,
    spot: Cell<R4>,       // the cut-out, gliding from `from` to `to`
    veil: Cell<f64>,      // 0..1, fades in and out
    closing: Cell<bool>,
    gen: Cell<u64>,       // the newest animation; older ones stop
    next: Cell<R4>,
    skip: Cell<R4>,
    back: Cell<R4>,
}

fn guide_seen_file() -> PathBuf { engine::config_dir().join("guide-seen") }
fn guide_seen() -> bool { guide_seen_file().exists() }

fn bounds_of(u: &Ui, w: &gtk::Widget) -> Option<R4> {
    w.compute_bounds(&u.overlay)
        .map(|r| (r.x() as f64, r.y() as f64, r.width() as f64, r.height() as f64))
        .filter(|r| r.2 > 0.0 && r.3 > 0.0)
}

// where step n's cut-out goes: the real part's place in the window now, padded 6 px
fn guide_target(u: &Ui, n: usize) -> R4 {
    let r = match n {
        0 => bounds_of(u, u.display.upcast_ref()),
        1 => bounds_of(u, u.rail.upcast_ref()).map(|r| (r.0 - 10.0, r.1, r.2 + 20.0, r.3)),
        2 => {   // the first three rows, below the list's heading
            let mut top: Option<f64> = None;
            let mut bottom = 0.0;
            let mut c = u.rows.first_child();
            let mut k = 0;
            while let Some(x) = c {
                if let Some(b) = bounds_of(u, &x) { if top.is_none() { top = Some(b.1); } bottom = b.1 + b.3; }
                k += 1;
                if k >= 3 { break; }
                c = x.next_sibling();
            }
            let list = bounds_of(u, u.list_sw.upcast_ref());
            match (top, list) {
                (Some(t), Some(l)) => {
                    let (t, b) = (t.max(l.1), bottom.min(l.1 + l.3));
                    Some((l.0 - 8.0, t, l.2 + 16.0, (b - t).max(20.0)))
                }
                (_, Some(l)) => Some((l.0 - 8.0, l.1, l.2 + 16.0, l.3.min(160.0))),
                _ => None,
            }
        }
        3 => bounds_of(u, u.psw.upcast_ref()),
        4 => bounds_of(u, u.map_key.upcast_ref()),
        5 => bounds_of(u, u.protect_key.upcast_ref()),
        6 => bounds_of(u, u.reset_key.upcast_ref()),
        7 => bounds_of(u, u.clean.upcast_ref()),
        _ => bounds_of(u, u.top_keys.upcast_ref()).map(|b| {   // the guide key and the theme keys, not the room for their names
            let kw = TK_C * 4.0 + TK_GAP;
            (b.0 + b.2 - kw, b.1, kw, TK_H + 2.0)
        }),
    };
    let (w, h) = (u.overlay.width() as f64, u.overlay.height() as f64);
    let r = r.unwrap_or((w / 2.0 - 100.0, h / 2.0 - 40.0, 200.0, 80.0));
    (r.0 - 6.0, r.1 - 6.0, r.2 + 12.0, r.3 + 12.0)
}

// Run step n's action first (close or open the item panel). True when the window changed under the guide.
fn guide_enter(u: &Rc<Ui>, n: usize) -> bool {
    match GUIDE_DO.get(n).copied().unwrap_or(0) {
        1 => {
            let open = u.st.borrow().open.is_some();
            if open { close_panel(u); }
            open
        }
        2 => {
            let first = {
                let st = u.st.borrow();
                st.cats.iter().filter(|c| c.level == st.tier && (c.bytes > 0 || c.files > 0)).max_by_key(|c| c.bytes).map(|c| c.id.clone())
            };
            let open_now = u.st.borrow().open.clone();
            match first {
                Some(id) if open_now.as_deref() != Some(id.as_str()) => { open_panel(u, &id); true }
                _ => false,
            }
        }
        _ => false,
    }
}

// After an action the layout settles (the panel slides for 0.26 s): point again at where the part ended up.
fn guide_retarget_later(u: &Rc<Ui>, g: &Rc<Guide>, n: usize) {
    let (uu, gw) = (u.clone(), Rc::downgrade(g));
    glib::timeout_add_local_once(Duration::from_millis(320), move || {
        if let Some(g) = gw.upgrade() {
            if g.closing.get() || g.step.get() != n { return; }
            let t = guide_target(&uu, n);
            if t != g.to.get() { g.from.set(g.spot.get()); g.to.set(t); guide_animate(&uu, &g); }
        }
    });
}
fn start_guide(u: &Rc<Ui>, at: usize) {
    if u.guide.borrow().is_some() { return; }
    u.guide_pending.set(false);
    if u.st.borrow().open.is_some() { close_panel(u); }
    let at = at.min(GUIDE.len() - 1);
    guide_enter(u, at);
    let da = gtk::DrawingArea::new();
    da.set_hexpand(true); da.set_vexpand(true);
    da.set_focusable(true);
    let t = guide_target(u, at);
    let none = (0.0, 0.0, 0.0, 0.0);
    let g = Rc::new(Guide {
        da: da.clone(), step: Cell::new(at), from: Cell::new(t), to: Cell::new(t), spot: Cell::new(t), veil: Cell::new(0.0),
        closing: Cell::new(false), gen: Cell::new(0), next: Cell::new(none), skip: Cell::new(none), back: Cell::new(none),
    });
    {
        let gw = Rc::downgrade(&g);
        da.set_draw_func(move |da, cr, w, h| { if let Some(g) = gw.upgrade() { draw_guide(&g, da, cr, w as f64, h as f64); } });
    }
    {   // the card's Next, Skip and Back; a click anywhere else does nothing (the window under it is out of reach)
        let click = gtk::GestureClick::new();
        let (uu, gw) = (u.clone(), Rc::downgrade(&g));
        click.connect_released(move |_, _, x, y| {
            let g = match gw.upgrade() { Some(g) => g, None => return };
            if inside(g.next.get(), x, y) { guide_go(&uu, &g, g.step.get() + 1); }
            else if inside(g.skip.get(), x, y) { guide_finish(&uu, &g); }
            else if inside(g.back.get(), x, y) && g.step.get() > 0 { guide_go(&uu, &g, g.step.get() - 1); }
        });
        da.add_controller(click);
        let motion = gtk::EventControllerMotion::new();
        let (gw, dw) = (Rc::downgrade(&g), da.downgrade());
        motion.connect_motion(move |_, x, y| {
            if let (Some(g), Some(d)) = (gw.upgrade(), dw.upgrade()) {
                let hot = inside(g.next.get(), x, y) || inside(g.skip.get(), x, y) || inside(g.back.get(), x, y);
                d.set_cursor_from_name(Some(if hot { "pointer" } else { "default" }));
            }
        });
        da.add_controller(motion);
    }
    {   // the window changed size: the cut-out moves to where its part is now
        let (uu, gw) = (u.clone(), Rc::downgrade(&g));
        da.connect_resize(move |_, _, _| {
            let (uu, gw) = (uu.clone(), gw.clone());
            glib::idle_add_local_once(move || {
                if let Some(g) = gw.upgrade() {
                    if g.closing.get() { return; }
                    let t = guide_target(&uu, g.step.get());
                    g.from.set(t); g.to.set(t); g.spot.set(t);
                    g.da.queue_draw();
                }
            });
        });
    }
    u.overlay.add_overlay(&da);
    *u.guide.borrow_mut() = Some(g.clone());
    da.grab_focus();
    guide_animate(u, &g);
    guide_retarget_later(u, &g, at);   // the panel may have just closed or opened
}

fn guide_go(u: &Rc<Ui>, g: &Rc<Guide>, n: usize) {
    if g.closing.get() { return; }
    if n >= GUIDE.len() { guide_finish(u, g); return; }
    let changed = guide_enter(u, n);
    g.from.set(g.spot.get());
    g.to.set(guide_target(u, n));
    g.step.set(n);
    guide_animate(u, g);
    if changed { guide_retarget_later(u, g, n); }
}

// skipped or finished: remembered, so it never starts by itself again
fn guide_finish(u: &Rc<Ui>, g: &Rc<Guide>) {
    if g.closing.get() { return; }
    let _ = std::fs::create_dir_all(engine::config_dir());
    let _ = std::fs::write(guide_seen_file(), "seen\n");
    g.closing.set(true);
    g.from.set(g.spot.get());
    g.to.set(g.spot.get());
    guide_animate(u, g);
}

fn guide_remove(u: &Rc<Ui>) {
    let g = u.guide.borrow_mut().take();
    if let Some(g) = g { u.overlay.remove_overlay(&g.da); }
}

// the veil fades (0.18 s) and the cut-out glides (0.3 s), ease-out, by the frame clock
fn guide_animate(u: &Rc<Ui>, g: &Rc<Guide>) {
    let gen = g.gen.get() + 1;
    g.gen.set(gen);
    if !animations_on() {
        g.veil.set(if g.closing.get() { 0.0 } else { 1.0 });
        g.spot.set(g.to.get());
        g.da.queue_draw();
        if g.closing.get() { let uu = u.clone(); glib::idle_add_local_once(move || guide_remove(&uu)); }
        return;
    }
    let v0 = g.veil.get();
    let start = Cell::new(None::<i64>);
    let (uu, gw) = (u.clone(), Rc::downgrade(g));
    g.da.add_tick_callback(move |da, clock| {
        let g = match gw.upgrade() { Some(g) => g, None => return glib::ControlFlow::Break };
        if g.gen.get() != gen { return glib::ControlFlow::Break; }
        let ft = clock.frame_time();
        let s = start.get().unwrap_or(ft);
        start.set(Some(s));
        let ms = (ft - s) as f64 / 1000.0;
        let fade = ease(ms / 180.0);
        if g.closing.get() { g.veil.set(v0 * (1.0 - fade)); } else { g.veil.set(g.veil.get().max(fade)); }
        let k = ease(ms / 300.0);
        let (f, t) = (g.from.get(), g.to.get());
        g.spot.set((f.0 + (t.0 - f.0) * k, f.1 + (t.1 - f.1) * k, f.2 + (t.2 - f.2) * k, f.3 + (t.3 - f.3) * k));
        da.queue_draw();
        if g.closing.get() && ms >= 180.0 {
            let uu = uu.clone();
            glib::idle_add_local_once(move || guide_remove(&uu));
            glib::ControlFlow::Break
        } else if !g.closing.get() && ms >= 300.0 { glib::ControlFlow::Break } else { glib::ControlFlow::Continue }
    });
}

fn draw_guide(g: &Guide, da: &gtk::DrawingArea, cr: &gtk::cairo::Context, w: f64, h: f64) {
    use crate::theme::{DISPLAY_INK, DISPLAY_MUTED};
    let a = g.veil.get();
    let s = g.spot.get();
    // the veil everywhere but the lit part, and an orange line round it
    cr.set_fill_rule(gtk::cairo::FillRule::EvenOdd);
    cr.new_path();
    cr.rectangle(0.0, 0.0, w, h);
    round_rect(cr, s.0, s.1, s.2, s.3, 6.0);
    rgba(cr, 0x121212, 0.58 * a);
    let _ = cr.fill();
    cr.set_fill_rule(gtk::cairo::FillRule::Winding);
    round_rect(cr, s.0, s.1, s.2, s.3, 6.0);
    rgba(cr, ORANGE, a);
    cr.set_line_width(2.0);
    let _ = cr.stroke();

    // the card: under the lit part if there's room, else above it; always inside the window
    let step = g.step.get().min(GUIDE.len() - 1);
    let (title, body) = GUIDE[step];
    const CW: f64 = 320.0;
    let bl = layout(da, "Schibsted Grotesk 9.75");
    bl.set_width(((CW - 28.0) * SC) as i32);
    bl.set_wrap(gtk::pango::WrapMode::WordChar);
    bl.set_text(body);
    let body_h = bl.pixel_size().1 as f64;
    // the title wraps inside the card too, and pushes the body down
    let tl = layout(da, "Schibsted Grotesk Bold 12.5");
    tl.set_width(((CW - 28.0) * SC) as i32);
    tl.set_wrap(gtk::pango::WrapMode::WordChar);
    tl.set_text(title);
    balance(&tl, CW - 28.0);
    let title_h = tl.pixel_size().1 as f64;
    let body_y = 14.0 + title_h + 6.0;
    let ch = body_y + body_h + 14.0 + 28.0 + 12.0;
    let x = (s.0 + s.2 / 2.0 - CW / 2.0).min(w - CW - 16.0).max(16.0);
    let mut y = if s.1 + s.3 + 14.0 + ch < h - 12.0 { s.1 + s.3 + 14.0 } else { s.1 - 14.0 - ch };
    if y < 12.0 { y = (h - ch - 12.0).min(s.1 + 24.0); }
    for i in (1..=6).rev() {
        let i = i as f64;
        rgba(cr, 0x000000, a * 0.06);
        cr.rectangle(x - i + 3.0, y - i + 6.0, CW + 2.0 * i, ch + 2.0 * i);
        let _ = cr.fill();
    }
    round_rect(cr, x, y, CW, ch, 5.0);
    rgba(cr, crate::theme::pal().screen, a);
    let _ = cr.fill();
    round_rect(cr, x + 0.5, y + 0.5, CW - 1.0, ch - 1.0, 5.0);
    rgba(cr, 0x5A564F, a);
    cr.set_line_width(1.0);
    let _ = cr.stroke();
    rgba(cr, DISPLAY_INK, a);
    cr.move_to(x + 14.0, y + 14.0);
    pangocairo::functions::show_layout(cr, &tl);
    rgba(cr, DISPLAY_INK, a * 0.82);
    cr.move_to(x + 14.0, y + body_y);
    pangocairo::functions::show_layout(cr, &bl);
    let fy = y + ch - 12.0 - 28.0;
    // the orange key, then Skip (not on the last step) and Back (from step 2), underlined; the counter at the left.
    // All four sit on one baseline: the key's.
    let last = step == GUIDE.len() - 1;
    let kl = layout(da, "Schibsted Grotesk Semi-Bold 9");
    kl.set_text(if last { "GOT IT" } else { "NEXT" });
    let (kw, kh) = kl.pixel_size();
    let base = fy + (28.0 - kh as f64) / 2.0 + kl.baseline() as f64 / SC;
    let fl = layout(da, "IBM Plex Mono 8.5");
    fl.set_text(&format!("{} / {}", step + 1, GUIDE.len()));
    rgba(cr, DISPLAY_MUTED, a);
    cr.move_to(x + 14.0, base - fl.baseline() as f64 / SC);
    pangocairo::functions::show_layout(cr, &fl);
    let nb = (x + CW - 14.0 - kw as f64 - 22.0, fy, kw as f64 + 22.0, 28.0);
    round_rect(cr, nb.0, nb.1 + 2.0, nb.2, nb.3, 3.0);
    rgba(cr, 0xB5400F, a);
    let _ = cr.fill();
    round_rect(cr, nb.0, nb.1, nb.2, nb.3, 3.0);
    rgba(cr, ORANGE, a);
    let _ = cr.fill();
    rgba(cr, ON_ORANGE, a);
    cr.move_to(nb.0 + (nb.2 - kw as f64) / 2.0, nb.1 + (nb.3 - kh as f64) / 2.0);
    pangocairo::functions::show_layout(cr, &kl);
    g.next.set(nb);
    let none = (0.0, 0.0, 0.0, 0.0);
    g.skip.set(none);
    g.back.set(none);
    let mut lx = nb.0 - 16.0;
    let link = |text: &str, lx: f64| -> R4 {
        kl.set_text(text);
        let (tw, th) = kl.pixel_size();
        let (tx, ty) = (lx - tw as f64, fy + (28.0 - th as f64) / 2.0);
        rgba(cr, DISPLAY_INK, a);
        cr.move_to(tx, ty);
        pangocairo::functions::show_layout(cr, &kl);
        cr.set_line_width(1.0);
        cr.new_path();
        cr.move_to(tx, ty + th as f64 + 1.5);
        cr.line_to(tx + tw as f64, ty + th as f64 + 1.5);
        let _ = cr.stroke();
        (tx - 4.0, fy, tw as f64 + 8.0, 28.0)
    };
    if !last {
        let r = link("Skip", lx);
        g.skip.set(r);
        lx = r.0 - 10.0;
    }
    if step > 0 {
        let r = link("Back", lx);
        g.back.set(r);
    }
}
