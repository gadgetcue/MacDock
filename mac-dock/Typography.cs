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
            LoadBundled();
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

            family = Pick(wanted, Preferred);
            displayFamily = string.IsNullOrEmpty(wanted) ? Pick(null, PreferredDisplay) : family;
        }

        static readonly Dictionary<string, bool> sysProbe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static bool SystemHas(string name)
        {
            lock (sysProbe)
            {
                bool ok;
                if (sysProbe.TryGetValue(name, out ok)) return ok;
                try { using (new FontFamily(name)) ok = true; } catch { ok = false; }
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
                try { using (new FontFamily(name)) ok = true; } catch { ok = false; }
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
                if (privateFonts && !SystemHas(name)) privateMap.TryGetValue(name + "|" + (style == FontStyle.Bold ? "Bold" : "Regular"), out priv);
                font = priv != null ? new Font(priv, points, style, GraphicsUnit.Point) : new Font(name, points, style, GraphicsUnit.Point);
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
