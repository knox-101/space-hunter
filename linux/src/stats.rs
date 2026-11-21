// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - public totals, only if you say yes (asked once). Sent: a random ID made on this computer, the
// version, "linux", and the total space freed and number of cleans so far. Never file names, paths, or anything about
// what was cleaned. At most once every 20 hours, and after a clean. The choice, the ID and the last send time live in
// ~/.config/space-hunter/share, next to the other settings. Sent with curl, the same way the updater talks to GitHub.

use std::path::PathBuf;
use std::process::Command;
use std::sync::{Mutex, MutexGuard};
use std::time::{SystemTime, UNIX_EPOCH};

pub const ENDPOINT: &str = "https://space-hunter-stats.knox-101.workers.dev";   // the stats/ worker
pub const EXPLAINED: &str = "https://github.com/knox-101/space-hunter#public-totals";

struct Share { loaded: bool, choice: String, id: String, last: u64 }   // last: unix seconds of the last good send
static SHARE: Mutex<Share> = Mutex::new(Share { loaded: false, choice: String::new(), id: String::new(), last: 0 });

fn path() -> PathBuf { crate::engine::config_dir().join("share") }
fn now() -> u64 { SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0) }

fn lock() -> MutexGuard<'static, Share> {
    let mut g = SHARE.lock().unwrap_or_else(|e| e.into_inner());
    if !g.loaded {
        g.loaded = true;
        let text = std::fs::read_to_string(path()).unwrap_or_default();
        let l: Vec<&str> = text.lines().map(|x| x.trim()).collect();
        if let Some(c) = l.first() { g.choice = c.to_string(); }
        if let Some(i) = l.get(1) { g.id = i.to_string(); }
        if let Some(t) = l.get(2) { g.last = t.parse::<u64>().unwrap_or(0); }
    }
    g
}

fn save(s: &Share) {
    let _ = std::fs::create_dir_all(crate::engine::config_dir());
    let _ = std::fs::write(path(), format!("{}\n{}\n{}\n", s.choice, s.id, s.last));
}

// A random UUID v4 (8-4-4-4-12, lowercase hex), from the system's randomness.
fn uuid() -> String {
    use std::io::Read;
    let mut b = [0u8; 16];
    let ok = std::fs::File::open("/dev/urandom").and_then(|mut f| f.read_exact(&mut b)).is_ok();
    if !ok {   // practically never: mix the clock and the process number instead
        use sha2::{Digest, Sha256};
        let t = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
        let h = Sha256::digest(format!("{}-{}", t, std::process::id()).as_bytes());
        b.copy_from_slice(&h[..16]);
    }
    b[6] = (b[6] & 0x0f) | 0x40;   // version 4
    b[8] = (b[8] & 0x3f) | 0x80;   // RFC 4122 variant
    let h: Vec<String> = b.iter().map(|x| format!("{:02x}", x)).collect();
    format!("{}-{}-{}-{}-{}", h[0..4].concat(), h[4..6].concat(), h[6..8].concat(), h[8..10].concat(), h[10..16].concat())
}

pub fn asked() -> bool { let s = lock(); s.choice == "yes" || s.choice == "no" }
pub fn is_on() -> bool { lock().choice == "yes" }

pub fn set(on: bool) {
    let mut s = lock();
    s.choice = if on { "yes" } else { "no" }.to_string();
    if on && s.id.is_empty() { s.id = uuid(); }
    if !on { s.last = 0; }
    save(&s);
}

// force: after a clean (so the total includes it) and right after you say yes. Never unless you said yes.
pub fn maybe_send(force: bool) {
    let id = {
        let s = lock();
        if s.choice != "yes" || s.id.is_empty() { return; }
        if !force && now().saturating_sub(s.last) < 20 * 3600 { return; }
        s.id.clone()
    };
    let (freed, cleans) = crate::engine::history::total();
    let v = crate::updater::current();
    let body = serde_json::json!({ "id": id, "v": v, "os": "linux", "freed": freed, "cleans": cleans }).to_string();
    std::thread::spawn(move || {
        // offline, blocked or no curl: nothing happens, and it tries again another time
        let out = Command::new("curl")
            .args(["-sS", "--max-time", "8", "-o", "/dev/null", "-w", "%{http_code}", "-X", "POST", "-H", "Content-Type: application/json"])
            .arg("-H").arg(format!("User-Agent: SpaceHunter/{}", v))
            .arg("--data-binary").arg(&body)
            .arg(format!("{}/ping", ENDPOINT))
            .output();
        if let Ok(o) = out {
            let code: u32 = String::from_utf8_lossy(&o.stdout).trim().parse().unwrap_or(0);
            if (200..300).contains(&code) {
                let mut s = lock();
                if s.choice == "yes" { s.last = now(); save(&s); }
            }
        }
    });
}
