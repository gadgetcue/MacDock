# Changelog

## v3.0
### Fixed
- **Freezes and lag.** MacDock no longer waits on other apps' windows: focusing, minimizing, restoring and window repositioning are asynchronous, and window scanning runs on its own thread.
- **Memory paging stalls.** The dock no longer trims its own working set. It keeps itself resident and runs at above-normal priority, so it stays responsive under heavy load.
- **Screen flashing.** The menu bar and island are no longer re-raised when they are already on top.
- **Wi-Fi panel.** Rebuilt on the native Windows Wi-Fi API: names with special characters display and connect correctly.
- Menu bar panels: a closing panel no longer disturbs the newer one, clicking empty bar space closes menus, and quick taps outside are picked up.

### Added
- Wi-Fi password prompt for new networks, and again when a saved password no longer works.
- The Wi-Fi panel shows the cached list at once, then a fresh scan; saved networks are marked, with a *Wi-Fi Settings...* link and a hint when Location access is off.
- "What's new in 3.0" page in the welcome tour.

### Changed
- Per-user installer asks before enabling start at sign-in, writes `install.log` and has a cleaner uninstaller.
- Long status text in menu bar panels gets the width it needs.

## v2.4
### Added
- **Focus tab in the Dynamic Island** (hover the island): a Pomodoro timer with a progress ring on the island, plus water and stand-up trackers. Focus length (15-60 min), break length, daily water goal, water interval and stand-up interval are click-to-step rows in Settings > Menu Bar & Island.
- **Volume and Brightness pop-ups** on the island, **Caps Lock / Num Lock alerts**, and **mic and camera in-use dots**. Each has its own on/off switch in Settings.
- **Notification layout**: the island now shows the app's icon, the title, and "App: preview" under it. MacDock's own alerts use its logo, and apps with no usable icon get a coloured letter badge.
- **Dock separators**: right-click an icon > *Add Separator After* / *Remove Separator*. They are stored as `===` lines in `dock.txt`, so they stay in place when icons are reordered and survive restarts and updates.
- **Settings window** is now a normal resizable window with Windows minimize/maximize/close buttons, the MacDock logo, and a title bar that follows the light or dark theme. Toggle switches animate and are green when on.
- **Installer (`MacDock-Setup.exe`)** now also: sets the dock to use the integrated GPU (power saving), registers start at login, creates the `MacDockGuard` scheduled task (restarts the dock within a minute if it is not running), and starts the dock through that task. The uninstaller removes the GPU preference.
- The installer bundles the fonts, the icon packs (light and dark) and the logo, so a fresh install has the same icons as the dev build.

### Fixed
- **Icon crash**: two threads copying the same cached icon bitmap could throw GDI+ "Object is currently in use elsewhere". Cached icons are now copied under a lock.
- **Dock no longer resizes pop-ups**: always-on-top windows such as Drive notifications are left alone, and small windows are slid out from under the bar but never shrunk.
- **No false hang restart after sleep**: the freeze watchdog ignores the gap when the PC was suspended instead of restarting the dock on wake.
- **Window list**: untitled but real app windows (captioned, or asking for a taskbar button) are listed, a captioned window with a hidden owner counts as the app's main window, and blank UWP shell frames are skipped.
- **Sharper menu bar and settings text** (grid-fitted anti-aliasing) and whole-pixel menu title widths.
- Faster cold start after wake or boot: the default-font warm-up now begins as soon as the app starts.

### Notes
- An idle-timer experiment (slower idle tick) was tried and reverted; idle behaviour is the same as v2.3.
- `mac-dock-rs/` (a Rust port, work in progress) is included as source only. It is not part of this release and the installer does not ship it. Not yet ported: LocalSend, folder Fan/Grid/List, macosicons.com lookup, welcome tour, window-title theming, PWA split.
- The in-app updater only installs a newer exe version, so v2.3 users get this build through *Check for Updates* (the `MacDock-v2.4.zip` asset).

## v2.3
### Performance
- **The menu bar no longer re-reads `dock.txt` on every tick.** It was reading and parsing the settings file about 100 times a second on the UI thread. Settings lookups are now remembered until the file changes. Measured on the dev PC with the dock idle: bar update cost fell from about 800 ms to about 130 ms of UI time per 10 s, and the whole dock's idle CPU fell from about 2.3 s to about 1.2 s per 20 s (roughly 45% less).
- Startup is unchanged: a warm restart shows the bar after about 2 s of CPU time. The first start after installing a new build is slower because icons are rebuilt once; they are cached on disk afterwards.

### Added
- **Status bar readouts**: optional disk usage, network speed, temperature (where the PC exposes it) and weather (looks up your city online). All off by default.
- **Minimize effect**: Genie, Scale or Fade (Settings > Dock).
- **Per-app hidden dock**: @hide-dock-apps=chrome.exe,code.exe in dock.txt auto-hides the dock while those apps are in front.
- **Usage color hints**: CPU/RAM turn orange above 80% and red above 90%; battery icon orange below 20%, red at 10%.
- **Trash shortcuts**: middle-click, or Ctrl/Shift-click, empties the Recycle Bin.
- **Menu bar double-click** on empty space maximizes or restores the front window.
- **Per-app volume** sliders in the Sound panel.
- **Settings no longer freezes the dock on first open** (the ~5 s Windows Forms default-font lookup now happens in the background at startup).
- **Crash and hang safety**: no 'Not responding' ghosting, background-thread errors are logged instead of ending the process, and a dock frozen for 60 s restarts itself through MacDockGuard.
- **Minimize into the Dock**: minimized windows appear as thumbnail tiles (app icon in the corner) before the Recycle Bin; click to restore. Toggle in Settings > Dock.
- **Hover previews switch**: Settings > Dock > Show window previews on hover.
- **Settings > Menu Bar & Island regrouped** into Status readouts, Menu bar buttons, Tray icons, Control Center, Dynamic Island & notifications, Keyboard shortcuts.
- **Menu bar item order and spacing**: move any right-side item (clock, battery, volume, tray icons...) and add extra spacing in Settings > Menu Bar & Island. Network speed can show download, upload or both, with or without arrows.
- **Fan thumbnails**: picture tiles pop on hover like icons; videos show a frame; thumbnails keep their aspect ratio.
- **Folder fan never shows a blank tile**: when Windows has no icon for an item (for example some subfolders), the fan falls back to the file-type icon, then a lettered tile.
- **Single-file installer** (installer\build-setup.ps1).
- **Redesigned welcome tour**: full-height, DPI-aware, with a clearer feature list.
- **Dynamic Island notifications** have a new layout with the app's artwork.
- **Smoother Genie** minimize and restore.

### Fixed
- **Settings is no longer a topmost window**, so other windows can sit above it like any normal app.
- **The Windows taskbar stays hidden across an auto-update** instead of flashing back while the dock restarts.

### Developer
- `mac-dock\scratch\click.trigger` accepts `@settings:<page>` (for example `@settings:menubar`) to open Settings on a page.
- `MacDock.exe --tour-shot <scale> <workAreaHeightPx> <page> <out.png>` renders the welcome tour offscreen.

## v2.2
### Fixed
- **Windows no longer cover the top status bar.** A window dragged or moved up under the bar (CPU/RAM, Bluetooth, volume, Wi-Fi, battery) used to draw its title bar and min/max/close buttons over the bar text. The dock now holds windows below the bar while you drag them, and slides any that end up there back down. Maximized and fullscreen windows are left alone, and elevated windows can't be moved by the dock.
- **Battery popup text overlap at 125% and higher display scaling.** The popup's rows were a fixed pixel height while the text scales with Windows DPI, so "Battery Saver", the large percentage and "Charging" ran into each other. Row heights now scale with the display.
- **Folder fan no longer freezes the dock.** Images are read to memory before decoding so a slow or cloud-backed file can't hold the paint lock, and the "Open in Explorer" arrow is drawn as a vector shape instead of building a symbol font on every open.
- **No more multi-second freezes with thousands of installed fonts.** Fonts are loaded from their files instead of being looked up by name, and text with glyphs Inter lacks falls back to Segoe UI instead of triggering a system-wide font search.
- A frozen app can no longer freeze the dock: window titles are read from cache and windows are restored asynchronously.

### Changed
- Folder fans warm up at startup so the first folder click opens instantly.
- Open windows come from a background scan (new `DockScan`), and icons are cached on disk and refreshed in the background, so the dock paints without waiting on slow apps.
- Pending elevated (UAC) prompts are brought forward more reliably.
- The LocalSend popup scanning label no longer shifts the layout.
- `MacDock.exe --fan-bench <folder>` writes folder fan frame timings for diagnosing slow opens.
## v2.1
### Added
- **Send with LocalSend from Explorer.** Right-click any file or a multi-selection and choose "Send with LocalSend" (also added to the Send to menu). A new popup lists nearby devices, shows Scanning while it looks, and sends the files to the one you pick. Downloads fan items have the same entry in their right-click menu.
- **New LocalSend popup** with a device list, progress and clean text alignment.
- **LocalSend port fallback.** If port 53317 is already taken by the LocalSend app or a stale instance, MacDock moves to the next free port and peers still find it.
- **Icon master switch and custom icon packs.** Settings > Icons has a "Use original app icons" switch that turns off MacDock's styled icons and any custom pack at once. Install your own pack from a folder or .zip of PNG, ICO, ICNS or JPG files named after each app (for example `Chrome.png`), open the pack folder, or remove it again.
- **Split view in the dock.** Windows tiled with Windows 11 snap layouts get one combined item; click it to bring the whole group forward together, click again to minimize it.
- **Welcome tour** for first-time users, reachable later from Settings.
- **Rename Control Center tiles** with the pencil chip when editing.
- **Pending UAC prompts surface automatically.** With the taskbar hidden, an elevation prompt that was waiting as a minimized button now comes to the front.

### Changed
- **Folder fan menus appear on top.** Right-click menus in a folder fan open above the fan instead of behind it. The "Open in Explorer" arrow stays at the top of the fan.
- **Tray icons: left click brings the app's own window forward**, instead of only opening the Windows flyout. Apps with no window get a click through UI Automation with the flyout kept invisible.
- **Mission Control** reopens faster through a pre-warmed window, and scrolling stays smooth.
- **Animation timing** follows the compositor refresh steadily, giving smoother hover, scroll and fade motion.
- **Control Center** opens instantly: Wi-Fi, WMI and radio state are cached and refreshed in the background.
- **Window previews** keep the close X on top after the layout changes.
- **Updater** rolls back and keeps the dock running if the restart cannot be scheduled, and shows the reason when an update fails.

### Fixed
- **Folder click hang.** Opening a folder fan could freeze the dock for seconds while big images decoded. Thumbnails now come from the shell's cache and Explorer hand-offs run off the UI thread.
- **Web app icons.** Aero Browser (Safari) web apps and Brave, Edge and Chrome web apps now keep their own icon in the dock and Launchpad, and a second click minimizes the right window. Web apps with a profile suffix also count as running.
- UWP windows that were minimized or suspended no longer fall back to the logo.
- Settings and Control Center no longer block on slow Wi-Fi, radio or WMI queries.
- Hung `netsh` calls can no longer stall the caller.
- Dock stays in sync while icons are being dragged.

## v2.0
### Added
- **Use original app icons** toggle (Settings > Icons): show each app's own icon instead of the MacDock icon pack; per-app choice and custom icons from the right-click menu.
- **Folders dropped on a dock icon are pinned** instead of being opened with the app; hold Shift or Ctrl to move or copy into a folder icon.

### Changed
- **Smoother scrolling** in Settings and Mission Control: a critically damped spring glides wheel notches together, and the page snapshot is reused instead of re-taken per notch.
- Window previews refresh when the exe changes, an entry is 10 minutes old, or a new window appears.
- Taskbar sync: owned windows (dialogs, pop-ups) are no longer shown as taskbar buttons, and UWP windows hosted by ApplicationFrameHost resolve to the real app.
- Mission Control: closing a window drops its tile and relayouts; thumbnails re-place correctly when cells move.
- Frame-time logging is off outside profiling, removing needless frame drops.

### Updating
- Apple menu > **Check for Updates...** downloads this release's zip and swaps in the new MacDock.exe automatically.
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

