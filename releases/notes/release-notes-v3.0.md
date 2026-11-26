## MacDock v3.0

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

### Install
Run `MacDock-Setup.exe` (per-user, no admin), or extract `MacDock-v3.0.zip` and run `Install.bat`. Existing installs can use *Apple menu > Check for Updates...*.
The files are not code-signed, so SmartScreen may warn: *More info > Run anyway*.

### SHA256
```text
509e9e35f62cf6577bc049f9f48364534beda4af8007fb6dcddaa729fb9257f7  MacDock-Setup.exe
ee10ddaa3e2a905cde58d6df57dacd73d461669148a917c6b8cb9f3149bfcf5a  MacDock-v3.0.zip
```
