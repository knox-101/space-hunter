// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Linux) - updates. Once per launch the app asks GitHub for the latest release of this repository
// (one small request; nothing about you is sent). The AppImage can update itself: it downloads the new AppImage and
// its signature, refuses it unless the signature matches the project's public key (built into this program), swaps
// the file and reopens it. Copies installed another way (Nix, from source) are told to update the way they came.
// Offline, nothing happens.

use rsa::pkcs1::DecodeRsaPublicKey;
use rsa::{Pkcs1v15Sign, RsaPublicKey};
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use std::process::Command;

const REPO: &str = "knox-101/space-hunter";
const KEY: &[u8] = include_bytes!("../../update-key/update-key.der");   // the public half of the release key
const ASSET: &str = "SpaceHunter-x86_64.AppImage";

pub struct Release { pub version: String, pub page: Option<String>, pub url: String, pub sig_url: String }

fn parse(v: &str) -> Option<(u32, u32, u32)> {
    let mut p = v.trim().trim_start_matches('v').split('.').map(|x| x.parse::<u32>().ok());
    Some((p.next()??, p.next()??, p.next().flatten().unwrap_or(0)))
}

pub fn current() -> String { crate::VERSION.trim().to_string() }

fn get(url: &str, out: Option<&Path>) -> Option<Vec<u8>> {
    // curl is on nearly every Linux system; without it there's simply no update check
    let mut c = Command::new("curl");
    c.args(["-fsSL", "--max-time", "120", "-H", "User-Agent: SpaceHunter", url]);
    if let Some(o) = out { c.arg("-o").arg(o); }
    let r = c.output().ok()?;
    if !r.status.success() { return None; }
    Some(r.stdout)
}

// The newest release, if it's newer than this copy. None when offline or up to date.
pub fn check() -> Option<Release> {
    let body = get(&format!("https://api.github.com/repos/{}/releases/latest", REPO), None)?;
    let j: serde_json::Value = serde_json::from_slice(&body).ok()?;
    let tag = j.get("tag_name")?.as_str()?.to_string();
    if parse(&tag)? <= parse(&current())? { return None; }
    let assets = j.get("assets")?.as_array()?;
    let find = |n: &str| assets.iter().find(|a| a.get("name").and_then(|x| x.as_str()) == Some(n)).and_then(|a| a.get("browser_download_url")?.as_str().map(|s| s.to_string()));
    Some(Release {
        version: tag.trim_start_matches('v').to_string(),
        page: j.get("html_url").and_then(|x| x.as_str()).map(|s| s.to_string()),
        url: find(ASSET)?,
        sig_url: find(&format!("{}.sig", ASSET))?,
    })
}

pub fn verify(data: &[u8], sig: &[u8], key_der: &[u8]) -> bool {
    match RsaPublicKey::from_pkcs1_der(key_der) {
        Ok(k) => k.verify(Pkcs1v15Sign::new::<Sha256>(), &Sha256::digest(data), sig).is_ok(),
        Err(_) => false,
    }
}

// Only the AppImage can replace itself; other installs update through what installed them.
pub fn self_path() -> Option<PathBuf> { std::env::var_os("APPIMAGE").map(PathBuf::from) }

// Ok(()) when the new version is opening (the caller quits), or a sentence saying why it didn't update.
pub fn install(r: &Release, say: &dyn Fn(&str)) -> Result<(), String> {
    let me = self_path().ok_or("This copy wasn't started from the AppImage, so update it the way you installed it.")?;
    let dir = me.parent().map(|p| p.to_path_buf()).unwrap_or_else(|| PathBuf::from("."));
    let new = dir.join(format!(".{}.new", ASSET));
    say(&format!("Downloading Space Hunter {} \u{2026}", r.version));
    if get(&r.url, Some(&new)).is_none() { let _ = std::fs::remove_file(&new); return Err("The download didn't finish. Nothing was changed; try again later.".into()); }
    let sig = get(&r.sig_url, None).ok_or("The signature couldn't be downloaded. Nothing was changed.")?;
    say("Checking its signature \u{2026}");
    let data = std::fs::read(&new).map_err(|_| "The download couldn't be read. Nothing was changed.".to_string())?;
    if !verify(&data, &sig, KEY) { let _ = std::fs::remove_file(&new); return Err("The download's signature didn't match, so it wasn't installed. Nothing was changed.".into()); }
    say("Installing \u{2026}");
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(&new, std::fs::Permissions::from_mode(0o755));
    // a rename swaps the file in one step; the running copy keeps working until it closes
    std::fs::rename(&new, &me).map_err(|_| { let _ = std::fs::remove_file(&new); "Space Hunter couldn't replace itself (is its folder read-only?). Nothing was changed.".to_string() })?;
    Command::new(&me).spawn().map_err(|_| "Updated, but it couldn't reopen. Open Space Hunter again yourself.".to_string())?;
    Ok(())
}

// test hook for CI: --test-verify <file> <sig> <key.der>
pub fn test_verify(file: &str, sig: &str, key: &str) -> bool {
    match (std::fs::read(file), std::fs::read(sig), std::fs::read(key)) { (Ok(d), Ok(s), Ok(k)) => verify(&d, &s, &k), _ => false }
}
