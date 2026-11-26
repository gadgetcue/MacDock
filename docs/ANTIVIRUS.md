# Antivirus / SmartScreen notes

MacDock is open source and unsigned, so Windows may warn on first run ("More info" > "Run anyway").
What we do to look and behave like normal software:

- Per-user install, no admin, no UAC prompt (`asInvoker` manifest). Files go to `%LOCALAPPDATA%\MacDock`.
- Real exe metadata (company, description, product, version) and an icon in every build.
- Setup asks before enabling start at sign-in and creates only a limited at-logon task (`MacDockGuard`). While start at login is on in Settings, the running app also keeps a Run entry and its restart task.
- `install.log` in the install folder lists every file, registry and task change.
- Uninstall from Settings > Apps (or `Setup.exe /uninstall`); it removes the task, registry entries and folder without `cmd`/`timeout` tricks.
- No DLL injection, no remote threads, no packed or encrypted code. No PowerShell embedded in installers.
- `SHA256SUMS.txt` is written next to each build so downloads can be verified.

Still needed from the owner (cannot be done in code):
1. Sign the exes: obtain a code-signing certificate, set `MACDOCK_PFX` and `MACDOCK_PFX_PASS`, rebuild (`installer\sign.ps1` signs when set).
2. Submit each release as a false positive: https://www.microsoft.com/wdsi/filesubmission (sign in, "Software developer", upload the exe).
3. Optionally exclude `%LOCALAPPDATA%\MacDock` locally in Windows Security > Exclusions.
