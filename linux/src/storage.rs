// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - the Storage map (see what fills a disk, folder by folder, and add a folder or file to the
// cleanup list; nothing is deleted here) and the Protected folders window.

use crate::engine::{self, fmt, Cat};
use gtk::prelude::*;
use gtk::{gio, glib};
use rayon::prelude::*;
use std::cell::{Cell, RefCell};
use std::collections::HashSet;
use std::fs;
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;

pub struct Node {
    pub name: String,
    pub path: PathBuf,
    pub size: u64,
    pub files: u64,
    pub own_size: u64,
    pub own_files: u64,
    pub denied: bool,
    pub kids: Vec<Node>,
}

impl Node {
    fn new(name: String, path: PathBuf) -> Node { Node { name, path, size: 0, files: 0, own_size: 0, own_files: 0, denied: false, kids: Vec::new() } }
}

struct Ctx { dev: u64, files: AtomicU64, bytes: AtomicU64, seen: Mutex<HashSet<(u64, u64)>>, cancel: std::sync::atomic::AtomicBool }

// Read one folder, then its sub-folders in parallel (an SSD answers several reads at once). Never crosses into
// another disk, never follows links, and counts a hard-linked file once.
fn fill(n: &mut Node, ctx: &Ctx) {
    if ctx.cancel.load(Ordering::Relaxed) { return; }
    let rd = match fs::read_dir(&n.path) { Ok(r) => r, Err(_) => { n.denied = true; return; } };
    let (mut c, mut b) = (0u64, 0u64);
    for e in rd.flatten() {
        let p = e.path();
        let m = match fs::symlink_metadata(&p) { Ok(m) => m, Err(_) => continue };
        let ft = m.file_type();
        if ft.is_symlink() { continue; }
        if ft.is_dir() {
            if m.dev() != ctx.dev { continue; }   // another disk mounted here
            n.kids.push(Node::new(e.file_name().to_string_lossy().to_string(), p));
        } else if ft.is_file() {
            if m.nlink() > 1 && !ctx.seen.lock().map(|mut s| s.insert((m.dev(), m.ino()))).unwrap_or(true) { continue; }
            let s = m.blocks() * 512;
            n.own_size += s; n.own_files += 1; c += 1; b += s;
        }
    }
    ctx.files.fetch_add(c, Ordering::Relaxed);
    ctx.bytes.fetch_add(b, Ordering::Relaxed);
    n.kids.par_iter_mut().for_each(|k| fill(k, ctx));
}

fn total(n: &mut Node) {
    n.size = n.own_size; n.files = n.own_files;
    for k in n.kids.iter_mut() { total(k); n.size += k.size; n.files += k.files; n.denied |= k.denied; }
    n.kids.sort_by(|a, b| b.size.cmp(&a.size));
}

#[derive(Clone)]
pub struct Place { pub path: PathBuf, pub name: String, pub free: u64, pub total: u64, pub mount: bool }

// Your home folder first, then every disk: the system disk and anything under /media, /run/media or /mnt.
pub fn places() -> Vec<Place> {
    let h = engine::home();
    let (f, t) = crate::disk(&h.to_string_lossy());
    let mut v = vec![Place { path: h, name: "Home folder".into(), free: f, total: t, mount: false }];
    let mounts = fs::read_to_string("/proc/self/mounts").unwrap_or_default();
    for l in mounts.lines() {
        let p: Vec<&str> = l.split_whitespace().collect();
        if p.len() < 3 { continue; }
        let mp = p[1].replace("\\040", " ");
        if !["ext4", "ext3", "btrfs", "xfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "f2fs", "zfs"].contains(&p[2]) { continue; }
        if !(mp == "/" || mp == "/home" || mp.starts_with("/media/") || mp.starts_with("/run/media/") || mp.starts_with("/mnt/")) { continue; }
        if v.iter().any(|x| x.path == Path::new(&mp)) { continue; }
        let (f, t) = crate::disk(&mp);
        let name = if mp == "/" { "System disk".to_string() } else { Path::new(&mp).file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or(mp.clone()) };
        v.push(Place { path: PathBuf::from(&mp), name, free: f, total: t, mount: true });
    }
    v
}

fn label(text: &str, class: &str) -> gtk::Label { let l = gtk::Label::new(Some(text)); l.add_css_class(class); l.set_xalign(0.0); l }
fn key(text: &str, classes: &[&str]) -> gtk::Button {
    let b = gtk::Button::with_label(&text.to_uppercase());
    b.add_css_class("key"); for c in classes { b.add_css_class(c); }
    b.set_valign(gtk::Align::Center); b
}
fn clear(b: &gtk::Box) { while let Some(c) = b.first_child() { b.remove(&c); } }
fn hspacer() -> gtk::Box { let s = gtk::Box::new(gtk::Orientation::Horizontal, 0); s.set_hexpand(true); s }
fn home_short(p: &Path) -> String { let h = engine::home(); match p.strip_prefix(&h) { Ok(r) if r.as_os_str().is_empty() => "~  (your home folder)".into(), Ok(r) => format!("~/{}", r.display()), Err(_) => p.display().to_string() } }
fn split(b: u64) -> (String, String) { let s = fmt(b); match s.split_once(' ') { Some((n, u)) => (n.into(), u.into()), None => (s, String::new()) } }

#[derive(Clone)]
struct Block { name: String, size: u64, target: Option<usize>, free: bool, marked: bool, rest: bool, rect: (f64, f64, f64, f64) }

fn worst(row: &[f64], side: f64) -> f64 {
    let s: f64 = row.iter().sum(); let mx = row.iter().cloned().fold(0.0, f64::max); let mn = row.iter().cloned().fold(f64::MAX, f64::min);
    (side * side * mx / (s * s)).max(s * s / (side * side * mn))
}

// squarified layout: blocks as close to square as the space allows
fn squarify(items: &mut [Block], w: f64, h: f64) {
    let total: f64 = items.iter().map(|b| b.size as f64).sum();
    if total <= 0.0 { return; }
    let scale = w * h / total;
    let (mut rx, mut ry, mut rw, mut rh) = (0.0, 0.0, w, h);
    let mut i = 0;
    while i < items.len() {
        let side = rw.min(rh); if side <= 0.0 { break; }
        let start = i; let mut row: Vec<f64> = Vec::new(); let mut prev = f64::MAX;
        while i < items.len() {
            row.push(items[i].size as f64 * scale);
            let wv = worst(&row, side);
            if row.len() > 1 && wv > prev { row.pop(); break; }
            prev = wv; i += 1;
        }
        let sum: f64 = row.iter().sum();
        if rw >= rh {
            let cw = sum / rh; let mut y = ry;
            for (k, a) in row.iter().enumerate() { let bh = a / cw; items[start + k].rect = (rx, y, cw, bh); y += bh; }
            rx += cw; rw -= cw;
        } else {
            let ch = sum / rw; let mut x = rx;
            for (k, a) in row.iter().enumerate() { let bw = a / ch; items[start + k].rect = (x, ry, bw, ch); x += bw; }
            ry += ch; rh -= ch;
        }
    }
}

struct Sm { root: Option<Node>, path: Vec<usize>, scanning: bool, place: Place, cats: Vec<Cat>, blocks: Vec<Block>, hover: Option<usize>, spin: std::time::Instant, changed: bool, drill: usize }

pub struct Storage {
    sm: RefCell<Sm>,
    win: gtk::Window,
    head: gtk::Label, hero: gtk::Label, unit: gtk::Label, path_lbl: gtk::Label, sub: gtk::Label,
    ring: gtk::DrawingArea, pbar: gtk::DrawingArea, pfrac: Cell<f64>,
    place_lbl: gtk::Label, place_btn: gtk::Button, pop: gtk::Popover, pop_box: gtk::Box,
    back: gtk::Button, crumbs: gtk::Box, add_here: gtk::Button,
    chart: gtk::DrawingArea, caption: gtk::Label, rows: gtk::Box, hint: gtk::Label,
    ctx: RefCell<Option<Arc<Ctx>>>,
    on_change: Rc<dyn Fn()>,
}

fn current<'a>(root: &'a Node, path: &[usize]) -> &'a Node {
    let mut n = root;
    for &i in path { if i < n.kids.len() { n = &n.kids[i]; } }
    n
}

pub fn open(parent: &gtk::ApplicationWindow, cats: Vec<Cat>, on_change: Rc<dyn Fn()>, start: Option<PathBuf>, drill: usize) -> Rc<Storage> {
    let win = gtk::Window::builder().transient_for(parent).title("Space Hunter \u{00B7} Storage map").default_width(940).default_height(800).build();
    let v = gtk::Box::new(gtk::Orientation::Vertical, 0);
    v.add_css_class("chassis");
    // the display
    let disp = gtk::Box::new(gtk::Orientation::Vertical, 4);
    disp.add_css_class("display");
    disp.set_margin_start(18); disp.set_margin_end(18); disp.set_margin_top(18);
    let head = label("Storage map", "display-label");
    disp.append(&head);
    let top = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let hero = label("", "hero-mid");
    let unit = label("", "hero-unit"); unit.set_valign(gtk::Align::End); unit.set_margin_bottom(8);
    top.append(&hero); top.append(&unit); top.append(&hspacer());
    let ring = gtk::DrawingArea::new(); ring.set_content_width(60); ring.set_content_height(60);
    top.append(&ring);
    disp.append(&top);
    let pbar = gtk::DrawingArea::new(); pbar.set_content_height(14);
    disp.append(&pbar);
    let path_lbl = label("", "panel-title-dark"); path_lbl.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
    let sub = label("", "beside-muted");
    disp.append(&path_lbl); disp.append(&sub);
    v.append(&disp);
    // the place key and Open in Files
    let keys = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    keys.set_margin_start(18); keys.set_margin_end(18); keys.set_margin_top(12); keys.set_margin_bottom(6);
    let place_btn = gtk::Button::new(); place_btn.add_css_class("key"); place_btn.add_css_class("drive");
    let pl = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    let place_lbl = label("", "drive-text"); place_lbl.set_hexpand(true);
    pl.append(&place_lbl); pl.append(&label("\u{25BE}", "drive-text"));
    place_btn.set_child(Some(&pl)); place_btn.set_size_request(320, -1);
    keys.append(&place_btn);
    let open_files = key("open in files", &[]);   // sits in the path bar, beside the folder it opens
    v.append(&keys);
    let pop = gtk::Popover::new(); pop.set_parent(&place_btn); pop.set_has_arrow(false);
    let pop_box = gtk::Box::new(gtk::Orientation::Vertical, 2);
    pop.set_child(Some(&pop_box));
    // the path bar: Back, the trail, and "+ Add this folder"
    let pb = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    pb.set_margin_start(18); pb.set_margin_end(18); pb.set_margin_top(6); pb.set_margin_bottom(6);
    let back = key("\u{2190}  back", &[]);
    let crumbs = gtk::Box::new(gtk::Orientation::Horizontal, 2); crumbs.set_hexpand(true);
    let add_here = key("+ add this folder", &[]);
    pb.append(&back); pb.append(&crumbs); pb.append(&add_here); pb.append(&open_files);
    v.append(&pb);
    // the block chart
    let chart = gtk::DrawingArea::new(); chart.set_content_height(230);
    chart.set_margin_start(18); chart.set_margin_end(18);
    v.append(&chart);
    let caption = label("", "chart-caption"); caption.set_margin_start(20); caption.set_margin_top(4);
    caption.set_ellipsize(gtk::pango::EllipsizeMode::End);
    v.append(&caption);
    // the list: folders, then the files in this folder
    let rows = gtk::Box::new(gtk::Orientation::Vertical, 0);
    rows.set_margin_start(18); rows.set_margin_end(18);
    let sw = gtk::ScrolledWindow::builder().child(&rows).vexpand(true).hscrollbar_policy(gtk::PolicyType::Never).build();
    sw.set_margin_top(6);
    v.append(&sw);
    let hint = label("\u{201C}Add to list\u{201D} puts a folder or file in Deep clean, unticked. Nothing here deletes anything.", "status");
    v.append(&hint);
    win.set_child(Some(&crate::sheet(v.upcast_ref::<gtk::Widget>())));

    let ps = places();
    let place = match &start { Some(p) => ps.iter().find(|x| &x.path == p).cloned().unwrap_or(Place { path: p.clone(), name: home_short(p), free: 0, total: 0, mount: false }), None => ps[0].clone() };
    let s = Rc::new(Storage {
        sm: RefCell::new(Sm { root: None, path: Vec::new(), scanning: false, place, cats, blocks: Vec::new(), hover: None, spin: std::time::Instant::now(), changed: false, drill }),
        win, head, hero, unit, path_lbl, sub, ring, pbar, pfrac: Cell::new(0.0), place_lbl, place_btn, pop, pop_box,
        back, crumbs, add_here, chart, caption, rows, hint, ctx: RefCell::new(None), on_change,
    });

    {
        let st = s.clone();
        s.ring.set_draw_func(move |_, cr, w, h| {
            let sm = st.sm.borrow();
            if !sm.scanning { return; }
            let (cx, cy, r) = (w as f64 / 2.0, h as f64 / 2.0, 24.0);
            cr.set_line_width(5.0);
            cr.set_source_rgb(0.227, 0.227, 0.220); cr.arc(cx, cy, r, 0.0, 2.0 * std::f64::consts::PI); let _ = cr.stroke();
            // motion follows the clock, not a count of frames, so a late frame never jumps
            let a = (sm.spin.elapsed().as_secs_f64() * 300f64.to_radians()) % (2.0 * std::f64::consts::PI);
            cr.set_line_cap(gtk::cairo::LineCap::Round);
            cr.set_source_rgb(1.0, 0.357, 0.122); cr.arc(cx, cy, r, a, a + 1.9); let _ = cr.stroke();
        });
        let st = s.clone();
        s.pbar.set_draw_func(move |_, cr, w, h| {
            let (w, h) = (w as f64, h as f64);
            cr.set_source_rgb(0.227, 0.227, 0.220); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill();
            cr.set_source_rgb(1.0, 0.357, 0.122);
            let f = st.pfrac.get();
            if f >= 0.0 { cr.rectangle(0.0, 0.0, w * f, h); }
            else { let seg = w * 0.22; let x = ((st.sm.borrow().spin.elapsed().as_secs_f64() * 150.0) % (w + seg)) - seg; cr.rectangle(x.max(0.0), 0.0, seg.min(w - x.max(0.0)), h); }   // total unknown: a moving block
            let _ = cr.fill();
        });
        let st = s.clone();
        s.chart.set_draw_func(move |_, cr, w, h| draw_chart(&st, cr, w as f64, h as f64));
        let motion = gtk::EventControllerMotion::new();
        let st = s.clone();
        motion.connect_motion(move |_, x, y| {
            let hit = st.sm.borrow().blocks.iter().position(|b| x >= b.rect.0 && x < b.rect.0 + b.rect.2 && y >= b.rect.1 && y < b.rect.1 + b.rect.3);
            if hit != st.sm.borrow().hover { st.sm.borrow_mut().hover = hit; st.chart.queue_draw(); caption_text(&st); }
        });
        let st = s.clone();
        motion.connect_leave(move |_| { st.sm.borrow_mut().hover = None; st.chart.queue_draw(); caption_text(&st); });
        s.chart.add_controller(motion);
        let click = gtk::GestureClick::new();
        let st = s.clone();
        click.connect_released(move |_, _, x, y| {
            let target = st.sm.borrow().blocks.iter().find(|b| x >= b.rect.0 && x < b.rect.0 + b.rect.2 && y >= b.rect.1 && y < b.rect.1 + b.rect.3).and_then(|b| b.target);
            if let Some(i) = target { go_into(&st, i); }
        });
        s.chart.add_controller(click);
        let st = s.clone();
        s.back.connect_clicked(move |_| { st.sm.borrow_mut().path.pop(); show(&st); });
        let st = s.clone();
        s.add_here.connect_clicked(move |_| {
            let p = { let sm = st.sm.borrow(); sm.root.as_ref().map(|r| current(r, &sm.path).path.clone()) };
            if let Some(p) = p { toggle_add(&st, &p); }
        });
        let st = s.clone();
        open_files.connect_clicked(move |_| {
            let p = { let sm = st.sm.borrow(); sm.root.as_ref().map(|r| current(r, &sm.path).path.clone()) };
            if let Some(p) = p { let _ = gio::AppInfo::launch_default_for_uri(&gio::File::for_path(&p).uri(), None::<&gio::AppLaunchContext>); }
        });
        let st = s.clone();
        s.place_btn.connect_clicked(move |_| {
            if st.sm.borrow().scanning { return; }
            clear(&st.pop_box);
            for p in places() {
                let b = gtk::Button::new(); b.add_css_class("place-row");
                let r = gtk::Box::new(gtk::Orientation::Horizontal, 12);
                r.append(&label(&p.name, "row-name"));
                r.append(&label(&format!("{}  \u{00B7}  {} free of {}", home_short(&p.path), fmt(p.free), fmt(p.total)), "row-sub"));
                b.set_child(Some(&r));
                let (st2, p2) = (st.clone(), p.clone());
                b.connect_clicked(move |_| { st2.pop.popdown(); st2.sm.borrow_mut().place = p2.clone(); scan(&st2); });
                st.pop_box.append(&b);
            }
            st.pop.popup();
        });
        let st = s.clone();
        s.win.connect_close_request(move |_| {
            st.pop.unparent();
            if let Some(c) = st.ctx.borrow().as_ref() { c.cancel.store(true, Ordering::Relaxed); }   // closing the map stops its scan
            if st.sm.borrow().changed { st.on_change.as_ref()(); }
            glib::Propagation::Proceed
        });
        // Backspace or Alt+Left go up a level, as in a file manager
        let keyc = gtk::EventControllerKey::new();
        let st = s.clone();
        keyc.connect_key_pressed(move |_, k, _, m| {
            if k == gtk::gdk::Key::BackSpace || (k == gtk::gdk::Key::Left && m.contains(gtk::gdk::ModifierType::ALT_MASK)) {
                if !st.sm.borrow().path.is_empty() { st.sm.borrow_mut().path.pop(); show(&st); return glib::Propagation::Stop; }
            }
            glib::Propagation::Proceed
        });
        s.win.add_controller(keyc);
    }
    s.win.present();
    scan(&s);
    s
}

fn scan(s: &Rc<Storage>) {
    let place = {
        let mut sm = s.sm.borrow_mut();
        if sm.scanning { return; }
        sm.scanning = true; sm.root = None; sm.path.clear(); sm.blocks.clear();
        sm.place.clone()
    };
    let dev = fs::symlink_metadata(&place.path).map(|m| m.dev()).unwrap_or(0);
    let ctx = Arc::new(Ctx { dev, files: AtomicU64::new(0), bytes: AtomicU64::new(0), seen: Mutex::new(HashSet::new()), cancel: std::sync::atomic::AtomicBool::new(false) });
    *s.ctx.borrow_mut() = Some(ctx.clone());
    let target = if place.mount && place.total > 0 { place.total - place.free } else { 0 };
    s.place_lbl.set_text(&format!("{}  \u{00B7}  {} free", place.name, fmt(place.free)));
    s.place_btn.set_sensitive(false);
    s.pbar.set_visible(true);
    s.path_lbl.set_text(""); s.sub.set_text("");
    show(s);
    let (tx, rx) = mpsc::channel::<Node>();
    let c2 = ctx.clone();
    std::thread::spawn(move || {
        let mut root = Node::new(place.name.clone(), place.path.clone());
        fill(&mut root, &c2);
        total(&mut root);
        let _ = tx.send(root);
    });
    // the ring and the moving block are redrawn every frame while the scan runs (the numbers below change less often);
    // it stops when the scan ends or the map closes
    s.sm.borrow_mut().spin = std::time::Instant::now();
    let weak = Rc::downgrade(s);
    s.ring.add_tick_callback(move |_, _| {
        let st = match weak.upgrade() { Some(st) => st, None => return glib::ControlFlow::Break };
        if !st.sm.borrow().scanning || !st.win.is_visible() { return glib::ControlFlow::Break; }
        st.ring.queue_draw(); st.pbar.queue_draw();
        glib::ControlFlow::Continue
    });
    let st = s.clone();
    glib::timeout_add_local(Duration::from_millis(60), move || {
        let files = ctx.files.load(Ordering::Relaxed);
        let bytes = ctx.bytes.load(Ordering::Relaxed);
        st.head.set_text(&format!("Storage map  \u{00B7}  measuring {}  \u{00B7}  counting files", home_short(&st.sm.borrow().place.path)));
        st.hero.set_text(&format!("{} FILES", files)); st.unit.set_text("");
        crate::ui::align_to_glyph(&st.hero, &[st.head.upcast_ref::<gtk::Widget>(), st.pbar.upcast_ref::<gtk::Widget>(), st.path_lbl.upcast_ref::<gtk::Widget>(), st.sub.upcast_ref::<gtk::Widget>()]);
        st.pfrac.set(if target > 0 { (bytes as f64 / target as f64).min(1.0) } else { -1.0 });
        st.sub.set_text(&if target > 0 { format!("Measured {} of {} used  \u{00B7}  {}%", fmt(bytes), fmt(target), (bytes as f64 * 100.0 / target as f64).min(100.0) as u32) } else { format!("Measured {} so far", fmt(bytes)) });
        if let Ok(root) = rx.try_recv() {
            let drill = {
                let mut sm = st.sm.borrow_mut();
                sm.root = Some(root); sm.scanning = false;
                let d = sm.drill; sm.drill = 0; d
            };
            for _ in 0..drill {   // test hook: open the biggest folder a few levels down
                let ok = { let sm = st.sm.borrow(); sm.root.as_ref().map(|r| !current(r, &sm.path).kids.is_empty()).unwrap_or(false) };
                if ok { st.sm.borrow_mut().path.push(0); }
            }
            st.place_btn.set_sensitive(true);
            st.pbar.set_visible(false);
            st.ring.queue_draw();
            show(&st);
            return glib::ControlFlow::Break;
        }
        glib::ControlFlow::Continue
    });
}

fn go_into(s: &Rc<Storage>, i: usize) {
    let ok = { let sm = s.sm.borrow(); sm.root.as_ref().map(|r| i < current(r, &sm.path).kids.len()).unwrap_or(false) };
    if ok { s.sm.borrow_mut().path.push(i); show(s); }
}

fn is_added(p: &Path) -> bool { engine::custom::all().iter().any(|c| Path::new(c) == p) }

fn toggle_add(s: &Rc<Storage>, p: &Path) {
    let ps = p.to_string_lossy().to_string();
    if is_added(p) {
        engine::custom::remove(&ps);
        s.hint.set_text(&format!("Taken off the cleanup list: {}.", home_short(p)));
    } else {
        engine::custom::add(&ps);
        s.hint.set_text(&format!("Added: {}. It's on the cleanup list, in Deep clean, unticked. Press \u{201C}Added\u{201D} to undo.", home_short(p)));
    }
    s.sm.borrow_mut().changed = true;
    show(s);
}

// Everything below the display, for the folder you're in.
fn show(s: &Rc<Storage>) {
    let sm = s.sm.borrow();
    let root = match &sm.root { Some(r) => r, None => {
        s.back.set_sensitive(false); s.add_here.set_visible(false); clear(&s.crumbs); clear(&s.rows);
        s.chart.set_visible(false); s.caption.set_text("");
        let m = label("Measuring\u{2026} the map and the list appear here.", "group-note"); m.set_xalign(0.5); m.set_margin_top(60);
        s.rows.append(&m);
        return;
    } };
    let cur = current(root, &sm.path);
    s.head.set_text("Storage map  \u{00B7}  just looking, nothing is deleted here");
    let (n, u) = split(cur.size);
    s.hero.set_text(&n); s.unit.set_text(&u);
    crate::ui::align_to_glyph(&s.hero, &[s.head.upcast_ref::<gtk::Widget>(), s.pbar.upcast_ref::<gtk::Widget>(), s.path_lbl.upcast_ref::<gtk::Widget>(), s.sub.upcast_ref::<gtk::Widget>()]);
    s.path_lbl.set_text(&home_short(&cur.path));
    s.sub.set_text(&format!("{} files{}", cur.files, if cur.denied { "  \u{00B7}  some folders couldn't be read" } else { "" }));
    // the path bar
    s.back.set_sensitive(!sm.path.is_empty());
    clear(&s.crumbs);
    let mut chain: Vec<(String, usize)> = vec![(sm.place.name.clone(), 0)];
    { let mut n = root; for (depth, &i) in sm.path.iter().enumerate() { n = &n.kids[i]; chain.push((n.name.clone(), depth + 1)); } }
    let skip = if chain.len() > 6 { chain.len() - 5 } else { 1 };
    for (k, (name, depth)) in chain.iter().enumerate() {
        if k > 0 && k < skip { if k == 1 { s.crumbs.append(&label("\u{2026}  \u{203A}", "crumb-sep")); } continue; }
        let last = k == chain.len() - 1;
        let b = gtk::Button::with_label(name);
        b.add_css_class(if last { "crumb-now" } else { "crumb" });
        let (st, d) = (s.clone(), *depth);
        b.connect_clicked(move |_| { st.sm.borrow_mut().path.truncate(d); show(&st); });
        s.crumbs.append(&b);
        if !last { s.crumbs.append(&label("\u{203A}", "crumb-sep")); }
    }
    let added_here = is_added(&cur.path);
    s.add_here.set_visible(added_here || engine::can_add(&cur.path, false).is_ok());
    s.add_here.set_label(if added_here { "ADDED  \u{00B7}  REMOVE" } else { "+ ADD THIS FOLDER" });
    if added_here { s.add_here.add_css_class("on"); } else { s.add_here.remove_css_class("on"); }
    // the chart (folders only, plus "Files here" and, at the top of a disk, free space); one block says nothing
    let free = if sm.path.is_empty() && sm.place.mount { sm.place.free } else { 0 };
    let n_blocks = cur.kids.iter().filter(|k| k.size > 0).count() + (cur.own_size > 0) as usize + (free > 0) as usize;
    drop(sm);   // let go before touching widgets: GTK may call back into the map straight away
    s.chart.set_visible(n_blocks > 1);
    s.chart.queue_draw();
    caption_text(s);
    // the list
    clear(&s.rows);
    let sm = s.sm.borrow();
    let root = sm.root.as_ref().unwrap();
    let cur = current(root, &sm.path);
    let max = cur.size.max(1);
    for (i, k) in cur.kids.iter().enumerate().filter(|(_, k)| k.size > 0) {
        let sub = sub_for(&sm.cats, &k.path, Some(k.files), k.denied);
        s.rows.append(&list_row(s, &k.name, &sub, k.size, max, &k.path, Some(i)));
    }
    // the files directly in this folder, one row each (read now, only for this folder)
    let mut files: Vec<(String, PathBuf, u64)> = Vec::new();
    if let Ok(rd) = fs::read_dir(&cur.path) {
        for e in rd.flatten() {
            if let Ok(m) = fs::symlink_metadata(e.path()) {
                if m.is_file() { files.push((e.file_name().to_string_lossy().to_string(), e.path(), m.blocks() * 512)); }
            }
        }
    }
    files.sort_by(|a, b| b.2.cmp(&a.2));
    for (name, p, size) in files.iter().take(400) {
        let ext = Path::new(name).extension().map(|e| format!("{} file", e.to_string_lossy().to_uppercase())).unwrap_or("file".into());
        let sub = sub_for(&sm.cats, p, None, false);
        let sub = if sub.starts_with("Can't") || sub.starts_with("Cleaned") { sub } else { ext };
        s.rows.append(&list_row(s, name, &sub, *size, max, p, None));
    }
    if files.len() > 400 {
        let m = label(&format!("+ {} smaller files", files.len() - 400), "group-note"); m.set_margin_top(10);
        s.rows.append(&m);
    }
}

// why something can't be added, as a short calm label (the rules themselves are in engine::can_add)
fn calm(why: &str) -> &'static str {
    let w = why.to_lowercase();
    if w.contains("space hunter") { "Space Hunter's own folder" }
    else if w.contains("installed tool") || w.contains("settings") || w.contains("sign-in") { "App settings or an installed tool" }
    else if w.contains("own files") || w.contains("personal") { "Your own files" }
    else if w.contains("outside your home") { "Outside your home folder" }
    else if w.contains("protected") { "Protected" }
    else if w.contains("already") { "Already on your list" }
    else { "Can't be added" }
}

fn sub_for(cats: &[Cat], p: &Path, files: Option<u64>, denied: bool) -> String {
    let (cov, inside) = engine::coverage(p, cats);
    if let Some(c) = cov { return format!("{}{}", if inside { "Cleaned by: " } else { "Has cleanup items: " }, c); }
    if !is_added(p) { if let Err(why) = engine::can_add(p, false) { return format!("{}{}", calm(&why), files.map(|f| format!("  \u{00B7}  {} file{}", f, if f == 1 { "" } else { "s" })).unwrap_or_default()); } }
    match files { Some(f) => format!("{} file{}{}", f, if f == 1 { "" } else { "s" }, if denied { "  \u{00B7}  partly unreadable" } else { "" }), None => String::new() }
}

// One row: name and what it is (click to go inside a folder), the add key, a share bar, the size.
fn list_row(s: &Rc<Storage>, name: &str, sub: &str, size: u64, max: u64, p: &Path, kid: Option<usize>) -> gtk::Box {
    let r = gtk::Box::new(gtk::Orientation::Horizontal, 12);
    r.add_css_class("rowline");
    let nb = gtk::Button::new(); nb.add_css_class("row-btn"); nb.set_hexpand(true);
    let names = gtk::Box::new(gtk::Orientation::Vertical, 2);
    let n = label(name, "row-name"); n.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
    let sl = label(sub, "row-sub"); sl.set_ellipsize(gtk::pango::EllipsizeMode::End);
    names.append(&n); names.append(&sl);
    nb.set_child(Some(&names));
    if let Some(i) = kid { let st = s.clone(); nb.connect_clicked(move |_| go_into(&st, i)); }
    else { let path = p.to_path_buf(); nb.connect_clicked(move |_| { if let Some(d) = path.parent() { let _ = gio::AppInfo::launch_default_for_uri(&gio::File::for_path(d).uri(), None::<&gio::AppLaunchContext>); } }); }
    r.append(&nb);
    let added = is_added(p);
    let (_, inside) = engine::coverage(p, &s.sm.borrow().cats);
    let chip_box = gtk::Box::new(gtk::Orientation::Horizontal, 0); chip_box.set_size_request(170, -1); chip_box.set_valign(gtk::Align::Center);
    if added || (!inside && engine::can_add(p, false).is_ok()) {
        let cls: &[&str] = if added { &["on", "small"] } else { &["small"] };
        let chip = key(if added { "added \u{00B7} remove" } else { "+ add to list" }, cls);
        let (st, path) = (s.clone(), p.to_path_buf());
        chip.connect_clicked(move |_| toggle_add(&st, &path));
        chip_box.append(&chip);
    } else if inside {
        chip_box.append(&label("ALREADY INCLUDED", "included"));
    }
    r.append(&chip_box);
    let bar = gtk::DrawingArea::new(); bar.set_content_width(180); bar.set_content_height(8); bar.set_valign(gtk::Align::Center);
    let frac = size as f64 / max as f64;
    bar.set_draw_func(move |_, cr, w, h| {
        let pal = crate::theme::pal();
        crate::theme::rgb(cr, pal.line); cr.rectangle(0.0, 0.0, w as f64, h as f64); let _ = cr.fill();
        crate::theme::rgb(cr, pal.ink); cr.rectangle(0.0, 0.0, (w as f64 * frac).max(if frac > 0.0 { 2.0 } else { 0.0 }), h as f64); let _ = cr.fill();
    });
    r.append(&bar);
    let sizes = gtk::Box::new(gtk::Orientation::Vertical, 2); sizes.set_size_request(90, -1); sizes.set_valign(gtk::Align::Center);
    let sz = label(&fmt(size), "row-size"); sz.set_xalign(1.0);
    let pct = label(&if frac < 0.01 { "<1%".to_string() } else { format!("{:.0}%", frac * 100.0) }, "row-sub"); pct.set_xalign(1.0);
    sizes.append(&sz); sizes.append(&pct);
    r.append(&sizes);
    r.append(&label(if kid.is_some() { "\u{203A}" } else { " " }, "chevron"));
    r
}

fn caption_text(s: &Rc<Storage>) {
    let sm = s.sm.borrow();
    if sm.root.is_none() || !s.chart.is_visible() { s.caption.set_text(""); return; }
    let t = match sm.hover.and_then(|h| sm.blocks.get(h)) {
        Some(b) => format!("{}  \u{00B7}  {}{}{}", b.name, fmt(b.size), if b.marked { "  \u{00B7}  part of it is already a cleanup item" } else { "" }, if b.target.is_some() { "  \u{00B7}  click to go inside" } else { "" }),
        None => format!("Click a block to go inside it  \u{00B7}  orange corner: part of it is already a cleanup item{}", if sm.place.mount && sm.path.is_empty() { "  \u{00B7}  hatched: free space" } else { "" }),
    };
    s.caption.set_text(&t);
}

fn draw_chart(s: &Rc<Storage>, cr: &gtk::cairo::Context, w: f64, h: f64) {
    let mut sm = s.sm.borrow_mut();
    let mut blocks: Vec<Block> = Vec::new();
    if let Some(root) = &sm.root {
        let cur = current(root, &sm.path);
        let mine = engine::custom::all();
        for (i, k) in cur.kids.iter().enumerate().filter(|(_, k)| k.size > 0) {
            let marked = mine.iter().any(|c| Path::new(c) == k.path) || engine::coverage(&k.path, &sm.cats).0.is_some();
            blocks.push(Block { name: k.name.clone(), size: k.size, target: if k.kids.is_empty() { None } else { Some(i) }, free: false, marked, rest: false, rect: (0.0, 0.0, 0.0, 0.0) });
        }
        if cur.own_size > 0 { blocks.push(Block { name: "Files here".into(), size: cur.own_size, target: None, free: false, marked: false, rest: false, rect: (0.0, 0.0, 0.0, 0.0) }); }
        let free = if sm.path.is_empty() && sm.place.mount { sm.place.free } else { 0 };
        blocks.sort_by(|a, b| b.size.cmp(&a.size));
        let total: u64 = blocks.iter().map(|b| b.size).sum::<u64>() + free;
        // tiny folders become one "smaller" block, so every block stays big enough to point at
        let (mut keep, rest): (Vec<Block>, Vec<Block>) = blocks.into_iter().partition(|b| b.size as f64 >= total as f64 * 0.012);
        keep.truncate(24);
        if !rest.is_empty() {
            keep.push(Block { name: format!("{} smaller", rest.len()), size: rest.iter().map(|b| b.size).sum(), target: None, free: false, marked: rest.iter().any(|b| b.marked), rest: true, rect: (0.0, 0.0, 0.0, 0.0) });
            keep.sort_by(|a, b| b.size.cmp(&a.size));
        }
        if free > 0 { keep.push(Block { name: "Free space".into(), size: free, target: None, free: true, marked: false, rest: false, rect: (0.0, 0.0, 0.0, 0.0) }); }   // always last
        squarify(&mut keep, w, h);
        blocks = keep;
    }
    let pal = crate::theme::pal();
    crate::theme::rgb(cr, pal.chassis); cr.rectangle(0.0, 0.0, w, h); let _ = cr.fill();
    let layout = pangocairo_layout(s, cr);
    for (i, b) in blocks.iter().enumerate() {
        let (x, y, bw, bh) = (b.rect.0 + 1.5, b.rect.1 + 1.5, b.rect.2 - 3.0, b.rect.3 - 3.0);
        if bw < 2.0 || bh < 2.0 { continue; }
        let hot = sm.hover == Some(i) && b.target.is_some();
        if b.free {
            crate::theme::rgb(cr, pal.hover); cr.rectangle(x, y, bw, bh); let _ = cr.fill();
            let _ = cr.save(); cr.rectangle(x, y, bw, bh); cr.clip();
            crate::theme::rgb(cr, pal.line); cr.set_line_width(1.5);
            let mut lx = x - bh; while lx < x + bw { cr.move_to(lx, y + bh); cr.line_to(lx + bh, y); lx += 5.0; }
            let _ = cr.stroke(); let _ = cr.restore();
        } else {
            crate::theme::rgb(cr, if hot { pal.screen } else if b.rest { pal.hover } else { pal.key });   // pointed at: dark like the display, in both themes
            cr.rectangle(x, y, bw, bh); let _ = cr.fill();
        }
        if b.marked {
            let c = 14.0f64.min(bw.min(bh) / 2.0);
            cr.set_source_rgb(1.0, 0.357, 0.122);
            cr.move_to(x + bw - c, y); cr.line_to(x + bw, y); cr.line_to(x + bw, y + c); cr.close_path(); let _ = cr.fill();
        }
        if bw >= 46.0 && bh >= 34.0 {
            let _ = cr.save(); cr.rectangle(x, y, bw - if b.marked { 14.0 } else { 4.0 }, bh); cr.clip();
            crate::theme::rgb(cr, if hot { crate::theme::DISPLAY_INK } else if b.free { pal.muted } else { pal.ink });
            layout.set_text(&b.name); cr.move_to(x + 8.0, y + 6.0); pangocairo_show(cr, &layout);
            crate::theme::rgb(cr, if hot { crate::theme::DISPLAY_MUTED } else { pal.soft });
            layout.set_text(&fmt(b.size)); cr.move_to(x + 8.0, y + 24.0); pangocairo_show(cr, &layout);
            let _ = cr.restore();
        }
    }
    sm.blocks = blocks;
}

// text on the chart in the app's own type
fn pangocairo_layout(s: &Rc<Storage>, cr: &gtk::cairo::Context) -> gtk::pango::Layout {
    let ctx = s.chart.create_pango_context();
    let l = gtk::pango::Layout::new(&ctx);
    l.set_font_description(Some(&gtk::pango::FontDescription::from_string("Schibsted Grotesk Semi-Bold 9.5")));
    let _ = cr;
    l
}
fn pangocairo_show(cr: &gtk::cairo::Context, l: &gtk::pango::Layout) { pangocairo::functions::show_layout(cr, l); }

// ---- Protected folders: never cleaned, whatever is ticked ----

const BUILT_IN: [&str; 9] = [
    "Documents, Desktop, Pictures, Videos, Music, Public and Templates (in your system's language)",
    "Downloads (only old installers and archives can be cleaned, and only if you tick that item)",
    "App settings and installed tools (~/.config, ~/.local, .rustup, .cargo, .dotnet, Flatpak and Snap data): only their known caches are cleaned",
    "Your code and project folders (Projects, src, code, dev)",
    "Cloud folders (Dropbox, Nextcloud, ownCloud, OneDrive)",
    "Keys and sign-in settings (.ssh, .gnupg, .aws, .azure, .kube, .docker, password store, keyrings)",
    "AI agents' memory folders (for example Claude Code's memory)",
    "Other people's and the system's files, and anything reached through a link",
    "Space Hunter itself: the program, its settings and its history",
];

pub fn open_protect(parent: &gtk::ApplicationWindow, on_change: Rc<dyn Fn()>) -> gtk::Window {
    let win = gtk::Window::builder().transient_for(parent).title("Space Hunter \u{00B7} Protected folders").default_width(760).default_height(640).build();
    let v = gtk::Box::new(gtk::Orientation::Vertical, 8);
    v.add_css_class("chassis");
    v.set_margin_start(18); v.set_margin_end(18); v.set_margin_top(18); v.set_margin_bottom(18);
    let disp = gtk::Box::new(gtk::Orientation::Vertical, 4);
    disp.add_css_class("display");
    disp.append(&label("Protected folders  \u{00B7}  never cleaned, whatever is ticked", "display-label"));
    let hero = label("", "hero-mid");
    disp.append(&hero);
    disp.append(&label("The places Space Hunter never touches, plus any folder you add.", "beside-muted"));
    v.append(&disp);
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 12); row.set_margin_top(8);
    let add = key("add folder", &["primary-small"]);
    row.append(&add);
    row.append(&label("Anything inside a protected folder is skipped by every item, every time.", "explain"));
    v.append(&row);
    let list = gtk::Box::new(gtk::Orientation::Vertical, 4);
    let sw = gtk::ScrolledWindow::builder().child(&list).vexpand(true).hscrollbar_policy(gtk::PolicyType::Never).build();
    v.append(&sw);
    win.set_child(Some(&crate::sheet(v.upcast_ref::<gtk::Widget>())));
    let changed = Rc::new(Cell::new(false));
    let fill: Rc<RefCell<Option<Rc<dyn Fn()>>>> = Rc::new(RefCell::new(None));
    let f = {
        let (list, hero, changed, fill2) = (list.clone(), hero.clone(), changed.clone(), fill.clone());
        Rc::new(move || {
            clear(&list);
            let mine = engine::keep::all();
            hero.set_text(&format!("{} BUILT IN + {} YOURS", BUILT_IN.len(), mine.len()));
            let h = label("Your protected folders", "section-title"); h.set_margin_top(10); list.append(&h);
            if mine.is_empty() { list.append(&label("None yet. Press Add folder above.", "group-note")); }
            for p in mine {
                let r = gtk::Box::new(gtk::Orientation::Horizontal, 10);
                let rm = key("remove", &["small"]);
                let (p2, c2, f2) = (p.clone(), changed.clone(), fill2.clone());
                rm.connect_clicked(move |_| { engine::keep::remove(&p2); c2.set(true); let f = f2.borrow().clone(); if let Some(f) = f { f.as_ref()(); } });
                r.append(&rm);
                let l = label(&home_short(Path::new(&p)), "panel-path"); l.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
                r.append(&l);
                list.append(&r);
            }
            let h = label("Built in", "section-title"); h.set_margin_top(14); list.append(&h);
            for t in BUILT_IN { let l = label(t, "explain"); l.set_wrap(true); list.append(&l); }
        }) as Rc<dyn Fn()>
    };
    *fill.borrow_mut() = Some(f.clone());
    f.as_ref()();
    let (w2, c2, f2) = (win.clone(), changed.clone(), f.clone());
    add.connect_clicked(move |_| {
        #[allow(deprecated)]
        let dlg = gtk::FileChooserNative::new(Some("Choose a folder to protect"), Some(&w2), gtk::FileChooserAction::SelectFolder, Some("Protect"), Some("Cancel"));
        let (c3, f3, keep_alive) = (c2.clone(), f2.clone(), dlg.clone());   // the dialog stays alive until it answers
        #[allow(deprecated)]
        dlg.connect_response(move |d, resp| {
            let _ = &keep_alive;
            if resp == gtk::ResponseType::Accept {
                if let Some(p) = d.file().and_then(|f| f.path()) { engine::keep::add(&p.to_string_lossy()); c3.set(true); f3.as_ref()(); }
            }
            d.destroy();
        });
        #[allow(deprecated)]
        dlg.show();
    });
    let c4 = changed.clone();
    win.connect_close_request(move |_| { if c4.get() { on_change.as_ref()(); } glib::Propagation::Proceed });
    win.present();
    win
}
