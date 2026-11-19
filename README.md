# Space Hunter

**Finds the folders silently filling your disk, explains each one, and clears the ones you pick after one
confirmation.** Free for Windows 10/11 and macOS 13+. No account, no tracking, under 1 MB.

### [⬇ Download the latest version](https://github.com/knox-101/space-hunter/releases/latest)

Windows: `SpaceHunter.exe` · Mac: `SpaceHunter-mac.zip` · first-run steps are on the download page.

![Space Hunter on Windows](docs/screenshots/windows-main.png)

## What it does

- **Shows every place separately**: temp files, Windows leftovers, browser and app caches, developer caches (npm,
  pip, Gradle, Xcode…), AI tool leftovers (e.g. Claude Code and Codex history, downloaded models), old installers.
  Only apps you actually have get a row.
- **Sorts them by risk**: **Quick clean** (caches and leftovers that rebuild, ticked at start), **History &
  re-downloads** (downloads again when needed, or old history) and **Personal & work items** (can hold your work or
  history; check before cleaning).
- **Explains each one**: click a row to see what it is, what gets deleted, whether it's safe and what happens after,
  and choose "only files older than N days" or "everything".
- **Asks before cleaning**, with a choice of **Delete** or **Recycle Bin / Trash**, and shows each file as it goes.
- **Space map**: see how your whole disk is split up, folder by folder, on any drive (about 10 seconds for a full
  Windows drive), and add a cache folder to the cleanup list from there.

![The Space map](docs/screenshots/windows-space-map.png)

## Safe by design

- Files in use, or changed more recently than an item's age setting, are skipped, never forced.
- Personal folders (Documents, Desktop, Pictures, OneDrive, `.ssh` and similar) are never cleaned. Folders you mark
  as **protected** are never touched, and AI agents' memory folders are always skipped.
- Its only network request is one check for a new version when it opens. Updates install only if they're signed
  with the project's key.

## More

- [Command line, updates and building from source](docs/advanced.md)
- [Add an app to the cleanup list](CONTRIBUTING.md): no code needed, just an entry in a JSON file
- [What changed in each version](CHANGELOG.md) · [Report a security problem](SECURITY.md)

## Licence

Free software under the [GNU GPL v3 or later](LICENSE): use, study, share and change it; shared changes stay under
the same licence. Copyright (c) 2026 Blesson Reji (knox-101). Fonts: SIL Open Font License, see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
