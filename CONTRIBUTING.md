# Contributing

- Open an issue before large changes.
- Source is in `mac-dock/` (C#, WinForms). `mac-dock\build.bat` builds and deploys it; run it from a path without a `#`. The scripts contain the maintainer's local folder path, so adjust it for your checkout.
- Keep changes small and focused, and say how you tested them on Windows 11.
- `mac-dock\check-icon.ps1` must pass for any build you publish.
- Do not commit executables, logs, secrets or personal data (including your own dock and Launchpad lists).
- Security issues: see [SECURITY.md](SECURITY.md).
