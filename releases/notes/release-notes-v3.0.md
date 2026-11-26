## MacDock v3.0
### Fixed
- **Start at sign-in is now opt-in.** Answering *No* in setup, silent installs and zip installs no longer turn autostart on. The every-minute scheduled task is gone: when enabled, a single at-logon task (`MacDockGuard`) is used; older minute-only tasks are replaced or removed.
- **Status bar popups.** Opening a menu or status panel closes the island, and a closing panel no longer disturbs the newer one.
- **Night Light slider.** Strength is applied silently through Windows' own settings handler, with no hidden Settings window; the current value is re-read when the panel opens and failed changes are retried.
- **Freezes and lag.** MacDock no longer waits on other apps' windows: focusing, minimizing, restoring and window repositioning are asynchronous, and window scanning runs on its own thread.
- **Memory paging stalls.** The dock no longer trims its own working set. It keeps itself resident and runs at above-normal priority, so it stays responsive under heavy load.
- **Screen flashing.** The menu bar and island are no longer re-raised when they are already on top.
- **Wi-Fi panel.** Rebuilt on the native Windows Wi-Fi API: names with special characters display and connect correctly.
- Clicking empty menu bar space closes menus, and quick taps outside are picked up.

### Added
- **Bluetooth scanning.** The Bluetooth panel finds and pairs new devices, with an activity spinner and a *Bluetooth Settings...* link.
- **Bluetooth status icon** in the menu bar shows off, on or connected.
- **Control Center icons** redrawn in the macOS 27 style: sharp vector glyphs and accent-coloured active toggles.
- Wi-Fi password prompt for new networks, and again when a saved password no longer works.
- The Wi-Fi panel shows the cached list at once, then a fresh scan; saved networks are marked, with a *Wi-Fi Settings...* link and a hint when Location access is off.
- "What's new in 3.0" page in the welcome tour.

### Changed
- Per-user installer asks before enabling start at sign-in, writes `install.log` and has a cleaner uninstaller.
- Long status text in menu bar panels gets the width it needs.

### Install
Run `MacDock-Setup.exe` (per-user, no admin), or extract `MacDock-v3.0.zip` and run `Install.bat`.
**Updated build:** if you already installed an earlier 3.0, run the new installer over it (*Check for Updates* only offers newer version numbers).
The files are not code-signed, so SmartScreen may warn: *More info > Run anyway*.

### SHA256
```text
9f7f37fb5e9fa930c0f9441abba9cd5bc99b768b4102f8042aadeb11d1612674  MacDock-Setup.exe
fc97645f77797157c57f975a164eb606b519496ed52e37e55ffac8e4c57d261f  MacDock-v3.0.zip
```
