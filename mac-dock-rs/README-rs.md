# MacDock (Rust port)
Single build output: `MacDock-rs.exe` (build with `powershell -File build.ps1`; the exe is moved out of `target\`, never copied).
Shares `dock.txt`, `docksize.txt`, `itemnames.txt`, `iconmap.txt` and the `icons` pack with the C# dock in `..\mac-dock`.
Test without touching the installed dock: `MacDock-rs.exe --test --lift 170` (no taskbar hiding, dock raised 170 px). `scratch\click.trigger` hook: item name, `@settings`, `@launchpad`, `@mission`, `@spotlight`, `@cc`, `@island`, `@quit`.
Deploy (only after approval): stop the C# dock, overwrite `mac-dock\MacDock.exe` with `MacDock-rs.exe`, start via the `MacDockGuard` task, delete the Rust copy so one install/build remains.
Not ported yet: LocalSend, folder Fan/Grid/List (folders open in Explorer), macosicons.com lookup, welcome tour, window-title theming, PWA split.
Debug: `MACDOCK_PROFILE=1` logs startup stages to `dock-rs.log`.
