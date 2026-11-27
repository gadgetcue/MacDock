// The owner's donation card: UPI payment QR on white, masked UPI ID on a crimson card.
// QR rows and ID are copied from the owner's existing card; never edit them by guesswork.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacDock
{
    static class DonationCard
    {
        public const string UpiMasked = "\u00B7\u00B7\u00B7\u00B71936@kotakbank";
        public const string Bank = "Kotak811";
        public const string Hint = "Scan to pay with any UPI app";
        public const int Width = 246, Height = 338; // logical pixels: QR 210 + 18 each side, card 308 + footer tab 30

        static readonly string[] Qr =
        {
            "#######..#...#..#.#..##.#.#######",
            "#.....#...#####...#...#...#.....#",
            "#.###.#.#.####..##..###...#.###.#",
            "#.###.#.#.###.###...#.###.#.###.#",
            "#.###.#.##.....#..####.#..#.###.#",
            "#.....#.####.#####..##....#.....#",
            "#######.#.#.#.#.#.#.#.#.#.#######",
            "........###..#.#.#####.##........",
            "#.#####....#.#.#####......#####..",
            "#.##.#...#..#.##.#.###.##.#...#.#",
            "#.....##..#.#..#.#.#.#...###..###",
            "#..#...#......#.#....##.#..#.##.#",
            "########.###...#....###.#####..#.",
            ".#..#...##.######...#...#..##.###",
            "#..#..##..#...#..#.#....#..#.#.#.",
            "...#.#.##...###.###...#.##...#.#.",
            "#....###.#.#.#...#.#..#.##..####.",
            "##.##...#.#......#..#..#.##......",
            ".#..#.#.....####.##..#..#...###..",
            "...###.####.#.#.##.####.##..#.###",
            "###.#.#....###.##........#####.#.",
            "###..#......#..#..#.#####.#.#.###",
            "#.....###.#....###..##.##.#.#..#.",
            "#..##...##.###.#....##.#.#..#.##.",
            "#.##..##..#.#####...###.#####.###",
            "........#.###.....#..#.##...#...#",
            "#######..###.##...#....##.#.#..#.",
            "#.....#.##.#....#.#.##.##...#.#.#",
            "#.###.#.###.#..#..###...#####....",
            "#.###.#.#..##..##.##..##...###...",
            "#.###.#.#.###....#.#.###.###.....",
            "#.....#..##..#..#.#..##.#...###..",
            "#######.#.##.#..#.#..#.###..#.##.",
        };

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // Draws the card with its top-left corner at (x, y), u = pixels per logical pixel.
        public static void Draw(Graphics g, float x, float y, float u)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Color crimson = Color.FromArgb(196, 18, 72), deep = Color.FromArgb(120, 14, 52), dark = Color.FromArgb(28, 10, 18);
            float k = 72f / g.DpiY; // points per pixel
            var card = new RectangleF(x, y, Width * u, 308 * u);
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var left = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
            {
                var tab = new RectangleF(x + 20 * u, card.Bottom - 18 * u, card.Width - 40 * u, 48 * u);
                using (var p = Round(tab, 14 * u)) using (var b = new SolidBrush(crimson)) g.FillPath(b, p);
                g.DrawString(Bank, Typography.Text(11.5f * u * k), Brushes.White, new RectangleF(tab.X, card.Bottom + 6 * u, tab.Width, 22 * u), center);
                using (var p = Round(card, 20 * u))
                {
                    using (var b = new LinearGradientBrush(new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2), dark, crimson, LinearGradientMode.Vertical))
                    {
                        b.InterpolationColors = new ColorBlend { Colors = new[] { dark, deep, crimson }, Positions = new[] { 0f, 0.45f, 1f } };
                        g.FillPath(b, p);
                    }
                    using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), Math.Max(1f, u))) g.DrawPath(pen, p);
                }
                var plate = new RectangleF(x + 18 * u, y + 18 * u, 210 * u, 210 * u);
                using (var p = Round(plate, 14 * u)) g.FillPath(Brushes.White, p);
                int n = Qr.Length;
                int m = Math.Max(1, (int)(plate.Width / (n + 5)));
                float ox = (float)Math.Round(plate.X + (plate.Width - m * n) / 2f), oy = (float)Math.Round(plate.Y + (plate.Height - m * n) / 2f);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                        if (Qr[r][c] == '#') g.FillRectangle(Brushes.Black, ox + c * m, oy + r * m, m, m);
                float ty = plate.Bottom + 14 * u;
                using (var b = new SolidBrush(Color.FromArgb(225, 255, 255, 255)))
                    g.DrawString("UPI ID:  " + UpiMasked, Typography.Text(12f * u * k), b, x + 18 * u, ty + 8 * u, left);
            }
            g.Restore(state);
        }
    }

    // The card centred in a Settings page.
    sealed class DonationBox : Control
    {
        public DonationBox()
        {
            DoubleBuffered = true; BackColor = SetUi.Content;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Height = (int)Math.Ceiling(DonationCard.Height * g.DpiY / 96f) + 8;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float u = e.Graphics.DpiY / 96f;
            DonationCard.Draw(e.Graphics, (float)Math.Round((Width - DonationCard.Width * u) / 2f), 4, u);
        }
    }
}