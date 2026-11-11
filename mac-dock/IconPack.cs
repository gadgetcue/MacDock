// Optional macOS-style icon pack. Drop PNG files into %APPDATA%\MacDock\icons
// (for example Finder.png, Safari.png, Terminal.png) and apps that have a macOS counterpart show that icon.
//
// iconmap.txt says which Windows app uses which icon, one pair per line:
//     explorer = Finder
// The left side is an exe name without ".exe", or the app's name as shown in Start. The right side is an
// icon file name without ".png". The file is created with sensible defaults and can be edited freely.
// Apps with no match keep their own icon. "@icons off" in dock.txt turns the pack off.
//
// Themed .icns icons: if a folder "icons" sits next to the mac-dock folder (or "icons-theme" next to the exe) its .icns
// files are used first. Naming: the ThemeSets table below says which file-name prefix is the light icon and which is
// the dark icon for an app name; apps with one icon use it in both modes. The theme is re-checked every minute
// (TopBar) and the pack is reset when it changes. Order: ..\icons .icns, then icons\dark\<name>.png, then icons\<name>.png.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MacDock
{
    static class IconPack
    {
        const int Size = 96;            // stored icon resolution, same as the dock's other icons
        const double MarginTrim = 0.07; // macOS icon files carry transparent padding; trim it so they fill the dock cell

        static readonly string Folder = AppPaths.IconsDir;
        static readonly string MapFile = AppPaths.IconMap;

        static readonly string[] DefaultMap =
        {
            "# Maps Windows apps to icon files in the icons folder (no .png). Left: exe name without .exe, or the app's name.",
            "explorer = Finder",
            "file explorer = Finder",
            "msedge = Safari",
            "microsoft edge = Safari",
            "notepad = TextEdit",
            "calc = Calculator",
            "calculator = Calculator",
            "calculatorapp = Calculator",
            "systemsettings = System Settings",
            "settings = System Settings",
            "taskmgr = Activity Monitor",
            "task manager = Activity Monitor",
            "cmd = Terminal",
            "powershell = Terminal",
            "pwsh = Terminal",
            "windowsterminal = Terminal",
            "wt = Terminal",
            "windows terminal = Terminal",
            "terminal = Terminal",
            "snippingtool = Screenshot",
            "snipping tool = Screenshot",
            "screenclippinghost = Screenshot",
            "photos = Photos",
            "camera = Photo Booth",
            "clock = Clock",
            "alarms & clock = Clock",
            "calendar = Calendar",
            "mail = Mail",
            "maps = Maps",
            "weather = Weather",
            "movies & tv = TV",
            "media player = Music",
            "groove music = Music",
            "sticky notes = Stickies",
            "stocks = Stocks",
            "money = Stocks",
            "phone link = Phone",
            "microsoft store = App Store",
            "store = App Store",
            "xbox = Games",
            "tips = Tips",
            "voice recorder = Voice Memos",
            "sound recorder = Voice Memos",
            "magnifier = Magnifier",
            "msinfo32 = System Information",
            "system information = System Information",
            "people = Contacts",
            "to do = Reminders",
            "microsoft to do = Reminders",
            "paint = Preview",
            "mspaint = Preview"
        };

        static Dictionary<string, string> map;
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static bool? enabled;

        public static bool Enabled
        {
            get
            {
                if (enabled == null)
                {
                    bool off = false;
                    try
                    {
                        string config = AppPaths.Config;
                        off = File.Exists(config) && File.ReadAllLines(config).Any(l =>
                            l.Trim().ToLowerInvariant().Replace(" ", "").StartsWith("@icons") && l.ToLowerInvariant().Contains("off"));
                    }
                    catch { }
                    enabled = !off && Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*.png").Any();
                }
                return enabled.Value;
            }
        }

        static Dictionary<string, string> Map()
        {
            if (map != null) return map;
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(MapFile)) File.WriteAllLines(MapFile, DefaultMap);
                foreach (string raw in File.ReadAllLines(MapFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            return map;
        }

        // The pack icon for a dock item, or null to keep the app's own icon.
        public static Bitmap ForItem(Kind kind, string target, string label)
        {
            if (!Enabled) return null;
            switch (kind)
            {
                case Kind.Mission: return Load("Mission Control");
                case Kind.Launchpad: return Load("Apps");
                case Kind.Url:
                    // Windows Settings links get the System Settings icon.
                    if (target != null && target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return Load("System Settings");
                    break;
                case Kind.App: break;
                default: return null;
            }

            var keys = new List<string>();
            if (kind == Kind.App && !ShellIcons.IsAppsFolderItem(target))
            {
                try { keys.Add(Path.GetFileNameWithoutExtension(target)); } catch { }
            }
            if (!string.IsNullOrEmpty(label)) keys.Add(label);
            return ForKeys(keys);
        }

        // The pack icon for an app by its display name (used by Mission Control and search).
        public static Bitmap ForName(string appName)
        {
            if (!Enabled || string.IsNullOrEmpty(appName)) return null;
            return ForKeys(new[] { appName });
        }

        // Turns an app name into a safe file name ("Visual Studio Code" stays as is; "a/b:c" becomes "a_b_c").
        public static string FileName(string name)
        {
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            return new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        // Forget what has been loaded, e.g. after new icons were downloaded.
        public static void Reset()
        {
            lock (cache)
            {
                foreach (Bitmap b in cache.Values) if (b != null) b.Dispose();
                cache.Clear();
            }
            map = null;
            enabled = null;
        }

        static Bitmap ForKeys(IEnumerable<string> keys)
        {
            Dictionary<string, string> m = Map();
            foreach (string key in keys)
            {
                // An icon file named exactly like the app wins ("Brave.png" for Brave). This is also how
                // icons from macosicons.com are picked up.
                if (!string.IsNullOrWhiteSpace(key))
                {
                    Bitmap direct = Load(FileName(key));
                    if (direct != null) return direct;
                }

                string iconName;
                if (key != null && m.TryGetValue(key.Trim(), out iconName))
                {
                    Bitmap b = Load(iconName);
                    if (b != null) return b;
                }
            }
            return null;
        }

        // Loads icons\<name>.png once, trims its padding, and returns a private copy sized for the dock.
        // In dark mode icons\dark\<name>.png wins when it exists; apps without a dark version keep their normal icon.
        static Bitmap Load(string name)
        {
            if (!Theme.Light) { Bitmap dk = LoadFile("darkp|" + name, Path.Combine(AppPaths.ExeDir, "icons-dark", name + ".png")); if (dk != null) return dk; }
            Bitmap themed = LoadThemed(name);
            if (themed != null) return themed;
            if (!Theme.Light)
            {
                Bitmap dark = LoadFile("dark|" + name, Path.Combine(Folder, "dark", name + ".png")) ?? LoadFile("darkp|" + name, Path.Combine(AppPaths.ExeDir, "icons-dark", name + ".png"));
                if (dark != null) return dark;
            }
            return LoadFile(name, Path.Combine(Folder, name + ".png"));
        }

        // ---- .icns icons from the "icons" folder next to the mac-dock folder (..\icons from the exe, else icons-theme) ----
        // Files are matched by name prefix; ThemeSet lists the app names that use them, a light file and a dark file
        // (the dark file may be omitted when one icon serves both modes). Missing folder or file: the normal pack is used.
        struct ThemeSet { public string[] Names; public string Light, Dark; }

        static readonly ThemeSet[] ThemeSets =
        {
            new ThemeSet { Names = new[] { "finder", "explorer", "file explorer" }, Light = "Finder_hf", Dark = "Finder_Dark" },
            new ThemeSet { Names = new[] { "antigravity" }, Light = "Antigravity__White", Dark = "Antigravity_6w" },
            new ThemeSet { Names = new[] { "claude", "claude code" }, Light = "Claude_Code_liquid_glass_white", Dark = "Claude_Code_liquid_glass_zs" },
            new ThemeSet { Names = new[] { "premiere pro", "adobe premiere pro", "premiere" }, Light = "Premiere_Pro__Default", Dark = "Premiere_Pro__Dark" },
            new ThemeSet { Names = new[] { "notes", "notepad", "windows notepad", "notepad.exe" }, Light = "Notes_" },
            // dark-only icons from ..\dark-icons: light mode keeps the normal icon (Light = null)
            new ThemeSet { Names = new[] { "bambu studio", "bambustudio", "bambu-studio" }, Dark = "Bambu_Studio" },
            new ThemeSet { Names = new[] { "calculator", "calc", "calculatorapp" }, Dark = "Calculator-dark" },
            new ThemeSet { Names = new[] { "chatgpt", "chatgpt classic", "chatgpt classic app" }, Dark = "ChatGPT-classic-Dark" },
            new ThemeSet { Names = new[] { "google drive", "googledrivefs" }, Dark = "Google_Drive-dark" },
            new ThemeSet { Names = new[] { "logi options+", "logi options", "logioptionsplus", "logioptionsplus_agent" }, Dark = "Logi_Options-dark" },
            new ThemeSet { Names = new[] { "nuvio" }, Dark = "Nuvio-dark" },
            new ThemeSet { Names = new[] { "obs studio", "obs64", "obs" }, Dark = "OBS_Studio_Dark" },
            new ThemeSet { Names = new[] { "audacity" }, Light = "Audacity__Default", Dark = "Audacity-dark" },
            new ThemeSet { Names = new[] { "android studio", "studio64" }, Light = "Android_Studio_7f" },
            new ThemeSet { Names = new[] { "telegram", "telegram desktop", "telegram.exe" }, Light = "Telegram_9s", Dark = "Telegram-Dark" },
            new ThemeSet { Names = new[] { "whatsapp" }, Light = "WhatsApp_macOS_27" },
            new ThemeSet { Names = new[] { "youtube" }, Light = "YouTube_eng" },
            new ThemeSet { Names = new[] { "brave", "brave browser" }, Light = "brave_browser" },
            new ThemeSet { Names = new[] { "safari" }, Light = "Safari_" },
            new ThemeSet { Names = new[] { "system settings", "settings" }, Light = "Settings_" },
            new ThemeSet { Names = new[] { "apps", "launchpad" }, Light = "Launchpad-" },
            new ThemeSet { Names = new[] { "davinci resolve", "resolve" }, Light = "DaVinci_Resolve" },
            new ThemeSet { Names = new[] { "free download manager", "fdm" }, Light = "Free_Download_Manager" },
            new ThemeSet { Names = new[] { "localsend" }, Light = "LocalSend_" },
            new ThemeSet { Names = new[] { "blip" }, Light = "Blip_" },
            new ThemeSet { Names = new[] { "windows app" }, Light = "windows_app_" },
            new ThemeSet { Names = new[] { "iriun webcam", "iriunwebcam", "iriun", "iriun_webcam" }, Dark = "IriunWebcam" },
            new ThemeSet { Names = new[] { "sticky notes", "stickynotes", "stickies", "stikynot", "microsoft sticky notes" }, Dark = "sticky-notes-dark" },
            new ThemeSet { Names = new[] { "vlc", "vlc media player", "vlc.exe", "vlcmediaplayer", "videolan" }, Dark = "VLC_Dark" },
            new ThemeSet { Names = new[] { "winrar", "winrar.exe", "winrar archiver", "rar" }, Dark = "WinRAR_dark" }
        };

        // Folders searched for .icns files: ..\icons, ..\dark-icons (dark-mode-only icons), icons-theme next to the exe.
        static List<string> themeDirs;

        static List<string> ThemeDirs()
        {
            if (themeDirs != null) return themeDirs;
            themeDirs = new List<string>();
            try
            {
                foreach (string d in new[] { Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "icons")), Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "dark-icons")), Path.Combine(AppPaths.ExeDir, "icons-theme") })
                    if (Directory.Exists(d) && Directory.EnumerateFiles(d, "*.icns").Any()) themeDirs.Add(d);
            }
            catch { }
            return themeDirs;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static string FindThemedFile(List<string> dirs, string clean)
        {
            var orderedDirs = new List<string>(dirs);
            if (!Theme.Light)
                orderedDirs.Sort((a, b) => b.IndexOf("dark-icons", StringComparison.OrdinalIgnoreCase).CompareTo(a.IndexOf("dark-icons", StringComparison.OrdinalIgnoreCase)));
            else
                orderedDirs.Sort((a, b) => a.IndexOf("dark-icons", StringComparison.OrdinalIgnoreCase).CompareTo(b.IndexOf("dark-icons", StringComparison.OrdinalIgnoreCase)));

            foreach (string dir in orderedDirs)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*.icns"))
                    {
                        string fn = Path.GetFileNameWithoutExtension(file);
                        string cleanFile = Clean(fn);
                        string stripped = cleanFile.Replace("dark", "").Replace("light", "");
                        if (cleanFile == clean || stripped == clean || (stripped.Length >= 3 && clean.Contains(stripped)) || (clean.Length >= 3 && stripped.Contains(clean)))
                            return file;
                    }
                }
                catch { }
            }
            return null;
        }

        static Bitmap LoadThemed(string name)
        {
            List<string> dirs = ThemeDirs();
            if (dirs.Count == 0 || string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim().ToLowerInvariant();
            foreach (ThemeSet set in ThemeSets)
            {
                if (Array.IndexOf(set.Names, n) < 0) continue;
                string prefix = !Theme.Light ? (set.Dark ?? set.Light) : (set.Light ?? set.Dark);
                if (prefix == null) return null;
                string key = "icns|" + prefix;
                Bitmap cached;
                lock (cache)
                {
                    if (!cache.TryGetValue(key, out cached))
                    {
                        string file = null;
                        try { foreach (string dir in dirs) { file = Directory.EnumerateFiles(dir, prefix + "*.icns").FirstOrDefault(); if (file != null) break; } } catch { }
                        cached = file == null ? null : Read(file);
                        cache[key] = cached;
                    }
                }
                return cached == null ? null : new Bitmap(cached);
            }

            // Fallback for missing icons: check if any .icns in dark-icons or icons matches the app name
            string clean = Clean(n);
            if (clean.Length > 1)
            {
                string key = "icns_auto|" + clean + "|" + (!Theme.Light);
                Bitmap cached;
                lock (cache)
                {
                    if (!cache.TryGetValue(key, out cached))
                    {
                        string match = FindThemedFile(dirs, clean);
                        cached = match == null ? null : Read(match);
                        cache[key] = cached;
                    }
                }
                if (cached != null) return new Bitmap(cached);
            }

            return null;
        }

        // The largest PNG inside an .icns container, or null.
        static Bitmap OpenIcns(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 16 || b[0] != 'i' || b[1] != 'c' || b[2] != 'n' || b[3] != 's') return null;
            int best = -1, bestLen = 0, o = 8;
            while (o + 8 <= b.Length)
            {
                int len = (b[o + 4] << 24) | (b[o + 5] << 16) | (b[o + 6] << 8) | b[o + 7];
                if (len < 8 || o + len > b.Length) break;
                if (len - 8 > bestLen && b[o + 8] == 0x89 && b[o + 9] == 'P') { best = o + 8; bestLen = len - 8; }
                o += len;
            }
            if (best < 0) return null;
            using (var ms = new MemoryStream(b, best, bestLen))
            using (var img = new Bitmap(ms)) return new Bitmap(img);
        }

        static Bitmap LoadFile(string key, string path)
        {
            Bitmap cached;
            lock (cache)
            {
                if (!cache.TryGetValue(key, out cached))
                {
                    cached = Read(path);
                    cache[key] = cached; // null is remembered too, so a missing file isn't retried
                }
            }
            return cached == null ? null : new Bitmap(cached);
        }

        // The rectangle that actually holds artwork (pixels that are mostly opaque), ignoring transparent padding and soft shadows.
        static Rectangle ArtBounds(Bitmap image)
        {
            BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
                var row = new byte[data.Stride];
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                    for (int x = 0; x < image.Width; x++)
                    {
                        if (row[x * 4 + 3] <= 128) continue; // alpha is the 4th byte of each BGRA pixel
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
            finally { image.UnlockBits(data); }
        }

        // True when the artwork's four corners are solid: a plain square block rather than a rounded icon shape.
        static bool HasSquareCorners(Bitmap image, Rectangle art)
        {
            if (art.IsEmpty) return false;
            int inset = Math.Max(1, Math.Min(art.Width, art.Height) / 60);
            Point[] corners =
            {
                new Point(art.Left + inset, art.Top + inset), new Point(art.Right - 1 - inset, art.Top + inset),
                new Point(art.Left + inset, art.Bottom - 1 - inset), new Point(art.Right - 1 - inset, art.Bottom - 1 - inset)
            };
            return corners.All(p => image.GetPixel(p.X, p.Y).A > 200);
        }

        static Bitmap Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var source = path.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? OpenIcns(path) : new Bitmap(path))
                {
                    // Apple's icons have transparent padding and already carry the rounded shape. Some community
                    // icons are full-bleed squares: their corners are opaque. Those are masked to the same shape.
                    Rectangle art = ArtBounds(source);
                    bool fullBleed = HasSquareCorners(source, art);

                    Rectangle crop;
                    if (fullBleed)
                    {
                        // Use exactly the square artwork, centered, and drop the padding around it.
                        int side = Math.Min(art.Width, art.Height);
                        crop = new Rectangle(art.Left + (art.Width - side) / 2, art.Top + (art.Height - side) / 2, side, side);
                    }
                    else if (art.IsEmpty)
                    {
                        crop = new Rectangle(0, 0, source.Width, source.Height);
                    }
                    else
                    {
                        // Crop to the artwork itself, not a fixed margin: Apple's icons carry padding, but an icon like
                        // Brave fills its whole image, and a fixed trim would cut its rounded corners off.
                        int side = Math.Min(Math.Max(art.Width, art.Height), Math.Min(source.Width, source.Height));
                        int x = Math.Max(0, Math.Min(source.Width - side, art.Left + art.Width / 2 - side / 2));
                        int y = Math.Max(0, Math.Min(source.Height - side, art.Top + art.Height / 2 - side / 2));
                        crop = new Rectangle(x, y, side, side);
                    }

                    var result = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(result))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        if (!fullBleed)
                        {
                            g.DrawImage(source, new Rectangle(0, 0, Size, Size), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel);
                        }
                        else
                        {
                            // Scale first, then paint it through a rounded-rectangle path so the corners are smooth.
                            using (var square = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb))
                            {
                                using (Graphics sg = Graphics.FromImage(square))
                                {
                                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                    sg.DrawImage(source, new Rectangle(0, 0, Size, Size), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel);
                                }
                                using (GraphicsPath shape = Layered.RoundedRect(new RectangleF(0.5f, 0.5f, Size - 1, Size - 1), Size * 0.225f))
                                using (var brush = new TextureBrush(square))
                                    g.FillPath(brush, shape);
                            }
                        }
                    }
                    return result;
                }
            }
            catch { return null; }
        }
    }
}
