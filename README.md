# MacDock

A macOS-style desktop shell for Windows 11, written in C# (WinForms). It builds with the compiler that ships with Windows, so no SDK is needed.

![Desktop with dock and menu bar](screenshots/v1.5/desktop.jpg)

See the [changelog](CHANGELOG.md) for what's new in v1.6.

## Installation guide
**Requirements:** Windows 11 (Windows 10 mostly works), x64, .NET Framework 4.x (already part of Windows). No admin rights and no SDK needed.

1. Download `MacDock-v1.6.zip` from the [Releases page](https://github.com/gadgetcue/MacDock/releases/latest) and (optionally) check its SHA256 against the release notes: `certutil -hashfile MacDock-v1.6.zip SHA256`.
2. Extract the zip to any folder (right-click > Extract All).
3. Double-click `Install.bat`. If SmartScreen says "Windows protected your PC", click **More info > Run anyway**.
4. MacDock is copied to `%LOCALAPPDATA%\MacDock`, a Start Menu shortcut is created, and the dock starts. It also starts at login (toggle in MacDock Settings).

If Defender blocks or quarantines it, see [Why does Windows Defender flag MacDock?](#why-does-windows-defender-flag-macdock).

**Quitting:** quitting MacDock from its menu restores the Windows taskbar automatically. If MacDock is killed while the taskbar is hidden, run `MacDock.exe /restore`.

**Start / restart:** open *MacDock* from the Start Menu, or run `%LOCALAPPDATA%\MacDock\bin\MacDock.exe`. To restart it, exit MacDock and start it again from the Start Menu. If the taskbar stays hidden after a crash, run `MacDock.exe /restore` (or `mac-dock\restore-taskbar.bat` from the repo).

**Uninstall:** run `Uninstall.bat` from the extracted folder (or `%LOCALAPPDATA%\MacDock\Uninstall.bat`). It restores the taskbar, stops MacDock, removes autostart, the optional guard task and the shortcut, and deletes the install folder. Pins and settings in `%APPDATA%\MacDock` are kept until you delete that folder.

## Why does Windows Defender flag MacDock?
Short version: **it is a false positive**, but it is understandable why it happens.

- **Unsigned, new, self-built.** The exe is built with the C# compiler that ships with Windows and has no code-signing certificate. Unsigned executables with few downloads have no reputation, so SmartScreen and Defender's cloud heuristics treat them cautiously.
- **It does the things a shell does.** MacDock uses global/low-level Windows hooks (keyboard, gestures), enumerates and thumbnails other apps' windows (window enumeration, DWM), applies glass effects with `SetWindowCompositionAttribute`, simulates input, hides or replaces taskbar behavior, and registers startup entries. Heuristic engines associate that combination with keyloggers and droppers, even though here it is used for the dock, menu bar and window previews.
- **What it does not do:** MacDock has no account and no analytics or telemetry code. Its network use is limited to LocalSend transfers with devices on your LAN, the optional macosicons.com icon lookup, and fetching favicons for website pins.

You do not have to take our word for it:
- The source is fully open in this repo. Read it, or build it yourself: `mac-dock\build.bat` (run it from a folder path without a `#` in it).
- Check the zip's SHA256 (listed in each release) and upload the file to [VirusTotal](https://www.virustotal.com/); a few engines may flag it heuristically.

**If Defender quarantined it:** Windows Security > Virus & threat protection > Protection history > select the item > Actions > **Restore** (or Allow on device). To stop it being re-flagged, add an exclusion: Virus & threat protection > Manage settings > Exclusions > Add or remove exclusions > Add an exclusion > Folder > `%LOCALAPPDATA%\MacDock`. Then re-run `Install.bat`.

**Help us:** you can report the false positive to Microsoft at <https://www.microsoft.com/en-us/wdsi/filesubmission> (choose "Software developer", upload the exe). Each accepted report improves detection for everyone.
## Features
- **Dock**: magnification, running indicators, drag to reorder, drop files on apps, and a working Recycle Bin. Pins live in `dock.txt` (apps, folders, files, websites, `ms-settings:` links).
- **Genie minimize**: windows are sucked into their dock icon and flow back out, drawn from live window thumbnails.
- **Top menu bar**: Apple-style menu, per-app menus, CPU/RAM readout, Wi-Fi, Bluetooth, volume, battery, clock, and optional hiding of the Windows taskbar.
- **Dynamic Island**: a small pill at the top that springs open for notifications, power events, now-playing media and a hover peek.
- **Window previews**: hover a dock icon to see live thumbnails of its windows (DWM); click one to focus it.
- **Clipboard history**: the Dynamic Island keeps recent copied items so you can peek and re-copy them.
- **Notifications**: Windows notifications are read straight from the system database, so they show up instantly.
- **Control Center**: Wi-Fi, Bluetooth, AirDrop-style tile, Focus, display brightness and sound sliders, using real Windows APIs.
- **Mission Control and Launchpad**: overview of every open window plus an All Apps page; add apps to the dock by dragging.
- **Touchpad gestures**: 4- or 5-finger swipe up/down on a Precision Touchpad opens and closes Mission Control.
- **Spotlight**: global hotkey search.
- **Settings app**: dock, theme and icon options.
- **Icon packs**: `.icns` icon packs in `icons/` and `dark-icons/`, with optional macosicons.com lookup.
- **Web apps**: Brave, Edge and Chrome "Install as app" windows are treated as their own apps.
- **LocalSend**: AirDrop-style file sharing with nearby devices.
- **Downloads and folder fan**: dock folders open as a Fan, Grid or List, with folder colors, rename and drag to the Recycle Bin.
- **Media tile and more Control Center tiles**: now-playing artwork and title, Night light, Project, Battery saver, Airplane mode.
- **Fullscreen aware**: the dock, bar and island hide for fullscreen apps.
- **Native glass**: DWM background blur on the dock, bar, menus and panels.

## Screenshots (v1.5)
![Desktop with dock and menu bar](screenshots/v1.5/desktop.jpg)

![Dock magnification](screenshots/v1.5/dock-magnify.jpg)

![Launchpad with the search pill](screenshots/v1.5/launchpad.jpg)

![Control Center](screenshots/v1.5/control-center.jpg)

![MacDock Settings](screenshots/v1.5/settings.jpg)

![Apple menu](screenshots/v1.5/apple-menu.jpg)

Screenshots were taken on dark mode only; light mode was not captured for this release.

## Memory use
Measured on the v1.5 build on Windows 11 (1920x1080) with the dock idle: **~7-43 MB working set** (about 24 MB average over a minute; Windows trims it), **~92-103 MB private bytes**, and a **peak working set of ~140 MB** since launch (start-up and opening Launchpad). Numbers vary with how many apps you pin and which icon packs you load.
## Videos
- [Demo 1](docs/demo-1.mp4)
- [Demo 2](docs/demo-2.mp4)

## Build from source
Requires only Windows 10/11:

```
cd mac-dock
build.bat
```

This produces `MacDock.exe` in `mac-dock/`. To package your own build, put it in `bin/` next to `icons/` and `dark-icons/` and run `Install.bat`. Run `restore-taskbar.bat` if the taskbar stays hidden.

## Configuration
Edit `mac-dock/dock.txt`. Lines starting with `#` are ignored; `Label | target` sets a custom name; `---` starts the right-hand section; `@hide-taskbar on|off` toggles the taskbar.
An optional macosicons.com API key goes in `dock.txt` as `@macosicons-key = YOUR_KEY` or in the `MACOSICONS_API_KEY` environment variable.

## Credits
Fonts: Inter (SIL Open Font License). Icons in `icons/` and `dark-icons/` belong to their respective authors.

