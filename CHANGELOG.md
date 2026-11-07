# Changelog

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
