# Changelog

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

