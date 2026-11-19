# Changelog

All notable changes to Space Hunter. Versions follow [semantic versioning](https://semver.org): the last number is
for fixes, the middle number is for new features, and the first number is for big changes.

## 1.0.0 (2026-11-19)

The first release, for Windows 10/11 and macOS 13+.

- 43 cleanup items on Windows and 33 on Mac, in three sections: **Safe to delete**, **Review first** and **Caution**. Each has a panel that
  says what it is, what gets deleted, whether it's safe and what happens after.
- Each item can clean either "only files older than N minutes/hours/days" or "everything".
- Clean always asks first, with a choice of **Delete** or **Recycle Bin / Trash**. A live log shows each file, and
  **Stop** works at any point.
- **Find big folders** measures a whole drive, and lets you put cache folders on the cleanup list. On Windows it
  reads the NTFS file index (about 10 s for 1.5 million files) and falls back to a folder-by-folder scan on other
  drives. One drive key opens a list of drives (with the window dimmed behind it); the Mac lists external disks too.
- **Protected folders** are never touched, and personal folders can't be added.
- Only apps that are installed get a row. Items are defined in `cleaners.json`, so adding an app needs no code.
- Scans measure four items at a time, Safe-to-delete items first.
- **Updates:** a quiet check on launch; a bar offers Update & relaunch, and only installs releases signed with the
  project's key.
- **Reset to default**, a "freed so far" total, and a command line (`--report`, `--auto`, `--version`).
