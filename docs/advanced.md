# Space Hunter: the details

## Everything it can clean

| Section | Ticked at start | Examples |
|---|---|---|
| **Safe to clean**: rebuilds or isn't needed | yes | Temp folders, Windows Update leftovers, Windows logs, crash dumps, thumbnail cache, graphics shader caches (including Steam's), graphics driver installer leftovers, browser caches (Chrome, Edge, Brave, Vivaldi, Opera, Firefox), chat app caches, app logs (e.g. OneDrive, Zoom), game launcher caches (e.g. Epic, Battle.net), Adobe media cache, AI app caches and logs (e.g. Claude), code editor and IDE caches (e.g. VS Code, JetBrains), npm / Yarn / pip / uv / Go build caches, Win+R history |
| **Review first**: comes back by re-downloading, or is history | no | The previous Windows installation (`Windows.old`), Prefetch, Steam and Spotify caches, AI agent VM images (e.g. Claude Cowork), downloaded AI models (e.g. Hugging Face), AI tool runtimes (e.g. Codex), npx packages, browser-automation browsers, Gradle, Maven, NuGet, Cargo, Go module, pnpm and conda caches, game engine caches (e.g. Unity, Unreal), Android emulator snapshots, the Recycle Bin |
| **Caution**: can hold your history or work | no | Old AI agent chat history (Claude Code, Codex), old AI agent workspaces, old installers and archives in Downloads, and any folder you add yourself |

There are 43 items on Windows and 33 on Mac; the Mac has the macOS equivalents (app caches, logs, Mail attachment
copies, Xcode DerivedData, simulator caches, old iPhone and iPad backups, the Trash). Every clean is logged in
`%LOCALAPPDATA%\SpaceHunter\log.txt` (Windows) or `~/Library/Application Support/SpaceHunter` (Mac).

## Updates

When it opens, Space Hunter asks GitHub once whether there's a newer release; nothing about you or your files is
sent. If there is one, a bar offers **Update & relaunch**: it downloads the new version, checks its signature against
the project's key built into the app, swaps it in and reopens, keeping your settings. Offline, nothing happens.
How releases are made: [`RELEASING.md`](../RELEASING.md).

## Command line (Windows)

Run these from an **Administrator** terminal. Space Hunter is a windowed app, so redirect the output to a file to
read it.

```bat
SpaceHunter.exe --report > report.txt   :: scan and list every item; deletes nothing
SpaceHunter.exe --auto                  :: clean the ticked Safe-to-delete items WITHOUT asking, then exit
SpaceHunter.exe --version > version.txt
```

`--auto` uses the ticks and ages you last set in the app, and only ever touches **Safe to clean** items. To run it
at sign-in, create a task in Task Scheduler with **Run with highest privileges** checked.

## Build from source

- **Windows**: run `windows\build.cmd`. It uses the C# compiler that ships with Windows, so nothing needs installing.
- **Mac**: `cd mac && ./build.sh` (needs the Xcode Command Line Tools). It builds a universal app for Apple silicon
  and Intel.
- Every push builds both on GitHub Actions (`.github/workflows/build.yml`) and tests the update signature check.
- The version number lives in [`VERSION`](../VERSION).

## Repository layout

| Path | What's there |
|---|---|
| `windows/` | The Windows app (C# WinForms on .NET Framework 4.x, which is part of Windows); its items are in `cleaners.json` |
| `mac/` | The Mac app (SwiftUI, one source file); its items are in `cleaners.json` |
| `fonts/` | Big Shoulders Display, Schibsted Grotesk and IBM Plex Mono, shared by both apps |
| `update-key/` | The public half of the release signing key, built into both apps |
| `tools/` | `make-release-key.ps1`, run once by the owner |
| `docs/` | This page and the README screenshots |
| `extras/startup-script/` | The PowerShell script Space Hunter grew from: a small at-sign-in temp cleaner |
