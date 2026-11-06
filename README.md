# MacDock

A macOS-style desktop shell for Windows 11, written in C# (WinForms, no SDK needed).

## Features
- Dock with magnification, bounce, genie minimize, running indicators and a Recycle Bin you can drop files on
- Top menu bar with Apple-style menu, system stats, and a Dynamic Island
- Launchpad, Spotlight search, Mission Control and Control Center
- Notifications, trackpad gestures, and a Settings app
- Icon packs (`.icns`), including dark variants, plus optional macOS icon lookup
- Optionally hides the Windows taskbar while running (`@hide-taskbar` in `dock.txt`)

## Build
Requires only Windows 10/11 (uses the built-in .NET Framework compiler):

```
cd mac-dock
build.bat
```

This produces `MacDock.exe`. Run `restore-taskbar.bat` if the taskbar stays hidden.

## Configuration
Edit `mac-dock/dock.txt` to set pinned items (apps, folders, files, websites, `ms-settings:` links). Lines starting with `#` are ignored; `Label | target` sets a custom name; `---` starts the right-hand section.
An optional macosicons.com API key can go in `dock.txt` as `@macosicons-key = YOUR_KEY` or in the `MACOSICONS_API_KEY` environment variable.

## Credits
Fonts: Inter (SIL Open Font License). Icons in `icons/` and `dark-icons/` belong to their respective authors.

Screenshots: coming soon.
