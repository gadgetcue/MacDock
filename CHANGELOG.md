# Changelog

## v1.9
### Added
- **Tray icons in the status bar**, with a collapsible tray and an arrow toggle; sharper tray icons and fixed click routing.
- **Apple menu**: Auto-hide Dock and Check for Updates.
- **Battery saver** toggle.
- **CPU and RAM** status bar toggles.
- **Profile picture** support.
- New **iOS-style Settings sidebar icons**.

### Changed
- Settings: fade, tidier spacing, smooth scrolling and a consistent Inter type scale.
- Dark dock icons.
- Faster startup via an icon cache.
- Smoother dock hover magnify.
- Second click closes status bar menus.

### Fixed
- Idle Dynamic Island circle.
- Launchpad issue with dark icons.
- Web apps show their own icon in the running dock.
## v1.8
### Added
- **Screenshot and screen record** buttons, in the menu bar and as Control Center tiles. The status bar buttons and the Control Center tiles are independent toggles in Settings.
- **Launchpad sort by install date** (newest first).
- **Control Center dark mode tile.**
- **Customizable Control Center**: tiles can be edited, removed and added, reordered by drag and drop, and resized.
- **Hide Start icon** option in Settings.
- **Dock corner radius** setting.
- New **Recycle Bin icon**.

### Fixed
- Scrolling in folder fans and in Settings.
- Launchpad context menu now flips to stay on screen.
## v1.7
### Added
- **Folder fan redesign**: labels lean along the arc and thumbnails tilt like the macOS Dock; an "Open in Explorer" arrow sits at the top of the fan.
- **Click-to-pin folder fans**: clicking a folder stack opens its fan and keeps it open; click the folder or the chevron tile to fold it back.
- **Dock tile chevron**: while a fan is open its dock slot becomes a dark tile with a chevron, fading and scaling smoothly in and out.
- **Emoji in Dynamic Island** notifications (rendered with Segoe UI Emoji).
- **Uninstall from Launchpad** now finds uninstallers shipped next to apps that don't register one.

### Changed
- LocalSend uses TLS 1.2 and 1.3 and disables Expect: 100-continue for better compatibility with newer peers.
- Icons are never blank: fully transparent icons fall back to a rounded tile with the app's initial (also in Mission Control).

## v1.6
### Added
- **Show Dynamic Island** toggle in Settings (Menu Bar & Island): hide or show the Island pill live.
- **Admin apps in the dock**: elevated (run as administrator) apps now appear and show as running.
- **Dock clamp**: maximized windows no longer reach below the dock's top edge, even for apps that ignore the work area.
- **Settings fade**: the Settings window fades out when closed.

### Changed
- Smoother icon glide when apps open or close; running-only apps keep a stable order.
- Hover previews wait for the cursor to rest, so fast sweeps across the dock stay smooth.
- Preview card sits above the icon label, with a larger red close button in its corner.
- No icon magnify, preview pop-up or auto-hide reveal while dragging or resizing a window past the dock.
- The hovered icon in a folder fan now draws on top of its neighbours.
## v1.5
### Added
- **Close all windows** option in the dock icon context menu: closes every window of an app in one go.
- **Taskbar restore on exit**: quitting MacDock (or `MacDock.exe /restore`) brings the Windows taskbar back automatically, so it can no longer stay hidden after the dock exits.
- **Launchpad search pill**: the Launchpad search field is now a glass pill, with the sort button matched to it.
- **Dynamic Island text**: crisper, better-fitted text in the Island pill.
- **Auto-hide dock** option (`@auto-hide-dock on` in `dock.txt`).

### Changed
- **Sharper dock icons at high DPI**: icons are rendered at a size that follows the dock size, zoom and display scale (125%+) instead of being stretched, which removes the blurriness.
- **LocalSend panel blur**: the LocalSend panel uses the same background blur as the other panels.

### Fixed
- **Auto-hide dock**: the dock reliably slides back in/out and no longer gets stuck hidden after toggling.
- **Never quits**: closing or force-closing any app, including Claude, can no longer take MacDock down. Force quit only kills the target app's own processes, and a guard task relaunches the dock if it is ever killed.
- Every build embeds the MacDock logo icon (a build check fails if it is missing).

### Memory use (measured, v1.5 on Windows 11, 1920x1080)
About 7-43 MB working set while idle (average ~24 MB over a minute, because Windows trims it), ~92-103 MB private bytes, and a peak working set of ~140 MB since launch (start-up and opening Launchpad). Your numbers will vary with the number of pinned apps and icon packs.
## v1.4
### Added
- **Reworked Genie minimize**: slice-based effect with a bottom-first pull, an accelerating curve and even frame pacing, so windows are sucked into (and restored from) the dock icon more smoothly.
- **LocalSend from Explorer**: a "Send to > MacDock LocalSend" shortcut and `MacDock.exe --send <file>` queue files for the LocalSend panel; optional autosave to accept incoming files without a prompt.
- **Keyboard access**: `Ctrl+Alt+Shift+D` puts the keyboard on the dock (arrows, Home/End, Enter to open, Menu key for the context menu, Esc to leave).
- **Multi-monitor and DPI**: choose the dock monitor (`monitor=` in `docksize.txt`); layout and fonts follow the monitor's DPI (per-monitor DPI manifest).
- **Windows theme and Reduce Motion**: follows the Windows colour mode and "Animation effects" switch; with animations off the bounce and open/close effects are instant.
- **Start-menu style Launchpad/All Apps motion**: short rise-and-fade open/close, prewarmed hidden copy, background-blurred backdrop.
- **Uninstall from Launchpad**: uses the program's own uninstaller (UAC when needed) and hides leftovers of programs that no longer exist.
- Window preview close button and an updated app logo used for every MacDock window.

### Changed
- Launched apps are brought to the front once their window appears; a single short bounce acknowledges a launch.
- Dragging a dock icon starts immediately (no long press).
- Settings window stays above the dock and maximizes into the area above it.
- Popup menus capture a margin around them so the glass blur is correct at the edges.
- MacDock relaunches itself through Explorer when started from a terminal or another app, so it is never tied to that process.
- Notifications and window-preview improvements, Win32 interop cleanups.

### Fixed
- Window previews and focus helpers when a window is minimized or topmost.
- Stale or missing icons in Launchpad.

## v1.3### Added
- **Drag and drop**: drop files onto dock apps to open them with that app.
- **Launchpad sort icon** for ordering apps.
- Executable metadata (version info) and an application manifest.

### Changed
- **Antivirus hardening** to reduce false positives: the optional guard task is now opt-in; restarts no longer go through `cmd`/`ping`; the installer no longer uses PowerShell with `ExecutionPolicy Bypass`; installer output is transparent about what it does.
- Faster folder hover.
- Win32 interop cleanups and window preview improvements.

### Fixed
- Build error from missing keyboard-hook declarations in `Win32.cs`.
## v1.2
### Added
- **LocalSend**: AirDrop-style file sharing with nearby devices (LocalSend v2 protocol), with a built-in loopback self-test (`MacDock.exe --ls-selftest`).
- **Downloads and folder fan**: folders on the dock open as a Fan, Grid or List. Includes curve and grow options, hover grow, a right-click menu, and drag-to-Recycle-Bin.
- **Folder colors** and **rename** for dock folders (rename also works in Launchpad).
- **Media tile** in Control Center showing artwork and the current title.
- **New Control Center tiles**: Night light, Project, Battery saver and Airplane mode.
- **Dock size and magnification** sliders in Settings > Dock.
- **Fullscreen handling**: the dock, top bar and Dynamic Island step aside while a fullscreen app owns the display.
- **Settings open animation** and hover glyphs on the traffic-light buttons.

### Changed
- Menus now open above the title.
- Window preview and Win32 helper cleanups.

### Fixed
- Frosted-glass background on popup menus.
- Dock, bar and island visibility after sleep, unlock and display changes.

## v1.1
- Window previews on dock icon hover (DWM thumbnails, click to focus) and a jitter fix.
- Dynamic Island clipboard history.
- Pinned apps match running windows by shortcut target, AppUserModelID and Store package.
- `Install.bat` / `Uninstall.bat` and an install guide in the README.

## v1.0
- Initial release: dock with magnification and genie minimize, top menu bar, Dynamic Island, Control Center, Mission Control, Launchpad, Spotlight, Settings app, icon packs and notification capture.

