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
    var isSafe: Bool { level == .safe }
}

final class Counter {
    var files: Int64 = 0, bytes: Int64 = 0, skipped: Int64 = 0, trashedBytes: Int64 = 0
    var cancel = false
    var onItem: ((String, Int64, Bool, Bool) -> Void)?   // path, size, ok, toTrash
}

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
            if d.object(forKey: "v4.age.\(c.id)") != nil { c.ageMinutes = d.integer(forKey: "v4.age.\(c.id)") }
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
            if d.object(forKey: "v4.age.\(c.id)") != nil { c.ageMinutes = d.integer(forKey: "v4.age.\(c.id)") }
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

    static func fmt(_ b: Int64) -> String {
        if b <= 0 { return "0 MB" }
        let f = ByteCountFormatter(); f.countStyle = .file; f.allowedUnits = [.useKB, .useMB, .useGB, .useTB]
        return f.string(fromByteCount: b)
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
            let mult: Double = unit.hasPrefix("TB") ? 1e12 : unit.hasPrefix("GB") ? 1e9 : unit.hasPrefix("MB") ? 1e6 : 1e3
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
}

enum Theme {
    static let chassis = Color(hex: 0xD8D5CE), pane = Color(hex: 0xE4E1DA), selected = Color(hex: 0xE9E6E0), hover = Color(hex: 0xE0DDD6)
    static let screen = Color(hex: 0x121212), screenInk = Color(hex: 0xE9E6DF), screenMuted = Color(hex: 0x8A8A86)
    static let track = Color(hex: 0x3A3A38), used = Color(hex: 0x6F6C66)
    static let ink = Color(hex: 0x1B1B1A), muted = Color(hex: 0x5F5C56), soft = Color(hex: 0x605C56), line = Color(hex: 0xC3BFB6)
    static let orange = Color(hex: 0xFF5B1F), orangeDeep = Color(hex: 0xB5400F)
    static let key = Color(hex: 0xECEAE5), keyShadow = Color(hex: 0xA9A59C), disabled = Color(hex: 0x9A968F)

    static func display(_ s: CGFloat) -> Font { .custom("BigShouldersDisplay-Black", size: s) }
    static func sans(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Regular", size: s) }
    static func semi(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-SemiBold", size: s) }
    static func bold(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Bold", size: s) }
    static func mono(_ s: CGFloat) -> Font { .custom("IBMPlexMono-Regular", size: s) }
    static func monoSemi(_ s: CGFloat) -> Font { .custom("IBMPlexMono-SemiBold", size: s) }

    // Names a newcomer can act on without opening anything: the first is the no-worry clean.
    static func tierName(_ l: Level) -> String { l == .safe ? "Quick clean" : l == .review ? "Big caches & history" : "Personal & work items" }
    static func tierDesc(_ l: Level) -> String { l == .safe ? "Caches and leftovers that rebuild" : l == .review ? "Big caches that download again, or old history" : "Can hold your work or history. Check before cleaning" }
    // one plain line under each section's heading, so nobody has to guess what's in it
    static func tierExplain(_ l: Level) -> String {
        l == .safe ? "Caches, temp files and leftovers that apps and macOS rebuild or no longer need. Your files, logins and passwords aren't touched. Clean these without worry; some things load a little slower once."
        : l == .review ? "Things that come back by downloading them again, or history you might still want. Read each one before you tick it."
        : "These can hold your own work or history. Nothing here is ticked for you; open an item to see exactly what goes."
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
        let fill = !enabled ? Color(hex: 0xD0CDC6) : toggled ? Theme.ink : primary ? Theme.orange : Theme.key
        let shadow = !enabled ? Theme.line : toggled ? Color.black : primary ? Theme.orangeDeep : Theme.keyShadow
        return configuration.label
            .textCase(.uppercase).font(Theme.semi(big ? 14 : 12))
            .foregroundColor(toggled ? Theme.screenInk : (enabled ? Theme.ink : Theme.disabled))
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

struct Check: View {
    let on: Bool
    var empty = false
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 2).fill(on && !empty ? Theme.orange : Color.clear)
            RoundedRectangle(cornerRadius: 2).stroke(empty ? Theme.line : (on ? Theme.orange : Theme.ink), lineWidth: 1.6)
            if on && !empty {
                Path { p in p.move(to: CGPoint(x: 4, y: 8.5)); p.addLine(to: CGPoint(x: 7.5, y: 12)); p.addLine(to: CGPoint(x: 13, y: 5)) }
                    .stroke(Theme.ink, style: StrokeStyle(lineWidth: 2.2, lineCap: .square))
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
    private var counter: Counter?
    // a few items are measured at once (an SSD answers several folder reads in parallel)
    private let scanQ = DispatchQueue(label: "scan", attributes: .concurrent)
    private let scanSlots = DispatchSemaphore(value: 4)

    var open: Cat? { cats.first { $0.id == openID } }
    var selectedBytes: Int64 { cats.filter { $0.checked }.reduce(0) { $0 + $1.bytes } }
    var notTicked: Int64 { cats.filter { !$0.checked && !$0.isSafe && $0.kind != .trash }.reduce(0) { $0 + $1.bytes } }

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
                DispatchQueue.main.async {
                    if self.scanGen[snapshot.id] == gen, let j = self.cats.firstIndex(where: { $0.id == snapshot.id }) {
                        self.cats[j].files = cnt.files; self.cats[j].bytes = cnt.bytes; self.cats[j].scanned = true
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
            save(cats[i])
        }
        scan()
        status = "Back to default: Quick clean items ticked, default ages. Protected folders stay protected."
    }

    func clean(toTrash: Bool) {
        // the Trash item always runs first, so files moved to the Trash in this clean are never emptied by it
        let sel = cats.filter { $0.checked && ($0.bytes > 0 || $0.files > 0) }.sorted { ($0.kind == .trash ? 0 : 1) < ($1.kind == .trash ? 0 : 1) }
        guard !sel.isEmpty else { return }
        expected = max(1, sel.reduce(0) { $0 + $1.bytes })
        freed = 0; trashed = 0; cleanedFiles = 0; skipped = 0; log = []
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
            for (i, c) in sel.enumerated() where !cnt.cancel {
                DispatchQueue.main.async { self.currentItem = "ITEM \(i + 1) OF \(sel.count)  ·  \(c.name.uppercased())" }
                lock.lock(); let b0 = cnt.bytes, t0 = cnt.trashedBytes, f0 = cnt.files; lock.unlock()
                Engine.process(c, delete: true, toTrash: toTrash && !c.isSafe, cnt: cnt)   // Quick clean is always deleted for good
                lock.lock(); let b1 = cnt.bytes, t1 = cnt.trashedBytes, f1 = cnt.files; lock.unlock()
                summary.append("\(c.name): freed \(Engine.fmt(b1 - b0))" + (t1 > t0 ? " + \(Engine.fmt(t1 - t0)) to Trash" : "") + ", \(f1 - f0) files")
                lines.append("\(c.name)   ·   " + (b1 > b0 || t1 == t0 ? "\(Engine.fmt(b1 - b0)) freed" : "") + (t1 > t0 ? (b1 > b0 ? " + " : "") + "\(Engine.fmt(t1 - t0)) to the Trash" : ""))
            }
            Engine.log((cnt.cancel ? "STOPPED " : "CLEANED ") + "\(Engine.fmt(cnt.bytes)), \(cnt.files) files; skipped \(cnt.skipped). " + summary.joined(separator: " | "))
            DispatchQueue.main.async {
                timer.fire(); timer.invalidate()
                self.log.append(contentsOf: summary.map { "— " + $0 })
                self.results = lines
                self.freed = cnt.bytes; self.trashed = cnt.trashedBytes; self.cleanedFiles = cnt.files; self.skipped = cnt.skipped
                self.cleanStopped = cnt.cancel; self.cleanDone = true; self.cleanRunning = false
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

    var body: some View {
        VStack(spacing: 0) {
            DisplayView().padding(.horizontal, 18).padding(.top, 34)
            HStack(spacing: 8) {
                Button { openWindow(id: "finder") } label: { HStack(spacing: 8) { MapIcon(); Text("Storage map") } }.buttonStyle(KeyStyle(accent: true))
                Spacer()
                Button("Scan again") { state.scan() }.buttonStyle(KeyStyle()).disabled(state.scanning > 0 || state.cleanRunning)
                Button("Protected folders") { openWindow(id: "protect") }.buttonStyle(KeyStyle())
                Button("Reset to default") { state.resetAll() }.buttonStyle(KeyStyle())
                    .padding(.trailing, 4)
                Button(state.cleanRunning ? "Show cleaning" : "Clean \(Engine.fmt(state.selectedBytes))") {
                    if state.cleaning { openWindow(id: "cleaning") } else { confirm = true }
                }
                .buttonStyle(KeyStyle(primary: true, big: true))
                .disabled(!state.cleaning && (state.scanning > 0 || state.selectedBytes == 0))
            }
            .padding(.horizontal, 18).padding(.vertical, 12)
            TierBar().padding(.horizontal, 18).padding(.bottom, 6)
            HStack(spacing: 0) {
                CategoryList()
                if let c = state.open {
                    DetailPanel(cat: c)
                        .frame(width: 440)
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
        .ignoresSafeArea(.container, edges: .top)
        .onAppear {
            state.scan()
            Updater.check { r in DispatchQueue.main.async { state.update = r } }   // one quiet check; offline = nothing happens
        }
        .sheet(isPresented: $confirm) { ConfirmSheet(confirm: $confirm).environmentObject(state) }
        .onChange(of: state.cleaning) { c in if c { openWindow(id: "cleaning") } }
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
                            Text(p.0).font(Theme.display(76))
                            Text(p.1).font(Theme.display(28))
                        }.foregroundColor(Theme.orange)
                        // beside the number: what kind of space it is, and what else is there if you look
                        if state.scanning == 0 && state.selectedBytes > 0 {
                            let onlySafe = !state.cats.contains { $0.checked && !$0.isSafe && ($0.bytes > 0 || $0.files > 0) }
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

struct TierBar: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        HStack(spacing: 10) {
            ForEach(Level.allCases, id: \.self) { lvl in TierTile(lvl: lvl) }
        }
        .frame(height: 86)
    }
}

struct TierTile: View {
    @EnvironmentObject var state: AppState
    let lvl: Level
    var body: some View {
        let items = state.cats.filter { $0.level == lvl }
        let total = items.reduce(Int64(0)) { $0 + $1.bytes }
        let ticked = items.filter { $0.checked }.reduce(Int64(0)) { $0 + $1.bytes }
        let nTicked = items.filter { $0.checked && ($0.bytes > 0 || $0.files > 0) }.count
        let on = state.tier == lvl
        let ink = on ? Theme.screenInk : Theme.ink
        let soft = on ? Theme.screenMuted : Theme.soft
        let nItems = items.filter { $0.bytes > 0 || $0.files > 0 }.count
        let sub = nItems == 0 ? "Nothing to clean here right now" : nTicked == 0 ? "None of \(nItems) ticked" : "\(nTicked) of \(nItems) ticked" + (ticked > 0 ? "  ·  \(Engine.fmt(ticked))" : "")
        Button {
            state.tier = lvl
            if let o = state.open, o.level != lvl { state.openID = nil }
        } label: {
            HStack(alignment: .top, spacing: 10) {
                if lvl != .safe { LevelMark(level: lvl, ink: ink, ground: on ? Theme.ink : Theme.key).frame(width: 5).padding(.vertical, 6) }
                VStack(alignment: .leading, spacing: 3) {
                    Text(Theme.tierName(lvl)).font(Theme.bold(15)).foregroundColor(ink)
                    Text(Theme.tierDesc(lvl)).font(Theme.sans(11.5)).foregroundColor(soft)
                    // how much of it is ticked, at a glance
                    HStack(spacing: 6) {
                        Check(on: nTicked > 0).scaleEffect(0.7).frame(width: 12, height: 12)
                        Text(sub).font(nTicked > 0 ? Theme.semi(11.5) : Theme.sans(11.5)).foregroundColor(nTicked > 0 ? ink : soft)
                    }
                }
                Spacer()
                Text(Engine.fmt(total) + " found").font(Theme.semi(17)).foregroundColor(ink)
            }
            .padding(14)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(RoundedRectangle(cornerRadius: 6).fill(on ? Theme.ink : Theme.key))
            .background(RoundedRectangle(cornerRadius: 6).fill(on ? Color.black : Theme.keyShadow).offset(y: 3))
        }
        .buttonStyle(.plain)
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
                HStack {
                    Text(Theme.tierName(state.tier).uppercased()).font(Theme.bold(13)).foregroundColor(Theme.ink)
                    Spacer()
                    Button("Tick all") { setAll(true, idx) }.buttonStyle(.plain).font(Theme.semi(12))
                    Button("Untick all") { setAll(false, idx) }.buttonStyle(.plain).font(Theme.semi(12)).padding(.leading, 14)
                }
                .padding(.horizontal, 22).padding(.top, 12)
                Text(Theme.tierExplain(state.tier)).font(Theme.sans(12)).foregroundColor(Theme.muted)
                    .frame(maxWidth: 760, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 22).padding(.top, 4).padding(.bottom, 12)
                Rectangle().fill(Theme.line).frame(height: 1).padding(.horizontal, 22)
                ForEach(full, id: \.self) { i in Row(index: i, compact: false) }
                if !empty.isEmpty {
                    Text("Nothing to clean right now (\(empty.count))").font(Theme.sans(12)).foregroundColor(Theme.soft)
                        .padding(.horizontal, 22).padding(.top, 14).padding(.bottom, 6)
                    ForEach(empty, id: \.self) { i in Row(index: i, compact: true) }
                }
            }
            .padding(.bottom, 12)
        }
        .background(Theme.chassis)
    }

    func isEmpty(_ c: Cat) -> Bool { c.refused == nil && c.scanned && c.bytes == 0 && c.files == 0 }
    func setAll(_ on: Bool, _ idx: [Int]) {
        for i in idx where !isEmpty(state.cats[i]) { state.cats[i].checked = on; state.save(state.cats[i]) }
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
        let age = c.kind == .trash ? "" : (c.ageMinutes > 0 ? "  ·  older than " + Engine.fmtAge(c.ageMinutes) : "  ·  all files, any age")
        HStack(alignment: .center, spacing: 14) {
            Check(on: c.checked, empty: empty)
                .contentShape(Rectangle())
                .onTapGesture { if !empty { state.cats[index].checked.toggle(); state.save(state.cats[index]) } }
            VStack(alignment: .leading, spacing: 3) {
                Text(c.name).font(isOpen ? Theme.bold(14) : Theme.semi(14)).foregroundColor(compact ? Theme.soft : Theme.ink).lineLimit(1)
                if !compact {
                    Text(c.short + age).font(Theme.sans(11.5)).foregroundColor(Theme.soft).lineLimit(1)
                }
            }
            Spacer(minLength: 12)
            VStack(alignment: .trailing, spacing: 3) {
                Text(c.scanned ? Engine.fmt(c.bytes) : "…").font(Theme.semi(14)).foregroundColor(empty ? Theme.soft : Theme.ink)
                if !compact { Text(c.scanned ? "\(c.files) file" + (c.files == 1 ? "" : "s") : "").font(Theme.sans(11)).foregroundColor(Theme.soft) }
            }
            Chevron(color: isOpen ? Theme.orange : (hovering ? Theme.ink : Theme.soft))
        }
        .padding(.leading, 22).padding(.trailing, 18).frame(height: compact ? 40 : 56)
        .background(isOpen ? Theme.selected : (hovering ? Theme.hover : Color.clear))
        .overlay(Rectangle().fill(Theme.line).frame(height: 1).padding(.leading, 22), alignment: .bottom)
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

    var body: some View {
        let c = state.cats.first { $0.id == cat.id } ?? cat
        let older = c.ageMinutes > 0
        ScrollView {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(Theme.tierName(c.level)).font(Theme.semi(12)).foregroundColor(Theme.muted)
                    Spacer()
                    Button("Close") { state.openID = nil }.buttonStyle(KeyStyle())
                }
                Text(c.name).font(Theme.bold(19)).foregroundColor(Theme.ink).lineLimit(2)
                Text(c.refused != nil ? "Space Hunter won't clean this: \(c.refused!). Use \u{201C}Remove from cleanup list\u{201D} below." : c.scanned ? "\(Engine.fmt(c.bytes))   ·   \(c.files) file" + (c.files == 1 ? "" : "s") : "Scanning…").font(Theme.sans(13)).foregroundColor(Theme.ink).padding(.bottom, 10)
                // every item that cleans folders shows what's in them (a single file you added has nothing inside)
                let folders = c.roots.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true }.map { $0.path }
                if c.kind != .trash, c.refused == nil, !folders.isEmpty {
                    InsideList(roots: folders, catID: c.id, active: c.checked, ageMinutes: c.ageMinutes, itemName: c.name.replacingOccurrences(of: "  (your folder)", with: ""),
                               isCustom: c.isCustom, exts: c.extensions, topOnly: c.topLevelOnly, exclude: c.exclude)
                }
                if c.kind != .trash && c.refused == nil { AgeControls(c: c, older: older, ageValue: $ageValue, unit: $unit) }
                Group {
                    if c.isCustom, let root = c.roots.first { FolderAbout(root: root.path) } else { section("What is it?", c.what) }
                    section("What gets cleaned", c.deletes)
                    section("Is it safe?", c.safeText)
                    section("What happens after", c.after)
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
            Text((older ? "Files untouched for \(Engine.fmtAge(c.ageMinutes)) or more." : "Every file, whatever its age.") + " Delete or Trash is chosen when you press Clean.")
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
        .id(tick)
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
        let sel = state.cats.filter { $0.checked && ($0.bytes > 0 || $0.files > 0) }
        // Quick clean is always deleted for good (caches rebuild, so the Trash would only fill up); the choice is for
        // the rest, and the safe choice comes first: Trash, unless you choose otherwise
        let onlySafe = sel.allSatisfy { $0.isSafe }
        let safeBytes = sel.filter { $0.isSafe }.reduce(Int64(0)) { $0 + $1.bytes }
        let otherBytes = state.selectedBytes - safeBytes
        let trashing = !onlySafe && (chosen ? toTrash : true)
        VStack(alignment: .leading, spacing: 8) {
            Text("Clean \(Engine.fmt(state.selectedBytes))?").font(Theme.bold(24)).foregroundColor(Theme.ink)
            // grouped by risk, so it's plain which ticked items need care
            ForEach([Level.safe, Level.review, Level.caution], id: \.self) { lvl in
                let items = sel.filter { $0.level == lvl }.sorted { $0.bytes > $1.bytes }
                if !items.isEmpty {
                    ConfirmHead(level: lvl)
                    ForEach(Array(items.prefix(4))) { c in ConfirmRow(name: c.name, bytes: c.bytes, level: lvl) }
                    if items.count > 4 { ConfirmRow(name: "+ \(items.count - 4) more", bytes: items.dropFirst(4).reduce(Int64(0)) { $0 + $1.bytes }, level: lvl) }
                }
            }
            if !onlySafe {
                Text("How to clean the rest").font(Theme.semi(13)).foregroundColor(Theme.muted).padding(.top, 8)
                HStack(spacing: 4) {
                    Button("Permanently delete") { toTrash = false; chosen = true }.buttonStyle(KeyStyle(toggled: !trashing))
                    Button("Move to Trash") { toTrash = true; chosen = true }.buttonStyle(KeyStyle(toggled: trashing))
                }
            }
            Text(onlySafe ? "Quick clean is deleted for good: these are caches and leftovers that rebuild, so the Trash would only fill up. The space comes back straight away."
                 : !trashing ? "Everything is deleted permanently and can't be restored. The space comes back straight away."
                 : (safeBytes > 0 ? "Quick clean (\(Engine.fmt(safeBytes))) is deleted for good; the rest (\(Engine.fmt(otherBytes))) goes to the Trash, so you can restore it. "
                                  : "Everything goes to the Trash, so you can restore it. ") + "That space comes back only when you empty the Trash.")
                .font(Theme.sans(12)).foregroundColor(Theme.soft).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            HStack(spacing: 8) {
                Spacer()
                Button("Cancel") { confirm = false }.buttonStyle(KeyStyle()).keyboardShortcut(.cancelAction)
                // the key says what will actually happen
                Button(!trashing ? "Permanently delete \(Engine.fmt(state.selectedBytes))" : safeBytes > 0 ? "Clean \(Engine.fmt(state.selectedBytes))" : "Move \(Engine.fmt(state.selectedBytes)) to Trash") {
                    confirm = false
                    let trash = trashing
                    // let the confirm sheet finish closing before the progress sheet opens
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { state.clean(toTrash: trash) }
                }.buttonStyle(KeyStyle(primary: true))
            }
        }
        .padding(26).frame(width: 560).frame(minHeight: 320).background(Theme.chassis)
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

struct ProgressSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var finishStart: Date?   // the finish: the bar clears and the number counts up, about a second
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            TimelineView(.animation(paused: finishStart == nil)) { tl in
                let finished = state.cleanDone && !state.cleanStopped
                let t: Double = finished ? (finishStart.map { min(1, tl.date.timeIntervalSince($0) / 1.2) } ?? 1) : 1
                let ease = 1 - pow(1 - t, 3)   // quick at first, settling on the real total
                let binOnly = state.freed == 0 && state.trashed > 0
                let shown = Int64(Double(binOnly ? state.trashed : state.freed) * (finished ? ease : 1))
                let p = Engine.parts(shown)
                let fraction = min(1, CGFloat(Double(state.freed + state.trashed) / Double(state.expected)))
                VStack(alignment: .leading, spacing: 4) {
                    Text(state.cleanDone ? (state.cleanStopped ? "Stopped  ·  nothing else will be cleaned" : t < 1 ? "Clearing up…" : "Done  ·  all ticked items cleaned") : "Cleaning  ·  " + state.currentItem)
                        .font(Theme.semi(12)).foregroundColor(Color(hex: 0xB4B1AA)).lineLimit(1)
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(p.0).font(Theme.display(60))
                        Text(p.1 + (binOnly ? " TO TRASH" : " FREED")).font(Theme.display(24))
                        Spacer()
                        VStack(alignment: .trailing, spacing: 4) {
                            Text("\(Int64(Double(state.cleanedFiles) * (finished ? ease : 1))) files  ·  \(state.skipped) skipped").font(Theme.semi(13)).foregroundColor(Theme.screenInk)
                            if state.trashed > 0 && !binOnly { Text("+ \(Engine.fmt(state.trashed)) moved to the Trash").font(Theme.sans(12)).foregroundColor(Theme.screenMuted) }
                            if binOnly && state.cleanDone { Text("Empty the Trash to get this space back").font(Theme.sans(12)).foregroundColor(Theme.screenMuted) }
                        }
                    }.foregroundColor(Theme.orange)
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Rectangle().fill(Theme.track).frame(height: 12)
                            if finished {
                                // the full bar breaks into pieces that drop away left to right: the space is cleared
                                HStack(spacing: 2) {
                                    ForEach(0..<28, id: \.self) { k in
                                        let local = max(0, min(1, (t - 0.55 * Double(k) / 28) / 0.4))
                                        let fall = local * local
                                        Rectangle().fill(Theme.orange)
                                            .frame(height: 12 * (1 - fall * 0.6))
                                            .offset(y: 16 * fall)
                                            .opacity(1 - fall)
                                    }
                                }
                                .frame(height: 12, alignment: .top)
                            } else {
                                Rectangle().fill(Theme.orange).frame(width: g.size.width * fraction, height: 12)
                            }
                        }
                    }.frame(height: 12).padding(.top, 6)
                }
            }
            .padding(20).background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
            .onChange(of: state.cleanDone) { d in
                if d && !state.cleanStopped && !reduceMotion {
                    finishStart = Date()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.3) { finishStart = nil }   // stop redrawing once it has settled
                }
            }

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

            HStack {
                if state.cleanDone { Button("Open log") { NSWorkspace.shared.open(Engine.logURL) }.buttonStyle(KeyStyle()) }
                Spacer()
                if !state.cleanDone { Button("Minimise") { NSApp.keyWindow?.miniaturize(nil) }.buttonStyle(KeyStyle()) }
                // Done closes the window; it's the one orange key once a clean has finished
                Button(state.cleanDone ? (state.cleanStopped ? "Close" : "Done") : "Stop") { if state.cleanDone { state.finishCleaning(); dismiss() } else { state.stop() } }
                    .buttonStyle(KeyStyle(primary: state.cleanDone && !state.cleanStopped))
            }
        }
        .padding(20)
        .frame(minWidth: 680, minHeight: 480)
        .background(Theme.chassis)
        .onDisappear { if state.cleanDone { state.finishCleaning() } }
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
            .background(RoundedRectangle(cornerRadius: 4).fill(enabled ? Theme.key : Color(hex: 0xD0CDC6)))
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
            Text("\u{201C}Add to list\u{201D} puts a folder or file in Personal & work items, unticked. Nothing here deletes anything.")
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
                    Text(p.0).font(Theme.display(52))
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
    var body: some View {
        TimelineView(.animation) { tl in
            let t = tl.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.1) / 1.1
            ZStack {
                Circle().stroke(Theme.track, lineWidth: 5)
                Circle().trim(from: 0, to: 0.3).stroke(Theme.orange, style: StrokeStyle(lineWidth: 5, lineCap: .round))
                    .rotationEffect(.degrees(360 * t))
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

// Inside a folder you added: its files and sub-folders, each with a tick box. Unticked ones stay; clicking a
// folder's name opens it, and Back goes up (never above the folder you added).
struct InsideList: View {
    @EnvironmentObject var state: AppState
    let roots: [String]
    let catID: String
    var active = true        // the item itself is ticked; otherwise every tick here is shown dimmed
    var ageMinutes = 0       // files newer than this stay, whatever is ticked
    var itemName = ""
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
        let entries = here.isEmpty ? roots.flatMap { r in list(r, sub: "in " + r.replacingOccurrences(of: Engine.home.path, with: "~")) } : list(here, sub: nil)
        VStack(alignment: .leading, spacing: 0) {
            Text((isCustom ? "Inside this folder" : "What's inside") + "  ·  untick what should stay").font(Theme.semi(12)).foregroundColor(Theme.muted).padding(.top, 4)
            Text(!active ? "Nothing here is cleaned until you tick \u{201C}\(itemName)\u{201D} in the list. Untick anything that should always stay."
                 : isCustom ? "Ticked files and folders are cleaned when you press Clean (only those older than the age you set). Click a folder's name to look inside it."
                 : "What this item cleans, with the folder each one comes from. Untick anything that should stay; you don't need to. Click a folder's name to look inside it.")
                .font(Theme.sans(12)).foregroundColor(Theme.soft).fixedSize(horizontal: false, vertical: true).padding(.bottom, 6)
            if !here.isEmpty {
                let root = rootOf(here) ?? here
                Button { here = roots.contains(here) ? "" : (here as NSString).deletingLastPathComponent } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "chevron.left").font(.system(size: 11, weight: .bold))
                        Text("Back  ·  " + (root as NSString).lastPathComponent + String(here.dropFirst(root.count)).replacingOccurrences(of: "/", with: "  \u{203A}  ")).lineLimit(1).truncationMode(.middle)
                    }
                }
                .buttonStyle(.plain).font(Theme.semi(12)).foregroundColor(Theme.ink).padding(.vertical, 6)
            }
            if entries.isEmpty { Text("Empty").font(Theme.sans(12)).foregroundColor(Theme.soft).padding(.vertical, 6) }
            ForEach(Array(entries.prefix(60))) { e in
                let parent = (e.id as NSString).deletingLastPathComponent
                let inherited = !roots.contains(parent) && Custom.isSkipped(parent, skip)
                let off = inherited || Custom.isSkipped(e.id, skip)
                HStack(spacing: 10) {
                    Button { if !inherited { Custom.setSkipped(e.id, !off); tick += 1; state.scan([catID]) } } label: { Check(on: !off, empty: inherited).saturation(active || off ? 1 : 0).opacity(active || off ? 1 : 0.6) }
                        .buttonStyle(.plain)
                    Button { if e.dir { here = e.id } } label: {
                        VStack(alignment: .leading, spacing: 1) {
                            HStack(spacing: 8) {
                                Text(e.name).lineLimit(1).truncationMode(.middle)
                                Spacer()
                                Text(e.dir ? (sizes[e.id].map { Engine.fmt($0) + "  ·  folder" } ?? "folder") : (ageMinutes > 0 && e.modified > Date().addingTimeInterval(-Double(ageMinutes) * 60)) ? "too new, stays" : e.size < 1024 ? "under 1 KB" : Engine.fmt(e.size)).foregroundColor(Theme.soft)
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
        .id(tick)
        .padding(.bottom, 12)
        .onChange(of: catID) { _ in here = "" }
        .onAppear { measure(here) }
        .onChange(of: here) { h in measure(h) }
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
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Rectangle().fill(Theme.track)
                if target > 0 {
                    Rectangle().fill(Theme.orange).frame(width: g.size.width * min(1, CGFloat(Double(done) / Double(target))))
                } else {
                    TimelineView(.animation) { tl in
                        let t = tl.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.6) / 1.6
                        Rectangle().fill(Theme.orange)
                            .frame(width: g.size.width * 0.22)
                            .offset(x: g.size.width * 1.22 * CGFloat(t) - g.size.width * 0.22)
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
                    .frame(width: 180, height: 26).background(RoundedRectangle(cornerRadius: 4).fill(Theme.ink))
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
                            else { Rectangle().fill(hot ? Theme.ink : b.rest ? Theme.hover : Theme.key) }
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
