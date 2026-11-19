# Changelog

All notable changes to Space Hunter. Versions follow [semantic versioning](https://semver.org): the last number is
for fixes, the middle number is for new features, and the first number is for big changes.

## 1.3.0 (2026-11-19)

- **What's inside, for every item**: an item's panel lists the folders and files it would clean, each with the folder
  it comes from. Open a folder to look inside it, and untick anything that should stay; most people leave it all
  ticked. Each item's rules still apply (for example, only old installers in Downloads are listed). On Linux it shows
  only your own files, since others' are never cleaned.
- **Quick clean is always deleted for good**: those are caches that rebuild, so the Recycle Bin or Trash would only
  fill up. When only Quick clean is ticked there's nothing to choose; otherwise the choice applies to the rest, and
  the confirm window says how much goes where.

## 1.2.0 (2026-11-19)

- **Space Hunter for Linux.** The same app, look and features as on Windows and Mac: the three sections, the item
  panel, the confirm and cleaning windows, the Storage map, protected folders and signed one-click updates. Download
  `SpaceHunter-x86_64.AppImage`, or with Nix: `nix run github:knox-101/space-hunter`. System folders (such as
  downloaded updates) ask for your password once. Folders inside installed tools and app settings are never offered
  for the cleanup list.
- **A finish moment**: when a clean ends, the bar clears and the freed total counts up for about a second, then
  **Done** closes the cleaning window. It's skipped when your system is set to reduce motion.
- The cleaning window can be **minimised**; the main window shows its progress, and Clean becomes **Show cleaning**.
- **Open in Explorer** (Finder, Files) now sits in the folder trail of the Storage map, beside the folder it opens.
- Protected folders: the Downloads line now says what can be cleaned there, instead of contradicting "never cleaned".

## 1.1.0 (2026-11-19)

- **Clearer sections**: **Quick clean** (caches and leftovers that rebuild), **Big caches & history** and
  **Personal & work items**. Each tile shows how much of it is ticked, such as "12 of 12 ticked · 236 MB".
- **Cleaning no longer blocks the app**: the cleaning window runs on its own and you can keep looking around. Clean
  is ready again once it's done.
- **Space Hunter never cleans itself**: its own folder, settings and log are always protected.
- **Storage map** (the Space map, renamed):
  - A **Back** key and the folder trail sit right above the chart; click any folder in the trail to jump there.
    Backspace or Alt+Left also go back.
  - Every file in the open folder is listed under its folders, and single files can be added to the cleanup list.
  - "+ Add this folder" adds the folder you're in.
  - A large spinner shows while it measures. On Mac, measuring is much faster.
- **Folders you add**: their panel describes what's inside (kinds of files, how old they are, what the folder looks
  like). You can open them and untick the files and sub-folders that should stay.
- Anything added before the safety rules refused it, such as a folder made right on a drive, is listed but never
  cleaned, and can be removed.
- The update notice is a card at the bottom, in place of the status line.
- Fixed: the cleaning window showed "Â·" in place of "·" (1.0.2).

## 1.0.2 (2026-11-19)

- **Safety fix:** the Space map no longer offers to add a folder made right on a drive (like `C:\Personal`), which
  usually holds your own files, or Windows' own `$Recycle.Bin` and `System Volume Information`.
- **The Space map shows the disk as blocks**: each folder's block is sized by its share, free space is a hatched block,
  and an orange corner marks folders that already hold a cleanup item. Click a block to go inside. Its key now has an
  orange outline and a small block icon.
- "Safe to delete" is now **Safe to clean**, and every section has one plain line saying what's in it and what isn't
  touched.
- Beside the big number: whether it's all safe to clean, and how much more you can check yourself. On a first run,
  the status line explains that one click on Clean is the quick cleanup.
- The confirm window's key says what will happen: **Move 40.5 GB to Recycle Bin** or **Permanently delete 40.5 GB**.
- Protected folders shows **7 built in + 0 yours** instead of "0 folders", with the full built-in list.
- Space map: the drive key names the drive when the map opens, and tiny folders show "<1%".
- The free-space line on the display now shows at every display scaling, not only at 100%.

## 1.0.1 (2026-11-19)

- **Find big folders is now the Space map**, with its own key at the left of the toolbar. Scan again, Protected
  folders and Reset to default now sit together next to Clean.
- Clearer labels on the dark display: "Space to free up" and "Disk C:" in the same type as the rest of the app.
- The Space map shows a small turning indicator while it gets ready and while it sorts folders.
- In the Space map, clicking **Added** takes a folder back off the cleanup list.
- The confirm window groups what you ticked under Safe to delete, Review first and Caution, and starts on
  **Recycle Bin / Trash** whenever anything beyond Safe to delete is ticked. "Delete" is now called
  **Permanently delete**, so the choice can't be misread.
- Caution is marked with a solid bar and Review with a hatched one, so the two differ without colour.

## 1.0.0 (2026-11-19)

The first release, for Windows 10/11 and macOS 13+. What it does: see the [README](https://github.com/knox-101/space-hunter#readme).
