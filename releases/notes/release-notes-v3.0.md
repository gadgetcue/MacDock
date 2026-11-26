## MacDock v3.0
### Fixed
- **Explorer restarts** (Fixes #7): the dock and menu bar screen space and the taskbar auto-hide are registered again.
- **Genie effect** (Fixes #8): the Windows minimize animation is suppressed while the genie effect is on (for the session only; restored on exit and at the next start). Not yet verified on a second display.
- **Dynamic Island at startup** (Fixes #9): it stays hidden when switched off. The startup case is not yet verified.
- **Auto-hide** (Fixes #19): a 0.4 s grace period before the dock slides away.
- **Start at sign-in is now opt-in.** Answering *No* in setup, silent installs and zip installs no longer turn autostart on. The every-minute scheduled task is gone: when enabled, a single at-logon task (`MacDockGuard`) is used; older minute-only tasks are replaced or removed.
- **Status bar popups.** Opening a menu or status panel closes the island, and a closing panel no longer disturbs the newer one.
- **Night Light slider.** Strength is applied silently through Windows' own settings handler, with no hidden Settings window; the current value is re-read when the panel opens and failed changes are retried.
- **Freezes and lag.** MacDock no longer waits on other apps' windows: focusing, minimizing, restoring and window repositioning are asynchronous, and window scanning runs on its own thread.
- **Memory paging stalls.** The dock no longer trims its own working set. It keeps itself resident and runs at above-normal priority, so it stays responsive under heavy load.
- **Screen flashing.** The menu bar and island are no longer re-raised when they are already on top.
- **Wi-Fi panel.** Rebuilt on the native Windows Wi-Fi API: names with special characters display and connect correctly.
- Clicking empty menu bar space closes menus, and quick taps outside are picked up.

### Added
- **Menu bar and island display** setting (Fixes #10). Not yet verified on a second display.
- **Show Launchpad button in Dock** switch (Fixes #13).
- **Space reserved under the menu bar** setting (Fixes #14).
- **What's new popup:** shown once after an update.
- **Support card** (UPI QR) in the welcome tour's new *Follow and support* page and in Settings > About This PC.
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

### Support
If MacDock saves you time, see the [Support section](https://github.com/gadgetcue/MacDock#support).

### SHA256
```text
098905ce1743830e4dd1caae9e7b110bcdaae7e6d859a3f60c6c7ae82c10ee36  MacDock-Setup.exe
ee00d37fce843925e2c4264a0fd1c9b3da09f5c741d271dbaf644f7f8cb8f52e  MacDock-v3.0.zip
```
