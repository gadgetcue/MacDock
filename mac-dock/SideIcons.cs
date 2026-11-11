// Settings sidebar icons: flat dark rounded tile + icon-font glyph in a per-category accent colour, drawn as a centred vector path (grayscale AA, no ClearType fringes).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MacDock
{
    static class SideIcons
    {
        static readonly Color Blue = Color.FromArgb(56, 142, 255), Grey = Color.FromArgb(172, 172, 180), Green = Color.FromArgb(76, 217, 100),
            Orange = Color.FromArgb(255, 149, 10), Red = Color.FromArgb(255, 69, 58), Pink = Color.FromArgb(255, 95, 150), White = Color.White;

        public static bool Accent(string key, out Color c)
        {
            c = Blue;
            switch (key)
            {
                case "general": case "dock": case "menubar": case "sys-about": c = Grey; return true;
                case "appearance": c = White; return true;
                case "icons": c = Orange; return true;
                case "sys-power": case "sys-time": c = Green; return true;
                case "sys-notif": c = Red; return true;
                case "sys-personal": c = Pink; return true;
                case "launchpad": case "sys-display": case "sys-network": case "sys-privacy": case "sys-apps": case "sys-accounts": case "sys-update": case "sys-files": return true;
            }
            return false;
        }

        // Flat tile (no outline) + glyph ink ~15.5px, centred on its real bounds. Returns false if the key has no accent/glyph.
        public static bool Draw(Graphics g, string key, RectangleF r, string glyph, string fontName)
        {
            Color c;
            if (fontName == null || !Accent(key, out c)) return false;
            switch (key) { case "launchpad": glyph = ""; break; case "menubar": glyph = ""; break; case "icons": glyph = ""; break; case "sys-privacy": glyph = ""; break; case "sys-apps": glyph = ""; break; }
            if (string.IsNullOrEmpty(glyph)) return false;
            var old = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var path = SetUi.Round(r, 6.5f))
            using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(36, 36, 38), Color.FromArgb(24, 24, 26), 90f))
                g.FillPath(lg, path);
            try
            {
                using (var gp = new GraphicsPath())
                using (var fam = new FontFamily(fontName))
                using (var sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    gp.AddString(glyph, fam, 0, 100f, new PointF(0, 0), sf);
                    RectangleF b = gp.GetBounds();
                    if (b.Width > 0.1f && b.Height > 0.1f)
                    {
                        float s = 15.5f / Math.Max(b.Width, b.Height);
                        var m = new Matrix();
                        m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                        m.Scale(s, s, MatrixOrder.Append);
                        m.Translate(r.X + r.Width / 2f, r.Y + r.Height / 2f, MatrixOrder.Append);
                        gp.Transform(m);
                        using (var br = new SolidBrush(c)) g.FillPath(br, gp);
                    }
                }
            }
            catch { }
            g.Restore(old);
            return true;
        }
    }
}
