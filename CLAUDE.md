# MacDock project rules (read first)

- MacDock must NEVER quit when the user closes/force-closes any app, including Claude. Force quit kills only the target app's PIDs, never MacDock's own.
- Every build/release must embed the logo icon (`/win32icon:macdock.ico`); `mac-dock\check-icon.ps1` must pass. Never leave an icon empty; fall back to the logo.
- Claude runs as a packaged app: its shell's `%LOCALAPPDATA%` is redirected to `Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local`. Never launch the dock via explorer.exe with an AppData path (causes the "specified path does not exist" popup). The dock relaunches itself through the `MacDockGuard` scheduled task, which runs `D:\#AI workflows\Claude work\Windows Apps\MacDock\mac-dock\MacDock.exe`.
- Build with `mac-dock\bi.ps1` (the csc line); `build.bat` breaks on '#' in the path.
- Keep all files inside `D:\#AI workflows\Claude work\Windows Apps\MacDock`.
- MacDockGuard must run `mac-dock\MacDock.exe` (icon pack in `MacDock\icons`, names/colors/fan style next to the exe). Never create `%LOCALAPPDATA%\MacDock\bin`: the dock then repoints the guard there and loses custom icons (happened 2026-11-17; old copy kept as `bin.old`). From a Claude shell, `%LOCALAPPDATA%` is virtualised: read the real log at `\localhost\C$\Users\<user>\AppData\Local\MacDock\dock.log`.
- This PC has ~3,600 user fonts: never create a GDI+ `Font`/`FontFamily` by name (6-28 s each). Use `Typography.Text/Copy/SystemFont/SystemFamily`, and pass odd UI text through `Typography.Safe` + `Typography.FontFor` (missing glyphs trigger a multi-second fallback search).
- Live test hook: write an item name (e.g. `Downloads`) to `mac-dock\scratch\click.trigger`; the running dock clicks it and logs `TRIGGER activate took N ms`.
- WinForms `Control.DefaultFont` costs ~5 s here (name lookup); it is pre-warmed on a background thread at startup. Don't add code that forces it on the UI thread before that.
