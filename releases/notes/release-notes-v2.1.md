## MacDock v2.1

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
