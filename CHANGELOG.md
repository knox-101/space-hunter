# Changelog

All notable changes to Space Hunter. Versions follow [semantic versioning](https://semver.org): the last number is
for fixes, the middle number is for new features, and the first number is for big changes.

## 1.6.3 (2026-11-21)

- **Mac: updating works wherever the app is.** An app opened straight from Downloads is run by macOS from a hidden,
  read-only copy, so the update couldn't replace it ("no permission to save"). Space Hunter now finds the real copy and
  replaces that, unpacks next to it, and clears the "downloaded from the internet" mark so it won't happen again. If the
  folder still can't be written to, the checked new version is put in Downloads and shown in Finder, to drag into
  Applications.

## 1.6.2 (2026-11-21)

- **What's inside follows the age you choose, as you change it.** Files too new for "only older than" show
  unticked with "too new, stays"; a folder with some newer files shows a dash; choose "everything" and it's all ticked.
- **One key fewer:** "Scan again" is gone. Default cleanup measures everything afresh, and sizes are measured every
  time you open Space Hunter and after every clean.
- The guide key's hover label ("Guide: how Space Hunter works") is no longer cut off.

## 1.6.1 (2026-11-21)

- **"Back to recommended" is now "Default cleanup":** it puts the ticks and ages back to the first-time setup.
- **The open section is a filled tab** standing on the rail, so you can see at a glance which one the list below
  belongs to; hovering another section lightly fills it.

## 1.6.0 (2026-11-21)

- **A finish you can't miss.** When cleaning ends, the display fills the window and your drive appears as a ring:
  the space you freed lifts out of it as an orange wedge, crumbles away, and the ring closes up while the free space
  counts up in the middle. One pulse says it's done, then a short list shows what went, with the total at the bottom.
  "What was cleaned" still opens the full log. Windows' "reduce animations" setting skips straight to the result.
- **"Deep clean"** is the new name for the third section (old AI chats and workspaces, old installers in Downloads,
  folders you added). Opening it shows a clear note that it may include your work, so check each item first.
- **The sections sit on a rail.** Each shows its size in big type with what's in it beside, and a small box that
  shows whether everything, some or nothing in it is ticked; click the box to tick or untick the whole section. An
  orange marker slides under the one you're looking at. Each section opens with one line on what to do with it
  (Quick clean: clean it every time; Deep clean: check each item first).
- **A first-run guide** walks through the display, the sections, the rows, an item's panel, the Storage map,
  Protected folders, Back to recommended, Clean and the theme keys, one at a time. Skip it any time; the **?** key at
  the top right shows it again.
- **Light, Dark and System are now three small icon keys** at the top right; hover one to see its name.
- **"Reset to default" is now "Back to recommended".**
- Sizes from 1000 MB up now read in GB.
- Quick clean now says what it is: safe to clean as often as you like.

## 1.5.2 (2026-11-20)

- **Permanently delete is chosen to start with**, every time you press Clean, and **Recycle Bin / Trash** is still
  one click away. It's now offered for Quick clean too: choose it and everything you ticked goes to the bin.
- **Storage map: the wheel scrolls the list wherever the pointer is on a row**, including over the bar, the size and
  the percentage, not only over the folder name.

## 1.5.1 (2026-11-20)

- **The item panel stays where you are.** Ticking a file or folder in What's inside, changing the age or keeping a
  folder no longer scrolls the panel back to the top or takes the file list back to the item's top folder.
- **Picked files tick the item.** When you tick files inside an unticked item, the item now shows a normal tick,
  keeps its description, and adds a small note with what you picked against the whole item, for example
  "25 of 312 files · 40 MB of 1.2 GB". Clicking its box clears your picks.
- **Smoother loading animations.** The Storage map's turning ring and moving bar, and the cleaning window's progress
  bar, are drawn about 60 times a second and move with the clock, so they no longer jitter.

## 1.5.0 (2026-11-19)

- **Pick exactly what goes.** Open an unticked item and tick just the files or folders you want cleaned. The item
  shows a dash instead of a tick and says, for example, "25 of 312 files picked · just these". Only those go, whatever
  their age, in the next clean; afterwards the item is unticked again, so nothing new is ever cleaned by surprise.
- **Select all / Clear** at the top right of every file list, and the file list works the same in every folder:
  open a folder to look inside, Back, the folder trail, and Open in Explorer / Finder / Files.
- **Age only where it makes sense.** "Only older than N" or "everything" is offered on things that fill up over
  time (Temp, logs, Windows Update files, Downloads, old AI chats and workspaces, folders you added). Caches are
  cleaned whole, so they no longer ask.
- **Shorter item panel.** Three short lines are always in view (what it is, what goes, what happens after), with
  "More about this item" for the full notes, so the file list has room.
- **Light, Dark or System theme**, from the switch at the top right. System follows your computer's setting as it
  changes. The display stays the darkest part of the window and orange stays the only accent.
- Small grey text and orange text are a little darker in the light theme, so they're easier to read.

## 1.4.0 (2026-11-19)

- **Update checks work on shared networks.** On a busy network (an office, a campus, a hotspot shared by many people)
  GitHub could refuse the update check, so no update was ever offered. Space Hunter now asks a second way when that
  happens. If you're on 1.3.1 or older and never saw an update, download this version once from the
  [download page](https://github.com/knox-101/space-hunter/releases/latest); after that, updates arrive by themselves.
- **The version is always shown**, bottom right of the main window.
- **A file's age counts from when it arrived.** Photos or projects you copied, unzipped or downloaded today count as
  new, even if their own date is years old, so an "older than N days" item never takes them.
- **Empty folders:** a clean removes the folders it emptied, and empty folders older than the item's age. An empty
  folder an app made a moment ago stays.
- **When a clean finishes**, the window shows a short list of what was cleaned, item by item, instead of every
  single file (every file is still in the log).
- **Storage map:** folders that can't be added say why in a few calm words ("Your own files", "App settings",
  "Browser profile") instead of a long warning on every row. Closing the map stops its scan straight away.
- AI tools' and editors' own folders (`.claude`, `.codex`, `.gemini`, `.cursor`, `.vscode`, `.config` and similar)
  can't be added to the cleanup list: they hold settings and sign-ins. Their caches and old chats stay cleanable
  items as before.
- Small grey text is darker, so it's easier to read.
- A size measured a moment ago can no longer be overwritten by an older, slower measurement of the same item.
- Linux: **What's inside** shows each folder's size, and the header reads "Freed so far", the same as on Windows
  and Mac.

## 1.3.1 (2026-11-19)

Fixes from a full stress test. Updating is recommended.

- **Windows: no more crash on very long paths.** A folder nested past Windows' old 260-character limit (for example
  deep `node_modules` in Temp) could close the app during its first scan. One odd file or folder is now skipped,
  never the whole scan.
- **Windows: a clean in progress can't be changed.** Pressing Reset to default, or changing an item, while a clean
  runs no longer switches the rest of that clean from Recycle Bin to permanent delete.
- **Windows: Recycle Bin is only used where there is one.** On USB and network drives, which have no bin, files are
  kept (choose Permanently delete to clean them) instead of being deleted for good without a word.
- **Windows: other accounts' folders, Windows' own key and credential folders, the top of AppData and browser
  profile folders can't be added to the cleanup list.**
- Windows: no update while a clean is running, so two copies never run at once. "Freed so far" now reads right in
  countries that write decimals with a comma.
- **Linux: the password helper only enters folders owned by root**, and cleaning never enters another user's folder,
  so a folder swapped for a link mid-clean can't redirect a delete. Empty-folder removal never crosses into another
  disk mounted inside.
- Linux: updates and the password helper trust only Space Hunter's own AppImage, never one inherited from another
  AppImage app. System items (downloaded updates, crash dumps) are now measured, so they can be cleaned. Closing the
  main window during a clean shows the cleaning window instead of cutting it off. One possible Storage map crash fixed.
- Mac: Space Hunter's own log is protected (the "Logs" item no longer resets "Freed so far"). Only folders in your
  home folder can be added, and never apps' settings in ~/Library (their caches and logs are fine).
- Clearer words: "press Clean to start", and Protected folders no longer points to a button that isn't there.

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
