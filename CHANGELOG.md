# Changelog

All notable changes to Space Hunter. Versions follow [semantic versioning](https://semver.org): the last number is
for fixes, the middle number is for new features, and the first number is for big changes.

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
