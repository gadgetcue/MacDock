// Settings text: same call shapes as TextRenderer, but drawn with GDI+ grayscale anti-aliasing (no ClearType colour fringes),
// matching the rest of MacDock's text (see Typography.Prepare).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MacDock
{
    static class SetText
    {
        static StringFormat Fmt(TextFormatFlags f)
        {
            var sf = new StringFormat(StringFormat.GenericTypographic);
            sf.Alignment = (f & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : (f & TextFormatFlags.Right) != 0 ? StringAlignment.Far : StringAlignment.Near;
            sf.LineAlignment = (f & TextFormatFlags.VerticalCenter) != 0 ? StringAlignment.Center : (f & TextFormatFlags.Bottom) != 0 ? StringAlignment.Far : StringAlignment.Near;
            sf.Trimming = (f & TextFormatFlags.EndEllipsis) != 0 ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            if ((f & TextFormatFlags.WordBreak) == 0) sf.FormatFlags |= StringFormatFlags.NoWrap;
            sf.HotkeyPrefix = (f & TextFormatFlags.NoPrefix) != 0 ? System.Drawing.Text.HotkeyPrefix.None : System.Drawing.Text.HotkeyPrefix.Show;
            return sf;
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c, TextFormatFlags f)
        {
            if (string.IsNullOrEmpty(text)) return;
            var old = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var sf = Fmt(f)) using (var b = new SolidBrush(c)) g.DrawString(text, font, b, r, sf);
            g.TextRenderingHint = old;
        }
        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c, Color back, TextFormatFlags f) { DrawText(g, text, font, r, c, f); }

        public static Size MeasureText(Graphics g, string text, Font font, Size box, TextFormatFlags f)
        {
            using (var sf = Fmt(f))
            {
                SizeF s = g.MeasureString(text, font, new SizeF(box.Width == int.MaxValue ? 100000 : box.Width, box.Height <= 0 ? 100000 : box.Height), sf);
                return new Size((int)Math.Ceiling(s.Width), (int)Math.Ceiling(s.Height));
            }
        }
        public static Size MeasureText(string text, Font font, Size box, TextFormatFlags f)
        {
            using (var bmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(bmp)) return MeasureText(g, text, font, box, f);
        }
    }
}
