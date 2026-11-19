# Contributing

Thanks for helping. The most useful contribution is **a new cleanup item**: an app or tool whose cache, logs or
leftovers fill people's disks. That needs no code, only an entry in a JSON file.

## Adding a cleanup item

- Windows items live in [`windows/cleaners.json`](windows/cleaners.json).
- Mac items live in [`mac/cleaners.json`](mac/cleaners.json).

Each item becomes one row in the app. A row only appears on computers where at least one of its folders exists, so
adding items for niche apps never clutters the list for anyone else.

```json
{
  "id": "gobuild",
  "name": "Go build cache",
  "level": "safe",
  "age": "0",
  "paths": ["%LOCALAPPDATA%\\go-build"],
  "short": "The next build compiles from scratch",
  "note": "Compiled Go code",
  "what": "Go keeps compiled packages so builds and tests run faster.",
  "deletes": "The go-build folder. Same as 'go clean -cache'.",
  "safe": "Yes. Your code and downloaded modules are untouched.",
  "after": "The next build compiles everything again."
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Short, unique and lowercase. Saved settings are keyed by it, so never rename one. |
| `name` | yes | The row title. Name the kind of thing, and give products as examples: "IDE caches & logs (e.g. JetBrains)". |
| `level` | yes | `safe`: rebuilds itself, nothing personal (ticked at start). `review`: comes back only by re-downloading, or is history. `caution`: can hold the user's own work or history. |
| `kind` | no | `contents` (default): files inside the folders. `subfolders`: whole sub-folders, each judged by its newest file. Special kinds: `run-history`, `recycle-bin`, `windows-old` (Windows), `trash` (Mac). |
| `age` | yes | Only files untouched this long are cleaned: `"0"` (everything), `"30m"`, `"1h"`, `"14d"`. Use an hour or more for folders apps write to while running. |
| `admin` | no | Windows only: `true` for folders inside Windows or ProgramData that need administrator rights. |
| `paths` | yes* | The folders. Windows: `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, `%PROGRAMDATA%`, `%WINDIR%`, `%SYSTEMDRIVE%`, `%PROGRAMFILES%`, `%PROGRAMFILES(X86)%`, `%TEMP%`. Mac: `~/...` and `$TMPDIR`. A `*` matches one folder level, e.g. `...\User Data\*\Cache`. |
| `exclude` | no | Folder names that are never entered (an AI agent's `memory`). |
| `extensions` | no | Only files with these extensions (Windows `".zip"`, Mac `"zip"`). |
| `topLevelOnly` | no | Only files directly in the folder; sub-folders are never touched. |
| `short` | yes | One line under the row: what happens if it's cleared. |
| `note` | no | Windows only: a two-to-four-word label. |
| `what`, `deletes`, `safe`, `after` | yes | The item's panel. Answer: what is it? What exactly gets deleted? Is it safe? What happens afterwards? Plain words, no fear language. |

## Rules for items

1. **Only folders that hold rebuildable or disposable data.** Never documents, photos, projects, mail, password
   stores, browser history or cookies. Space Hunter frees space; it is not a privacy wiper.
2. **Point at the narrowest folder.** Use `...\Code\Cache`, not `...\Code`.
3. **Pick the honest level.** If clearing it costs a re-download of more than a few hundred MB, it's `review`. If it
   could lose someone's work, it's `caution`.
4. **Say how you know.** In the pull request, link the app's documentation or explain how you checked that the
   folder rebuilds itself.

Space Hunter refuses some folders whatever an item says: whole drives, Windows, Program Files, and personal folders
(Documents, Desktop, Pictures, OneDrive, `.ssh` and similar).

## Building and testing

- **Windows:** `windows\build.cmd`, then run `SpaceHunter.exe --report > report.txt` from an Administrator terminal to
  check your item's numbers. Nothing is deleted.
- **Mac:** `cd mac && ./build.sh`, then open `build/Space Hunter.app`.

## Licence

Space Hunter is free software under the [GNU GPL v3 or later](LICENSE). By contributing, you agree that your
contribution is released under the same licence.
