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
}

enum Custom {   // folders added to the cleanup list from the Space map
    static var all: [String] { Store2.lines("your-folders.txt") }
    static func add(_ p: String) { var a = all; if !a.contains(p) { a.append(p) }; Store2.write("your-folders.txt", a) }
    static func remove(_ p: String) { Store2.write("your-folders.txt", all.filter { $0 != p }) }
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
            var c = Cat(id: "custom:" + p.lowercased(), name: (p as NSString).lastPathComponent + "  (your folder)", level: .caution,
                        kind: .contents, roots: [URL(fileURLWithPath: p)], defaultAge: 30 * day, ageMinutes: 30 * day, lastAge: 30 * day, checked: false)
            c.isCustom = true
            c.short = "Everything inside goes; the folder itself stays"
            c.what = "A folder you added from the Space map: " + p
            c.deletes = "Every file inside it (older than the age you set) and the sub-folders that end up empty. The folder itself stays."
            c.safeText = "Only you know. Open it and check before ticking. Protected folders inside it are skipped."
            c.after = "The files are gone, unless you choose Trash in the confirm window."
            if d.object(forKey: "v4.age.\(c.id)") != nil { c.ageMinutes = d.integer(forKey: "v4.age.\(c.id)") }
            if d.object(forKey: "v4.checked.\(c.id)") != nil { c.checked = d.bool(forKey: "v4.checked.\(c.id)") }
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
                                         .totalFileAllocatedSizeKey, .fileSizeKey, .contentModificationDateKey]

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
                body(url, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), v.contentModificationDate ?? .distantPast)
            }
        }
    }

    static func newest(_ dir: URL, cnt: Counter) -> Date {
        var n = (try? dir.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
        walk(dir, exclude: [], kept: [], cnt: cnt) { _, _, d in if d > n { n = d } }
        return n
    }

    static func removeEmptyDirs(_ root: URL, keepRoot: Bool, exclude: Set<String>, kept: [String]) {
        var all: [URL] = []
        if let e = fm.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [], errorHandler: { _, _ in true }) {
            for case let url as URL in e {
                let v = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                if v?.isDirectory == true && v?.isSymbolicLink != true {
                    if exclude.contains(url.lastPathComponent) || Keep.isKept(url.path, kept) { e.skipDescendants(); continue }
                    all.append(url)
                }
            }
        }
        if !keepRoot { all.append(root) }
        for dir in all.sorted(by: { $0.pathComponents.count > $1.pathComponents.count }) {
            if let c = try? fm.contentsOfDirectory(atPath: dir.path), c.isEmpty || c == [".DS_Store"] {
                try? fm.removeItem(at: dir)
            }
        }
    }

    static func process(_ c: Cat, delete: Bool, toTrash: Bool, cnt: Counter) {
        let cutoff = Date().addingTimeInterval(-Double(c.ageMinutes) * 60)
        let kept = Keep.all
        let trashIt = toTrash && c.kind != .trash   // emptying the Trash can't go to the Trash
        func handle(_ url: URL, _ size: Int64, _ date: Date, _ cut: Date) {
            if let ext = c.extensions, !ext.contains(url.pathExtension.lowercased()) { return }
            if Keep.isKept(url.path, kept) { return }
            if date >= cut { if delete { cnt.skipped += 1 }; return }
            if !delete { cnt.files += 1; cnt.bytes += size; return }
            var ok = true
            do {
                if trashIt { try fm.trashItem(at: url, resultingItemURL: nil) } else { try fm.removeItem(at: url) }
            } catch { ok = false }
            if ok { cnt.files += 1; if trashIt { cnt.trashedBytes += size } else { cnt.bytes += size } } else { cnt.skipped += 1 }
            cnt.onItem?(url.path, size, ok, trashIt)
        }
        for root in c.roots {
            if cnt.cancel { return }
            guard fm.fileExists(atPath: root.path), isSafeRoot(root, allowPersonal: c.extensions != nil && c.topLevelOnly) else { continue }
            if (try? root.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { continue }   // never follow a linked root
            if Keep.isKept(root.path, kept) { continue }
            switch c.kind {
            case .contents, .trash:
                if c.topLevelOnly {
                    let items = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: keys)) ?? []
                    for url in items {
                        guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true, v.isSymbolicLink != true else { continue }
                        handle(url, Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0), v.contentModificationDate ?? .distantPast, cutoff)
                    }
                } else {
                    walk(root, exclude: c.exclude, kept: kept, cnt: cnt) { url, s, dd in handle(url, s, dd, cutoff) }
                    if delete { removeEmptyDirs(root, keepRoot: true, exclude: c.exclude, kept: kept) }
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

    // Can a folder from the Space map be added to the cleanup list? nil = yes, otherwise the reason.
    static func canAdd(_ path: String) -> String? {
        let p = (path as NSString).standardizingPath
        if p == "/" { return "a whole disk" }
        // a folder made right at the top of the disk (like /Personal) or a whole user folder: likely your own files
        if p.split(separator: "/").count <= 2 { return "likely your own files (made right on the disk)" }
        for sys in ["/System", "/Library", "/Applications", "/usr", "/bin", "/sbin", "/private", "/opt", "/Volumes", "/cores"] where p == sys || p.hasPrefix(sys + "/") {
            return "part of macOS or installed apps"
        }
        for keep in [home.path, u("Library").path, u("Library/Application Support").path] + personal where keep == p || keep.hasPrefix(p + "/") {
            return "it holds your home or personal folders"
        }
        if let k = personalFolderOf(p) { return "it's inside your personal files (\((k as NSString).lastPathComponent))" }
        if Keep.isKept(p, Keep.all) { return "it's a protected folder" }
        if Custom.all.contains(where: { p == $0 || p.hasPrefix($0 + "/") }) { return "it's already on the cleanup list" }
        return nil
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
    static let ink = Color(hex: 0x1B1B1A), muted = Color(hex: 0x5F5C56), soft = Color(hex: 0x7D7972), line = Color(hex: 0xC3BFB6)
    static let orange = Color(hex: 0xFF5B1F), orangeDeep = Color(hex: 0xB5400F)
    static let key = Color(hex: 0xECEAE5), keyShadow = Color(hex: 0xA9A59C), disabled = Color(hex: 0x9A968F)

    static func display(_ s: CGFloat) -> Font { .custom("BigShouldersDisplay-Black", size: s) }
    static func sans(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Regular", size: s) }
    static func semi(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-SemiBold", size: s) }
    static func bold(_ s: CGFloat) -> Font { .custom("SchibstedGrotesk-Bold", size: s) }
    static func mono(_ s: CGFloat) -> Font { .custom("IBMPlexMono-Regular", size: s) }
    static func monoSemi(_ s: CGFloat) -> Font { .custom("IBMPlexMono-SemiBold", size: s) }

    static func tierName(_ l: Level) -> String { l == .safe ? "Safe to clean" : l == .review ? "Review first" : "Caution" }
    static func tierDesc(_ l: Level) -> String { l == .safe ? "Rebuilds or isn't needed" : l == .review ? "Re-downloads or history" : "Can hold your history or work" }
    // one plain line under each section's heading, so nobody has to guess what "safe" means here
    static func tierExplain(_ l: Level) -> String {
        l == .safe ? "Caches, temp files and leftovers that apps and macOS rebuild or no longer need. Your files, logins and passwords aren't touched. Some things load a little slower once."
        : l == .review ? "These come back by downloading again, or they're history you might still want. Read each one before you tick it."
        : "These can hold your own work or history. Nothing here is ticked for you; open an item to see exactly what goes."
    }
}

// A square hardware key: light, orange when primary, dark when toggled (a lit segment).
struct KeyStyle: ButtonStyle {
    var primary = false
    var toggled = false
    var big = false
    var accent = false   // a tool of its own (the Space map): an orange outline
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

// four blocks of different sizes: a tiny picture of the Space map
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
    @Published var cleaning = false
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
        // Safe-to-delete items first, so the ticked total and the CLEAN key are ready first
        let targets = cats.indices.filter { ids == nil || ids!.contains(cats[$0].id) }.sorted { cats[$0].level.rawValue < cats[$1].level.rawValue }
        for i in targets {
            cats[i].scanned = false
            let snapshot = cats[i]
            scanning += 1
            scanQ.async { [scanSlots] in
                scanSlots.wait(); defer { scanSlots.signal() }
                let cnt = Counter()
                Engine.process(snapshot, delete: false, toTrash: false, cnt: cnt)
                DispatchQueue.main.async {
                    if let j = self.cats.firstIndex(where: { $0.id == snapshot.id }) {
                        self.cats[j].files = cnt.files; self.cats[j].bytes = cnt.bytes; self.cats[j].scanned = true
                    }
                    self.scanning -= 1
                    self.status = self.scanning > 0 ? "Scanning \(snapshot.name)…" : (self.freedAll.0 == 0 ? "Quick cleanup: the Safe to clean items are ticked for you, so one click on Clean is enough. You see the list before anything goes." : "Scan done. Click a row to see what it is. Tick the box to include it.")
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
        status = "Back to default: Safe to clean items ticked, default ages. Protected folders stay protected."
    }

    func clean(toTrash: Bool) {
        // the Trash item always runs first, so files moved to the Trash in this clean are never emptied by it
        let sel = cats.filter { $0.checked && ($0.bytes > 0 || $0.files > 0) }.sorted { ($0.kind == .trash ? 0 : 1) < ($1.kind == .trash ? 0 : 1) }
        guard !sel.isEmpty else { return }
        expected = max(1, sel.reduce(0) { $0 + $1.bytes })
        freed = 0; trashed = 0; cleanedFiles = 0; skipped = 0; log = []
        cleanDone = false; cleanStopped = false; cleaning = true
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
        }
        DispatchQueue.global(qos: .userInitiated).async {
            var summary: [String] = []
            for (i, c) in sel.enumerated() where !cnt.cancel {
                DispatchQueue.main.async { self.currentItem = "ITEM \(i + 1) OF \(sel.count)  ·  \(c.name.uppercased())" }
                lock.lock(); let b0 = cnt.bytes, t0 = cnt.trashedBytes, f0 = cnt.files; lock.unlock()
                Engine.process(c, delete: true, toTrash: toTrash, cnt: cnt)
                lock.lock(); let b1 = cnt.bytes, t1 = cnt.trashedBytes, f1 = cnt.files; lock.unlock()
                summary.append("\(c.name): freed \(Engine.fmt(b1 - b0))" + (t1 > t0 ? " + \(Engine.fmt(t1 - t0)) to Trash" : "") + ", \(f1 - f0) files")
            }
            Engine.log((cnt.cancel ? "STOPPED " : "CLEANED ") + "\(Engine.fmt(cnt.bytes)), \(cnt.files) files; skipped \(cnt.skipped). " + summary.joined(separator: " | "))
            DispatchQueue.main.async {
                timer.fire(); timer.invalidate()
                self.log.append(contentsOf: summary.map { "— " + $0 })
                self.freed = cnt.bytes; self.trashed = cnt.trashedBytes; self.cleanedFiles = cnt.files; self.skipped = cnt.skipped
                self.cleanStopped = cnt.cancel; self.cleanDone = true
                self.disk = Engine.disk()
            }
        }
    }

    func stop() { counter?.cancel = true }
    func finishCleaning() { cleaning = false; scan() }
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
            guard let data, let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = obj["tag_name"] as? String else { return done(nil) }
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
            Text(message ?? "Space Hunter \(release.version) is ready  ·  you have \(Updater.current). Updating takes a few seconds and keeps your settings.")
                .font(Theme.semi(13)).foregroundColor(Theme.ink).lineLimit(2)
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
        .padding(.horizontal, 22).padding(.vertical, 10)
        .background(Theme.pane)
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
        Window("Space map", id: "finder") {
            FinderView().environmentObject(state)
        }
        .defaultSize(width: 940, height: 760)
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
                Button { openWindow(id: "finder") } label: { HStack(spacing: 8) { MapIcon(); Text("Space map") } }.buttonStyle(KeyStyle(accent: true))
                Spacer()
                Button("Scan again") { state.scan() }.buttonStyle(KeyStyle()).disabled(state.scanning > 0)
                Button("Protected folders") { openWindow(id: "protect") }.buttonStyle(KeyStyle())
                Button("Reset to default") { state.resetAll() }.buttonStyle(KeyStyle())
                    .padding(.trailing, 4)
                Button("Clean \(Engine.fmt(state.selectedBytes))") { confirm = true }
                    .buttonStyle(KeyStyle(primary: true, big: true))
                    .disabled(state.scanning > 0 || state.selectedBytes == 0)
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
            if let u = state.update { UpdateBar(release: u) }
            Rectangle().fill(Theme.line).frame(height: 1)
            Text(state.status).font(Theme.sans(11)).foregroundColor(Theme.muted)
                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 22).padding(.vertical, 8)
        }
        .background(Theme.chassis)
        .ignoresSafeArea(.container, edges: .top)
        .onAppear {
            state.scan()
            Updater.check { r in DispatchQueue.main.async { state.update = r } }   // one quiet check; offline = nothing happens
        }
        .sheet(isPresented: $confirm) { ConfirmSheet(confirm: $confirm).environmentObject(state) }
        .sheet(isPresented: $state.cleaning) { ProgressSheet().environmentObject(state) }
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
                                Text(onlySafe ? (state.freedAll.0 == 0 ? "all safe to clean  ·  one click on Clean" : "all safe to clean") : "includes items to check first")
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
        let sub = "\(items.count) items  ·  " + (nTicked == 0 ? "none ticked" : "\(nTicked) ticked" + (ticked > 0 ? ", \(Engine.fmt(ticked))" : ""))
        Button {
            state.tier = lvl
            if let o = state.open, o.level != lvl { state.openID = nil }
        } label: {
            HStack(alignment: .top, spacing: 10) {
                if lvl != .safe { LevelMark(level: lvl, ink: ink, ground: on ? Theme.ink : Theme.key).frame(width: 5).padding(.vertical, 6) }
                VStack(alignment: .leading, spacing: 3) {
                    Text(Theme.tierName(lvl) + (lvl == .caution ? "  !" : "")).font(Theme.bold(15)).foregroundColor(ink)
                    Text(Theme.tierDesc(lvl)).font(Theme.sans(11.5)).foregroundColor(soft)
                    Text(sub).font(nTicked > 0 ? Theme.semi(11.5) : Theme.sans(11.5)).foregroundColor(nTicked > 0 ? ink : soft)
                }
                Spacer()
                Text(Engine.fmt(total)).font(Theme.semi(17)).foregroundColor(ink)
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
                    Text(Theme.tierName(state.tier).uppercased() + "  ·  " + Theme.tierDesc(state.tier)).font(Theme.bold(13)).foregroundColor(Theme.ink)
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
                    Text("NOTHING TO CLEAR RIGHT NOW  ·  \(empty.count)").font(Theme.monoSemi(10)).tracking(1.2).foregroundColor(Theme.soft)
                        .padding(.horizontal, 22).padding(.top, 14).padding(.bottom, 6)
                    ForEach(empty, id: \.self) { i in Row(index: i, compact: true) }
                }
            }
            .padding(.bottom, 12)
        }
        .background(Theme.chassis)
    }

    func isEmpty(_ c: Cat) -> Bool { c.scanned && c.bytes == 0 && c.files == 0 }
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
        let empty = c.scanned && c.bytes == 0 && c.files == 0
        let isOpen = state.openID == c.id
        let age = c.kind == .trash ? "" : (c.ageMinutes > 0 ? "  ·  older than " + Engine.fmtAge(c.ageMinutes) : "  ·  everything")
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
                    Text(Theme.tierName(c.level) + (c.level == .caution ? "  !" : "")).font(Theme.semi(12)).foregroundColor(Theme.muted)
                    Spacer()
                    Button("Close") { state.openID = nil }.buttonStyle(KeyStyle())
                }
                Text(c.name).font(Theme.bold(19)).foregroundColor(Theme.ink).lineLimit(2)
                Text(c.scanned ? "\(Engine.fmt(c.bytes))   ·   \(c.files) files" : "Scanning…").font(Theme.sans(13)).foregroundColor(Theme.ink).padding(.bottom, 10)
                if c.kind != .trash { AgeControls(c: c, older: older, ageValue: $ageValue, unit: $unit) }
                Group {
                    section("What is it?", c.what)
                    section("What gets cleaned", c.deletes)
                    section("Is it safe?", c.safeText)
                    section("What happens after", c.after)
                }
                FolderKeeps(cat: c)
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
        // the safe choice first: anything beyond "Safe to clean" goes to the Trash unless you choose otherwise
        let trashing = chosen ? toTrash : sel.contains { !$0.isSafe }
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
            Text("How to clean").font(Theme.semi(13)).foregroundColor(Theme.muted).padding(.top, 8)
            HStack(spacing: 4) {
                Button("Permanently delete") { toTrash = false; chosen = true }.buttonStyle(KeyStyle(toggled: !trashing))
                Button("Move to Trash") { toTrash = true; chosen = true }.buttonStyle(KeyStyle(toggled: trashing))
            }
            Text(trashing ? "Everything goes to the Trash, so you can restore it. The space comes back only when you empty the Trash. If the Trash item is ticked, the old Trash is emptied first."
                         : "Everything is deleted permanently and can't be restored. The space comes back straight away.")
                .font(Theme.sans(12)).foregroundColor(Theme.soft).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            HStack(spacing: 8) {
                Spacer()
                Button("Cancel") { confirm = false }.buttonStyle(KeyStyle()).keyboardShortcut(.cancelAction)
                // the key says what will actually happen
                Button(trashing ? "Move \(Engine.fmt(state.selectedBytes)) to Trash" : "Permanently delete \(Engine.fmt(state.selectedBytes))") {
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
        Text((level == .safe ? "Safe to clean" : level == .review ? "Review first" : "Caution  !").uppercased())
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
    var body: some View {
        let binOnly = state.freed == 0 && state.trashed > 0
        let p = Engine.parts(binOnly ? state.trashed : state.freed)
        let fraction: CGFloat = state.cleanDone && !state.cleanStopped ? 1 : min(1, CGFloat(Double(state.freed + state.trashed) / Double(state.expected)))
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text(state.cleanDone ? (state.cleanStopped ? "Stopped  ·  nothing else will be cleaned" : "Done  ·  all ticked items") : "Cleaning  ·  " + state.currentItem)
                    .font(Theme.semi(12)).foregroundColor(Color(hex: 0xB4B1AA)).lineLimit(1)
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(p.0).font(Theme.display(60))
                    Text(p.1 + (binOnly ? " TO TRASH" : " FREED")).font(Theme.display(24))
                    Spacer()
                    VStack(alignment: .trailing, spacing: 4) {
                        Text("\(state.cleanedFiles) files  ·  \(state.skipped) skipped").font(Theme.semi(13)).foregroundColor(Theme.screenInk)
                        if state.trashed > 0 && !binOnly { Text("+ \(Engine.fmt(state.trashed)) moved to the Trash").font(Theme.sans(12)).foregroundColor(Theme.screenMuted) }
                    }
                }.foregroundColor(Theme.orange)
                GeometryReader { g in
                    ZStack(alignment: .leading) {
                        Rectangle().fill(Theme.track)
                        Rectangle().fill(Theme.orange).frame(width: g.size.width * fraction)
                    }
                }.frame(height: 12).padding(.top, 6)
            }
            .padding(20).background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))

            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 1) {
                        ForEach(Array(state.log.enumerated()), id: \.offset) { i, line in
                            Text(line).font(Theme.mono(11)).foregroundColor(Theme.ink).lineLimit(1).truncationMode(.middle).id(i)
                        }
                    }.padding(12)
                }
                .background(Theme.pane)
                .onChange(of: state.log.count) { n in if n > 0 { proxy.scrollTo(n - 1, anchor: .bottom) } }
            }

            HStack {
                if state.cleanDone { Button("Open log") { NSWorkspace.shared.open(Engine.logURL) }.buttonStyle(KeyStyle()) }
                Spacer()
                Button(state.cleanDone ? "Close" : "Stop") { if state.cleanDone { state.finishCleaning() } else { state.stop() } }.buttonStyle(KeyStyle())
            }
        }
        .padding(20)
        .frame(width: 780, height: 560)
        .background(Theme.chassis)
        .interactiveDismissDisabled(true)
    }
}

// MARK: - Protected folders

struct ProtectView: View {
    @EnvironmentObject var state: AppState
    @State private var tick = 0
    let always = ["Documents, Desktop, Pictures, Movies, Music, Public and iCloud Drive",
                  "Downloads, except old installers and archives, and those only when you tick that item",
                  "Your code and project folders (Projects, Developer)",
                  "Keys and sign-in settings (.ssh, .gnupg, .aws, .kube, .docker)",
                  "AI agents' memory folders (for example Claude Code's memory)",
                  "macOS and installed apps",
                  "Anything reached through a link (links are never followed)"]
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
                    if mine.isEmpty { Text("None yet. Add a folder above, or press Keep on a folder in an item's panel.").font(Theme.sans(13)).foregroundColor(Theme.soft) }
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

// MARK: - Space map

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

    private func fill(_ n: FolderNode) {
        if cancel { return }
        let url = URL(fileURLWithPath: n.path)
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileSizeKey]
        guard let items = try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: keys) else { return }
        var addCount: Int64 = 0, addBytes: Int64 = 0
        for item in items {
            guard let v = try? item.resourceValues(forKeys: Set(keys)) else { continue }
            if v.isSymbolicLink == true { continue }
            if v.isDirectory == true {
                if item.path == "/System/Volumes" || item.path == "/Volumes" || item.path == "/dev" { continue }
                n.kids.append(FolderNode(name: item.lastPathComponent, path: item.path, parent: n))
            } else {
                let s = Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0)
                n.ownSize += s; n.ownFiles += 1; addCount += 1; addBytes += s
            }
        }
        lock.lock(); count += addCount; bytes += addBytes; lock.unlock()
        for k in n.kids { fill(k) }
    }

    private func total(_ n: FolderNode) {
        n.size = n.ownSize; n.files = n.ownFiles
        for k in n.kids { total(k); n.size += k.size; n.files += k.files }
        n.kids.sort { $0.size > $1.size }
    }
}

// The places the Space map can measure: your home folder, then every disk (internal and external).
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
                Button("Up one level") { if let p = current?.parent { current = p } }.buttonStyle(KeyStyle()).disabled(current?.parent == nil)
                Button("Show in Finder") { if let c = current { NSWorkspace.shared.open(URL(fileURLWithPath: c.path)) } }.buttonStyle(KeyStyle()).disabled(current == nil)
            }
            if let c = current {
                TreeMap(node: c, free: (c.parent == nil && place?.id == c.path) ? (place?.free ?? 0) : 0, added: added) { k in current = k }
                    .frame(height: 240)
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(c.kids.filter { $0.size > 0 }) { k in
                            FolderRow(node: k, parentSize: max(1, c.size), isAdded: added.contains(k.path),
                                      open: { if !k.kids.isEmpty { current = k } },
                                      add: { Custom.add(k.path); added.insert(k.path); state.reload() })
                        }
                    }
                }
            } else {
                Spacer()
            }
            Text("\u{201C}Add to cleanup list\u{201D} puts a folder in the Caution section, unticked. Nothing here deletes anything.")
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
            Text(scanner.running ? "Space map  ·  counting files" : "Space map  ·  just looking, nothing is deleted here")
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
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.screen))
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
            if let r = refusal, !isAdded { return "Can't add: " + r }
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
                Button("+ Add to cleanup list") { add() }.buttonStyle(KeyStyle()).frame(width: 200, alignment: .leading)
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
