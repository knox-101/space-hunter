// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (macOS) - finds the folders that silently fill your disk, explains each one, and cleans the
// ticked ones. Same design and features as the Windows version ("Instrument + Strata": warm-grey chassis,
// one dark display, orange as the only accent, square hardware keys, three safety sections).
// Build with ./build.sh (Xcode or Command Line Tools). macOS 13+.

import SwiftUI
import AppKit
import CoreText
import Security

// MARK: - Model

enum Kind { case contents, subfolders, trash }
enum Level: Int, CaseIterable { case safe, review, caution }

struct Cat: Identifiable {
    let id: String
    var name: String
    let level: Level
    let kind: Kind
    var roots: [URL]
    var exclude: Set<String> = []
    var extensions: Set<String>? = nil      // only files with these extensions (Downloads)
    var topLevelOnly = false                 // only files directly in the folder
    var isCustom = false
    var refused: String? = nil               // something you added that the safety rules now refuse: never cleaned
    let defaultAge: Int
    var ageMinutes: Int
    var lastAge: Int
    var checked: Bool
    var files: Int64 = 0
    var bytes: Int64 = 0
    var scanned = false
    var short = "", what = "", deletes = "", safeText = "", after = ""
    // Files and folders picked by hand inside an unticked item: cleaned exactly as picked (whatever their age), in the
    // next clean only. Never saved, so nothing new is ever cleaned by surprise later.
    var picks: Set<String> = []
    var pickFiles: Int64 = 0
    var pickBytes: Int64 = -1                // -1: not measured yet
    var isSafe: Bool { level == .safe }
    var partial: Bool { !checked && !picks.isEmpty }
    var included: Bool { checked || partial }
    var cleanBytes: Int64 { checked ? bytes : partial ? max(0, pickBytes) : 0 }
    // Only items that fill up over time offer "older than"; caches and the like are cleaned all at once.
    var ageChoice: Bool { (kind == .contents || kind == .subfolders) && (defaultAge > 0 || isCustom) }
}

final class Counter {
    var files: Int64 = 0, bytes: Int64 = 0, skipped: Int64 = 0, trashedBytes: Int64 = 0
    var cancel = false
    var onItem: ((String, Int64, Bool, Bool) -> Void)?   // path, size, ok, toTrash
}

// What one item freed in a clean (for the list at the end of the cleaning window)
struct FreedItem { let name: String; let bytes: Int64; let trashed: Int64 }

enum Store2 {   // small text files in ~/Library/Application Support/SpaceHunter
    static var dir: URL {
        let d = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/SpaceHunter")
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d
    }
    static func lines(_ name: String) -> [String] {
        ((try? String(contentsOf: dir.appendingPathComponent(name), encoding: .utf8)) ?? "")
            .split(separator: "\n").map { String($0) }.filter { !$0.isEmpty }
    }
    static func write(_ name: String, _ lines: [String]) {
        try? lines.joined(separator: "\n").write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8)
    }
}

enum Keep {   // protected folders: never shown, counted or cleaned
    static var all: [String] { Store2.lines("keep.txt") }
    static func add(_ p: String) { var a = all; if !a.contains(p) { a.append(p) }; Store2.write("keep.txt", a) }
    static func remove(_ p: String) { Store2.write("keep.txt", all.filter { $0 != p }) }
    static func isKept(_ path: String, _ list: [String]) -> Bool { list.contains { path == $0 || path.hasPrefix($0 + "/") } }
    // Space Hunter itself (the app and its settings folder): always protected, and can't be taken off the list
    static var builtIn: [String] { [Bundle.main.bundlePath, Store2.dir.path, Engine.u("Library/Logs/SpaceHunter").path] }   // incl. its log, which holds "Freed so far"
}

enum Custom {   // folders added to the cleanup list from the Storage map
    static var all: [String] { Store2.lines("your-folders.txt") }
    static func add(_ p: String) { var a = all; if !a.contains(p) { a.append(p) }; Store2.write("your-folders.txt", a) }
    static func remove(_ p: String) { Store2.write("your-folders.txt", all.filter { $0 != p }) }
    // inside a folder you added: the files and sub-folders you unticked, which that item never cleans
    static var skip: [String] { Store2.lines("your-folders-skip.txt") }
    static func isSkipped(_ path: String, _ list: [String]) -> Bool { list.contains { path == $0 || path.hasPrefix($0 + "/") } }
    static func setSkipped(_ p: String, _ on: Bool) { var a = skip.filter { $0 != p }; if on { a.append(p) }; Store2.write("your-folders-skip.txt", a) }
}

enum Engine {
    static let fm = FileManager.default
    static let home = fm.homeDirectoryForCurrentUser
    static let hour = 60, day = 1440

    static func u(_ path: String) -> URL { home.appendingPathComponent(path) }

    static func dirs(_ parent: URL) -> [URL] {
        let items = (try? fm.contentsOfDirectory(at: parent, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])) ?? []
        return items.filter { url in
            let v = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            return v?.isDirectory == true && v?.isSymbolicLink != true
        }
    }

    // The items live in cleaners.json (in the app's Resources); only apps that are on this Mac get a row.
    static func build() -> [Cat] {
        var list: [Cat] = []
        let d = UserDefaults.standard
        for def in Cleaners.load() {
            let age = Cleaners.age(def.age ?? "0")
            let kind: Kind = def.kind == "subfolders" ? .subfolders : def.kind == "trash" ? .trash : .contents
            let level: Level = def.level == "review" ? .review : def.level == "caution" ? .caution : .safe
            var roots: [URL] = []
            for p in def.paths ?? [] { for u in Cleaners.expand(p) where !roots.contains(u) { roots.append(u) } }
            var c = Cat(id: def.id, name: def.name, level: level, kind: kind, roots: roots, exclude: Set(def.exclude ?? []),
                        extensions: def.extensions.map { Set($0) }, topLevelOnly: def.topLevelOnly ?? false,
                        defaultAge: age, ageMinutes: age, lastAge: age > 0 ? age : 14 * day, checked: level == .safe)
            c.short = def.short ?? ""; c.what = def.what ?? ""; c.deletes = def.deletes ?? ""
            c.safeText = def.safe ?? ""; c.after = def.after ?? ""
            guard Cleaners.installed(c) else { continue }
            if d.object(forKey: "v4.checked.\(c.id)") != nil { c.checked = d.bool(forKey: "v4.checked.\(c.id)") }
            if d.object(forKey: "v4.age.\(c.id)") != nil { c.ageMinutes = c.ageChoice ? d.integer(forKey: "v4.age.\(c.id)") : 0 }   // no age on caches
            if d.object(forKey: "v4.last.\(c.id)") != nil { c.lastAge = max(1, d.integer(forKey: "v4.last.\(c.id)")) }
            list.append(c)
        }

        for p in Custom.all {
            var isDir: ObjCBool = false
            let isFile = fm.fileExists(atPath: p, isDirectory: &isDir) && !isDir.boolValue   // a single file you picked: it goes whatever its age
            var c = Cat(id: "custom:" + p.lowercased(), name: (p as NSString).lastPathComponent + (isFile ? "  (your file)" : "  (your folder)"), level: .caution,
                        kind: .contents, roots: [URL(fileURLWithPath: p)], defaultAge: isFile ? 0 : 30 * day, ageMinutes: isFile ? 0 : 30 * day, lastAge: 30 * day, checked: false)
            c.isCustom = true
            c.short = "Ticked contents go; the folder itself stays"
            c.what = "A folder you added from the Storage map: " + p
            c.deletes = "Every file inside it (older than the age you set) and the sub-folders that end up empty, except what you untick under \u{201C}Inside this folder\u{201D}. The folder itself stays."
            c.safeText = "Only you know. Open it and check before ticking. Protected folders inside it are skipped."
            c.after = "The files are gone, unless you choose Trash in the confirm window."
            if isFile {
                c.short = "This one file goes"; c.what = "A file you added from the Storage map: " + p
                c.deletes = "Just this file, whatever its age."; c.safeText = "Only you know. Open it and check before ticking."
                c.after = "The file is gone, unless you choose Trash in the confirm window."
            }
            if d.object(forKey: "v4.age.\(c.id)") != nil { c.ageMinutes = c.ageChoice ? d.integer(forKey: "v4.age.\(c.id)") : 0 }
            if d.object(forKey: "v4.checked.\(c.id)") != nil { c.checked = d.bool(forKey: "v4.checked.\(c.id)") }
            // added before the safety rules caught it: listed so it can be removed, never cleaned
            if let why = canAdd(p, existing: true) { c.refused = why; c.checked = false; c.short = "Not cleaned: \(why). Remove it from the list" }
            list.append(c)
        }
        return list
    }

    // Your personal folders: nothing inside them is ever cleaned (except old installers in Downloads, by filter).
    static var personal: [String] {
        ["Documents", "Desktop", "Pictures", "Movies", "Music", "Downloads", "Public", "Library/Mobile Documents",
         "Projects", "Developer", ".ssh", ".gnupg", ".aws", ".kube", ".docker"].map { u($0).path }
    }
    static func personalFolderOf(_ path: String) -> String? {
        personal.first { path == $0 || path.hasPrefix($0 + "/") }
    }

    // Never operate on these, whatever an item says.
    static func isSafeRoot(_ url: URL, allowPersonal: Bool) -> Bool {
        let p = url.standardizedFileURL.path
        let banned = ["/", "/Users", home.path, u("Library").path, u("Library/Application Support").path]
        if banned.contains(p) { return false }
        if !allowPersonal, personalFolderOf(p) != nil { return false }
        if allowPersonal, let k = personalFolderOf(p), k != u("Downloads").path { return false }
        return p.hasPrefix(home.path + "/") || p.hasPrefix(fm.temporaryDirectory.standardizedFileURL.path)
            || p.hasPrefix("/private/var/folders/") || p.hasPrefix("/var/folders/")
    }

    static let keys: [URLResourceKey] = [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey,
                                         .totalFileAllocatedSizeKey, .fileSizeKey, .contentModificationDateKey, .creationDateKey, .addedToDirectoryDateKey]

    static func walk(_ root: URL, exclude: Set<String>, kept: [String], cnt: Counter, _ body: (URL, Int64, Date) -> Void) {
        guard let e = fm.enumerator(at: root, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true }) else { return }
        for case let url as URL in e {
            if cnt.cancel { return }
            guard let v = try? url.resourceValues(forKeys: Set(keys)) else { continue }
            if v.isSymbolicLink == true { continue }          // never follow links out of the folder
            if v.isDirectory == true {
                if exclude.contains(url.lastPathComponent) || Keep.isKept(url.path, kept) { e.skipDescendants() }
                continue
            }
            if v.isRegularFile == true {
                // the newest of changed, created and added to this folder: photos copied in today are new, whatever their date
                let t = ageDate(v)
                body(url, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), t)
            }
        }
    }

    // A file's age: the newest of changed, created and added to its folder, so photos copied, unzipped or AirDropped
    // today count as new, whatever their own date.
    static func ageDate(_ v: URLResourceValues) -> Date {
        [v.contentModificationDate, v.creationDate, v.addedToDirectoryDate].compactMap { $0 }.max() ?? .distantPast
    }

    static func newest(_ dir: URL, cnt: Counter) -> Date {
        var n = (try? dir.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
        walk(dir, exclude: [], kept: [], cnt: cnt) { _, _, d in if d > n { n = d } }
        return n
    }

    // Removes the empty folders this clean emptied (touched), and empty folders older than the age rule; a folder a
    // program made a moment ago stays. Never goes into another disk mounted inside.
    static func removeEmptyDirs(_ root: URL, keepRoot: Bool, exclude: Set<String>, kept: [String], cutoff: Date = .distantFuture, touched: Set<String> = []) {
        let vk: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .volumeIdentifierKey]
        let rootVol = (try? root.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier as? NSObject
        var all: [URL] = []
        if let e = fm.enumerator(at: root, includingPropertiesForKeys: vk, options: [], errorHandler: { _, _ in true }) {
            for case let url as URL in e {
                let v = try? url.resourceValues(forKeys: Set(vk))
                if v?.isDirectory == true && v?.isSymbolicLink != true {
                    if exclude.contains(url.lastPathComponent) || Keep.isKept(url.path, kept) || (v?.volumeIdentifier as? NSObject) != rootVol { e.skipDescendants(); continue }
                    all.append(url)
                }
            }
        }
        if !keepRoot { all.append(root) }
        var emptied = touched
        for dir in all.sorted(by: { $0.pathComponents.count > $1.pathComponents.count }) {
            guard let c = try? fm.contentsOfDirectory(atPath: dir.path), c.isEmpty || c == [".DS_Store"] else { continue }
            let v = try? dir.resourceValues(forKeys: [.contentModificationDateKey, .creationDateKey, .addedToDirectoryDateKey])
            let old = v.map { ageDate($0) < cutoff } ?? false
            if emptied.contains(dir.path) || old {
                if c == [".DS_Store"] { try? fm.removeItem(at: dir.appendingPathComponent(".DS_Store")) }
                if rmdir(dir.path) == 0 { emptied.insert(dir.deletingLastPathComponent().path) }   // only if still empty; its parent may be empty now too
            }
        }
    }

    static func process(_ c: Cat, delete: Bool, toTrash: Bool, cnt: Counter) {
        let cutoff = Date().addingTimeInterval(-Double(c.ageMinutes) * 60)
        let kept = Keep.all + Keep.builtIn + Custom.skip   // what you unticked under "What's inside"
        let trashIt = toTrash && c.kind != .trash   // emptying the Trash can't go to the Trash
        var touched = Set<String>()   // folders this clean took files out of
        func handle(_ url: URL, _ size: Int64, _ date: Date, _ cut: Date) {
            if let ext = c.extensions, !ext.contains(url.pathExtension.lowercased()) { return }
            if Keep.isKept(url.path, kept) { return }
            if date >= cut { if delete { cnt.skipped += 1 }; return }
            if !delete { cnt.files += 1; cnt.bytes += size; return }
            var ok = true
            do {
                if trashIt { try fm.trashItem(at: url, resultingItemURL: nil) } else { try fm.removeItem(at: url) }
            } catch { ok = false }
            if ok { cnt.files += 1; touched.insert(url.deletingLastPathComponent().path); if trashIt { cnt.trashedBytes += size } else { cnt.bytes += size } } else { cnt.skipped += 1 }
            cnt.onItem?(url.path, size, ok, trashIt)
        }
        if c.refused != nil { return }   // the safety rules refuse it now
        if delete && c.partial { processPicks(c, delete: true, toTrash: toTrash, cnt: cnt); return }   // only what you picked
        for root in c.roots {
            if cnt.cancel { return }
            var isDir: ObjCBool = false
            if c.isCustom, fm.fileExists(atPath: root.path, isDirectory: &isDir), !isDir.boolValue {   // a single file you added
                if let v = try? root.resourceValues(forKeys: Set(keys)), v.isSymbolicLink != true, !Keep.isKept(root.path, kept) {
                    handle(root, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), ageDate(v), cutoff)
                }
                continue
            }
            guard fm.fileExists(atPath: root.path), isSafeRoot(root, allowPersonal: c.extensions != nil && c.topLevelOnly) else { continue }
            if (try? root.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { continue }   // never follow a linked root
            if Keep.isKept(root.path, kept) { continue }
            switch c.kind {
            case .contents, .trash:
                if c.topLevelOnly {
                    let items = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: keys)) ?? []
                    for url in items {
                        guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true, v.isSymbolicLink != true else { continue }
                        handle(url, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), ageDate(v), cutoff)
                    }
                } else {
                    walk(root, exclude: c.exclude, kept: kept, cnt: cnt) { url, s, dd in handle(url, s, dd, cutoff) }
                    if delete { removeEmptyDirs(root, keepRoot: true, exclude: c.exclude, kept: kept, cutoff: cutoff, touched: touched) }
                }
            case .subfolders:
                for sub in dirs(root) {
                    if cnt.cancel { return }
                    if Keep.isKept(sub.path, kept) || newest(sub, cnt: cnt) >= cutoff { continue }
                    walk(sub, exclude: c.exclude, kept: kept, cnt: cnt) { url, s, dd in handle(url, s, dd, .distantFuture) }
                    if delete { removeEmptyDirs(sub, keepRoot: false, exclude: c.exclude, kept: kept) }
                }
            }
        }
    }

    // The files and folders picked by hand in an unticked item: each one is cleaned whole, whatever its age. Only picks
    // inside the item's own folders count, and protected, kept and "always keep" files still stay.
    static func processPicks(_ c: Cat, delete: Bool, toTrash: Bool, cnt: Counter) {
        if c.refused != nil { return }
        let kept = Keep.all + Keep.builtIn + Custom.skip
        let trashIt = toTrash && c.kind != .trash
        let allowPersonal = c.extensions != nil && c.topLevelOnly
        // the item's own folders that exist now (the same rules as a normal clean)
        var roots: [String] = []
        for r in c.roots {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: r.path, isDirectory: &isDir), isDir.boolValue, isSafeRoot(r, allowPersonal: allowPersonal) else { continue }
            if (try? r.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { continue }
            if Keep.isKept(r.path, kept) { continue }
            var p = r.path; while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
            roots.append(p)
        }
        let all = Array(c.picks)
        let picks = all.filter { p in roots.contains { p.hasPrefix($0 + "/") } }
                       .filter { p in !all.contains { q in q != p && p.hasPrefix(q + "/") } }   // a pick inside a picked folder is covered
        var touched = Set<String>()
        func handle(_ url: URL, _ size: Int64) {
            if let ext = c.extensions, !ext.contains(url.pathExtension.lowercased()) { return }
            if Keep.isKept(url.path, kept) { return }
            if !delete { cnt.files += 1; cnt.bytes += size; return }
            var ok = true
            do {
                if trashIt { try fm.trashItem(at: url, resultingItemURL: nil) } else { try fm.removeItem(at: url) }
            } catch { ok = false }
            if ok { cnt.files += 1; touched.insert(url.deletingLastPathComponent().path); if trashIt { cnt.trashedBytes += size } else { cnt.bytes += size } } else { cnt.skipped += 1 }
            cnt.onItem?(url.path, size, ok, trashIt)
        }
        let vk: [URLResourceKey] = keys + [.volumeIdentifierKey]
        for p in picks {
            if cnt.cancel { return }
            if Keep.isKept(p, kept) { continue }   // protected, kept, or on the "always keep" list
            let url = URL(fileURLWithPath: p)
            guard let v = try? url.resourceValues(forKeys: Set(vk)), v.isSymbolicLink != true else { continue }   // never follow a link
            // never another disk mounted inside the item's folder
            if let r = roots.first(where: { p.hasPrefix($0 + "/") }) {
                let rootVol = (try? URL(fileURLWithPath: r).resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier as? NSObject
                if (v.volumeIdentifier as? NSObject) != rootVol { continue }
            }
            if v.isRegularFile == true {
                handle(url, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0))
                continue
            }
            guard v.isDirectory == true, !c.topLevelOnly, !c.exclude.contains(url.lastPathComponent) else { continue }
            walk(url, exclude: c.exclude, kept: kept, cnt: cnt) { u, s, _ in handle(u, s) }
            if delete { removeEmptyDirs(url, keepRoot: false, exclude: c.exclude, kept: kept, cutoff: .distantFuture, touched: touched) }   // the picked folder goes too, once empty
        }
    }

    // Can a folder from the Storage map be added to the cleanup list? nil = yes, otherwise the reason.
    static func canAdd(_ path: String, existing: Bool = false) -> String? {
        let p = (path as NSString).standardizingPath
        if p == "/" { return "a whole disk" }
        // only inside your home folder, and never apps' settings in ~/Library (their caches and logs are fine)
        if !p.hasPrefix(home.path + "/") { return "outside your home folder" }
        // AI tools' and editors' own folders hold settings, sign-in tokens and memory; their caches and old chats are items
        for t in [".claude", ".codex", ".gemini", ".cursor", ".vscode", ".config", ".copilot", ".continue", ".aider"] where p == u(t).path || p.hasPrefix(u(t).path + "/") {
            return "it holds an app's settings and sign-ins"
        }
        let lib = u("Library").path
        if p.hasPrefix(lib + "/"), !["Caches", "Logs", "Developer"].contains(where: { p == lib + "/" + $0 || p.hasPrefix(lib + "/" + $0 + "/") }) {
            return "it holds an app's settings"
        }
        // a folder made right at the top of the disk (like /Personal) or a whole user folder: likely your own files
        if p.split(separator: "/").count <= 2 { return "likely your own files (made right on the disk)" }
        for sys in ["/System", "/Library", "/Applications", "/usr", "/bin", "/sbin", "/private", "/opt", "/Volumes", "/cores"] where p == sys || p.hasPrefix(sys + "/") {
            return "part of macOS or installed apps"
        }
        for keep in [home.path, u("Library").path, u("Library/Application Support").path] + personal where keep == p || keep.hasPrefix(p + "/") {
            return "it holds your home or personal folders"
        }
        if let k = personalFolderOf(p) { return "it's inside your personal files (\((k as NSString).lastPathComponent))" }
        if Keep.isKept(p, Keep.builtIn) { return "it's Space Hunter's own folder" }
        if Keep.isKept(p, Keep.all) { return "it's a protected folder" }
        if !existing && Custom.all.contains(where: { p == $0 || p.hasPrefix($0 + "/") }) { return "it's already on the cleanup list" }
        return nil
    }

    // why something can't be added, as a short calm label (the rules themselves are in canAdd)
    static func calm(_ why: String) -> String {
        let w = why.lowercased()
        if w.contains("space hunter") { return "Space Hunter's own folder" }
        if w.contains("outside your home") { return "Outside your home folder" }
        if w.contains("settings") || w.contains("sign-in") { return "App settings" }
        if w.contains("macos or installed") { return "macOS or an installed app" }
        if w.contains("own files") || w.contains("personal") || w.contains("home") { return "Your own files" }
        if w.contains("protected") { return "Protected" }
        if w.contains("already") { return "Already on your list" }
        if w.contains("disk") { return "A whole disk" }
        return "Can't be added"
    }

    // Which cleanup-list items already cover a folder (it's inside them) or sit inside it.
    static func coverage(_ path: String, _ cats: [Cat]) -> (String?, Bool) {
        for c in cats { for r in c.roots where path == r.path || path.hasPrefix(r.path + "/") { return (c.name, true) } }
        let holds = cats.filter { $0.roots.contains { $0.path.hasPrefix(path + "/") } }.map { $0.name }
        return holds.isEmpty ? (nil, false) : (holds.prefix(2).joined(separator: ", ") + (holds.count > 2 ? " +\(holds.count - 2)" : ""), false)
    }

    // "1.5 GB" the same way everywhere (and in the log, which is read back): 1000 MB and up shows as GB, 1000 GB as TB,
    // so a size never reads "1000 MB"
    static func fmt(_ b: Int64) -> String {
        let kb = 1024.0, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024
        let v = Double(b)
        if b <= 0 { return "0 MB" }
        if v >= 1000 * gb { return String(format: "%.2f TB", v / tb) }
        if v >= 1000 * mb { return String(format: "%.1f GB", v / gb) }
        if v >= mb { return String(format: "%.0f MB", v / mb) }
        return String(format: "%.0f KB", max(1, v / kb))
    }

    static func parts(_ b: Int64) -> (String, String) {
        let s = fmt(b).split(separator: " ")
        return s.count == 2 ? (String(s[0]), String(s[1])) : (fmt(b), "")
    }

    static func fmtAge(_ m: Int) -> String {
        if m <= 0 { return "any age" }
        if m % day == 0 { return "\(m / day) day" + (m == day ? "" : "s") }
        if m % hour == 0 { return "\(m / hour) hour" + (m == hour ? "" : "s") }
        return "\(m) min"
    }

    static func diskName() -> String {
        let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeNameKey])
        return v?.volumeName ?? "this Mac"
    }

    static func disk() -> (free: Int64, total: Int64) {
        let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeTotalCapacityKey])
        return (v?.volumeAvailableCapacityForImportantUsage ?? 0, Int64(v?.volumeTotalCapacity ?? 0))
    }

    static var logURL: URL { u("Library/Logs/SpaceHunter/log.txt") }
    static func log(_ line: String) {
        let dir = u("Library/Logs/SpaceHunter")
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let stamp = ISO8601DateFormatter().string(from: Date())
        let data = (stamp + "  " + line + "\n").data(using: .utf8)!
        if let h = try? FileHandle(forWritingTo: logURL) { h.seekToEndOfFile(); h.write(data); try? h.close() }
        else { try? data.write(to: logURL) }
    }

    // Everything freed so far, read back from the log ("CLEANED 12.3 GB ...").
    static func freedAllTime() -> (Int64, Int) {
        guard let text = try? String(contentsOf: logURL, encoding: .utf8) else { return (0, 0) }
        var total: Int64 = 0, n = 0
        for line in text.split(separator: "\n") {
            guard let r = line.range(of: "CLEANED ") else { continue }
            let rest = line[r.upperBound...].split(separator: " ")
            guard rest.count >= 2, let v = Double(rest[0].replacingOccurrences(of: ",", with: "")) else { continue }
            let unit = rest[1].uppercased()
            // binary units, as fmt writes them (older lines were decimal; the difference is a few percent)
            let mult: Double = unit.hasPrefix("TB") ? 1_099_511_627_776 : unit.hasPrefix("GB") ? 1_073_741_824 : unit.hasPrefix("MB") ? 1_048_576 : 1024
            total += Int64(v * mult); n += 1
        }
        return (total, n)
    }
}

// MARK: - Notes (the questions people have before deleting)

// Reads the cleanup items from cleaners.json. Adding an app means adding an entry there; see CONTRIBUTING.md.
struct ItemDef: Decodable {
    let id: String, name: String, level: String
    let kind: String?, age: String?, paths: [String]?, exclude: [String]?, extensions: [String]?, topLevelOnly: Bool?
    let short: String?, what: String?, deletes: String?, safe: String?, after: String?
}

enum Cleaners {
    struct File: Decodable { let items: [ItemDef] }

    static func load() -> [ItemDef] {
        guard let url = Bundle.main.url(forResource: "cleaners", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(File.self, from: data) else { return [] }
        return file.items
    }

    // "0" = everything; "30m", "1h", "14d"
    static func age(_ s: String) -> Int {
        guard s != "0", let n = Int(s.dropLast()) else { return 0 }
        switch s.last { case "m": return n; case "h": return n * Engine.hour; case "d": return n * Engine.day; default: return 0 }
    }

    // ~/Library/Application Support/Claude*/Cache  ->  every matching folder that exists.
    // A path without wildcards is returned as it is, whether or not it exists.
    static func expand(_ pattern: String) -> [URL] {
        var p = pattern
        if p.hasPrefix("~/") { p = Engine.home.path + p.dropFirst(1) }
        if p.hasPrefix("$TMPDIR") {
            var t = Engine.fm.temporaryDirectory.path; while t.hasSuffix("/") { t.removeLast() }
            p = t + p.dropFirst("$TMPDIR".count)
        }
        guard p.hasPrefix("/") else { return [] }   // an unknown variable: never guess
        if !p.contains("*") && !p.contains("?") { return [URL(fileURLWithPath: p)] }
        var current = [URL(fileURLWithPath: "/")]
        for part in p.split(separator: "/").map(String.init) {
            if part.contains("*") || part.contains("?") {
                current = current.flatMap { dir in Engine.dirs(dir).filter { fnmatch(part, $0.lastPathComponent, 0) == 0 } }
            } else {
                current = current.map { $0.appendingPathComponent(part) }
            }
            if current.isEmpty { break }
        }
        return current
    }

    static func installed(_ c: Cat) -> Bool {
        if c.kind == .trash { return true }
        return c.roots.contains { var isDir: ObjCBool = false; return Engine.fm.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue }
    }
}

// MARK: - Fonts and theme

enum Fonts {
    static func register() {
        guard let dir = Bundle.main.resourceURL?.appendingPathComponent("fonts"),
              let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        for f in files where f.pathExtension.lowercased() == "ttf" {
            CTFontManagerRegisterFontsForURL(f as CFURL, .process, nil)
        }
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
    // one colour for the light theme, another for the dark one; macOS picks the right one wherever it's drawn
    static func themed(_ light: UInt32, _ dark: UInt32) -> Color {
        func ns(_ h: UInt32) -> NSColor {
            NSColor(srgbRed: CGFloat((h >> 16) & 0xFF) / 255, green: CGFloat((h >> 8) & 0xFF) / 255, blue: CGFloat(h & 0xFF) / 255, alpha: 1)
        }
        let l = ns(light), d = ns(dark)
        return Color(nsColor: NSColor(name: nil, dynamicProvider: { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? d : l
        }))
    }
}

// Light, Dark or System (follows macOS, live). Saved in the app's settings and applied at launch.
enum ThemeMode {
    static let key = "v5.theme"
    static var saved: String {
        let s = UserDefaults.standard.string(forKey: key) ?? "system"
        return s == "light" || s == "dark" ? s : "system"
    }
    static func apply(_ mode: String) {
        guard let app = NSApp else { return }
        app.appearance = mode == "light" ? NSAppearance(named: .aqua) : mode == "dark" ? NSAppearance(named: .darkAqua) : nil
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) { ThemeMode.apply(ThemeMode.saved) }
}

enum Theme {
    // Light is the original warm-grey chassis; Dark keeps the same idea (one display, darker than everything around
    // it, orange as the only accent).
    static let chassis = Color.themed(0xD8D5CE, 0x1E1E1C), pane = Color.themed(0xE4E1DA, 0x262624)
    static let selected = Color.themed(0xE9E6E0, 0x30302D), hover = Color.themed(0xE0DDD6, 0x2A2A27)
    static let screen = Color.themed(0x121212, 0x0B0B0A), screenInk = Color(hex: 0xE9E6DF), screenMuted = Color(hex: 0x8A8A86)
    static let track = Color(hex: 0x3A3A38), used = Color(hex: 0x6F6C66)
    static let ink = Color.themed(0x1B1B1A, 0xECE9E3), muted = Color.themed(0x55524C, 0xABA79F)
    static let soft = Color.themed(0x57534D, 0xA6A29A), line = Color.themed(0xC3BFB6, 0x3B3A37)
    static let orange = Color(hex: 0xFF5B1F), orangeDeep = Color.themed(0x9A3409, 0xFF7A45)   // deep orange: text and links
    static let orangeShadow = Color(hex: 0xB5400F)   // the shadow under the orange key, in both
    static let onOrange = Color(hex: 0x1B1B1A)       // text, ticks and dashes on orange, in both
    static let key = Color.themed(0xECEAE5, 0x34332F), keyShadow = Color.themed(0xA9A59C, 0x0E0E0D)
    static let disabled = Color.themed(0x9A968F, 0x6E6B65), keyOff = Color.themed(0xD0CDC6, 0x2C2B28)

    static func display(_ s: CGFloat) -> Font { .custom("BigShouldersDisplay-Black", size: s) }
    static func sans(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Regular", size: s) }
    static func semi(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-SemiBold", size: s) }
    static func bold(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Bold", size: s) }
    static func mono(_ s: CGFloat) -> Font { .custom("IBMPlexMono-Regular", size: s) }
    static func monoSemi(_ s: CGFloat) -> Font { .custom("IBMPlexMono-SemiBold", size: s) }

    // Names a newcomer can act on without opening anything: the first is the no-worry clean.
    static func tierName(_ l: Level) -> String { l == .safe ? "Quick clean" : l == .review ? "Big caches & history" : "Deep clean" }
    static func tierDesc(_ l: Level) -> String { l == .safe ? "Caches and leftovers that rebuild" : l == .review ? "Big caches that download again, or old history" : "Old AI chats, old downloads, folders you added" }
    // one plain line under each section's heading, so nobody has to guess what's in it
    static func tierExplain(_ l: Level) -> String {
        l == .safe ? "Caches, temp files and leftovers that apps and macOS rebuild or no longer need. Your files, logins and passwords aren't touched."
        : l == .review ? "Things that download again when they're needed, or history you might still want."
        : "Old AI agent chats and workspaces, old installers in Downloads, and folders you added. Nothing here is ticked for you."
    }
    // the last two words held together, so a wrapped line never ends with one word on its own
    static func keepLastTwo(_ s: String) -> String {
        guard let r = s.range(of: " ", options: .backwards) else { return s }
        return s.replacingCharacters(in: r, with: "\u{00A0}")
    }
    // the one thing to do in each section, on its own highlighted line
    static func tierCta(_ l: Level) -> String {
        l == .safe ? "Clean this every time you open Space Hunter: it's always safe."
        : l == .review ? "Tick what you won't need soon. Each item says what comes back, and how."
        : "This may include your work. Open each item and check before cleaning."
    }
}

// A square hardware key: light, orange when primary, dark when toggled (a lit segment).
struct KeyStyle: ButtonStyle {
    var primary = false
    var toggled = false
    var big = false
    var accent = false   // a tool of its own (the Storage map): an orange outline
    @Environment(\.isEnabled) var enabled
    func makeBody(configuration: Configuration) -> some View {
        let pressed = configuration.isPressed
        let fill = !enabled ? Theme.keyOff : toggled ? Theme.ink : primary ? Theme.orange : Theme.key
        let shadow = !enabled ? Theme.line : toggled ? Color.black : primary ? Theme.orangeShadow : Theme.keyShadow
        let text = !enabled ? Theme.disabled : toggled ? Theme.pane : primary ? Theme.onOrange : Theme.ink
        return configuration.label
            .textCase(.uppercase).font(Theme.semi(big ? 14 : 12))
            .foregroundColor(text)
            .padding(.horizontal, big ? 28 : 14).frame(minWidth: big ? 230 : 30, minHeight: big ? 44 : 32)
            .background(RoundedRectangle(cornerRadius: 4).fill(fill))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(accent && enabled ? Theme.orange : Color.clear, lineWidth: 2))
            .background(RoundedRectangle(cornerRadius: 4).fill(shadow).offset(y: pressed ? 0 : 2))
            .offset(y: pressed ? 2 : 0)
            .contentShape(Rectangle())
    }
}

// four blocks of different sizes: a tiny picture of the Storage map
struct MapIcon: View {
    var body: some View {
        ZStack(alignment: .topLeading) {
            Rectangle().fill(Theme.ink).frame(width: 7, height: 12)
            Rectangle().fill(Theme.ink).frame(width: 6, height: 7).offset(x: 8)
            Rectangle().fill(Theme.ink).frame(width: 3, height: 4).offset(x: 8, y: 8)
            Rectangle().fill(Theme.orange).frame(width: 2, height: 4).offset(x: 12, y: 8)
        }.frame(width: 14, height: 12, alignment: .topLeading)
    }
}

struct Hatch: View {
    var ink = Theme.orange
    var ground = Theme.track
    var body: some View {
        Canvas { ctx, size in
            var p = Path(); var x: CGFloat = -size.height
            while x < size.width { p.move(to: CGPoint(x: x, y: size.height)); p.addLine(to: CGPoint(x: x + size.height, y: 0)); x += 5 }
            ctx.stroke(p, with: .color(ink), lineWidth: 1.5)
        }
        .background(ground).clipped()
    }
}

// A tick box: empty (an ink outline), all (orange with a tick), some (orange with a dash, files picked by hand),
// grey when there's nothing to clean, or greyed when something around it decides (locked).
struct Check: View {
    let on: Bool
    var empty = false
    var partial = false
    var locked = false
    var body: some View {
        let filled = !empty && (on || partial)
        let fill: Color = !filled ? Color.clear : locked ? Theme.line : Theme.orange
        let edge: Color = empty || locked ? Theme.line : filled ? Theme.orange : Theme.ink
        ZStack {
            RoundedRectangle(cornerRadius: 2).fill(fill)
            RoundedRectangle(cornerRadius: 2).stroke(edge, lineWidth: 1.6)
            if filled && partial && !on {
                Rectangle().fill(Theme.onOrange).frame(width: 9, height: 2.4)
            } else if filled {
                Path { p in p.move(to: CGPoint(x: 4, y: 8.5)); p.addLine(to: CGPoint(x: 7.5, y: 12)); p.addLine(to: CGPoint(x: 13, y: 5)) }
                    .stroke(Theme.onOrange, style: StrokeStyle(lineWidth: 2.2, lineCap: .square))
            }
        }
        .frame(width: 17, height: 17)
    }
}

struct Chevron: View {
    var color: Color
    var body: some View {
        Path { p in p.move(to: CGPoint(x: 1, y: 1)); p.addLine(to: CGPoint(x: 7, y: 7)); p.addLine(to: CGPoint(x: 1, y: 13)) }
            .stroke(color, style: StrokeStyle(lineWidth: 1.8, lineCap: .round, lineJoin: .round))
            .frame(width: 9, height: 14)
    }
}

// MARK: - App state

final class AppState: ObservableObject {
    @Published var cats: [Cat] = Engine.build()
    @Published var tier: Level = .safe
    @Published var openID: String?
    @Published var scanning = 0
    @Published var disk = Engine.disk()
    @Published var status = ""
    @Published var freedAll = Engine.freedAllTime()
    @Published var cleaning = false        // the cleaning window is showing
    @Published var cleanRunning = false    // a clean is in progress
    @Published var cleanDone = false
    @Published var cleanStopped = false
    @Published var freed: Int64 = 0
    @Published var trashed: Int64 = 0
    @Published var cleanedFiles: Int64 = 0
    @Published var skipped: Int64 = 0
    @Published var currentItem = ""
    @Published var log: [String] = []
    @Published var expected: Int64 = 1
    @Published var update: ReleaseInfo?
    private var scanGen: [String: Int] = [:]   // per item: which measurement is the newest
    @Published var results: [String] = []      // one plain line per item, shown when a clean is done
    // for the finish: what each item freed, and the disk measured before and after (never a made-up number)
    @Published var freedItems: [FreedItem] = []
    @Published var freeBefore: Int64 = 0
    @Published var freeAfter: Int64 = -1
    @Published var diskTotal: Int64 = 0
    @Published var diskName = ""
    @Published var cleanSeconds: Double = 0
    private var counter: Counter?
    // a few items are measured at once (an SSD answers several folder reads in parallel)
    private let scanQ = DispatchQueue(label: "scan", attributes: .concurrent)
    private let scanSlots = DispatchSemaphore(value: 4)

    @Published var theme = ThemeMode.saved   // "light", "dark" or "system"

    var open: Cat? { cats.first { $0.id == openID } }
    var selectedBytes: Int64 { cats.filter { $0.included }.reduce(0) { $0 + $1.cleanBytes } }
    var notTicked: Int64 { cats.filter { !$0.included && !$0.isSafe && $0.kind != .trash }.reduce(0) { $0 + $1.bytes } }

    func setTheme(_ mode: String) {
        theme = mode
        UserDefaults.standard.set(mode, forKey: ThemeMode.key)
        ThemeMode.apply(mode)
    }

    // What Clean would clean now, as frozen copies: changing ticks, ages or Reset can't alter a running clean, and an
    // item with files picked by hand counts only those.
    func cleanSelection() -> [Cat] {
        cats.filter { ($0.checked && ($0.bytes > 0 || $0.files > 0)) || ($0.partial && $0.pickBytes > 0) }
            .sorted { ($0.kind == .trash ? 0 : 1) < ($1.kind == .trash ? 0 : 1) }
            .map { (c: Cat) -> Cat in
                var s = c
                if c.partial { s.bytes = c.pickBytes; s.files = c.pickFiles; s.name = c.name + " (your picks)" }
                return s
            }
    }

    // The item's tick or its hand-picked files changed (in the item's file list): save and measure it again.
    func picksChanged(_ id: String) {
        guard let i = cats.firstIndex(where: { $0.id == id }) else { return }
        cats[i].pickBytes = -1; cats[i].pickFiles = 0
        save(cats[i]); scan([id])
    }

    // the item's box in the list: picked -> none, none -> all, all -> none (picks are cleared either way)
    func toggle(_ id: String) {
        guard let i = cats.firstIndex(where: { $0.id == id }) else { return }
        let picked = cats[i].partial
        cats[i].checked = !picked && !cats[i].checked
        cats[i].picks = []; cats[i].pickBytes = -1; cats[i].pickFiles = 0
        save(cats[i])
    }

    // The small box on a section's column: ticks every item in it that has something to clean, or unticks them all
    // when all are ticked already (picks are cleared either way). The open section stays as it is.
    func tickSection(_ lvl: Level) {
        let idx = cats.indices.filter { cats[$0].level == lvl && cats[$0].refused == nil && (cats[$0].bytes > 0 || cats[$0].files > 0) }
        guard !idx.isEmpty else { return }
        let on = !idx.allSatisfy { cats[$0].checked }
        for i in idx {
            cats[i].checked = on
            cats[i].picks = []; cats[i].pickBytes = -1; cats[i].pickFiles = 0
            save(cats[i])
        }
    }

    func save(_ c: Cat) {
        let d = UserDefaults.standard
        d.set(c.checked, forKey: "v4.checked.\(c.id)")
        d.set(c.ageMinutes, forKey: "v4.age.\(c.id)")
        d.set(c.lastAge, forKey: "v4.last.\(c.id)")
    }

    func reload() { cats = Engine.build(); scan() }

    func scan(_ ids: [String]? = nil) {
        if cleanRunning { return }   // measured again once the clean is done
        // Safe-to-delete items first, so the ticked total and the CLEAN key are ready first
        let targets = cats.indices.filter { ids == nil || ids!.contains(cats[$0].id) }.sorted { cats[$0].level.rawValue < cats[$1].level.rawValue }
        for i in targets {
            cats[i].scanned = false
            scanGen[cats[i].id, default: 0] += 1
            let gen = scanGen[cats[i].id]!
            let snapshot = cats[i]
            scanning += 1
            scanQ.async { [scanSlots] in
                scanSlots.wait(); defer { scanSlots.signal() }
                let cnt = Counter()
                Engine.process(snapshot, delete: false, toTrash: false, cnt: cnt)
                // with files picked by hand: also what those come to
                var picked: (files: Int64, bytes: Int64) = (0, -1)
                if snapshot.partial {
                    let pc = Counter()
                    Engine.processPicks(snapshot, delete: false, toTrash: false, cnt: pc)
                    picked = (pc.files, pc.bytes)
                }
                let pickedNow = picked
                DispatchQueue.main.async {
                    if self.scanGen[snapshot.id] == gen, let j = self.cats.firstIndex(where: { $0.id == snapshot.id }) {
                        self.cats[j].files = cnt.files; self.cats[j].bytes = cnt.bytes; self.cats[j].scanned = true
                        self.cats[j].pickFiles = pickedNow.files; self.cats[j].pickBytes = pickedNow.bytes
                    }
                    self.scanning -= 1
                    self.status = self.scanning > 0 ? "Scanning \(snapshot.name)…" : (self.freedAll.0 == 0 ? "Quick clean is ticked for you: press Clean, check the list, and it's done." : "Scan done. Click a row to see what it is. Tick the box to include it.")
                    if self.scanning == 0 { self.disk = Engine.disk(); self.freedAll = Engine.freedAllTime() }
                }
            }
        }
    }

    func setAge(_ id: String, _ minutes: Int) {
        guard let i = cats.firstIndex(where: { $0.id == id }) else { return }
        cats[i].ageMinutes = minutes; if minutes > 0 { cats[i].lastAge = minutes }
        save(cats[i]); scan([id])
    }

    func resetAll() {
        for i in cats.indices {
            cats[i].checked = cats[i].isSafe; cats[i].ageMinutes = cats[i].defaultAge
            cats[i].lastAge = cats[i].defaultAge > 0 ? cats[i].defaultAge : 14 * Engine.day
            cats[i].picks = []; cats[i].pickBytes = -1; cats[i].pickFiles = 0
            save(cats[i])
        }
        scan()
        status = "Back to recommended: Quick clean ticked, the usual ages. Protected folders stay protected."
    }

    func clean(toTrash: Bool, sel: [Cat]) {
        // sel: the frozen copies the confirm sheet showed (cleanSelection); the Trash item always runs first, so files
        // moved to the Trash in this clean are never emptied by it
        guard !sel.isEmpty else { return }
        let cleanedIDs = Set(sel.map { $0.id })
        expected = max(1, sel.reduce(0) { $0 + $1.bytes })
        freed = 0; trashed = 0; cleanedFiles = 0; skipped = 0; log = []
        // the disk before anything goes, for the ring at the end
        let before = Engine.disk()
        freeBefore = before.free; diskTotal = before.total; freeAfter = -1; diskName = Engine.diskName()
        freedItems = []; cleanSeconds = 0
        let started = Date()
        cleanDone = false; cleanStopped = false; cleaning = true; cleanRunning = true
        status = "Cleaning in its own window. You can keep looking around here; Clean is ready again when it's done."
        let cnt = Counter(); counter = cnt
        var pending: [String] = []
        let lock = NSLock()
        cnt.onItem = { path, size, ok, bin in
            let p = path.replacingOccurrences(of: Engine.home.path, with: "~")
            lock.lock(); pending.append((ok ? (bin ? "bin  " : "del  ") + Engine.fmt(size) : "skip") + "  " + p); lock.unlock()
        }
        let timer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { _ in
            lock.lock(); var batch = pending; pending.removeAll()
            let b = cnt.bytes, t = cnt.trashedBytes, f = cnt.files, s = cnt.skipped
            lock.unlock()
            if batch.count > 400 { let dropped = batch.count - 400; batch = ["   … \(dropped) more"] + batch.suffix(400) }
            self.log.append(contentsOf: batch)
            if self.log.count > 3000 { self.log.removeFirst(self.log.count - 3000) }
            self.freed = b; self.trashed = t; self.cleanedFiles = f; self.skipped = s
            // live progress in the main window too, even with the cleaning window minimised
            if self.cleanRunning { self.status = "Cleaning  ·  \(Engine.fmt(b)) freed" + (t > 0 ? " + \(Engine.fmt(t)) to the Trash" : "") + " so far.  You can keep looking around." }
        }
        DispatchQueue.global(qos: .userInitiated).async {
            var summary: [String] = [], lines: [String] = []
            var items: [FreedItem] = []
            for (i, c) in sel.enumerated() where !cnt.cancel {
                DispatchQueue.main.async { self.currentItem = "ITEM \(i + 1) OF \(sel.count)  ·  \(c.name.uppercased())" }
                lock.lock(); let b0 = cnt.bytes, t0 = cnt.trashedBytes, f0 = cnt.files; lock.unlock()
                Engine.process(c, delete: true, toTrash: toTrash, cnt: cnt)   // the confirm window's choice applies to every item
                lock.lock(); let b1 = cnt.bytes, t1 = cnt.trashedBytes, f1 = cnt.files; lock.unlock()
                items.append(FreedItem(name: c.name, bytes: b1 - b0, trashed: t1 - t0))
                summary.append("\(c.name): freed \(Engine.fmt(b1 - b0))" + (t1 > t0 ? " + \(Engine.fmt(t1 - t0)) to Trash" : "") + ", \(f1 - f0) files")
                lines.append("\(c.name)   ·   " + (b1 > b0 || t1 == t0 ? "\(Engine.fmt(b1 - b0)) freed" : "") + (t1 > t0 ? (b1 > b0 ? " + " : "") + "\(Engine.fmt(t1 - t0)) to the Trash" : ""))
            }
            Engine.log((cnt.cancel ? "STOPPED " : "CLEANED ") + "\(Engine.fmt(cnt.bytes)), \(cnt.files) files; skipped \(cnt.skipped). " + summary.joined(separator: " | "))
            let after = Engine.disk()   // the disk once everything has gone
            let seconds = Date().timeIntervalSince(started)
            DispatchQueue.main.async {
                timer.fire(); timer.invalidate()
                self.log.append(contentsOf: summary.map { "— " + $0 })
                self.results = lines
                self.freedItems = items; self.freeAfter = after.free; self.cleanSeconds = seconds
                self.freed = cnt.bytes; self.trashed = cnt.trashedBytes; self.cleanedFiles = cnt.files; self.skipped = cnt.skipped
                self.cleanStopped = cnt.cancel; self.cleanDone = true; self.cleanRunning = false
                // picks are for one clean
                for j in self.cats.indices where cleanedIDs.contains(self.cats[j].id) {
                    self.cats[j].picks = []; self.cats[j].pickBytes = -1; self.cats[j].pickFiles = 0
                }
                self.disk = Engine.disk(); self.scan()
                self.status = "Cleaning finished. The list is being measured again."
            }
        }
    }

    func stop() { counter?.cancel = true }
    func finishCleaning() { cleaning = false }
}

// MARK: - Updates
// Once per launch the app asks GitHub for the latest release of this repository (one small request; nothing about
// you is sent). Installing downloads the new app and its signature, refuses it unless the signature matches the
// project's public key (bundled in this app), swaps the app and reopens it. Without a bundled key it never installs.

struct ReleaseInfo { let version: String; let page: String?; let zipURL: URL; let sigURL: URL }

enum Updater {
    static let repo = "knox-101/space-hunter"
    static var current: String { (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0" }

    static func newer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let l = i < x.count ? x[i] : 0, r = i < y.count ? y[i] : 0
            if l != r { return l > r }
        }
        return false
    }

    static func check(_ done: @escaping (ReleaseInfo?) -> Void) {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!, timeoutInterval: 10)
        req.setValue("SpaceHunter-updater", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: req) { data, _, _ in
            // GitHub's API allows 60 checks an hour per internet address; on shared Wi-Fi (a campus, an office) that runs
            // out, so fall back to the release page itself, which isn't limited that way
            guard let data, let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = obj["tag_name"] as? String else { return checkByPage(done) }
            let v = tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag
            guard newer(v, than: current) else { return done(nil) }
            var zip: URL?, sig: URL?
            for a in (obj["assets"] as? [[String: Any]]) ?? [] {
                guard let n = a["name"] as? String, let u = (a["browser_download_url"] as? String).flatMap(URL.init(string:)) else { continue }
                if n == "SpaceHunter-mac.zip" { zip = u } else if n == "SpaceHunter-mac.zip.sig" { sig = u }
            }
            guard let zip, let sig else { return done(nil) }
            done(ReleaseInfo(version: v, page: obj["html_url"] as? String, zipURL: zip, sigURL: sig))
        }.resume()
    }

    // The newest version from where github.com/.../releases/latest lands (e.g. .../releases/tag/v1.4.0); its files always
    // sit at the standard download addresses.
    static func checkByPage(_ done: @escaping (ReleaseInfo?) -> Void) {
        var req = URLRequest(url: URL(string: "https://github.com/\(repo)/releases/latest")!, timeoutInterval: 10)
        req.setValue("SpaceHunter-updater", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { _, resp, _ in
            let prefix = "/\(repo)/releases/tag/"
            guard let u = resp?.url, u.host == "github.com", u.scheme == "https", u.path.hasPrefix(prefix) else { return done(nil) }
            let tag = String(u.path.dropFirst(prefix.count))
            guard !tag.isEmpty, tag.allSatisfy({ $0.isNumber || $0 == "." || $0 == "v" || $0 == "V" }) else { return done(nil) }
            let final = "https://github.com" + u.path
            let v = tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag
            guard newer(v, than: current),
                  let zip = URL(string: "https://github.com/\(repo)/releases/download/\(tag)/SpaceHunter-mac.zip"),
                  let sig = URL(string: "https://github.com/\(repo)/releases/download/\(tag)/SpaceHunter-mac.zip.sig") else { return done(nil) }
            done(ReleaseInfo(version: v, page: final, zipURL: zip, sigURL: sig))
        }.resume()
    }

    static func publicKey() -> SecKey? {
        guard let url = Bundle.main.url(forResource: "update-key", withExtension: "der"), let der = try? Data(contentsOf: url) else { return nil }
        let attrs: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeRSA, kSecAttrKeyClass as String: kSecAttrKeyClassPublic]
        return SecKeyCreateWithData(der as CFData, attrs as CFDictionary, nil)
    }

    static func verify(_ data: Data, _ sig: Data, key: SecKey) -> Bool {
        SecKeyVerifySignature(key, .rsaSignatureMessagePKCS1v15SHA256, data as CFData, sig as CFData, nil)
    }

    // Returns nil when the new version is opening (the caller quits), or a sentence saying why it didn't update.
    static func install(_ r: ReleaseInfo, say: @escaping (String) -> Void) -> String? {
        guard let key = publicKey() else { return "This copy wasn't built with the release key, so it can't check updates. Download the new version from GitHub instead." }
        do {
            say("Downloading Space Hunter \(r.version) …")
            let zip = try Data(contentsOf: r.zipURL), sig = try Data(contentsOf: r.sigURL)
            say("Checking its signature …")
            guard verify(zip, sig, key: key) else { return "The download's signature didn't match, so it wasn't installed. Nothing was changed." }
            say("Installing …")
            let fm = FileManager.default
            let dir = fm.temporaryDirectory.appendingPathComponent("SpaceHunter-update-\(UUID().uuidString)")
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
            let zipFile = dir.appendingPathComponent("update.zip")
            try zip.write(to: zipFile)
            let unzip = Process()
            unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            unzip.arguments = ["-x", "-k", zipFile.path, dir.path]
            try unzip.run(); unzip.waitUntilExit()
            let fresh = dir.appendingPathComponent("Space Hunter.app")
            guard unzip.terminationStatus == 0, fm.fileExists(atPath: fresh.path) else { return "The update didn't unpack. Nothing was changed." }
            let here = Bundle.main.bundleURL
            _ = try fm.replaceItemAt(here, withItemAt: fresh)
            // reopen once this copy has quit
            let reopen = Process()
            reopen.executableURL = URL(fileURLWithPath: "/bin/sh")
            reopen.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", here.path]
            try reopen.run()
            return nil
        } catch {
            return "The update didn't install (\(error.localizedDescription)). Nothing was changed."
        }
    }
}

struct UpdateBar: View {
    @EnvironmentObject var state: AppState
    let release: ReleaseInfo
    @State private var message: String?
    @State private var busy = false
    var body: some View {
        HStack(spacing: 8) {
            Circle().fill(Theme.orange).frame(width: 10, height: 10).padding(.trailing, 8)
            VStack(alignment: .leading, spacing: 3) {
                Text("Space Hunter \(release.version) is ready").font(Theme.semi(14)).foregroundColor(Theme.screenInk)
                Text(message ?? "You have \(Updater.current). Updating takes a few seconds and keeps your settings.")
                    .font(Theme.sans(12)).foregroundColor(Theme.screenMuted).lineLimit(2)
            }
            Spacer()
            Button("What's new") { if let p = release.page, let u = URL(string: p) { NSWorkspace.shared.open(u) } }
                .buttonStyle(KeyStyle()).disabled(busy)
            Button("Later") { state.update = nil }.buttonStyle(KeyStyle()).disabled(busy)
            Button(message == nil ? "Update & relaunch" : "Try again") {
                busy = true
                DispatchQueue.global(qos: .userInitiated).async {
                    let why = Updater.install(release) { m in DispatchQueue.main.async { message = m } }
                    DispatchQueue.main.async {
                        if why == nil { NSApp.terminate(nil) } else { message = why; busy = false }
                    }
                }
            }
            .buttonStyle(KeyStyle(primary: true)).disabled(busy)
        }
        .padding(.horizontal, 20).padding(.vertical, 14)
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
        .padding(.horizontal, 18).padding(.vertical, 10)
    }
}

// MARK: - App

@main
struct SpaceHunterApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate   // applies the saved theme at launch
    @StateObject private var state = AppState()
    init() {
        // test hook: SpaceHunter --test-verify <file> <sig> <key.der>  (prints and exits; used by CI)
        let a = CommandLine.arguments
        if let i = a.firstIndex(of: "--test-verify"), a.count > i + 3,
           let d = try? Data(contentsOf: URL(fileURLWithPath: a[i + 1])), let s = try? Data(contentsOf: URL(fileURLWithPath: a[i + 2])),
           let k = try? Data(contentsOf: URL(fileURLWithPath: a[i + 3])),
           let key = SecKeyCreateWithData(k as CFData, [kSecAttrKeyType as String: kSecAttrKeyTypeRSA, kSecAttrKeyClass as String: kSecAttrKeyClassPublic] as CFDictionary, nil) {
            let ok = Updater.verify(d, s, key: key)
            print(ok ? "SIGNATURE OK" : "SIGNATURE BAD"); exit(ok ? 0 : 1)
        }
        Fonts.register()
    }
    var body: some Scene {
        WindowGroup("Space Hunter") {
            MainView().environmentObject(state).frame(minWidth: 1000, minHeight: 680)
        }
        .windowStyle(.hiddenTitleBar)
        Window("Storage map", id: "finder") {
            FinderView().environmentObject(state)
        }
        .defaultSize(width: 940, height: 760)
        Window("Cleaning", id: "cleaning") {
            ProgressSheet().environmentObject(state)
        }
        .defaultSize(width: 780, height: 560)
        Window("Protected folders", id: "protect") {
            ProtectView().environmentObject(state)
        }
        .defaultSize(width: 720, height: 600)
    }
}

// MARK: - Main window

struct MainView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow
    @State private var confirm = false
    @StateObject private var guide = GuideModel()

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Spacer()
                TopKeys(guide: { startGuide() })   // the guide key, then Light / Dark / System
            }
                .padding(.horizontal, 18).padding(.top, 6).padding(.bottom, 6)
            DisplayView().guidePart(0).padding(.horizontal, 18)
            HStack(spacing: 8) {
                Button { openWindow(id: "finder") } label: { HStack(spacing: 8) { MapIcon(); Text("Storage map") } }.buttonStyle(KeyStyle(accent: true))
                    .guidePart(2)
                Spacer()
                Button("Scan again") { state.scan() }.buttonStyle(KeyStyle()).disabled(state.scanning > 0 || state.cleanRunning)
                Button("Protected folders") { openWindow(id: "protect") }.buttonStyle(KeyStyle())
                    .guidePart(5)
                Button("Back to recommended") { state.resetAll() }.buttonStyle(KeyStyle())
                    .guidePart(6)
                    .padding(.trailing, 4)
                Button(state.cleanRunning ? "Show cleaning" : "Clean \(Engine.fmt(state.selectedBytes))") {
                    if state.cleaning { openWindow(id: "cleaning") } else { confirm = true }
                }
                .buttonStyle(KeyStyle(primary: true, big: true))
                .disabled(!state.cleaning && (state.scanning > 0 || state.selectedBytes == 0))
                .guidePart(3)
            }
            .padding(.horizontal, 18).padding(.vertical, 12)
            TierBar().guidePart(10).padding(.horizontal, 18).padding(.bottom, 4)
            HStack(spacing: 0) {
                CategoryList()
                if let c = state.open {
                    DetailPanel(cat: c)
                        .frame(width: 440)
                        .guidePart(13)
                        .transition(.move(edge: .trailing))
                }
            }
            .animation(.easeOut(duration: 0.26), value: state.openID)
            if let u = state.update { UpdateBar(release: u) } else {
                Rectangle().fill(Theme.line).frame(height: 1)
                HStack {
                    Text(state.status).font(Theme.sans(11)).foregroundColor(Theme.muted)
                    Spacer()
                    Text("Space Hunter \(Updater.current)").font(Theme.sans(10.5)).foregroundColor(Theme.soft)   // which version this is
                }
                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 22).padding(.vertical, 8)
            }
        }
        .background(Theme.chassis)
        .background(WindowReader { w in guide.window = w })
        // the first-run guide: the window dims and a lit cut-out sits on each real part in turn
        .overlayPreferenceValue(GuideAnchors.self) { anchors in
            GeometryReader { g in
                if guide.step != nil {
                    GuideOverlay(guide: guide, targets: guideTargets(g, anchors), size: g.size)
                }
            }
        }
        .ignoresSafeArea(.container, edges: .top)
        .onAppear {
            ThemeMode.apply(state.theme)
            state.scan()
            Updater.check { r in DispatchQueue.main.async { state.update = r } }   // one quiet check; offline = nothing happens
            // what a guide step does before it points: open the biggest item's panel, or close the panel
            let s = state
            guide.enter = { step in
                if step == 3 {
                    let items = s.cats.filter { $0.level == s.tier && ($0.bytes > 0 || $0.files > 0) }
                    if let first = items.max(by: { $0.bytes < $1.bytes }) { s.openID = first.id }
                } else if step == 1 || step == 2 || step == 4 {
                    s.openID = nil
                }
            }
        }
        .sheet(isPresented: $confirm) { ConfirmSheet(confirm: $confirm).environmentObject(state) }
        .onChange(of: state.cleaning) { c in if c { openWindow(id: "cleaning") } }
        // once, the first time a scan finishes (never during a clean); if another window is in front, when this one is
        .onChange(of: state.scanning) { n in if n == 0 { autoGuide() } }
    }

    func startGuide() {
        state.openID = nil   // the item panel closes first
        guide.start()
    }

    func autoGuide() {
        let s = state
        guide.autoStart(when: { !s.cleaning && !s.cleanRunning }, before: { s.openID = nil })
    }

    // The parts the nine steps light, in order: the display; the section rail; the first three rows under the list's
    // header; the item panel; the Storage map key; Protected folders; Back to recommended; Clean; the top-right keys.
    func guideTargets(_ g: GeometryProxy, _ a: [Int: Anchor<CGRect>]) -> [CGRect?] {
        func part(_ i: Int) -> CGRect? { a[i].map { g[$0] } }
        let rail: CGRect? = part(10).map { $0.insetBy(dx: -10, dy: 0) }
        var rows: CGRect? = nil
        if let l = part(11) {
            let top: CGFloat = max(l.minY, part(12).map { $0.maxY } ?? (l.minY + 110))
            let h: CGFloat = max(20, min(l.maxY - top, 3 * 56))
            rows = CGRect(x: l.minX + 8, y: top, width: l.width - 16, height: h)
        }
        return [part(0), rail, rows, part(13), part(2), part(5), part(6), part(3), part(9)]
    }
}

// Top right: the guide key, then Light / Dark / System as three icon keys. Hovering one names it beside them.
struct TopKeys: View {
    @EnvironmentObject var state: AppState
    let guide: () -> Void
    @State private var hover: Int? = nil   // 0...2 theme, 3 guide
    let modes = ["light", "dark", "system"]
    let names = ["Light", "Dark", "System default"]
    var body: some View {
        let tip: String = hover == nil ? "" : hover == 3 ? "Guide: how Space Hunter works" : names[hover!] + (state.theme == modes[hover!] ? "  (on)" : "")
        HStack(spacing: 10) {
            Text(tip).font(Theme.sans(11.5)).foregroundColor(Theme.ink).lineLimit(1)
            HStack(spacing: 10) {
                // the guide key: a small key of its own
                TopIcon(kind: 3, ink: Theme.ink)
                    .frame(width: 26, height: 24)
                    .background(RoundedRectangle(cornerRadius: 4).fill(hover == 3 ? Theme.hover : Theme.key))
                    .background(RoundedRectangle(cornerRadius: 4).fill(Theme.keyShadow).offset(y: 2))
                    .contentShape(Rectangle())
                    .onHover { h in if h { hover = 3 } else if hover == 3 { hover = nil } }
                    .onTapGesture { guide() }
                // the theme group: one strip, the chosen one lit
                HStack(spacing: 0) {
                    ForEach(0..<3, id: \.self) { i in
                        let on = state.theme == modes[i]
                        TopIcon(kind: i, ink: on ? Theme.pane : Theme.muted)
                            .frame(width: 26, height: 24)
                            .background(RoundedRectangle(cornerRadius: 3).fill(on ? Theme.ink : (hover == i ? Theme.hover : Color.clear)).padding(2.5))
                            .contentShape(Rectangle())
                            .onHover { h in if h { hover = i } else if hover == i { hover = nil } }
                            .onTapGesture { state.setTheme(modes[i]) }
                    }
                }
                .background(RoundedRectangle(cornerRadius: 4).fill(Theme.key))
                .background(RoundedRectangle(cornerRadius: 4).fill(Theme.keyShadow).offset(y: 2))
            }
            .guidePart(9)
        }
    }
}

// The small icons on the top-right keys: 0 sun, 1 crescent moon, 2 half-filled circle (System), 3 "?" in a circle.
struct TopIcon: View {
    let kind: Int
    let ink: Color
    var body: some View {
        Canvas { ctx, size in
            let cx = size.width / 2, cy = size.height / 2
            let line = StrokeStyle(lineWidth: 1.6, lineCap: .round)
            switch kind {
            case 0:
                ctx.fill(Path(ellipseIn: CGRect(x: cx - 3.6, y: cy - 3.6, width: 7.2, height: 7.2)), with: .color(ink))
                var rays = Path()
                for k in 0..<8 {
                    let a = Double(k) * Double.pi / 4
                    rays.move(to: CGPoint(x: cx + CGFloat(cos(a)) * 6, y: cy + CGFloat(sin(a)) * 6))
                    rays.addLine(to: CGPoint(x: cx + CGFloat(cos(a)) * 8.2, y: cy + CGFloat(sin(a)) * 8.2))
                }
                ctx.stroke(rays, with: .color(ink), style: line)
            case 1:
                var c = ctx
                c.clip(to: Path(ellipseIn: CGRect(x: cx - 2.2, y: cy - 9.2, width: 12, height: 12)), options: .inverse)
                c.fill(Path(ellipseIn: CGRect(x: cx - 6.5, y: cy - 6.5, width: 13, height: 13)), with: .color(ink))
            case 2:
                ctx.stroke(Path(ellipseIn: CGRect(x: cx - 6.5, y: cy - 6.5, width: 13, height: 13)), with: .color(ink), style: line)
                var half = Path()
                half.move(to: CGPoint(x: cx, y: cy + 6.5))
                for k in 0...24 {   // the left half, from the bottom round to the top
                    let a = (90.0 + 180.0 * Double(k) / 24) * Double.pi / 180
                    half.addLine(to: CGPoint(x: cx + 6.5 * CGFloat(cos(a)), y: cy + 6.5 * CGFloat(sin(a))))
                }
                half.closeSubpath()
                ctx.fill(half, with: .color(ink))
            default:
                ctx.stroke(Path(ellipseIn: CGRect(x: cx - 7.5, y: cy - 7.5, width: 15, height: 15)), with: .color(ink), style: line)
                ctx.draw(Text("?").font(Theme.bold(11)).foregroundColor(ink), at: CGPoint(x: cx, y: cy), anchor: .center)
            }
        }
    }
}

struct DisplayView: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        let p = Engine.parts(state.selectedBytes)
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(state.scanning > 0 ? "Scanning …" : "Space to free up")
                        .font(Theme.semi(13)).foregroundColor(Color(hex: 0xB4B1AA))
                    HStack(alignment: .center, spacing: 16) {
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Text(p.0).font(Theme.display(76)).padding(.leading, -7)   // the glyph side bearing: the digit lines up with the label and bar
                            Text(p.1).font(Theme.display(28))
                        }.foregroundColor(Theme.orange)
                        // beside the number: what kind of space it is, and what else is there if you look
                        if state.scanning == 0 && state.selectedBytes > 0 {
                            let onlySafe = !state.cats.contains { $0.included && !$0.isSafe && ($0.bytes > 0 || $0.files > 0) }
                            VStack(alignment: .leading, spacing: 4) {
                                Text(onlySafe ? (state.freedAll.0 == 0 ? "all safe to clean  ·  press Clean to start" : "all safe to clean") : "includes items to check first")
                                    .font(Theme.semi(13)).foregroundColor(Theme.screenInk)
                                if state.notTicked > 0 {
                                    Text("+ \(Engine.fmt(state.notTicked)) more you can check yourself").font(Theme.semi(13)).foregroundColor(Theme.screenMuted)
                                }
                            }
                        }
                    }
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 6) {
                    Text("Disk").font(Theme.semi(13)).foregroundColor(Color(hex: 0xB4B1AA))
                    Text("\(Engine.fmt(state.disk.free)) free of \(Engine.fmt(state.disk.total))").font(Theme.sans(14)).foregroundColor(Color(hex: 0xB4B1AA))
                    if state.freedAll.0 > 0 {
                        Text("Freed so far: \(Engine.fmt(state.freedAll.0)) in \(state.freedAll.1) clean" + (state.freedAll.1 == 1 ? "" : "s"))
                            .font(Theme.sans(12)).foregroundColor(Theme.screenMuted)
                    }
                }
            }
            DiskBar(total: state.disk.total, free: state.disk.free, selected: state.selectedBytes, notTicked: state.notTicked)
                .frame(height: 16).padding(.top, 16)
            HStack(spacing: 22) {
                legend(AnyView(Rectangle().fill(Theme.orange)), "Ticked: \(Engine.fmt(state.selectedBytes))", Theme.screenInk)
                legend(AnyView(Hatch()), "Not ticked: \(Engine.fmt(state.notTicked))", Theme.screenInk)
                legend(AnyView(Rectangle().fill(Theme.used)), "Used", Theme.screenMuted)
                legend(AnyView(Rectangle().fill(Theme.track).overlay(Rectangle().stroke(Theme.screenMuted, lineWidth: 1))), "Free", Theme.screenMuted)
            }
            .padding(.top, 12)
        }
        .padding(.horizontal, 22).padding(.vertical, 18)
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
    }

    func legend(_ swatch: AnyView, _ text: String, _ ink: Color) -> some View {
        HStack(spacing: 7) {
            swatch.frame(width: 13, height: 13)
            Text(text).font(Theme.semi(13)).foregroundColor(ink)
        }
    }
}

// The disk as one bar: used | ticked (orange, with a caret) | not ticked (hatched) | free.
struct DiskBar: View {
    let total: Int64, free: Int64, selected: Int64, notTicked: Int64
    var body: some View {
        GeometryReader { g in
            let w = g.size.width
            let t = Double(max(total, 1))
            let used = Double(total - free) / t
            let sel = min(used, Double(selected) / t)
            let rev = min(used - sel, Double(notTicked) / t)
            let wSel: CGFloat = sel > 0 ? max(4, w * CGFloat(sel)) : 0
            let wRev: CGFloat = rev > 0 ? max(4, w * CGFloat(rev)) : 0
            let wRest: CGFloat = max(0, w * CGFloat(used) - wSel - wRev)
            ZStack(alignment: .topLeading) {
                HStack(spacing: 0) {
                    Rectangle().fill(Theme.used).frame(width: wRest)
                    Rectangle().fill(Theme.orange).frame(width: wSel)
                    Hatch().frame(width: wRev)
                    Rectangle().fill(Theme.track)
                }
                if sel > 0 {
                    Path { p in
                        p.move(to: CGPoint(x: -6, y: -11)); p.addLine(to: CGPoint(x: 6, y: -11)); p.addLine(to: CGPoint(x: 0, y: -3)); p.closeSubpath()
                    }
                    .fill(Theme.orange).offset(x: wRest + wSel / 2)
                }
            }
        }
    }
}

// The three sections as one rail: three equal columns over a thin line. Each names its section, has a small box for
// ticking the whole section, says how much of it is ticked, and shows its size with what's in it beside the number.
// An orange marker on the rail sits under the open section and slides over when you pick another.
struct TierBar: View {
    @EnvironmentObject var state: AppState
    @State private var hover: Level? = nil
    var body: some View {
        GeometryReader { g in
            let w = g.size.width / 3
            ZStack(alignment: .bottomLeading) {
                Rectangle().fill(Theme.line).frame(height: 2).padding(.bottom, 1)
                HStack(spacing: 0) {
                    ForEach(Level.allCases, id: \.self) { lvl in
                        TierColumn(lvl: lvl, hover: $hover).frame(width: w, height: g.size.height)
                    }
                }
                Rectangle().fill(Theme.orange).frame(width: w, height: 5)
                    .offset(x: w * CGFloat(state.tier.rawValue))
                    .animation(.easeOut(duration: 0.32), value: state.tier)
            }
        }
        .frame(height: 82)
    }
}

struct TierColumn: View {
    @EnvironmentObject var state: AppState
    let lvl: Level
    @Binding var hover: Level?
    @State private var boxHover = false
    var body: some View {
        let items = state.cats.filter { $0.level == lvl }
        let total = items.reduce(Int64(0)) { $0 + $1.bytes }
        let ticked = items.filter { $0.included }.reduce(Int64(0)) { $0 + $1.cleanBytes }
        let nTicked = items.filter { $0.included && ($0.bytes > 0 || $0.files > 0) }.count
        let nItems = items.filter { $0.bytes > 0 || $0.files > 0 }.count
        let on = state.tier == lvl
        let hot = hover == lvl && !on
        let headInk: Color = on || hot ? Theme.ink : Theme.muted
        let headFont: Font = on ? Theme.semi(13) : Theme.sans(13)
        // the space, not a count: "none ticked", "all 17 MB ticked", "14 of 17 MB ticked"
        let sub: String = nItems == 0 ? "nothing to clean" : nTicked == 0 ? "none ticked"
            : nTicked == nItems && ticked >= total ? "all \(Engine.fmt(total)) ticked" : TierColumn.of(ticked, total) + " ticked"
        let boxState: Int = nTicked == 0 ? 0 : nTicked == nItems ? 2 : 1
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .center, spacing: 0) {
                ZStack(alignment: .leading) {   // sized by the semibold name, so the box never shifts when the open section changes
                    Text(Theme.tierName(lvl)).font(Theme.semi(13)).lineLimit(1).fixedSize().hidden()
                    Text(Theme.tierName(lvl)).font(headFont).foregroundColor(headInk).lineLimit(1).fixedSize()
                }
                if nItems > 0 {
                    // the box and its words are one target (reaching 5 pt round them): ticks the whole section, or
                    // unticks it when all of it is ticked; the open section stays as it is
                    HStack(alignment: .center, spacing: 6) {
                        SectionBox(state: boxState, ink: on || hot ? Theme.ink : Theme.soft)
                        Text(sub).font(headFont).foregroundColor(headInk).lineLimit(1).underline(boxHover)
                    }
                    .padding(5).contentShape(Rectangle())
                    .onHover { boxHover = $0 }
                    .onTapGesture { state.tickSection(lvl) }
                    .padding(-5)
                    .padding(.leading, 10)
                } else {
                    Text(sub).font(headFont).foregroundColor(headInk).lineLimit(1).padding(.leading, 10)
                }
            }
            HStack(alignment: .center, spacing: 8) {
                // the open section's size is ink: orange means "ticked" in the display
                Text(Engine.fmt(total)).font(Theme.display(34)).foregroundColor(on || hot ? Theme.ink : Theme.muted).padding(.leading, -3)
                    .lineLimit(1).fixedSize().layoutPriority(1)
                Text(Theme.keepLastTwo(Theme.tierDesc(lvl))).font(Theme.sans(12)).foregroundColor(on ? Theme.muted : Theme.soft)
                    .lineLimit(2).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.trailing, 14).padding(.top, 6).padding(.bottom, 12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .contentShape(Rectangle())
        .onHover { h in if h { hover = lvl } else if hover == lvl { hover = nil } }
        .onTapGesture {
            state.tier = lvl
            if let o = state.open, o.level != lvl { state.openID = nil }
        }
    }

    // "14 of 17 MB" when both share a unit, else "900 MB of 2.1 GB"
    static func of(_ a: Int64, _ b: Int64) -> String {
        let x = Engine.parts(a), y = Engine.parts(b)
        return (x.1 == y.1 ? x.0 : Engine.fmt(a)) + " of " + Engine.fmt(b)
    }
}

// A section's small box: an outline when none of it is ticked, orange with a dash when some, with a tick when all.
struct SectionBox: View {
    let state: Int   // 0 none, 1 some, 2 all
    let ink: Color
    var body: some View {
        ZStack {
            if state == 0 {
                RoundedRectangle(cornerRadius: 2).strokeBorder(ink, lineWidth: 1.4)
            } else {
                RoundedRectangle(cornerRadius: 2).fill(Theme.orange)
                if state == 2 {
                    Path { p in p.move(to: CGPoint(x: 3, y: 7.2)); p.addLine(to: CGPoint(x: 6, y: 10.2)); p.addLine(to: CGPoint(x: 11.2, y: 3.8)) }
                        .stroke(Theme.onOrange, style: StrokeStyle(lineWidth: 1.7, lineCap: .square))
                } else {
                    Rectangle().fill(Theme.onOrange).frame(width: 7, height: 2)
                }
            }
        }
        .frame(width: 14, height: 14)
    }
}

struct CategoryList: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        let idx = state.cats.indices.filter { state.cats[$0].level == state.tier }
        let full = idx.filter { !isEmpty(state.cats[$0]) }.sorted { state.cats[$0].bytes > state.cats[$1].bytes }
        let empty = idx.filter { isEmpty(state.cats[$0]) }
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    HStack {
                        Text(Theme.tierName(state.tier).uppercased()).font(Theme.bold(13)).foregroundColor(Theme.ink)
                        Spacer()
                        Button("Tick all") { setAll(true, idx) }.buttonStyle(.plain).font(Theme.semi(12))
                        Button("Untick all") { setAll(false, idx) }.buttonStyle(.plain).font(Theme.semi(12)).padding(.leading, 14)
                    }
                    .padding(.horizontal, 18).padding(.top, 12)
                    // what this section is, in one plain line
                    Text(Theme.keepLastTwo(Theme.tierExplain(state.tier))).font(Theme.sans(12)).foregroundColor(Theme.muted)
                        .frame(maxWidth: 820, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 18).padding(.top, 8)
                    // the call to action: its own line, an orange bar and orange-deep words
                    HStack(alignment: .center, spacing: 7) {
                        Rectangle().fill(Theme.orange).frame(width: 3, height: 17)
                        Text(Theme.tierCta(state.tier)).font(Theme.semi(13)).foregroundColor(Theme.orangeDeep).lineLimit(1)
                    }
                    .padding(.horizontal, 18).padding(.vertical, 8)
                    Rectangle().fill(Theme.line).frame(height: 1).padding(.horizontal, 18)
                }
                .guidePart(12)
                ForEach(full, id: \.self) { i in Row(index: i, compact: false) }
                if !empty.isEmpty {
                    Text("Nothing to clean right now (\(empty.count))").font(Theme.sans(12)).foregroundColor(Theme.soft)
                        .padding(.horizontal, 18).padding(.top, 14).padding(.bottom, 6)
                    ForEach(empty, id: \.self) { i in Row(index: i, compact: true) }
                }
            }
            .padding(.bottom, 12)
        }
        .background(Theme.chassis)
        .guidePart(11)
    }

    func isEmpty(_ c: Cat) -> Bool { c.refused == nil && c.scanned && c.bytes == 0 && c.files == 0 }
    func setAll(_ on: Bool, _ idx: [Int]) {
        for i in idx where !isEmpty(state.cats[i]) {
            state.cats[i].checked = on
            state.cats[i].picks = []; state.cats[i].pickBytes = -1; state.cats[i].pickFiles = 0
            state.save(state.cats[i])
        }
    }
}

struct Row: View {
    @EnvironmentObject var state: AppState
    let index: Int
    let compact: Bool
    @State private var hovering = false
    var body: some View {
        let c = state.cats[index]
        let empty = c.refused != nil || (c.scanned && c.bytes == 0 && c.files == 0)
        let isOpen = state.openID == c.id
        let ageNow: String = c.ageMinutes > 0 ? "  ·  older than " + Engine.fmtAge(c.ageMinutes) : "  ·  everything"
        let age: String = c.ageChoice ? ageNow : (c.kind == .contents || c.kind == .subfolders) ? "  ·  all of it" : ""
        // picked by hand: the item's short text, then a chip "25 of 312 files  ·  1.2 GB of 4.8 GB"
        let pickFilesText: String = c.pickFiles <= c.files ? "\(c.pickFiles) of \(c.files) files"
            : "\(c.pickFiles) file" + (c.pickFiles == 1 ? "" : "s")
        let pickSizeText: String = c.pickBytes <= c.bytes ? Engine.fmt(c.pickBytes) + " of " + Engine.fmt(c.bytes) : Engine.fmt(c.pickBytes)
        let chip: String = c.pickBytes < 0 ? "measuring your picks…" : pickFilesText + "  ·  " + pickSizeText
        let pickSize: String = c.pickBytes < 0 ? "…" : Engine.fmt(c.pickBytes)
        let size: String = !c.scanned ? "…" : c.partial ? pickSize : Engine.fmt(c.bytes)
        let fileCount: String = "\(c.files) file" + (c.files == 1 ? "" : "s")
        let files: String = !c.scanned ? "" : c.partial ? "picked" : fileCount
        HStack(alignment: .center, spacing: 14) {
            // an item with files picked by hand draws as ticked
            Check(on: c.checked || c.partial, empty: empty)
                .contentShape(Rectangle())
                .onTapGesture { if !empty { state.toggle(c.id) } }
            VStack(alignment: .leading, spacing: 3) {
                Text(c.name).font(isOpen ? Theme.bold(14) : Theme.semi(14)).foregroundColor(compact ? Theme.soft : Theme.ink).lineLimit(1)
                if !compact {
                    if c.partial {
                        // the chip keeps its full width; the short text gives way
                        HStack(spacing: 8) {
                            Text(c.short).font(Theme.sans(11.5)).foregroundColor(Theme.soft).lineLimit(1)
                            Text(chip).font(Theme.semi(11)).foregroundColor(Theme.screenInk).lineLimit(1)
                                .padding(.horizontal, 6).padding(.vertical, 1)
                                .background(RoundedRectangle(cornerRadius: 3).fill(Theme.screen))
                                .fixedSize()
                                .layoutPriority(1)
                        }
                    } else {
                        Text(c.short + age).font(Theme.sans(11.5)).foregroundColor(Theme.soft).lineLimit(1)
                    }
                }
            }
            Spacer(minLength: 12)
            VStack(alignment: .trailing, spacing: 3) {
                Text(size).font(Theme.semi(14)).foregroundColor(empty ? Theme.soft : Theme.ink)
                if !compact { Text(files).font(Theme.sans(11)).foregroundColor(Theme.soft) }
            }
            Chevron(color: isOpen ? Theme.orange : (hovering ? Theme.ink : Theme.soft))
        }
        .padding(.horizontal, 18).frame(height: compact ? 40 : 56)
        .background(isOpen ? Theme.selected : (hovering ? Theme.hover : Color.clear))
        .overlay(Rectangle().fill(Theme.line).frame(height: 1).padding(.horizontal, 18), alignment: .bottom)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { state.openID = isOpen ? nil : c.id }
    }
}

struct DetailPanel: View {
    @EnvironmentObject var state: AppState
    let cat: Cat
    @State private var ageValue = 14
    @State private var unit = 2   // 0 minutes, 1 hours, 2 days
    @State private var more = false   // the full notes, shown by "More about this item"

    var body: some View {
        let c = state.cats.first { $0.id == cat.id } ?? cat
        let older = c.ageMinutes > 0
        // every item that cleans folders shows what's in them (a single file you added has nothing inside)
        let folders = c.roots.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true }.map { $0.path }
        let hasInside = c.kind != .trash && c.refused == nil && !folders.isEmpty
        let showAge = c.ageChoice && c.refused == nil && !c.partial
        let stats: String = c.refused != nil ? "Space Hunter won't clean this: \(c.refused!). Use \u{201C}Remove from cleanup list\u{201D} below."
            : c.scanned ? "\(Engine.fmt(c.bytes))   ·   \(c.files) file" + (c.files == 1 ? "" : "s") : "Scanning…"
        ScrollView {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(Theme.tierName(c.level)).font(Theme.semi(12)).foregroundColor(Theme.muted)
                    Spacer()
                    Button("Close") { state.openID = nil }.buttonStyle(KeyStyle())
                }
                Text(c.name).font(Theme.bold(19)).foregroundColor(Theme.ink).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                Text(stats).font(Theme.sans(13)).foregroundColor(Theme.ink).padding(.bottom, 6)
                // three short lines always in view; the full notes open with "More about this item"
                VStack(alignment: .leading, spacing: 4) {
                    summaryLine("What it is", DetailPanel.first(c.what))
                    summaryLine("What goes", goes(c, hasInside: hasInside))
                    summaryLine("After", DetailPanel.first(c.after))
                }
                Button { more.toggle() } label: { Text(more ? "Less about this item" : "More about this item").underline() }
                    .buttonStyle(.plain).font(Theme.semi(12.5)).foregroundColor(Theme.ink).padding(.bottom, 10)
                if more {
                    Group {
                        if c.isCustom, let root = c.roots.first { FolderAbout(root: root.path) } else { section("What is it?", c.what) }
                        section("What gets cleaned", c.deletes)
                        section("Is it safe?", c.safeText)
                        section("What happens after", c.after)
                    }
                }
                Group {
                    if showAge { AgeControls(c: c, older: older, ageValue: $ageValue, unit: $unit) }
                    if hasInside {
                        InsideList(roots: folders, catID: c.id, isCustom: c.isCustom, exts: c.extensions, topOnly: c.topLevelOnly, exclude: c.exclude)
                    }
                }
                if !c.isCustom && c.kind == .trash { FolderKeeps(cat: c) }   // the list above already shows the folders
                if c.isCustom {
                    Button("Remove from cleanup list") {
                        for r in c.roots { Custom.remove(r.path) }
                        state.openID = nil; state.reload()
                        state.status = "Removed from the cleanup list: \(c.name)"
                    }.buttonStyle(KeyStyle()).padding(.top, 8)
                }
            }
            .padding(.horizontal, 26).padding(.vertical, 20)
        }
        .id(cat.id)   // a different item starts at the top; re-measuring the same one keeps the scroll where it is
        .background(Theme.pane)
        .overlay(Rectangle().fill(Theme.line).frame(width: 1), alignment: .leading)
        .onAppear { load(c) }
        .onChange(of: cat.id) { _ in load(state.cats.first { $0.id == cat.id } ?? cat) }
    }

    func load(_ c: Cat) {
        let m = c.ageMinutes > 0 ? c.ageMinutes : c.lastAge
        if m % Engine.day == 0 { unit = 2; ageValue = m / Engine.day }
        else if m % Engine.hour == 0 { unit = 1; ageValue = m / Engine.hour }
        else { unit = 0; ageValue = m }
    }

    func section(_ h: String, _ body: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(h).font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
            Text(body).font(Theme.sans(13)).foregroundColor(Theme.ink).fixedSize(horizontal: false, vertical: true)
        }.padding(.bottom, 8)
    }

    // one line of the short summary: a label column, then the text
    func summaryLine(_ label: String, _ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 0) {
            Text(label).font(Theme.sans(12)).foregroundColor(Theme.muted).frame(width: 92, alignment: .leading)
            Text(text).font(Theme.sans(13)).foregroundColor(Theme.ink).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    // What the item will clean right now: all of it (by its age rule), only what you picked, or nothing yet.
    func goes(_ c: Cat, hasInside: Bool) -> String {
        if c.partial { return "Only the files you tick below, whatever their age, in the next clean." }
        if !c.checked && hasInside { return "Nothing yet: tick the item, or tick files below to clean just those." }
        if c.ageChoice { return c.ageMinutes > 0 ? "Files untouched for \(Engine.fmtAge(c.ageMinutes)) or more." : "Everything in it, whatever its age." }
        return DetailPanel.first(c.deletes)
    }

    // the first sentence of a note, for the short summary
    static func first(_ t: String) -> String {
        guard let r = t.range(of: ". ") else { return t }
        let i = t.distance(from: t.startIndex, to: r.lowerBound)
        return i > 0 && i < 160 ? String(t[..<r.lowerBound]) + "." : t
    }
}

// "Only older than N" vs "Everything" — the chosen one is lit, the other is overridden.
struct AgeControls: View {
    @EnvironmentObject var state: AppState
    let c: Cat
    let older: Bool
    @Binding var ageValue: Int
    @Binding var unit: Int
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("What to clean").font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
            HStack(spacing: 4) {
                Button("Only older than") { if !older { state.setAge(c.id, c.lastAge) } }.buttonStyle(KeyStyle(toggled: older))
                Button("Everything") { if older { state.setAge(c.id, 0) } }.buttonStyle(KeyStyle(toggled: !older))
            }
            HStack(spacing: 4) {
                Button("−") { if ageValue > 1 { ageValue -= 1; commit() } }.buttonStyle(KeyStyle())
                Text("\(ageValue)").font(Theme.semi(17)).foregroundColor(older ? Theme.ink : Theme.disabled).frame(width: 44)
                Button("+") { if ageValue < 999 { ageValue += 1; commit() } }.buttonStyle(KeyStyle())
                Spacer().frame(width: 8)
                Button("min") { unit = 0; commit() }.buttonStyle(KeyStyle(toggled: older && unit == 0))
                Button("hours") { unit = 1; commit() }.buttonStyle(KeyStyle(toggled: older && unit == 1))
                Button("days") { unit = 2; commit() }.buttonStyle(KeyStyle(toggled: older && unit == 2))
            }
            .disabled(!older)
            Text(older ? "Files untouched or added within \(Engine.fmtAge(c.ageMinutes)) stay." : "Every file, whatever its age.")
                .font(Theme.sans(12)).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true).padding(.bottom, 10)
        }
    }

    func commit() {
        let minutes = max(1, ageValue) * (unit == 2 ? Engine.day : unit == 1 ? Engine.hour : 1)
        state.setAge(c.id, minutes)
    }
}

// The item's folders, each with a keep switch (kept folders are protected).
struct FolderKeeps: View {
    @EnvironmentObject var state: AppState
    let cat: Cat
    @State private var tick = 0
    var body: some View {
        let list = folders()
        let kept = Keep.all
        VStack(alignment: .leading, spacing: 4) {
            if !list.isEmpty {
                Text("Folders  ·  keep one to never clean it").font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
                ForEach(Array(list.prefix(14)), id: \.self) { p in
                    FolderKeepRow(path: p, isKept: Keep.isKept(p, kept)) {
                        if Keep.isKept(p, Keep.all) { Keep.remove(p) } else { Keep.add(p) }
                        tick += 1; state.scan([cat.id])
                    }
                }
                if list.count > 14 { Text("+ \(list.count - 14) more").font(Theme.sans(11)).foregroundColor(Theme.soft) }
            }
        }
        .background(Color.clear.opacity(tick < 0 ? 1 : 0))   // redraw on tick without rebuilding (keeps the scroll)
    }

    func folders() -> [String] {
        let roots = cat.roots.filter { FileManager.default.fileExists(atPath: $0.path) }
        if cat.kind != .subfolders { return roots.map { $0.path } }
        return roots.flatMap { Engine.dirs($0).map { $0.path } }
    }
}

struct FolderKeepRow: View {
    let path: String
    let isKept: Bool
    let toggle: () -> Void
    var body: some View {
        HStack(spacing: 8) {
            Button(isKept ? "Kept" : "Keep") { toggle() }.buttonStyle(KeyStyle(toggled: isKept))
            Button(path.replacingOccurrences(of: Engine.home.path, with: "~")) { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
                .buttonStyle(.plain).font(Theme.sans(12)).foregroundColor(isKept ? Theme.soft : Theme.ink).lineLimit(1).truncationMode(.middle)
        }
    }
}

// MARK: - Confirm and progress

struct ConfirmSheet: View {
    @EnvironmentObject var state: AppState
    @Binding var confirm: Bool
    @State private var toTrash = false
    @State private var chosen = false
    var body: some View {
        // frozen copies: an item with files picked by hand counts only those, named "… (your picks)"
        let sel = state.cleanSelection()
        let total = sel.reduce(Int64(0)) { $0 + $1.bytes }
        // one choice for every ticked item, Quick clean included; it starts on Permanently delete every time
        let onlySafe = sel.allSatisfy { $0.isSafe }
        let trashing = chosen && toTrash
        VStack(alignment: .leading, spacing: 8) {
            Text("Clean \(Engine.fmt(total))?").font(Theme.bold(24)).foregroundColor(Theme.ink)
            // grouped by risk, so it's plain which ticked items need care
            ForEach([Level.safe, Level.review, Level.caution], id: \.self) { lvl in
                let items = sel.filter { $0.level == lvl }.sorted { $0.bytes > $1.bytes }
                if !items.isEmpty {
                    ConfirmHead(level: lvl)
                    ForEach(Array(items.prefix(4))) { c in ConfirmRow(name: c.name, bytes: c.bytes, level: lvl) }
                    if items.count > 4 { ConfirmRow(name: "+ \(items.count - 4) more", bytes: items.dropFirst(4).reduce(Int64(0)) { $0 + $1.bytes }, level: lvl) }
                }
            }
            Text("How to clean").font(Theme.semi(13)).foregroundColor(Theme.muted).padding(.top, 8)
            HStack(spacing: 4) {
                Button("Permanently delete") { toTrash = false; chosen = true }.buttonStyle(KeyStyle(toggled: !trashing))
                Button("Move to Trash") { toTrash = true; chosen = true }.buttonStyle(KeyStyle(toggled: trashing))
            }
            Text(!trashing ? (onlySafe ? "These are caches and leftovers that rebuild. " : "") + "Everything is deleted permanently and can't be restored. The space comes back straight away."
                 : "Everything goes to the Trash, so you can restore it. That space comes back only when you empty the Trash.")
                .font(Theme.sans(12)).foregroundColor(Theme.soft).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            HStack(spacing: 8) {
                Spacer()
                Button("Cancel") { confirm = false }.buttonStyle(KeyStyle()).keyboardShortcut(.cancelAction)
                // the key says what will actually happen
                Button(!trashing ? "Permanently delete \(Engine.fmt(total))" : "Move \(Engine.fmt(total)) to Trash") {
                    confirm = false
                    let trash = trashing
                    let chosenSel = sel   // exactly what this sheet showed
                    // let the confirm sheet finish closing before the progress sheet opens
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { state.clean(toTrash: trash, sel: chosenSel) }
                }.buttonStyle(KeyStyle(primary: true))
            }
        }
        .padding(26).frame(width: 560).frame(minHeight: 320).background(Theme.chassis)
        .onAppear { toTrash = false; chosen = false }   // starts on Permanently delete every time
    }
}

struct ConfirmHead: View {
    let level: Level
    var body: some View {
        Text(Theme.tierName(level).uppercased())
            .font(Theme.bold(11)).foregroundColor(Theme.muted).padding(.top, 6)
    }
}

// The mark for each section, readable without colour: Safe none, Review a hatched bar, Caution a solid bar.
struct LevelMark: View {
    let level: Level
    var ink = Theme.ink
    var ground = Theme.chassis
    var body: some View {
        switch level {
        case .safe: Color.clear
        case .review: Hatch(ink: ink, ground: ground)
        case .caution: Rectangle().fill(ink)
        }
    }
}

struct ConfirmRow: View {
    let name: String
    let bytes: Int64
    let level: Level
    var body: some View {
        HStack(spacing: 10) {
            LevelMark(level: level).frame(width: 5, height: 18)
            Text(name).font(Theme.semi(14))
            Spacer()
            Text(Engine.fmt(bytes)).font(Theme.semi(14))
        }
        .foregroundColor(Theme.ink)
        .padding(.vertical, 3)
        .overlay(Rectangle().fill(Theme.line).frame(height: 1).padding(.leading, 15), alignment: .bottom)
    }
}

// The cleaning bar's shown width, gliding toward the real fraction each frame (not observed: it only changes while drawing).
final class BarGlide {
    var shown: CGFloat = 0
    var last: Date?
    func step(_ target: CGFloat, at now: Date) -> CGFloat {
        let dt = last.map { min(0.5, max(0, now.timeIntervalSince($0))) } ?? 0
        last = now
        if target < shown { shown = target }   // snap down
        else { shown += (target - shown) * CGFloat(1 - exp(-dt / 0.12)) }
        return shown
    }
}

// The finish, in milliseconds from the moment a clean is done (about 2.5 s; the same beats as the Windows version):
// the display grows over the log, the bar gives way to the disk drawn as a ring, the freed wedge lifts out and
// crumbles, the free figure counts up, one "Done" pulse, then the list of what went.
enum FinishBeat {
    static let grow = 420.0, ringAt = 250.0, ringMs = 520.0, liftAt = 820.0, liftMs = 300.0, crumbleAt = 1120.0, crumbleMs = 520.0
    static let closeAt = 1250.0, closeMs = 520.0, doneAt = 1820.0, pulseMs = 650.0, listAt = 2150.0, listMs = 320.0
    static var total: Double { listAt + listMs }
    static func ease(_ t: Double) -> Double { let x = max(0, min(1, t)); return 1 - pow(1 - x, 3) }

    // the freed wedge's dust: made once, the same every time
    struct Speck { let a: Double, r: Double, vx: Double, vy: Double, s: Double, delay: Double }
    static let specks: [Speck] = {
        var seed: UInt64 = 7
        func next() -> Double {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            return Double(seed >> 11) / 9007199254740992.0
        }
        var out: [Speck] = []
        for _ in 0..<90 {
            let a = next(), r = next() - 0.5, vx = next() * 1.6 + 0.4, vy = -next() * 1.4 - 0.2, s = next() * 3 + 2, d = next() * 180
            out.append(Speck(a: a, r: r, vx: vx, vy: vy, s: s, delay: d))
        }
        return out
    }()

    // an arc as short straight pieces: degrees, 0 = 3 o'clock, -90 = 12 o'clock, positive = clockwise on screen
    static func arc(_ c: CGPoint, _ r: CGFloat, _ startDeg: Double, _ extDeg: Double) -> Path {
        var p = Path()
        let n = max(2, Int(abs(extDeg) / 2) + 1)
        for k in 0...n {
            let a = (startDeg + extDeg * Double(k) / Double(n)) * Double.pi / 180
            let pt = CGPoint(x: c.x + r * CGFloat(cos(a)), y: c.y + r * CGFloat(sin(a)))
            if k == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
        }
        return p
    }

    // The bar while cleaning; at the end it fades and the disk appears as a ring (measured before and after).
    static func draw(_ ctx: inout GraphicsContext, size: CGSize, ms: Double, bar: CGFloat, details: Bool, binOnly: Bool,
                     before: Int64, after: Int64, total: Int64, disk: String) {
        let W = size.width, pad: CGFloat = 20
        let fin = ms >= 0
        let by: CGFloat = 126, bh: CGFloat = 12
        if !fin || details || ms < ringAt + 200 {
            let a: Double = fin && !details ? 1 - ease(ms / (ringAt + 200)) : 1
            let bw = W - 2 * pad
            ctx.fill(Path(CGRect(x: pad, y: by, width: bw, height: bh)), with: .color(Theme.track.opacity(a)))
            ctx.fill(Path(CGRect(x: pad, y: by, width: bw * (fin ? 1 : bar), height: bh)), with: .color(Theme.orange.opacity(a)))
            if !fin || details { return }
        }
        guard total > 0, after >= 0 else { return }
        var usedAfter = min(1, max(0, 1 - Double(after) / Double(total)))
        var slice: Double = binOnly ? 0 : max(0, Double(after - before) / Double(total))
        // a small clean still shows a wedge (at least 5 degrees for 1 MB or more); the numbers stay exact
        if slice > 0 && slice * 360 < 5 { slice = after - before >= Int64(1 << 20) ? 5.0 / 360 : 0 }
        if usedAfter + slice > 1 { usedAfter = 1 - slice }
        let R: CGFloat = 104, sw: CGFloat = 26
        // centred while it plays; it moves to the right when the list arrives
        let cxEnd = W - pad - R - sw / 2 - 40, cxMid = W / 2
        let c = CGPoint(x: cxMid + (cxEnd - cxMid) * CGFloat(ease((ms - listAt) / 380)), y: 214)
        let box = CGRect(x: c.x - R, y: c.y - R, width: 2 * R, height: 2 * R)
        let sweep = ease((ms - ringAt) / ringMs)                                     // the ring draws itself round
        let lift = slice > 0 ? ease((ms - liftAt) / liftMs) : 0                       // the freed wedge lifts out...
        let crumble = slice > 0 ? max(0, min(1, (ms - crumbleAt) / crumbleMs)) : 1    // ...turns to dust...
        let close = slice > 0 ? ease((ms - closeAt) / closeMs) : 1                    // ...and free space grows into the gap
        let gap = slice * (1 - close)
        func deg(_ f: Double) -> Double { -90 + 360 * f }
        let butt = StrokeStyle(lineWidth: sw, lineCap: .butt)
        // free space: from the end of the gap round to the top
        let fExt = 360 * (1 - usedAfter - gap)
        if sweep > 0 && fExt > 0.3 {
            ctx.stroke(arc(c, R, deg(usedAfter + gap), fExt), with: .color(Theme.track.opacity(min(1, sweep * 2))), style: butt)
        }
        // used space, sweeping round; the freed wedge sits at its end in orange until it lifts out
        let drawn = (usedAfter + slice) * sweep
        let used = 360 * min(drawn, usedAfter)
        if used > 0.3 { ctx.stroke(arc(c, R, -90, used), with: .color(Theme.used), style: butt) }
        let start = deg(usedAfter)
        if slice > 0 && crumble < 1 && drawn > usedAfter {
            let ext = 360 * min(slice, drawn - usedAfter)
            let mid = (start + ext / 2) * Double.pi / 180, off = 16 * lift
            let wc = CGPoint(x: c.x + CGFloat(cos(mid) * off), y: c.y + CGFloat(sin(mid) * off))
            let wa = 1 - ease(crumble * 1.4)
            if ext > 0.3 && wa > 0 { ctx.stroke(arc(wc, R, start, ext), with: .color(Theme.orange.opacity(wa)), style: butt) }
        }
        if slice > 0 && ms >= crumbleAt {   // the wedge turns to dust and falls away
            for sp in specks {
                let tt = (ms - crumbleAt - sp.delay) / 1000
                if tt <= 0 { continue }
                let pa = max(0, 1 - tt / 0.75)
                if pa <= 0 { continue }
                let an = (start + 360 * slice * sp.a) * Double.pi / 180
                let rr = Double(R) + sp.r * Double(sw)
                let mx = cos(an), my = sin(an)
                let px = Double(c.x) + mx * (rr + 16) + mx * sp.vx * 90 * tt
                let py = Double(c.y) + my * (rr + 16) + sp.vy * 90 * tt + 260 * tt * tt
                ctx.fill(Path(CGRect(x: px, y: py, width: sp.s, height: sp.s)), with: .color(Theme.orange.opacity(pa)))
            }
        }
        if slice > 0 && close > 0 {   // what you gained stays marked: an orange edge on the outer rim of the newly free span
            let ee = 360 * (slice - gap)
            if ee > 0.3 { ctx.stroke(arc(c, R + sw / 2 - 2, deg(usedAfter + gap), ee), with: .color(Theme.orange), style: StrokeStyle(lineWidth: 4, lineCap: .butt)) }
        }
        if ms >= doneAt {   // done: the whole ring flashes orange once and settles
            let pt = (ms - doneAt) / pulseMs
            if pt < 1 {
                ctx.stroke(Path(ellipseIn: box), with: .color(Theme.orange.opacity(150.0 / 255 * (1 - pt) * (1 - pt))), lineWidth: sw + 2)
            }
        }
        if sweep > 0 {   // in the middle: free space, counting up as the ring closes, and what you gained
            let ca = min(1, sweep * 1.5)
            let shown = before + Int64(Double(after - before) * close)
            ctx.draw(Text(Engine.fmt(shown)).font(Theme.display(30)).foregroundColor(Theme.screenInk.opacity(ca)),
                     at: CGPoint(x: c.x, y: c.y - 34), anchor: .top)
            let sub = "free of \(Engine.fmt(total)) on \(disk)"
            var gainY = c.y + 22
            if sub.count <= 28 {
                ctx.draw(Text(sub).font(Theme.sans(12)).foregroundColor(Theme.screenMuted.opacity(ca)), at: CGPoint(x: c.x, y: c.y + 2), anchor: .top)
            } else {   // a long disk name: two lines, so it stays inside the ring
                ctx.draw(Text("free of \(Engine.fmt(total))").font(Theme.sans(12)).foregroundColor(Theme.screenMuted.opacity(ca)), at: CGPoint(x: c.x, y: c.y + 2), anchor: .top)
                ctx.draw(Text("on \(disk)").font(Theme.sans(12)).foregroundColor(Theme.screenMuted.opacity(ca)), at: CGPoint(x: c.x, y: c.y + 17), anchor: .top)
                gainY = c.y + 37
            }
            let gain = after - before
            if gain > 0 && close > 0 {
                ctx.draw(Text("+ " + Engine.fmt(gain)).font(Theme.monoSemi(12.5)).foregroundColor(Theme.orange.opacity(close)),
                         at: CGPoint(x: c.x, y: gainY), anchor: .top)
            }
        }
    }
}

struct DottedRule: Shape {
    func path(in r: CGRect) -> Path {
        var p = Path(); p.move(to: CGPoint(x: r.minX, y: r.midY)); p.addLine(to: CGPoint(x: r.maxX, y: r.midY)); return p
    }
}

struct ProgressSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var finishStart: Date?     // the finish is playing
    @State private var settled = false        // the finish has played (or was skipped)
    @State private var grown = false          // the log has sunk away
    @State private var doneBeat = false       // the Done key is up
    @State private var listShown = false      // "What was cleaned" is up
    @State private var details = false        // the log back under a small display
    @State private var gen = 0
    @State private var glide = BarGlide()
    private let baseH: CGFloat = 158

    var finished: Bool { state.cleanDone && !state.cleanStopped }
    var canClose: Bool { state.cleanDone && (state.cleanStopped || doneBeat) }
    var windowTitle: String { !state.cleanDone ? "Cleaning" : state.cleanStopped ? "Stopped" : doneBeat ? "Done" : "Cleaning" }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ZStack(alignment: .top) {
                logView
                    .padding(.top, baseH + 10)
                    .offset(y: finished && grown && !details ? 360 : 0)   // sinks away as the display grows
                    .opacity(finished && grown && !details ? 0 : 1)
                // redrawn every frame while cleaning (the bar glides) and during the finish; still once it has settled
                TimelineView(.animation(paused: state.cleanDone && finishStart == nil)) { tl in
                    GeometryReader { g in
                        let ms = msNow(tl.date)
                        let grow: CGFloat = !finished || details ? 0 : settled ? 1 : CGFloat(FinishBeat.ease(ms / FinishBeat.grow))
                        display(ms: ms, date: tl.date, width: g.size.width)
                            .frame(width: g.size.width, height: baseH + max(0, g.size.height - baseH) * grow)
                    }
                }
                .allowsHitTesting(false)
            }
            .clipped()

            HStack(spacing: 16) {
                if finished && listShown {
                    Button { details.toggle() } label: { Text(details ? "Back to the summary" : "What was cleaned").underline() }
                        .buttonStyle(.plain).font(Theme.semi(13)).foregroundColor(Theme.ink)
                }
                if state.cleanDone && (state.cleanStopped || details) {
                    Button("Open log file") { NSWorkspace.shared.open(Engine.logURL) }.buttonStyle(KeyStyle())
                }
                Spacer()
                if !state.cleanDone {
                    Button("Minimise") { NSApp.keyWindow?.miniaturize(nil) }.buttonStyle(KeyStyle())
                    Button("Stop") { state.stop() }.buttonStyle(KeyStyle())
                } else if state.cleanStopped {
                    Button("Close") { close() }.buttonStyle(KeyStyle()).keyboardShortcut(.defaultAction)
                } else if !doneBeat {
                    Button("Finishing") { }.buttonStyle(KeyStyle()).disabled(true)
                } else {
                    // Done closes the window; it's the one orange key once a clean has finished
                    Button("Done") { close() }.buttonStyle(KeyStyle(primary: true)).keyboardShortcut(.defaultAction)
                }
            }
            .frame(minHeight: 34)
        }
        .padding(20)
        .frame(minWidth: 680, minHeight: 480)
        .background(Theme.chassis)
        // Esc closes it too, once it's done
        .background(Button("") { if canClose { close() } }.keyboardShortcut(.cancelAction).opacity(0).allowsHitTesting(false))
        .navigationTitle(windowTitle)
        .onAppear {
            // opened again after the clean ended (it was closed while cleaning): straight to the end
            if state.cleanDone { if state.cleanStopped { settled = true } else { settle() } }
        }
        .onChange(of: state.cleanRunning) { r in
            if r { gen += 1; finishStart = nil; settled = false; grown = false; doneBeat = false; listShown = false; details = false }
        }
        .onChange(of: state.cleanDone) { d in if d { beginFinish() } }
        .onDisappear { if state.cleanDone { state.finishCleaning() } }
    }

    func close() { state.finishCleaning(); dismiss() }

    func msNow(_ d: Date) -> Double {
        if !finished { return -1 }                  // cleaning, or stopped
        if settled { return FinishBeat.total }
        guard let s = finishStart else { return 0 }
        return max(0, d.timeIntervalSince(s) * 1000)
    }

    func settle() {
        gen += 1
        finishStart = nil; grown = true; doneBeat = true; listShown = true; settled = true
    }

    func beginFinish() {
        if state.cleanStopped { settled = true; return }   // stopped: no finish; the log stays, so you can see where it stopped
        if reduceMotion || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion { settle(); return }
        gen += 1
        let g = gen
        finishStart = Date()
        withAnimation(.easeOut(duration: FinishBeat.grow / 1000)) { grown = true }
        DispatchQueue.main.asyncAfter(deadline: .now() + FinishBeat.doneAt / 1000) { if gen == g { doneBeat = true } }
        DispatchQueue.main.asyncAfter(deadline: .now() + FinishBeat.listAt / 1000) { if gen == g { listShown = true } }
        DispatchQueue.main.asyncAfter(deadline: .now() + FinishBeat.total / 1000 + 0.1) {   // stop redrawing once it has settled
            if gen == g { settled = true; finishStart = nil }
        }
    }

    @ViewBuilder
    func display(ms: Double, date: Date, width W: CGFloat) -> some View {
        let fin = ms >= 0
        let beat = fin && ms >= FinishBeat.doneAt
        let binOnly = state.freed == 0 && state.trashed > 0
        let total = binOnly ? state.trashed : state.freed
        let p = Engine.parts(total)   // the real total the whole time: no count-up
        let fraction = min(1, CGFloat(Double(state.freed + state.trashed) / Double(max(1, state.expected))))
        let barFraction: CGFloat = state.cleanDone ? fraction : glide.step(fraction, at: date)
        let label: String = !state.cleanDone ? "Cleaning  ·  " + state.currentItem
            : state.cleanStopped ? "Stopped  ·  nothing else will be cleaned"
            : !beat ? "Finishing"
            : binOnly ? "Done  ·  moved to the Trash" : "Done  ·  everything you ticked is cleaned"
        let showDetails = details
        let before = state.freeBefore, after = state.freeAfter, diskTotal = state.diskTotal, disk = state.diskName
        ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: 7).fill(Theme.screen)
            Canvas { ctx, size in
                FinishBeat.draw(&ctx, size: size, ms: ms, bar: barFraction, details: showDetails, binOnly: binOnly,
                                before: before, after: after, total: diskTotal, disk: disk)
            }
            VStack(alignment: .leading, spacing: 4) {
                if beat {
                    // "DONE" at the size of "GB FREED", then what it means, small
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("DONE").font(Theme.display(24)).foregroundColor(Theme.screenInk)
                        Text(label.replacingOccurrences(of: "Done  ·  ", with: "·  ")).font(Theme.semi(12)).foregroundColor(Theme.screenMuted).lineLimit(1)
                    }
                    .frame(height: 20, alignment: .bottomLeading)
                } else {
                    Text(label).font(Theme.semi(12)).foregroundColor(Color(hex: 0xB4B1AA)).lineLimit(1)
                        .frame(height: 20, alignment: .bottomLeading)
                }
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(p.0).font(Theme.display(60)).padding(.leading, -5)   // side bearing
                    Text(p.1 + (binOnly ? " TO TRASH" : " FREED")).font(Theme.display(24))
                    Spacer()
                    if !fin || showDetails {   // while cleaning: files and skipped
                        VStack(alignment: .trailing, spacing: 4) {
                            Text("\(state.cleanedFiles) files  ·  \(state.skipped) skipped").font(Theme.semi(13)).foregroundColor(Theme.screenInk)
                            if state.trashed > 0 && !binOnly { Text("+ \(Engine.fmt(state.trashed)) moved to the Trash").font(Theme.sans(12)).foregroundColor(Theme.screenMuted) }
                            if binOnly && state.cleanDone { Text("Empty the Trash to get this space back").font(Theme.sans(12)).foregroundColor(Theme.screenMuted) }
                        }
                    }
                }.foregroundColor(Theme.orange)
            }
            .padding(.horizontal, 20).padding(.top, 22)
            if fin && ms >= FinishBeat.listAt && !showDetails {
                resultList(width: W, binOnly: binOnly, total: total)
                    .opacity(FinishBeat.ease((ms - FinishBeat.listAt) / FinishBeat.listMs))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 7))
    }

    // The list of what went, beside the ring: the biggest items, the rest, and at the bottom the total, like a slip.
    func resultList(width W: CGFloat, binOnly: Bool, total: Int64) -> some View {
        let items = state.freedItems.filter { $0.bytes + $0.trashed > 0 }.sorted { $0.bytes + $0.trashed > $1.bytes + $1.trashed }
        let show = items.count > 5 ? 4 : items.count
        let rest = Array(items.dropFirst(show))
        let restBytes = rest.reduce(Int64(0)) { $0 + $1.bytes + $1.trashed }
        let colW = max(140, W - 334 - 20)
        let secs = max(1, Int(ceil(state.cleanSeconds)))   // never 0:00
        let time = String(format: "%ld:%02ld", secs / 60, secs % 60)
        let kept = state.skipped
        let files = "\(state.cleanedFiles) file" + (state.cleanedFiles == 1 ? "" : "s") + (kept > 0 ? "  ·  \(kept) kept (in use or too new)" : "")
        let mono = Theme.mono(12.5)
        return VStack(alignment: .leading, spacing: 0) {
            ForEach(0..<show, id: \.self) { i in
                slipLine(items[i].name, Engine.fmt(items[i].bytes + items[i].trashed) + (items[i].trashed > 0 && items[i].bytes == 0 ? "  Trash" : ""), Theme.screenInk, mono)
            }
            if !rest.isEmpty { slipLine("+ \(rest.count) more", Engine.fmt(restBytes), Theme.screenMuted, mono) }
            if items.isEmpty { slipLine("Nothing needed cleaning", "", Theme.screenMuted, mono) }
            Spacer(minLength: 6)
            DottedRule().stroke(Theme.track, style: StrokeStyle(lineWidth: 1, dash: [1, 2])).frame(height: 1).padding(.bottom, 4)
            slipLine(binOnly ? "MOVED TO THE TRASH" : "FREED", Engine.fmt(total), Theme.orange, Theme.monoSemi(12.5))
            if state.trashed > 0 && !binOnly { slipLine("+ moved to the Trash (empty it to get this back)", Engine.fmt(state.trashed), Theme.screenMuted, mono) }
            if binOnly { slipLine("Empty the Trash to get this space back", "", Theme.screenMuted, mono) }
            slipLine(files, time, Theme.screenMuted, mono)
        }
        .frame(width: colW, alignment: .leading)
        .padding(.leading, 20).padding(.top, 132).padding(.bottom, 18)
        .frame(maxHeight: .infinity, alignment: .topLeading)
    }

    func slipLine(_ l: String, _ r: String, _ ink: Color, _ font: Font) -> some View {
        HStack(spacing: 12) {
            Text(l).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 0)
            Text(r).lineLimit(1).fixedSize()
        }
        .font(font).foregroundColor(ink).frame(height: 22)
    }

    var logView: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 1) {
                    ForEach(Array((state.cleanDone && !state.results.isEmpty ? ["What was cleaned", ""] + state.results + ["", "Every file:"] + state.log : state.log).enumerated()), id: \.offset) { i, line in
                        Text(line).font(Theme.mono(11)).foregroundColor(Theme.ink).lineLimit(1).truncationMode(.middle).id(i)
                    }
                }.padding(12)
            }
            .background(Theme.pane)
            .onChange(of: state.log.count) { n in if n > 0 { proxy.scrollTo(n - 1, anchor: .bottom) } }
            .onChange(of: state.cleanDone) { d in if d { proxy.scrollTo(0, anchor: .top) } }   // the summary sits on top
        }
    }
}

// MARK: - First-run guide
// The window dims, a lit cut-out sits on the real part, and a small display-style card explains it. The cut-out
// glides from one part to the next. Skip on every step; "?" replays it.

struct GuideAnchors: PreferenceKey {
    static var defaultValue: [Int: Anchor<CGRect>] = [:]
    static func reduce(value: inout [Int: Anchor<CGRect>], nextValue: () -> [Int: Anchor<CGRect>]) {
        value.merge(nextValue(), uniquingKeysWith: { $1 })
    }
}

extension View {
    // marks a part the guide can light (0 display, 2 Storage map, 3 Clean, 5 Protected folders, 6 Back to recommended,
    // 9 top-right keys, 10 section rail, 11 list, 12 list header, 13 item panel)
    func guidePart(_ i: Int) -> some View { anchorPreference(key: GuideAnchors.self, value: .bounds) { [i: $0] } }
}

// Finds the window a view is in (the guide only starts when the main window is the one in front).
struct WindowReader: NSViewRepresentable {
    let found: (NSWindow?) -> Void
    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        DispatchQueue.main.async { [weak v] in found(v?.window) }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        let w = nsView.window
        if w != nil { DispatchQueue.main.async { found(w) } }
    }
}

final class GuideModel: ObservableObject {
    static let steps: [(title: String, body: String)] = [
        ("This is what you can free", "The big number is what's ticked right now. The bar is your disk: orange is ticked, hatched is what you could still free. On the right, your disk's free space."),
        ("Three sections, by how careful to be", "Quick clean is safe and ticked for you. Big caches come back when they're needed. Deep clean may include your work. The small box ticks or unticks a whole section."),
        ("Each row is one thing to clean", "Tick a row to include it; the size is what it would free. Click the row itself to see what it is."),
        ("What it is, what goes, what happens after", "Every item explains itself. Some let you choose \u{201C}only older than\u{201D} or \u{201C}everything\u{201D}. Under What's inside, untick files to keep them, or tick just a few to clean only those."),
        ("See what fills your disk", "Storage map shows every folder by size, on any drive. Nothing there deletes anything; you can add a folder to your cleanup list."),
        ("Keep what must never go", "Protected folders are never cleaned, whatever is ticked. Space Hunter protects your own files and itself already."),
        ("Back to recommended", "Puts the ticks and ages back to how they started. Scan again measures everything afresh."),
        ("Clean always asks first", "You see the list of what goes and choose Permanently delete or Trash. When it's done, a ring shows the space you got back."),
        ("Your look, and this guide", "Light, dark or your Mac's setting. The ? key shows this guide again whenever you want."),
    ]
    // what a step does before it points (open or close the item panel); set by the main window
    var enter: ((Int) -> Void)?
    static let seenKey = "guideSeen"
    static var seen: Bool { UserDefaults.standard.bool(forKey: seenKey) }
    // a test or screenshot run (CI): the guide never starts by itself. "--test-run" as an argument
    // (open … --args --test-run), or SH_TEST_RUN / CI / GITHUB_ACTIONS in the environment.
    static var testRun: Bool {
        let env = ProcessInfo.processInfo.environment
        return CommandLine.arguments.contains("--test-run") || env["SH_TEST_RUN"] != nil || env["CI"] != nil || env["GITHUB_ACTIONS"] != nil
    }

    @Published var step: Int? = nil
    @Published var shown = false   // the veil, faded in
    weak var window: NSWindow?
    private var closing = false
    private var keyMonitor: Any?
    private var waitObserver: NSObjectProtocol?

    func start() {
        guard step == nil else { return }
        closing = false
        enter?(0)
        step = 0
        DispatchQueue.main.async { withAnimation(.easeOut(duration: 0.18)) { self.shown = true } }
        // Esc skips; Enter, Space and the right arrow go on; the left arrow goes back. The window behind gets no keys.
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self = self, self.step != nil else { return e }
            if e.modifierFlags.contains(.command) { return e }
            if let w = self.window, e.window !== w { return e }
            switch e.keyCode {
            case 53: self.finish()
            case 36, 76, 49, 124: self.next()
            case 123: self.back()
            default: break
            }
            return nil
        }
    }

    func go(_ n: Int) {
        guard step != nil, !closing, n >= 0 else { return }
        if n >= GuideModel.steps.count { finish(); return }
        enter?(n)   // e.g. the panel opens first; the cut-out then glides to where the part is now
        withAnimation(.easeOut(duration: 0.3)) { step = n }
    }
    func next() { go((step ?? 0) + 1) }
    func back() { if let s = step, s > 0 { go(s - 1) } }

    // skipped or finished: remembered, so it starts by itself only once
    func finish() {
        guard step != nil, !closing else { return }
        closing = true
        UserDefaults.standard.set(true, forKey: GuideModel.seenKey)
        if let m = keyMonitor { NSEvent.removeMonitor(m); keyMonitor = nil }
        withAnimation(.easeOut(duration: 0.18)) { shown = false }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            guard let self = self else { return }
            self.step = nil; self.closing = false
        }
    }

    // Once, the first time a scan finishes: now if this window is the one in front, else when it next is.
    func autoStart(when ok: @escaping () -> Bool, before: @escaping () -> Void) {
        guard !GuideModel.testRun, !GuideModel.seen, step == nil, waitObserver == nil else { return }
        if NSApp.isActive, let w = window, w.isKeyWindow {
            if ok() { before(); start() }
            return
        }
        waitObserver = NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { [weak self] note in
            guard let self = self else { return }
            if let w = self.window, (note.object as? NSWindow) !== w { return }
            if let o = self.waitObserver { NotificationCenter.default.removeObserver(o); self.waitObserver = nil }
            if !GuideModel.seen, self.step == nil, ok() { before(); self.start() }
        }
    }
}

// The veil with a rounded cut-out (veil: true), or just the cut-out's outline. The cut-out glides between steps.
struct GuideShape: Shape {
    var hole: CGRect
    var veil: Bool
    var animatableData: AnimatablePair<AnimatablePair<CGFloat, CGFloat>, AnimatablePair<CGFloat, CGFloat>> {
        get { AnimatablePair(AnimatablePair(hole.minX, hole.minY), AnimatablePair(hole.width, hole.height)) }
        set { hole = CGRect(x: newValue.first.first, y: newValue.first.second, width: newValue.second.first, height: newValue.second.second) }
    }
    func path(in rect: CGRect) -> Path {
        var p = Path()
        if veil { p.addRect(rect) }
        p.addRoundedRect(in: hole, cornerSize: CGSize(width: 6, height: 6))
        return p
    }
}

struct GuideCardHeight: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

struct GuideOverlay: View {
    @ObservedObject var guide: GuideModel
    let targets: [CGRect?]
    let size: CGSize
    @State private var cardH: CGFloat = 170
    private let cardW: CGFloat = 320

    var body: some View {
        let step = min(max(guide.step ?? 0, 0), GuideModel.steps.count - 1)
        let fallback = CGRect(x: size.width / 2 - 80, y: size.height / 2 - 40, width: 160, height: 80)
        let target: CGRect = (step < targets.count ? targets[step] : nil) ?? fallback
        let spot = target.insetBy(dx: -6, dy: -6)
        let o = cardOrigin(spot)
        let last = step == GuideModel.steps.count - 1
        ZStack(alignment: .topLeading) {
            Color.clear.contentShape(Rectangle()).onTapGesture { }   // the window behind waits
            GuideShape(hole: spot, veil: true).fill(Color(hex: 0x121212).opacity(0.58), style: FillStyle(eoFill: true))
            GuideShape(hole: spot, veil: false).stroke(Theme.orange, lineWidth: 2)
            VStack(alignment: .leading, spacing: 0) {
                Text(GuideModel.steps[step].title).font(Theme.bold(16.5)).foregroundColor(Theme.screenInk)
                    .fixedSize(horizontal: false, vertical: true)   // a long title wraps and pushes the body down
                Text(GuideModel.steps[step].body).font(Theme.sans(13)).foregroundColor(Theme.screenInk.opacity(0.82))
                    .fixedSize(horizontal: false, vertical: true).padding(.top, 8)
                HStack(alignment: .firstTextBaseline, spacing: 14) {
                    Text("\(step + 1) / \(GuideModel.steps.count)").font(Theme.mono(11.5)).foregroundColor(Theme.screenMuted)
                    Spacer(minLength: 8)
                    if step > 0 {
                        Button { guide.back() } label: { Text("Back").underline() }
                            .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.screenInk)
                    }
                    if !last {
                        Button { guide.finish() } label: { Text("Skip").underline() }
                            .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.screenInk)
                    }
                    Button(last ? "Got it" : "Next") { guide.next() }.buttonStyle(KeyStyle(primary: true))
                }
                .padding(.top, 14)
            }
            .padding(14)
            .frame(width: cardW, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 5).fill(Theme.screen))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color(hex: 0x5A564F), lineWidth: 1))
            .shadow(color: Color.black.opacity(0.3), radius: 10, x: 3, y: 6)
            .background(GeometryReader { p in Color.clear.preference(key: GuideCardHeight.self, value: p.size.height) })
            .offset(x: o.x, y: o.y)
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .opacity(guide.shown ? 1 : 0)
        .onPreferenceChange(GuideCardHeight.self) { h in if h > 0 { cardH = h } }
    }

    // under the lit part if there's room, else above it; always inside the window
    func cardOrigin(_ spot: CGRect) -> CGPoint {
        let x = max(16, min(size.width - cardW - 16, spot.midX - cardW / 2))
        var y = spot.maxY + 14 + cardH < size.height - 12 ? spot.maxY + 14 : spot.minY - 14 - cardH
        if y < 12 { y = min(size.height - cardH - 12, spot.minY + 24) }
        return CGPoint(x: x, y: y)
    }
}

// MARK: - Protected folders

struct ProtectView: View {
    @EnvironmentObject var state: AppState
    @State private var tick = 0
    let always = ["Documents, Desktop, Pictures, Movies, Music, Public and iCloud Drive",
                  "Downloads (only old installers and archives can be cleaned, and only if you tick that item)",
                  "Your code and project folders (Projects, Developer)",
                  "Keys and sign-in settings (.ssh, .gnupg, .aws, .kube, .docker)",
                  "AI agents' memory folders (for example Claude Code's memory)",
                  "macOS and installed apps",
                  "Anything reached through a link (links are never followed)",
                  "Space Hunter itself: the app, its settings and its log"]
    var body: some View {
        let mine = Keep.all
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Protected folders  ·  never cleaned, whatever is ticked").font(Theme.semi(12)).foregroundColor(Color(hex: 0xB4B1AA))
                // never "0 folders": the built-in protection is always there, even before you add your own
                Text("\(always.count) BUILT IN + \(mine.count) YOURS").font(Theme.display(44)).foregroundColor(Theme.orange)
                Text("The places Space Hunter never touches, plus any folder you add.").font(Theme.sans(13)).foregroundColor(Theme.screenMuted)
            }
            .padding(20).frame(maxWidth: .infinity, alignment: .leading).background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
            HStack {
                Button("Add folder") { pick() }.buttonStyle(KeyStyle(primary: true))
                Text("Anything inside a protected folder is skipped by every item, every time.").font(Theme.sans(12)).foregroundColor(Theme.muted)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Your protected folders").font(Theme.semi(14)).padding(.top, 8)
                    if mine.isEmpty { Text("None yet. Press Add folder above.").font(Theme.sans(13)).foregroundColor(Theme.soft) }
                    ForEach(mine, id: \.self) { p in
                        HStack(spacing: 10) {
                            Button("Remove") { Keep.remove(p); tick += 1; state.scan() }.buttonStyle(KeyStyle())
                            Text(p.replacingOccurrences(of: Engine.home.path, with: "~")).font(Theme.sans(13)).lineLimit(1).truncationMode(.middle)
                        }
                    }
                    Text("Built in").font(Theme.semi(14)).padding(.top, 12)
                    ForEach(always, id: \.self) { t in
                        Text(t).font(Theme.sans(13)).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .id(tick)
        }
        .padding(18).frame(minWidth: 560, minHeight: 420).background(Theme.chassis)
    }

    func pick() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false
        panel.prompt = "Protect"
        if panel.runModal() == .OK, let url = panel.url { Keep.add(url.path); tick += 1; state.scan() }
    }
}

// MARK: - Storage map

final class FolderNode: Identifiable {
    let id = UUID()
    let name: String, path: String
    weak var parent: FolderNode?
    var kids: [FolderNode] = []
    var size: Int64 = 0, files: Int64 = 0, ownSize: Int64 = 0, ownFiles: Int64 = 0
    init(name: String, path: String, parent: FolderNode?) { self.name = name; self.path = path; self.parent = parent }
}

final class Scanner: ObservableObject {
    @Published var scanned: Int64 = 0
    @Published var scannedBytes: Int64 = 0
    @Published var running = false
    @Published var root: FolderNode?
    var cancel = false
    private var count: Int64 = 0, bytes: Int64 = 0
    private let lock = NSLock()

    func start(_ path: String) {
        running = true; root = nil; scanned = 0; scannedBytes = 0; count = 0; bytes = 0; cancel = false
        let timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { _ in
            self.lock.lock(); let c = self.count, b = self.bytes; self.lock.unlock()
            self.scanned = c; self.scannedBytes = b
        }
        DispatchQueue.global(qos: .userInitiated).async {
            let node = FolderNode(name: path, path: path, parent: nil)
            self.fill(node)
            self.total(node)
            DispatchQueue.main.async {
                timer.invalidate(); self.root = node; self.running = false
                self.lock.lock(); self.scanned = self.count; self.scannedBytes = self.bytes; self.lock.unlock()
            }
        }
    }

    // Reads each folder with plain directory calls (much faster than asking for URL properties one file at a
    // time) and measures the first two levels' folders in parallel, as SSDs answer several reads at once.
    private func fill(_ n: FolderNode, depth: Int = 0) {
        if cancel { return }
        guard let dir = opendir(n.path) else { return }
        let fd = dirfd(dir)
        var addCount: Int64 = 0, addBytes: Int64 = 0
        var st = stat()
        while let ent = readdir(dir) {
            let name = withUnsafeBytes(of: ent.pointee.d_name) { String(cString: $0.bindMemory(to: CChar.self).baseAddress!) }
            if name == "." || name == ".." { continue }
            if fstatat(fd, name, &st, AT_SYMLINK_NOFOLLOW) != 0 { continue }
            let mode = st.st_mode & S_IFMT
            if mode == S_IFDIR {
                let p = n.path == "/" ? "/" + name : n.path + "/" + name
                if p == "/System/Volumes" || p == "/Volumes" || p == "/dev" { continue }
                n.kids.append(FolderNode(name: name, path: p, parent: n))
            } else if mode == S_IFREG {
                let size = Int64(st.st_blocks) * 512
                n.ownSize += size; n.ownFiles += 1; addCount += 1; addBytes += size
            }
        }
        closedir(dir)
        lock.lock(); count += addCount; bytes += addBytes; lock.unlock()
        let kids = n.kids
        if depth < 2 && kids.count > 1 {
            DispatchQueue.concurrentPerform(iterations: kids.count) { i in fill(kids[i], depth: depth + 1) }
        } else {
            for k in kids { fill(k, depth: depth + 1) }
        }
    }

    private func total(_ n: FolderNode) {
        n.size = n.ownSize; n.files = n.ownFiles
        for k in n.kids { total(k); n.size += k.size; n.files += k.files }
        n.kids.sort { $0.size > $1.size }
    }
}

// The places the Storage map can measure: your home folder, then every disk (internal and external).
struct VolumeChoice: Identifiable, Equatable {
    let id: String          // the path measured
    let name: String
    let total: Int64, free: Int64
    let removable: Bool

    static func all() -> [VolumeChoice] {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeTotalCapacityKey, .volumeAvailableCapacityKey, .volumeIsRemovableKey, .volumeIsInternalKey]
        var out: [VolumeChoice] = []
        let rootV = try? URL(fileURLWithPath: "/").resourceValues(forKeys: Set(keys))
        out.append(VolumeChoice(id: Engine.home.path, name: "Home folder", total: Int64(rootV?.volumeTotalCapacity ?? 0),
                                free: Int64(rootV?.volumeAvailableCapacity ?? 0), removable: false))
        for u in FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? [] {
            guard let v = try? u.resourceValues(forKeys: Set(keys)), let total = v.volumeTotalCapacity, total > 0 else { continue }
            out.append(VolumeChoice(id: u.path, name: v.volumeName ?? u.lastPathComponent, total: Int64(total),
                                    free: Int64(v.volumeAvailableCapacity ?? 0), removable: v.volumeIsInternal == false || v.volumeIsRemovable == true))
        }
        return out
    }
}

struct DriveKeyStyle: ButtonStyle {
    var open = false
    @Environment(\.isEnabled) var enabled
    func makeBody(configuration: Configuration) -> some View {
        let pressed = configuration.isPressed || open
        return configuration.label
            .foregroundColor(enabled ? Theme.ink : Theme.disabled)
            .padding(.horizontal, 14).frame(width: 300, height: 32)
            .background(RoundedRectangle(cornerRadius: 4).fill(enabled ? Theme.key : Theme.keyOff))
            .background(RoundedRectangle(cornerRadius: 4).fill(enabled ? Theme.keyShadow : Theme.line).offset(y: pressed ? 0 : 2))
            .offset(y: pressed ? 2 : 0)
            .contentShape(Rectangle())
    }
}

struct DriveKeyAnchor: PreferenceKey {
    static var defaultValue: Anchor<CGRect>? = nil
    static func reduce(value: inout Anchor<CGRect>?, nextValue: () -> Anchor<CGRect>?) { value = value ?? nextValue() }
}

// The list the drive key opens. The window behind it dims until a place is picked or the list is dismissed.
struct DriveList: View {
    let choices: [VolumeChoice]
    let current: String?
    let pick: (VolumeChoice) -> Void
    @State private var hover: String?
    var body: some View {
        VStack(spacing: 0) {
            ForEach(Array(choices.enumerated()), id: \.element.id) { i, c in
                let sel = c.id == current
                HStack(spacing: 14) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(c.name + (c.removable ? "   EXTERNAL" : "")).font(Theme.semi(14)).foregroundColor(Theme.ink).lineLimit(1)
                        Text("\(Engine.fmt(c.free)) free of \(Engine.fmt(c.total))").font(Theme.sans(12)).foregroundColor(Theme.muted)
                    }
                    Spacer(minLength: 12)
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Rectangle().fill(Theme.line)
                            Rectangle().fill(sel ? Theme.orange : Theme.used)
                                .frame(width: g.size.width * CGFloat(c.total > 0 ? Double(c.total - c.free) / Double(c.total) : 0))
                        }
                    }.frame(width: 180, height: 8)
                }
                .padding(.horizontal, 20).frame(height: 56)
                .background(hover == c.id ? Theme.hover : Color.clear)
                .overlay(alignment: .leading) { if sel { Rectangle().fill(Theme.orange).frame(width: 4).padding(.vertical, 6) } }
                .overlay(alignment: .bottom) { if i < choices.count - 1 { Rectangle().fill(Theme.line).frame(height: 1).padding(.horizontal, 16) } }
                .contentShape(Rectangle())
                .onHover { inside in if inside { hover = c.id } else if hover == c.id { hover = nil } }
                .onTapGesture { pick(c) }
            }
        }
        .padding(.vertical, 8)
        .frame(width: 520)
        .background(RoundedRectangle(cornerRadius: 5).fill(Theme.key))
        .shadow(color: .black.opacity(0.25), radius: 10, x: 3, y: 6)
    }
}

struct FinderView: View {
    @EnvironmentObject var state: AppState
    @StateObject private var scanner = Scanner()
    @State private var current: FolderNode?
    @State private var added: Set<String> = []
    @State private var target: Int64 = 0
    @State private var place: VolumeChoice?
    @State private var listOpen = false
    @State private var choices: [VolumeChoice] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            FinderHeader(scanner: scanner, current: current, target: target)
            HStack(spacing: 8) {
                Button {
                    choices = VolumeChoice.all()
                    withAnimation(.easeOut(duration: 0.16)) { listOpen = true }
                } label: {
                    HStack(spacing: 10) {
                        Text(place?.name ?? "Home folder").font(Theme.bold(13)).lineLimit(1)
                        if let p = place { Text("·  \(Engine.fmt(p.free)) free").font(Theme.sans(13)).lineLimit(1) }
                        Spacer(minLength: 4)
                        Image(systemName: "chevron.down").font(.system(size: 11, weight: .bold))
                    }
                }
                .buttonStyle(DriveKeyStyle(open: listOpen))
                .disabled(scanner.running)
                .anchorPreference(key: DriveKeyAnchor.self, value: .bounds) { $0 }
                Spacer()
            }
            if let c = current {
                let isAdded = added.contains(c.path) || Custom.all.contains(c.path)
                PathBar(node: c, canAdd: Engine.canAdd(c.path) == nil, isAdded: isAdded, toggle: {
                    if isAdded { Custom.remove(c.path); added.remove(c.path) } else { Custom.add(c.path); added.insert(c.path) }
                    state.reload()
                }) { n in current = n }
                if c.kids.filter({ $0.size > 0 }).count + (c.ownSize > 0 ? 1 : 0) + ((c.parent == nil && place?.id == c.path) ? 1 : 0) > 1 {
                TreeMap(node: c, free: (c.parent == nil && place?.id == c.path) ? (place?.free ?? 0) : 0, added: added) { k in current = k }
                    .frame(height: 240)
                }
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(c.kids.filter { $0.size > 0 }) { k in
                            FolderRow(node: k, parentSize: max(1, c.size), isAdded: added.contains(k.path),
                                      open: { if !k.kids.isEmpty { current = k } },
                                      add: { Custom.add(k.path); added.insert(k.path); state.reload() })
                        }
                        FileRows(path: c.path, parentSize: max(1, c.size))
                    }
                }
            } else {
                Spacer()
            }
            Text("\u{201C}Add to list\u{201D} puts a folder or file in Deep clean, unticked. Nothing here deletes anything.")
                .font(Theme.sans(12)).foregroundColor(Theme.muted)
        }
        .padding(18).frame(minWidth: 760, minHeight: 520).background(Theme.chassis)
        .overlayPreferenceValue(DriveKeyAnchor.self) { anchor in
            GeometryReader { g in
                if listOpen, let a = anchor {
                    let r = g[a]
                    ZStack(alignment: .topLeading) {
                        Color.black.opacity(0.42).contentShape(Rectangle())
                            .onTapGesture { withAnimation(.easeOut(duration: 0.12)) { listOpen = false } }
                        DriveList(choices: choices, current: place?.id ?? Engine.home.path) { c in
                            withAnimation(.easeOut(duration: 0.12)) { listOpen = false }
                            if c.id != scanner.root?.path { place = c; start(c.id) }
                        }
                        .offset(x: r.minX, y: r.maxY + 6)
                    }
                    .transition(.opacity)
                }
            }
        }
        .onExitCommand { withAnimation(.easeOut(duration: 0.12)) { listOpen = false } }
        .onAppear { if scanner.root == nil { place = VolumeChoice.all().first; start(Engine.home.path) } }
        .onDisappear { scanner.cancel = true }   // closing the map stops its scan
        .onChange(of: scanner.root?.id) { _ in current = scanner.root }
    }

    func start(_ path: String) {
        guard !scanner.running else { return }
        let dk = Engine.disk()
        if let p = place, p.id == path, path != Engine.home.path { target = p.total - p.free } else { target = path == "/" ? dk.total - dk.free : 0 }
        current = nil; scanner.start(path)
    }
}

struct FinderHeader: View {
    @ObservedObject var scanner: Scanner
    let current: FolderNode?
    let target: Int64
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(scanner.running ? "Storage map  ·  counting files" : "Storage map  ·  just looking, nothing is deleted here")
                .font(Theme.semi(12)).foregroundColor(Color(hex: 0xB4B1AA))
            if scanner.running {
                Text("\(scanner.scanned) FILES").font(Theme.display(52)).foregroundColor(Theme.orange)
                ScanBar(done: scanner.scannedBytes, target: target).frame(height: 14)
                Text(target > 0 ? "Measured \(Engine.fmt(scanner.scannedBytes)) of \(Engine.fmt(target)) used" : "Measured \(Engine.fmt(scanner.scannedBytes)) so far")
                    .font(Theme.semi(13)).foregroundColor(Theme.screenInk)
            } else if let c = current {
                let p = Engine.parts(c.size)
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(p.0).font(Theme.display(52)).padding(.leading, -5)   // side bearing
                    Text(p.1).font(Theme.display(22))
                }.foregroundColor(Theme.orange)
                Text(c.path.replacingOccurrences(of: Engine.home.path, with: "~")).font(Theme.sans(15)).foregroundColor(Theme.screenInk).lineLimit(1).truncationMode(.middle)
                Text("\(c.files) files").font(Theme.sans(12)).foregroundColor(Theme.screenMuted)
            }
        }
        .padding(20).frame(maxWidth: .infinity, alignment: .leading).frame(minHeight: 150)
        .overlay(alignment: .trailing) { if scanner.running { SpinRing().frame(width: 56, height: 56).padding(.trailing, 28) } }
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
    }
}

// A large turning ring for the whole scan: impossible to miss.
struct SpinRing: View {
    @State private var start = Date()
    var body: some View {
        // driven by time at the display's frame rate (300° a second), so a late frame never jumps
        TimelineView(.animation) { tl in
            let angle = (max(0, tl.date.timeIntervalSince(start)) * 300).truncatingRemainder(dividingBy: 360)
            ZStack {
                Circle().stroke(Theme.track, lineWidth: 5)
                Circle().trim(from: 0, to: 0.3).stroke(Theme.orange, style: StrokeStyle(lineWidth: 5, lineCap: .round))
                    .rotationEffect(.degrees(angle))
            }
        }
    }
}

// Where you are in the Storage map: a Back key, then every folder from the top down, each one clickable.
struct PathBar: View {
    let node: FolderNode
    var canAdd = false
    var isAdded = false
    var toggle: () -> Void = {}
    let go: (FolderNode) -> Void
    var body: some View {
        let chain = trail()
        HStack(spacing: 10) {
            Button { if let p = node.parent { go(p) } } label: {
                HStack(spacing: 6) { Image(systemName: "chevron.left").font(.system(size: 11, weight: .bold)); Text("Back") }
            }
            .buttonStyle(KeyStyle()).disabled(node.parent == nil)
            .keyboardShortcut(.leftArrow, modifiers: .command)
            ScrollViewReader { proxy in
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(Array(chain.enumerated()), id: \.offset) { i, n in
                            if i > 0 { Chevron(color: Theme.soft).scaleEffect(0.7) }
                            Button(name(n)) { go(n) }
                                .buttonStyle(.plain)
                                .font(i == chain.count - 1 ? Theme.bold(13) : Theme.sans(13))
                                .foregroundColor(i == chain.count - 1 ? Theme.ink : Theme.muted)
                                .disabled(i == chain.count - 1)
                                .id(i)
                        }
                    }
                }
                .onAppear { proxy.scrollTo(chain.count - 1, anchor: .trailing) }
                .onChange(of: node.id) { _ in proxy.scrollTo(trail().count - 1, anchor: .trailing) }
            }
            // add the folder you've opened, so you don't have to go back to add it
            if canAdd || isAdded {
                Button(isAdded ? "Added  ·  remove" : "+ Add this folder") { toggle() }.buttonStyle(KeyStyle(toggled: isAdded))
            }
            // open the folder you're looking at, beside its name in the trail
            Button("Show in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: node.path)) }.buttonStyle(KeyStyle())
        }
    }
    func trail() -> [FolderNode] { var c: [FolderNode] = []; var n: FolderNode? = node; while let x = n { c.insert(x, at: 0); n = x.parent }; return c }
    func name(_ n: FolderNode) -> String {
        if n.parent != nil { return n.name }
        return n.path == "/" ? "Disk" : n.path == Engine.home.path ? "Home" : (n.path as NSString).lastPathComponent
    }
}

// What's inside an item: its files and sub-folders, each with a tick box. In a ticked item everything ticked is
// cleaned (unticking keeps it always); in an unticked item only what you tick here is cleaned, in the next clean.
// Clicking a folder's name opens it, and Back goes up (never above the item's own folders).
struct InsideList: View {
    @EnvironmentObject var state: AppState
    let roots: [String]
    let catID: String
    var isCustom = false
    var exts: Set<String>? = nil   // only these files (old installers in Downloads)
    var topOnly = false
    var exclude: Set<String> = []  // folders never touched (an agent's memory)
    @State private var here = ""   // "" = the top, all of the item's folders at once
    @State private var tick = 0
    @State private var sizes: [String: Int64] = [:]   // each folder's size, filled in as it's measured
    struct Entry: Identifiable { let id: String; let name: String; let dir: Bool; let size: Int64; var modified = Date.distantPast; var sub: String? = nil }

    func rootOf(_ p: String) -> String? { roots.first { p == $0 || p.hasPrefix($0 + "/") } }

    var body: some View {
        let skip = Custom.skip
        let c = state.cats.first { $0.id == catID }
        let whole = c?.checked ?? true          // ticked: everything here is cleaned unless unticked
        let partial = c?.partial ?? false       // unticked with picks: only what is ticked here
        let picks: Set<String> = c?.picks ?? []
        let pickBytes: Int64 = c?.pickBytes ?? -1
        // files newer than the item's age stay, but only in a ticked item (a file picked by hand goes whatever its age)
        let ageLimit: Int = whole && (c?.ageChoice ?? false) ? (c?.ageMinutes ?? 0) : 0
        let entries = here.isEmpty ? roots.flatMap { r in list(r, sub: "in " + r.replacingOccurrences(of: Engine.home.path, with: "~")) } : list(here, sub: nil)
        let pickedText: String = "\(picks.count) picked" + (pickBytes >= 0 ? "  ·  " + Engine.fmt(pickBytes) : "")
        let barText: String = c == nil ? "" : whole ? "All of it, except anything unticked" : partial ? pickedText : "Nothing picked yet"
        let hint: String = whole ? "Everything ticked is cleaned. Untick anything that should always stay. Click a folder to look inside it."
            : partial ? "Just the ticked files and folders are cleaned, this once. Select all cleans the whole item."
            : "Tick files or folders to clean just those, this once, or Select all for the whole item. Click a folder to look inside it."
        VStack(alignment: .leading, spacing: 0) {
            Text(isCustom ? "Inside this folder" : "What's inside").font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
            Text(hint).font(Theme.sans(12)).foregroundColor(Theme.soft).fixedSize(horizontal: false, vertical: true).padding(.bottom, 6)
            // the bar: what is picked, and Select all / Clear at the right
            HStack(spacing: 16) {
                Text(barText).font(Theme.semi(12)).foregroundColor(partial ? Theme.orangeDeep : Theme.muted).lineLimit(1)
                Spacer(minLength: 8)
                Button { selectAll() } label: { Text("Select all").underline() }
                    .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.ink)
                Button { clearAll() } label: { Text("Clear").underline() }
                    .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.ink)
            }
            .padding(.vertical, 6)
            .overlay(Rectangle().fill(Theme.line).frame(height: 1), alignment: .bottom)
            if !here.isEmpty {
                let root = rootOf(here) ?? here
                HStack(spacing: 8) {
                    Button { here = roots.contains(here) ? "" : (here as NSString).deletingLastPathComponent } label: {
                        HStack(spacing: 6) {
                            Image(systemName: "chevron.left").font(.system(size: 11, weight: .bold))
                            Text("Back  ·  " + (root as NSString).lastPathComponent + String(here.dropFirst(root.count)).replacingOccurrences(of: "/", with: "  \u{203A}  ")).lineLimit(1).truncationMode(.middle)
                        }
                    }
                    .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.ink)
                    Spacer(minLength: 8)
                    Button { NSWorkspace.shared.open(URL(fileURLWithPath: here)) } label: { Text("Open in Finder").underline() }
                        .buttonStyle(.plain).font(Theme.sans(12)).foregroundColor(Theme.ink)
                }
                .padding(.vertical, 6)
                .overlay(Rectangle().fill(Theme.line).frame(height: 1), alignment: .bottom)
            }
            if entries.isEmpty { Text("Empty").font(Theme.sans(12)).foregroundColor(Theme.soft).padding(.vertical, 6) }
            ForEach(Array(entries.prefix(60))) { e in
                let parent = (e.id as NSString).deletingLastPathComponent
                let inheritedSkip = !roots.contains(parent) && Custom.isSkipped(parent, skip)
                let ownSkip = Custom.isSkipped(e.id, skip)
                let inheritedPick = picks.contains(where: { e.id.hasPrefix($0 + "/") })   // a folder around it is picked
                let locked = inheritedSkip || (!whole && (inheritedPick || ownSkip))      // something around it decides
                let on = whole ? !(inheritedSkip || ownSkip) : (picks.contains(e.id) || inheritedPick)
                let tooNew = ageLimit > 0 && !e.dir && e.modified > Date().addingTimeInterval(-Double(ageLimit) * 60)
                let fileSize: String = tooNew ? "too new, stays" : e.size < 1024 ? "under 1 KB" : Engine.fmt(e.size)
                let sizeText: String = e.dir ? (sizes[e.id].map { Engine.fmt($0) + "  ·  folder" } ?? "folder") : fileSize
                HStack(spacing: 10) {
                    Button { tap(e.id, whole: whole, locked: locked, ownSkip: ownSkip) } label: { Check(on: on, locked: locked) }
                        .buttonStyle(.plain)
                    Button { if e.dir { here = e.id } } label: {
                        VStack(alignment: .leading, spacing: 1) {
                            HStack(spacing: 8) {
                                Text(e.name).lineLimit(1).truncationMode(.middle)
                                Spacer()
                                Text(sizeText).foregroundColor(Theme.soft)
                                if e.dir { Chevron(color: Theme.soft) }
                            }
                            if let sub = e.sub { Text(sub).font(Theme.sans(10.5)).foregroundColor(Theme.soft).lineLimit(1).truncationMode(.middle) }   // which folder it comes from
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).font(Theme.sans(12.5)).foregroundColor(Theme.ink)
                }
                .padding(.vertical, 5)
                .overlay(Rectangle().fill(Theme.line).frame(height: 1), alignment: .bottom)
            }
            if entries.count > 60 { Text("+ \(entries.count - 60) more (open the folder in Finder to see them all)").font(Theme.sans(11)).foregroundColor(Theme.soft).padding(.top, 6) }
        }
        // tick only redraws the list (the keep list isn't observed). It used to be the list's .id, which rebuilt it on
        // every tick: the folder shown went back to the top and the panel's scroll jumped up.
        .background(Color.clear.opacity(tick < 0 ? 1 : 0))
        .padding(.bottom, 12)
        .onChange(of: catID) { _ in here = "" }
        .onAppear { measure(here) }
        .onChange(of: here) { h in measure(h) }
    }

    // A row's box. In a ticked item, unticking puts it on the "always keep" list; in an unticked item, ticking picks
    // it for the next clean (a picked folder covers everything in it).
    func tap(_ path: String, whole: Bool, locked: Bool, ownSkip: Bool) {
        if locked { return }
        if whole {
            Custom.setSkipped(path, !ownSkip); tick += 1; state.scan([catID])
            return
        }
        guard let i = state.cats.firstIndex(where: { $0.id == catID }) else { return }
        var p = state.cats[i].picks
        if p.contains(path) { p.remove(path) }
        else { p = p.filter { !$0.hasPrefix(path + "/") }; p.insert(path) }   // now covered by the folder
        state.cats[i].picks = p
        tick += 1; state.picksChanged(catID)
    }

    // Select all: the whole item, with nothing held back here
    func selectAll() {
        for sp in Custom.skip where roots.contains(where: { sp == $0 || sp.hasPrefix($0 + "/") }) { Custom.setSkipped(sp, false) }
        guard let i = state.cats.firstIndex(where: { $0.id == catID }) else { return }
        state.cats[i].checked = true; state.cats[i].picks = []
        tick += 1; state.picksChanged(catID)
    }

    // Clear: nothing from this item
    func clearAll() {
        guard let i = state.cats.firstIndex(where: { $0.id == catID }) else { return }
        state.cats[i].checked = false; state.cats[i].picks = []
        tick += 1; state.picksChanged(catID)
    }

    func measure(_ dir: String) {
        let dirs = (dir.isEmpty ? roots.flatMap { list($0, sub: nil) } : list(dir, sub: nil)).filter { $0.dir }.map { $0.id }
        DispatchQueue.global(qos: .utility).async {
            for d in dirs {
                var sum: Int64 = 0
                if let e = FileManager.default.enumerator(at: URL(fileURLWithPath: d), includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey], options: [], errorHandler: { _, _ in true }) {
                    for case let u as URL in e {
                        if let v = try? u.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey]), v.isRegularFile == true { sum += Int64(v.totalFileAllocatedSize ?? 0) }
                    }
                }
                DispatchQueue.main.async { sizes[d] = sum }
            }
        }
    }

    func list(_ dir: String, sub: String?) -> [Entry] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileSizeKey, .contentModificationDateKey, .creationDateKey, .addedToDirectoryDateKey]
        let items = (try? FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: dir), includingPropertiesForKeys: keys)) ?? []
        var dirs: [Entry] = [], files: [Entry] = []
        for u in items {
            guard let v = try? u.resourceValues(forKeys: Set(keys)), v.isSymbolicLink != true else { continue }
            if v.isDirectory == true {
                if topOnly || exclude.contains(u.lastPathComponent) { continue }
                dirs.append(Entry(id: u.path, name: u.lastPathComponent, dir: true, size: 0, sub: sub))
            } else {
                if let x = exts, !x.contains(u.pathExtension.lowercased()) { continue }
                files.append(Entry(id: u.path, name: u.lastPathComponent, dir: false, size: Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), modified: Engine.ageDate(v), sub: sub))
            }
        }
        return dirs.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending } + files.sorted { $0.size > $1.size }
    }
}

// Progress while measuring: a filling bar when the total is known, a moving block when it isn't.
struct ScanBar: View {
    let done: Int64
    let target: Int64
    @State private var start = Date()
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Rectangle().fill(Theme.track)
                if target > 0 {
                    Rectangle().fill(Theme.orange).frame(width: g.size.width * min(1, CGFloat(Double(done) / Double(target))))
                } else {
                    // 150 pt a second by the clock, wrapped round the track and the block's own width
                    TimelineView(.animation) { tl in
                        let block = g.size.width * 0.22
                        let span = Double(max(1, g.size.width + block))
                        let x = (max(0, tl.date.timeIntervalSince(start)) * 150).truncatingRemainder(dividingBy: span)
                        Rectangle().fill(Theme.orange)
                            .frame(width: block)
                            .offset(x: CGFloat(x) - block)
                    }
                }
            }
            .clipped()
        }
    }
}

struct FolderRow: View {
    @EnvironmentObject var state: AppState
    let node: FolderNode
    let parentSize: Int64
    let isAdded: Bool
    let open: () -> Void
    let add: () -> Void
    var body: some View {
        let cov = Engine.coverage(node.path, state.cats)
        let refusal = Engine.canAdd(node.path)
        let sub: String = {
            if let name = cov.0 { return (cov.1 ? "Cleaned by: " : "Has cleanup items: ") + name }
            if let r = refusal, !isAdded { return Engine.calm(r) + "  ·  \(node.files) file" + (node.files == 1 ? "" : "s") }
            return "\(node.files) file" + (node.files == 1 ? "" : "s")
        }()
        let frac = Double(node.size) / Double(parentSize)
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(node.name).font(Theme.semi(14)).foregroundColor(Theme.ink).lineLimit(1)
                Text(sub).font(Theme.sans(11.5)).foregroundColor(Theme.soft).lineLimit(1)
            }
            .frame(width: 250, alignment: .leading)
            if isAdded || cov.1 {
                Text(isAdded ? "ADDED" : "ALREADY INCLUDED").font(Theme.semi(11)).foregroundColor(Theme.screenInk)
                    .frame(width: 180, height: 26).background(RoundedRectangle(cornerRadius: 4).fill(Theme.screen))
            } else if refusal == nil {
                Button("+ Add to list") { add() }.buttonStyle(KeyStyle()).frame(width: 200, alignment: .leading)
            } else {
                Spacer().frame(width: 180)
            }
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Rectangle().fill(Theme.line)
                    Rectangle().fill(Theme.ink).frame(width: max(2, g.size.width * CGFloat(frac)))
                }
            }.frame(height: 8)
            VStack(alignment: .trailing, spacing: 2) {
                Text(Engine.fmt(node.size)).font(Theme.semi(14))
                Text(frac < 0.01 ? "<1%" : String(format: "%.0f%%", 100.0 * frac)).font(Theme.sans(11)).foregroundColor(Theme.soft)
            }.frame(width: 90, alignment: .trailing)
            Chevron(color: node.kids.isEmpty ? Color.clear : Theme.soft)
        }
        .padding(.horizontal, 12).frame(height: 52)
        .overlay(Rectangle().fill(Theme.line).frame(height: 1), alignment: .bottom)
        .contentShape(Rectangle())
        .onTapGesture { open() }
    }
}


// The block chart: each folder is a block sized by its share of the folder you're in; at the top of a disk, free
// space is a hatched block. An orange corner: part of it is already a cleanup item. Click a block to go in.
struct TreeMap: View {
    @EnvironmentObject var state: AppState
    let node: FolderNode
    let free: Int64
    let added: Set<String>
    let open: (FolderNode) -> Void
    @State private var hover: Int? = nil

    struct Block { let node: FolderNode?; let name: String; let size: Int64; let free: Bool; let marked: Bool; let rest: Bool; var rect: CGRect = .zero }

    // the blocks in size order (the order doesn't depend on the space they're drawn in)
    func blocks() -> [Block] {
        var all: [Block] = node.kids.filter { $0.size > 0 }.map { k in
            Block(node: k, name: k.name, size: k.size, free: false,
                  marked: added.contains(k.path) || Custom.all.contains(k.path) || Engine.coverage(k.path, state.cats).0 != nil, rest: false)
        }
        if node.ownSize > 0 { all.append(Block(node: nil, name: "Files here", size: node.ownSize, free: false, marked: false, rest: false)) }
        let total = all.reduce(Int64(0)) { $0 + $1.size } + free
        guard total > 0 else { return [] }
        all.sort { $0.size > $1.size }
        // tiny folders become one "smaller" block, so every block stays big enough to point at
        var keep: [Block] = [], rest: [Block] = []
        for b in all { if Double(b.size) >= Double(total) * 0.012 && keep.count < 24 { keep.append(b) } else { rest.append(b) } }
        if !rest.isEmpty {
            keep.append(Block(node: nil, name: "\(rest.count) smaller", size: rest.reduce(Int64(0)) { $0 + $1.size }, free: false, marked: rest.contains { $0.marked }, rest: true))
            keep.sort { $0.size > $1.size }
        }
        if free > 0 { keep.append(Block(node: nil, name: "Free space", size: free, free: true, marked: false, rest: false)) }   // always last: the empty end of the disk
        return keep
    }

    // squarified layout: blocks as close to square as the space allows
    func layout(_ size: CGSize) -> [Block] {
        var keep = blocks()
        guard !keep.isEmpty, size.width > 40, size.height > 30 else { return [] }
        let scale = Double(size.width * size.height) / Double(keep.reduce(Int64(0)) { $0 + $1.size })
        var rect = CGRect(origin: .zero, size: size), i = 0
        func worst(_ row: [Double], _ side: Double) -> Double {
            let s = row.reduce(0, +), mx = row.max() ?? 0, mn = row.min() ?? 1
            return max(side * side * mx / (s * s), s * s / (side * side * mn))
        }
        while i < keep.count {
            let side = Double(min(rect.width, rect.height)); if side <= 0 { break }
            var row: [Double] = [], prev = Double.greatestFiniteMagnitude; let start = i
            while i < keep.count {
                row.append(Double(keep[i].size) * scale)
                let w = worst(row, side)
                if row.count > 1 && w > prev { row.removeLast(); break }
                prev = w; i += 1
            }
            let sum = row.reduce(0, +)
            if rect.width >= rect.height {
                let cw = CGFloat(sum / Double(rect.height)); var y = rect.minY
                for k in 0..<row.count { let h = CGFloat(row[k]) / cw; keep[start + k].rect = CGRect(x: rect.minX, y: y, width: cw, height: h); y += h }
                rect = CGRect(x: rect.minX + cw, y: rect.minY, width: rect.width - cw, height: rect.height)
            } else {
                let rh = CGFloat(sum / Double(rect.width)); var x = rect.minX
                for k in 0..<row.count { let w = CGFloat(row[k]) / rh; keep[start + k].rect = CGRect(x: x, y: rect.minY, width: w, height: rh); x += w }
                rect = CGRect(x: rect.minX, y: rect.minY + rh, width: rect.width, height: rect.height - rh)
            }
        }
        return keep
    }

    var caption: String {
        let bs = blocks()
        if let h = hover, h < bs.count {
            let b = bs[h]
            return b.name + "  ·  " + Engine.fmt(b.size) + (b.marked ? "  ·  part of it is already a cleanup item" : "") + (!(b.node?.kids.isEmpty ?? true) ? "  ·  click to go inside" : "")
        }
        return "Click a block to go inside it  ·  orange corner: part of it is already a cleanup item" + (free > 0 ? "  ·  hatched: free space" : "")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            GeometryReader { g in
                let bs = layout(g.size)
                ZStack(alignment: .topLeading) {
                    ForEach(bs.indices, id: \.self) { i in
                        let b = bs[i]
                        let r = b.rect.insetBy(dx: 1.5, dy: 1.5)
                        let canOpen = !(b.node?.kids.isEmpty ?? true)
                        let hot = hover == i && canOpen
                        ZStack(alignment: .topLeading) {
                            if b.free { Hatch(ink: Theme.line, ground: Theme.hover) }
                            else { Rectangle().fill(hot ? Theme.screen : b.rest ? Theme.hover : Theme.key) }
                            if r.width >= 46 && r.height >= 34 {
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(b.name).font(Theme.semi(12)).foregroundColor(b.free ? Theme.muted : hot ? Theme.screenInk : Theme.ink).lineLimit(1)
                                    Text(Engine.fmt(b.size)).font(Theme.sans(11)).foregroundColor(hot ? Theme.screenMuted : Theme.soft).lineLimit(1)
                                }.padding(.horizontal, 8).padding(.vertical, 6).padding(.trailing, b.marked ? 8 : 0)
                            }
                            if b.marked {
                                let c = min(14, min(r.width, r.height) / 2)
                                Path { p in p.move(to: CGPoint(x: r.width - c, y: 0)); p.addLine(to: CGPoint(x: r.width, y: 0)); p.addLine(to: CGPoint(x: r.width, y: c)); p.closeSubpath() }
                                    .fill(Theme.orange)
                            }
                        }
                        .frame(width: max(0, r.width), height: max(0, r.height)).clipped()
                        .contentShape(Rectangle())
                        .onHover { h in if h { hover = i } else if hover == i { hover = nil } }
                        .onTapGesture { if canOpen, let n = b.node { open(n) } }
                        .offset(x: r.minX, y: r.minY)
                    }
                }
            }
            Text(caption).font(Theme.sans(11.5)).foregroundColor(Theme.muted).lineLimit(1)
        }
    }
}


// A folder you added: what's actually in it, in plain words (worked out here; nothing is sent anywhere).
struct FolderAbout: View {
    let root: String
    @State private var text = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("What is it?").font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
            Text(text.isEmpty ? "Looking inside \((root as NSString).lastPathComponent)…" : text)
                .font(Theme.sans(13)).foregroundColor(Theme.ink).fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 8)
        .onAppear { load() }
        .onChange(of: root) { _ in text = ""; load() }
    }
    func load() {
        let r = root
        DispatchQueue.global(qos: .userInitiated).async {
            let d = FolderSummary.describe(r)
            DispatchQueue.main.async { if r == root { text = d } }
        }
    }
}

enum FolderSummary {
    static let kinds: [(String, [String])] = [
        ("videos", ["mp4", "mov", "mkv", "avi", "wmv", "webm", "m4v", "mts"]),
        ("photos and images", ["jpg", "jpeg", "png", "gif", "heic", "webp", "bmp", "tif", "tiff", "raw", "cr2", "nef", "arw", "dng", "psd", "svg"]),
        ("music and audio", ["mp3", "wav", "flac", "aac", "m4a", "ogg", "aiff"]),
        ("documents", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "odt", "csv", "rtf", "pages", "key", "numbers"]),
        ("3D and CAD files", ["stl", "3mf", "obj", "fbx", "blend", "step", "stp", "iges", "igs", "sldprt", "sldasm", "slddrw", "dwg", "dxf", "f3d", "gcode"]),
        ("archives and installers", ["zip", "rar", "7z", "tar", "gz", "iso", "dmg", "pkg", "exe", "msi", "deb", "appimage"]),
        ("code", ["swift", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "go", "rs", "kt", "html", "css", "json", "ipynb", "m", "cs"]),
    ]

    static func describe(_ root: String) -> String {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: root, isDirectory: &isDir), !isDir.boolValue {
            let a = try? FileManager.default.attributesOfItem(atPath: root)
            let size = (a?[.size] as? NSNumber)?.int64Value ?? 0, t = (a?[.modificationDate] as? Date) ?? .distantPast
            let e = (root as NSString).pathExtension.uppercased()
            return "A single file: " + (e.isEmpty ? "" : "a \(e) file of ") + Engine.fmt(size) + ", last changed " + ago(t) + "."
        }
        var kindOf: [String: String] = [:]
        for (k, exts) in kinds { for e in exts { kindOf[e] = k } }
        var bytes: [String: Int64] = [:], files: [String: Int64] = [:]
        var total: Int64 = 0, count: Int64 = 0, old30: Int64 = 0, old365: Int64 = 0
        var newest = Date.distantPast
        var signs: [String] = []
        let kept = Keep.all + Keep.builtIn + Custom.skip
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .isRegularFileKey, .totalFileAllocatedSizeKey, .fileSizeKey, .contentModificationDateKey, .creationDateKey, .addedToDirectoryDateKey]
        guard let e = FileManager.default.enumerator(at: URL(fileURLWithPath: root), includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true }) else { return "" }
        func sign(_ s: String) { if !signs.contains(s) { signs.append(s) } }
        for case let url as URL in e {
            guard let v = try? url.resourceValues(forKeys: Set(keys)) else { continue }
            if v.isSymbolicLink == true { continue }
            if Keep.isKept(url.path, kept) { if v.isDirectory == true { e.skipDescendants() }; continue }
            if v.isDirectory == true {
                switch url.lastPathComponent.lowercased() {
                case ".git": sign("a code project (its history is in a .git folder)")
                case "node_modules": sign("JavaScript packages (node_modules) that reinstall with npm install")
                case "cache", "caches", ".cache": sign("cache folders that apps rebuild")
                case "venv", ".venv": sign("a Python environment (venv) that can be recreated")
                case "build", "dist", "target", "deriveddata": sign("build output that is made again on the next build")
                default: break
                }
                continue
            }
            guard v.isRegularFile == true else { continue }
            let len = Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), t = Engine.ageDate(v)
            let k = kindOf[url.pathExtension.lowercased()] ?? "other files"
            bytes[k, default: 0] += len; files[k, default: 0] += 1
            total += len; count += 1
            if t > newest { newest = t }
            let days = Date().timeIntervalSince(t) / 86400
            if days > 30 { old30 += len }; if days > 365 { old365 += len }
            if count >= 400_000 { break }
        }
        if count == 0 { return "It's empty right now." }
        var s = "\(Engine.fmt(total)) in \(count) files" + (count >= 400_000 ? " (counted the first 400,000)" : "") + ". "
        let top = bytes.sorted { $0.value > $1.value }.prefix(3)
        s += (top.first!.value * 100 / max(1, total) >= 60 ? "Mostly " : "Mainly ")
        s += top.map { "\($0.key) (\(Engine.fmt($0.value)), \(files[$0.key] ?? 0))" }.joined(separator: ", ") + ". "
        if newest > .distantPast { s += "Last changed " + ago(newest) + ". " }
        if old30 > 0 { s += "\(Engine.fmt(old30)) hasn't been touched for over a month" + (old365 > 0 ? ", \(Engine.fmt(old365)) for over a year" : "") + ". " }
        if !signs.isEmpty { s += "It has " + signs.joined(separator: "; ") + "." }
        return s.trimmingCharacters(in: .whitespaces)
    }

    static func ago(_ t: Date) -> String {
        let h = Date().timeIntervalSince(t) / 3600
        if h < 1 { return "within the last hour" }
        if h < 24 { return "\(Int(h)) hours ago" }
        let d = h / 24
        if d < 60 { return "\(Int(d)) days ago" }
        if d < 730 { return "\(Int(d / 30)) months ago" }
        return "\(Int(d / 365)) years ago"
    }
}


// The files directly in the open folder, one row each, under its folders (read only for this folder).
struct FileRows: View {
    @EnvironmentObject var state: AppState
    let path: String
    let parentSize: Int64
    @State private var tick = 0
    @State private var files: [(name: String, path: String, size: Int64)] = []
    var body: some View {
        Group {
            ForEach(Array(files.prefix(400).enumerated()), id: \.offset) { _, f in
                let frac = Double(f.size) / Double(parentSize)
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(f.name).font(Theme.semi(14)).foregroundColor(Theme.ink).lineLimit(1).truncationMode(.middle)
                        Text(kind(f.name)).font(Theme.sans(11.5)).foregroundColor(Theme.soft)
                    }
                    .frame(width: 250, alignment: .leading)
                    let isAdded = Custom.all.contains(f.path)
                    if isAdded {
                        Button("Added  ·  remove") { Custom.remove(f.path); tick += 1; state.reload() }.buttonStyle(KeyStyle(toggled: true)).frame(width: 180, alignment: .leading)
                    } else if Engine.canAdd(f.path) == nil {
                        Button("+ Add to list") { Custom.add(f.path); tick += 1; state.reload() }.buttonStyle(KeyStyle()).frame(width: 180, alignment: .leading)
                    } else {
                        Spacer().frame(width: 180)
                    }
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Rectangle().fill(Theme.line)
                            Rectangle().fill(Theme.ink).frame(width: max(2, g.size.width * CGFloat(frac)))
                        }
                    }.frame(height: 8)
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(Engine.fmt(f.size)).font(Theme.semi(14))
                        Text(frac < 0.01 ? "<1%" : String(format: "%.0f%%", 100.0 * frac)).font(Theme.sans(11)).foregroundColor(Theme.soft)
                    }.frame(width: 90, alignment: .trailing)
                    Chevron(color: .clear)
                }
                .padding(.horizontal, 12).frame(height: 52)
                .overlay(Rectangle().fill(Theme.line).frame(height: 1), alignment: .bottom)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: f.path)]) }
            }
            if files.count > 400 {
                Text("+ \(files.count - 400) smaller files").font(Theme.sans(12)).foregroundColor(Theme.soft).padding(12)
            }
        }
        .id(tick)
        .onAppear { load() }
        .onChange(of: path) { _ in load() }
    }
    func kind(_ n: String) -> String { let e = (n as NSString).pathExtension.uppercased(); return e.isEmpty ? "file" : e + " file" }
    func load() {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileSizeKey]
        let items = (try? FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: path), includingPropertiesForKeys: keys)) ?? []
        files = items.compactMap { u in
            guard let v = try? u.resourceValues(forKeys: Set(keys)), v.isRegularFile == true, v.isSymbolicLink != true else { return nil }
            return (u.lastPathComponent, u.path, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0))
        }.sorted { $0.size > $1.size }
    }
}
