### Windows 10 or 11 (Intel, AMD or ARM) → [SpaceHunter.exe](https://github.com/knox-101/space-hunter/releases/download/vVERSION/SpaceHunter.exe)

Double-click it; there's nothing to install. If Windows SmartScreen warns, click **More info → Run anyway**. It asks for administrator rights so it can clean Windows' own folders.

### Mac → [SpaceHunter-mac.zip](https://github.com/knox-101/space-hunter/releases/download/vVERSION/SpaceHunter-mac.zip)

For macOS 11 or later, Apple silicon or Intel. Unzip it and move **Space Hunter** to Applications **before opening it** (opened straight from Downloads, older versions couldn't update themselves). The first time, open it once (macOS will refuse), then go to **System Settings → Privacy & Security** (on macOS 11–12: **System Preferences → Security & Privacy**) and click **Open Anyway**. On macOS 11–14, right-click the app → **Open** also works.

**Already have Space Hunter 1.6.2 or older on a Mac and the update says it "doesn't have permission to save"?** Download `SpaceHunter-mac.zip` above once by hand, unzip it and drag **Space Hunter** into Applications, replacing the old one. Your settings stay, and from 1.6.3 on updates install by themselves.

### Linux → [SpaceHunter-x86_64.AppImage](https://github.com/knox-101/space-hunter/releases/download/vVERSION/SpaceHunter-x86_64.AppImage)

For any current Linux with GTK 4 (Ubuntu 22.04 or later, Fedora, Debian 12, Arch, Mint 21 and others). Make it runnable (right-click → Properties → Allow executing, or `chmod +x SpaceHunter-x86_64.AppImage`) and double-click it. With Nix: `nix run github:knox-101/space-hunter`.

Already have Space Hunter? It offers this version by itself the next time you open it. You don't need the `.sig` files; they're for that built-in updater.

### What's new

