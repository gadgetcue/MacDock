// One place for fonts, so the typeface is easy to change.
//
// Apple's SF Pro is licensed for designing and building software for Apple platforms only, so it is not
// bundled or used here. The default is Inter (open source, SIL OFL, built to be a close SF Pro relative).
// Inter's .ttf files are loaded from a "fonts" folder next to MacDock.exe (no install needed); if that folder
// is missing the installed Inter is used, then Segoe UI Variable, which ships with Windows 11. To use another font, add this line to
// dock.txt:   @font = Font Name

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MacDock
{
    enum Weight { Regular, Medium, Semibold }

    static class Typography
    {
        static readonly string[] Preferred = { "Inter", "Segoe UI Variable Text", "Segoe UI" };
        static readonly string[] PreferredDisplay = { "Inter", "Segoe UI Variable Display", "Segoe UI" };

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);
        const uint FR_PRIVATE = 0x10;

        static readonly List<PrivateFontCollection> privateList = new List<PrivateFontCollection>();
        static readonly Dictionary<string, FontFamily> privateMap = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        static bool privateFonts { get { return privateList.Count > 0; } } // kept alive: fonts made from these families skip the slow system-wide font lookup
        static readonly HashSet<string> bundled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Makes the .ttf files in <exe folder>\fonts available to this process only (GDI and GDI+).
        static void LoadBundled()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
                if (!Directory.Exists(dir)) return;
                // One collection per file: GDI+ merges several static files of one family wrongly (Regular drew as the Bold face).
                foreach (string file in Directory.GetFiles(dir, "*.ttf"))
                {
                    if (AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero) == 0) continue;
                    var pc = new PrivateFontCollection();
                    pc.AddFontFile(file);
                    privateList.Add(pc);
                    FontFamily ff = pc.Families[0];
                    string st = Path.GetFileNameWithoutExtension(file).EndsWith("Bold", StringComparison.OrdinalIgnoreCase) && string.Equals(ff.Name, "Inter", StringComparison.OrdinalIgnoreCase) ? "Bold" : "Regular";
                    privateMap[ff.Name + "|" + st] = ff;
                    bundled.Add(ff.Name);
                }
            }
            catch { }
        }

        static readonly Dictionary<string, bool> probe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static string family, displayFamily;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        // Reads "@font = Name" from dock.txt, if present. Must run before any font is requested.
        public static void Configure(string configPath)
        {
            LoadBundled(); Program.T("typo-bundled");
            string wanted = null;
            try
            {
                foreach (string raw in File.ReadAllLines(configPath))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("@font", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    wanted = (eq > 0 ? line.Substring(eq + 1) : line.Substring(5)).Trim();
                }
            }
            catch { }

            Program.T("typo-read"); family = Pick(wanted, Preferred); Program.T("typo-pick");
            displayFamily = string.IsNullOrEmpty(wanted) ? Pick(null, PreferredDisplay) : family;
        }

        // ---- Fast font lookups. GDI+ resolves a family *by name* by walking every installed font; with thousands of user fonts that took
        // 6-28 s per lookup (the dock froze on start and on folder clicks). GDI answers the same question from the system font cache instantly.
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetTextFaceW(IntPtr dc, int n, System.Text.StringBuilder face);
        public static bool Installed(string name) { lock (sysProbe) { bool ok; if (!sysProbe.TryGetValue(name, out ok)) sysProbe[name] = ok = GdiHas(name); return ok; } }
        static bool GdiHas(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 31) return false;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero), f = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                f = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 0, 0, name);
                if (f == IntPtr.Zero) return false;
                old = SelectObject(dc, f);
                var sb = new System.Text.StringBuilder(64);
                GetTextFaceW(dc, sb.Capacity, sb);
                return string.Equals(sb.ToString(), name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { if (old != IntPtr.Zero) SelectObject(dc, old); if (f != IntPtr.Zero) DeleteObject(f); DeleteDC(dc); }
        }

        // System fonts that MacDock draws symbols/emoji with, loaded straight from their files (no by-name lookup).
        static readonly Dictionary<string, string> SystemFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Segoe UI Emoji", "seguiemj.ttf" }, { "Segoe UI Symbol", "seguisym.ttf" }, { "Segoe MDL2 Assets", "segmdl2.ttf" },
            { "Segoe Fluent Icons", "SegoeIcons.ttf" }, { "Segoe UI", "segoeui.ttf" },
        };
        static readonly Dictionary<string, FontFamily> sysFamilies = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        public static FontFamily SystemFamily(string name)
        {
            lock (sysFamilies)
            {
                FontFamily ff;
                if (sysFamilies.TryGetValue(name, out ff)) return ff;
                string file;
                if (SystemFiles.TryGetValue(name, out file))
                {
                    try
                    {
                        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
                        if (File.Exists(path)) { var pc = new PrivateFontCollection(); pc.AddFontFile(path); privateList.Add(pc); ff = pc.Families[0]; }
                    }
                    catch { ff = null; }
                }
                // Any other installed family: find its file in the Fonts registry (per-user and machine) and load that file privately.
                if (ff == null) ff = FromRegistry(name);
                if (ff == null) ff = new FontFamily(name); // last resort (slow on machines with many fonts), cached so it is paid once
                sysFamilies[name] = ff;
                return ff;
            }
        }
        static FontFamily FromRegistry(string name)
        {
            try
            {
                var roots = new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine };
                foreach (var root in roots)
                    using (var k = root.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
                    {
                        if (k == null) continue;
                        foreach (string v in k.GetValueNames())
                        {
                            string face = v; int p = face.IndexOf(" ("); if (p > 0) face = face.Substring(0, p);
                            bool match = false;
                            foreach (string part in face.Split('&')) if (string.Equals(part.Trim(), name, StringComparison.OrdinalIgnoreCase)) match = true;
                            if (!match) continue;
                            string file = k.GetValue(v) as string; if (string.IsNullOrEmpty(file)) continue;
                            if (!Path.IsPathRooted(file)) file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
                            if (!File.Exists(file)) continue;
                            var pc = new PrivateFontCollection(); pc.AddFontFile(file);
                            foreach (FontFamily f in pc.Families) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) { privateList.Add(pc); return f; }
                        }
                    }
            }
            catch { }
            return null;
        }
        // ---- Glyph coverage. When text holds a character the font lacks, GDI+ searches every installed font for a fallback; with
        // thousands of fonts that took 3-10 s for one label (e.g. "AMD Software꞉ Adrenalin Edition", U+A789). Safe() swaps known
        // look-alikes for plain characters, and FontFor() picks Segoe UI (loaded from its file) when Inter cannot draw the text.
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetGlyphIndicesW(IntPtr dc, string s, int n, [Out] ushort[] idx, uint flags);
        static readonly Dictionary<string, Dictionary<char, bool>> covers = new Dictionary<string, Dictionary<char, bool>>(StringComparer.OrdinalIgnoreCase);
        static bool Covers(string face, string s)
        {
            Dictionary<char, bool> map;
            lock (covers)
            {
                if (!covers.TryGetValue(face, out map)) covers[face] = map = new Dictionary<char, bool>();
                var todo = new List<char>();
                foreach (char ch in s) { bool ok; if (!map.TryGetValue(ch, out ok)) { if (!todo.Contains(ch)) todo.Add(ch); } else if (!ok) return false; }
                if (todo.Count == 0) return true;
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero), f = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    f = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, face); old = SelectObject(dc, f);
                    string q = new string(todo.ToArray()); var idx = new ushort[q.Length];
                    GetGlyphIndicesW(dc, q, q.Length, idx, 1 /*GGI_MARK_NONEXISTING_GLYPHS*/);
                    for (int i = 0; i < q.Length; i++) map[q[i]] = idx[i] != 0xFFFF || char.IsWhiteSpace(q[i]) || char.IsControl(q[i]);
                }
                catch { foreach (char ch in todo) map[ch] = true; }
                finally { if (old != IntPtr.Zero) SelectObject(dc, old); if (f != IntPtr.Zero) DeleteObject(f); DeleteDC(dc); }
                foreach (char ch in s) if (!map[ch]) return false;
                return true;
            }
        }
        static readonly Dictionary<char, string> lookalike = new Dictionary<char, string>
        {
            { '\uA789', ":" }, { '\u2236', ":" }, { '\uFF1A', ":" }, { '\u2215', "/" }, { '\u2044', "/" }, { '\u2010', "-" }, { '\u2011', "-" },
            { '\u2012', "-" }, { '\u2212', "-" }, { '\u00A0', " " }, { '\u2009', " " }, { '\u200B', "" }, { '\u200E', "" }, { '\u200F', "" }, { '\uFEFF', "" },
        };
        static readonly Dictionary<string, string> safeCache = new Dictionary<string, string>();
        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            lock (safeCache)
            {
                string r;
                if (safeCache.TryGetValue(s, out r)) return r;
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (char ch in s) { string rep; if (lookalike.TryGetValue(ch, out rep)) sb.Append(rep); else sb.Append(ch); }
                r = sb.ToString();
                if (safeCache.Count > 4000) safeCache.Clear();
                safeCache[s] = r;
                return r;
            }
        }
        static readonly Dictionary<Font, Font> segoeTwin = new Dictionary<Font, Font>();
        // The font to draw s with: f itself when its family has every glyph, else a Segoe UI twin of the same size and style.
        public static Font FontFor(Font f, string s)
        {
            if (f == null || string.IsNullOrEmpty(s) || Covers(f.FontFamily.Name, s)) return f;
            if (!Covers("Segoe UI", s)) return f;
            lock (segoeTwin)
            {
                Font t;
                if (!segoeTwin.TryGetValue(f, out t)) segoeTwin[f] = t = new Font(SystemFamily("Segoe UI"), f.Size, f.Style, f.Unit);
                return t;
            }
        }

        public static Font SystemFont(string name, float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
        {
            return new Font(SystemFamily(name), size, style, unit);
        }

        static readonly Dictionary<string, bool> sysProbe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static bool SystemHas(string name)
        {
            lock (sysProbe)
            {
                bool ok;
                if (sysProbe.TryGetValue(name, out ok)) return ok;
                ok = GdiHas(name);
                sysProbe[name] = ok;
                return ok;
            }
        }

        static bool Has(string name)
        {
            if (bundled.Contains(name)) return true; // Inter ships with the dock: skip enumerating every installed font
            // Ask GDI+ for the family by name (cheap, cached) instead of enumerating every installed font, which took seconds on a busy machine.
            lock (probe)
            {
                bool ok;
                if (probe.TryGetValue(name, out ok)) return ok;
                ok = GdiHas(name);
                probe[name] = ok;
                return ok;
            }
        }

        static string Pick(string wanted, string[] fallbacks)
        {
            if (!string.IsNullOrEmpty(wanted) && Has(wanted)) return wanted;
            foreach (string f in fallbacks) if (Has(f)) return f;
            return "Segoe UI";
        }

        // Fonts for small UI text (menus, labels, status items).
        public static Font Text(float points, Weight weight = Weight.Regular) { return Get(family, points, weight); }
        public static Font Copy(float points, Weight weight = Weight.Regular) { return (Font)Get(family, points, weight).Clone(); } // for callers that Dispose: Text() returns a shared cached Font that must never be disposed

        // ----- type scale: one set of named roles (Inter) so hierarchy is the same in every window -----
        public static Font Title() { return Text(15f, Weight.Semibold); }      // page / window titles
        public static Font Heading() { return Text(10f, Weight.Semibold); }    // group headings, list titles
        public static Font Menu() { return Text(11f); }                        // menu and sidebar items
        public static Font Body() { return Text(10.5f); }                      // rows and labels
        public static Font Secondary() { return Text(10f); }                   // values, shortcuts, subtitles
        public static Font Caption() { return Text(9f); }                      // footnotes, small labels
        public static Font Label() { return Text(9f, Weight.Semibold); }       // SECTION LABELS

        // Fonts for large text (clock peek, big numbers).
        public static Font Display(float points, Weight weight = Weight.Regular) { return Get(displayFamily, points, weight); }

        static Font Get(string baseFamily, float points, Weight weight)
        {
            if (baseFamily == null) { Configure(null); baseFamily = family; }
            string key = baseFamily + "|" + points + "|" + weight;
            Font font;
            lock (cache)
            {
                if (cache.TryGetValue(key, out font)) return font;

                string name = baseFamily;
                FontStyle style = FontStyle.Regular;
                if (weight == Weight.Medium && Has(baseFamily + " Medium")) name = baseFamily + " Medium";
                else if (weight == Weight.Semibold)
                {
                    if (Has(baseFamily + " SemiBold")) name = baseFamily + " SemiBold";
                    else if (Has(baseFamily + " Semibold")) name = baseFamily + " Semibold";
                    else if (Has(baseFamily + " Medium")) name = baseFamily + " Medium";
                    else style = FontStyle.Bold;
                }
                FontFamily priv = null;
                if (privateFonts && (bundled.Contains(name) || !SystemHas(name))) privateMap.TryGetValue(name + "|" + (style == FontStyle.Bold ? "Bold" : "Regular"), out priv);
                font = priv != null ? new Font(priv, points, style, GraphicsUnit.Point) : new Font(SystemFamily(name), points, style, GraphicsUnit.Point);
                cache[key] = font;
                return font;
            }
        }

        // Text is drawn with grayscale anti-aliasing and no grid fitting, which keeps letter spacing even.
        public static void Prepare(Graphics g)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        // Tight format: no extra padding, trimmed with an ellipsis, vertically centered.
        public static StringFormat Format(StringAlignment horizontal = StringAlignment.Near)
        {
            var f = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = horizontal,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        // Digits share one width, so a clock or a percentage never jitters as the numbers change.
        public static float MeasureTabular(Graphics g, string text, Font font)
        {
            float cell = DigitWidth(g, font), width = 0;
            foreach (char c in text) width += char.IsDigit(c) ? cell : CharWidth(g, c, font);
            return width;
        }

        public static void DrawTabular(Graphics g, string text, Font font, Brush brush, float x, float top, float height)
        {
            float cell = DigitWidth(g, font);
            StringFormat format = Format();
            foreach (char c in text)
            {
                string s = c.ToString();
                float advance = char.IsDigit(c) ? cell : CharWidth(g, c, font);
                float offset = char.IsDigit(c) ? (cell - CharWidth(g, c, font)) / 2f : 0; // center digits in their cell
                if (c != ' ') g.DrawString(s, font, brush, new RectangleF(x + offset, top, advance + 4, height), format);
                x += advance;
            }
        }

        // Glyph widths never change for a given font, so measure each one once.
        static readonly Dictionary<Font, Dictionary<char, float>> widths = new Dictionary<Font, Dictionary<char, float>>();
        static readonly Dictionary<Font, float> digitWidths = new Dictionary<Font, float>();

        static float DigitWidth(Graphics g, Font font)
        {
            float w;
            if (digitWidths.TryGetValue(font, out w)) return w;
            for (char c = '0'; c <= '9'; c++) w = Math.Max(w, CharWidth(g, c, font));
            digitWidths[font] = w;
            return w;
        }

        static float CharWidth(Graphics g, char c, Font font)
        {
            // A space measures as zero with the typographic format, so give it a fixed fraction of the em.
            if (c == ' ') return font.Size * 1.333f * 0.28f;

            Dictionary<char, float> perFont;
            if (!widths.TryGetValue(font, out perFont)) widths[font] = perFont = new Dictionary<char, float>();
            float width;
            if (!perFont.TryGetValue(c, out width))
            {
                width = g.MeasureString(c.ToString(), font, 200, StringFormat.GenericTypographic).Width;
                perFont[c] = width;
            }
            return width;
        }
    }
}
