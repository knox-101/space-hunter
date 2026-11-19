# Startup temp cleaner (PowerShell)

The small script Space Hunter grew from. It clears your Temp folder, Windows Temp, Prefetch and the Win+R history
2 minutes after you sign in, skips files that are in use, and shows no windows.

- **Install:** run `Install-TempCleaner.ps1` once from an Administrator PowerShell. It registers a scheduled task
  called "TempCleaner" and puts a "Clean Temp" shortcut on your Desktop.
- **Uninstall:** `.\Install-TempCleaner.ps1 -Uninstall`

The Space Hunter app does all of this and more, with a choice of what to clean. Use this script only if you want
a silent clean at every sign-in with nothing to open.
