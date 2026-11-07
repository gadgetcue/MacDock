// Hover popup above a dock icon: a live DWM thumbnail + title for each of an app's windows. Click one to focus it.
// Glass is a layered window (the same Launchpad glass as the dock menu). DWM thumbnails can't render onto a layered
// window, so a second, plain topmost window sits above it, clipped to just the thumbnail tiles.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    sealed class WindowPreview : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)]
        struct THUMBPROPS
        {
            public int dwFlags; public Win32.RECT rcDestination, rcSource; public byte opacity; public int fVisible, fSourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

        const int TileW = 260, ThumbH = 160, TitleH = 26, Pad = 12, Gap = 10, Radius = 14;

        sealed class Tile { public IntPtr Window, Thumb; public Rectangle Cell, Box; public string Title; public bool Minimized; }

        readonly List<Tile> tiles = new List<Tile>();
        readonly Bitmap appIcon;
        readonly Action<IntPtr> focus;
        readonly Action closed;
        readonly Func<Point, bool> keepOpen; // true while the cursor is over the dock icon this popup belongs to
        readonly Font font = Typography.Text(9f);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ThumbHost host;
        Surface surface;
        Bitmap backdrop;
        int hover = -1;
        bool wasDown = true;

        public WindowPreview(List<IntPtr> windows, Bitmap icon, Point anchorTopCenter, Action<IntPtr> focus, Func<Point, bool> keepOpen, Action closed)
        {
            this.appIcon = icon; this.focus = focus; this.keepOpen = keepOpen; this.closed = closed;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;

            var sb = new StringBuilder(256);
            foreach (IntPtr h in windows)
            {
                if (!NativeMethods.IsWindow(h)) continue;
                sb.Clear(); Win32.GetWindowText(h, sb, sb.Capacity);
                tiles.Add(new Tile { Window = h, Title = sb.ToString(), Minimized = NativeMethods.IsIconic(h) });
            }
            int n = Math.Max(1, tiles.Count);
            Rectangle area = Screen.FromPoint(anchorTopCenter).Bounds;
            int perRow = Math.Max(1, Math.Min(n, (area.Width - 40 - Pad * 2 + Gap) / (TileW + Gap)));
            int rowsN = (n + perRow - 1) / perRow;
            int cellH = ThumbH + TitleH;
            int width = Pad * 2 + perRow * TileW + (perRow - 1) * Gap;
            int height = Pad * 2 + rowsN * cellH + (rowsN - 1) * Gap;
            for (int i = 0; i < tiles.Count; i++)
            {
                int r = i / perRow, c = i % perRow;
                int rowCount = Math.Min(perRow, n - r * perRow); // centre a short last row
                int x0 = Pad + (perRow - rowCount) * (TileW + Gap) / 2;
                tiles[i].Cell = new Rectangle(x0 + c * (TileW + Gap), Pad + r * (cellH + Gap), TileW, cellH);
            }
            int left = Math.Max(area.Left + 4, Math.Min(anchorTopCenter.X - width / 2, area.Right - width - 4));
            int top = Math.Max(area.Top + 4, anchorTopCenter.Y - height);
            Bounds = new Rectangle(left, top, width, height);

            try
            {
                using (var shot = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (Graphics sg = Graphics.FromImage(shot)) sg.CopyFromScreen(Bounds.Location, Point.Empty, Bounds.Size);
                    backdrop = GlassPanel.BlurBitmap(shot, 20);
                }
            }
            catch { backdrop = null; }

            host = new ThumbHost();
            timer.Interval = 30;
            timer.Tick += (s, e) => Poll();
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public new void Show()
        {
            base.Show();
            host.Bounds = Bounds;
            host.Show();
            SetupThumbs();
            Redraw();
            timer.Start();
        }

        void SetupThumbs()
        {
            IntPtr region = CreateRoundRectRgn(0, 0, 0, 0, 0, 0); // empty
            foreach (Tile t in tiles)
            {
                SIZE src;
                IntPtr th;
                if (DwmRegisterThumbnail(host.Handle, t.Window, out th) != 0) { t.Minimized = true; continue; }
                if (DwmQueryThumbnailSourceSize(th, out src) != 0 || src.cx <= 0 || src.cy <= 0) { DwmUnregisterThumbnail(th); t.Minimized = true; continue; }
                t.Thumb = th;
                t.Box = Box(t, src.cx, src.cy);
                var p = new THUMBPROPS
                {
                    dwFlags = 0x1 | 0x4 | 0x8, // destination, opacity, visible
                    rcDestination = new Win32.RECT { Left = t.Box.Left, Top = t.Box.Top, Right = t.Box.Right, Bottom = t.Box.Bottom },
                    opacity = 255, fVisible = 1
                };
                DwmUpdateThumbnailProperties(th, ref p);
                IntPtr r = CreateRoundRectRgn(t.Box.Left, t.Box.Top, t.Box.Right + 1, t.Box.Bottom + 1, 10, 10);
                CombineRgn(region, region, r, 2); // RGN_OR
                DeleteObject(r);
            }
            SetWindowRgn(host.Handle, region, true);
        }

        // Fits the window's picture inside the tile's thumbnail area, keeping its aspect ratio.
        static Rectangle Box(Tile t, int sw, int sh)
        {
            int areaW = TileW - 12, areaH = ThumbH - 12;
            double s = Math.Min(Math.Min((double)areaW / sw, (double)areaH / sh), 1.0);
            int w = Math.Max(1, (int)(sw * s)), h = Math.Max(1, (int)(sh * s));
            return new Rectangle(t.Cell.Left + (TileW - w) / 2, t.Cell.Top + 6 + (areaH - h) / 2, w, h);
        }

        void Poll()
        {
            NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
            Point sp = new Point(p.X, p.Y);
            Rectangle zone = Rectangle.Inflate(Bounds, 6, 0);
            zone.Height += 50; // the gap down to the dock icon keeps the popup open
            bool over = Bounds.Contains(sp);
            if (!over && !keepOpen(sp) && !zone.Contains(sp)) { Close(); return; }

            int idx = -1;
            if (over)
            {
                Point c = PointToClient(sp);
                for (int i = 0; i < tiles.Count; i++) if (tiles[i].Cell.Contains(c)) { idx = i; break; }
            }
            if (idx != hover) { hover = idx; Redraw(); }

            bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !wasDown && idx >= 0)
            {
                IntPtr w = tiles[idx].Window;
                Close();
                focus(w);
                return;
            }
            wasDown = down;
            if ((Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0) Close();
        }

        void Redraw()
        {
            if (!IsHandleCreated) return;
            if (surface == null) surface = new Surface(Width, Height);
            bool light = Theme.Light;
            Color text = light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(240, 240, 244);
            Color tileFill = light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(34, 255, 255, 255);
            Color hotFill = light ? Color.FromArgb(46, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);
            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Typography.Prepare(g);
                GlassPanel.DrawLaunchpadGlass(g, new Rectangle(0, 0, Width - 1, Height - 1), Radius, backdrop, Point.Empty);
                var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                for (int i = 0; i < tiles.Count; i++)
                {
                    Tile t = tiles[i];
                    using (GraphicsPath gp = Layered.RoundedRect(new RectangleF(t.Cell.Left, t.Cell.Top, t.Cell.Width, t.Cell.Height), 10))
                    using (var b = new SolidBrush(i == hover ? hotFill : tileFill))
                        g.FillPath(b, gp);
                    if (t.Minimized && appIcon != null) // nothing to mirror while minimized: show the app icon instead
                        g.DrawImage(appIcon, new Rectangle(t.Cell.Left + (TileW - 56) / 2, t.Cell.Top + (ThumbH - 56) / 2, 56, 56));
                    using (var tb = new SolidBrush(text))
                        g.DrawString(t.Title, font, tb, new RectangleF(t.Cell.Left + 10, t.Cell.Top + ThumbH - 2, TileW - 20, TitleH), fmt);
                }
            }
            surface.Present(Handle, Left, Top, 255);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            foreach (Tile t in tiles) if (t.Thumb != IntPtr.Zero) { DwmUnregisterThumbnail(t.Thumb); t.Thumb = IntPtr.Zero; }
            host.Close();
            base.OnFormClosed(e);
            if (closed != null) closed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); host.Dispose(); if (surface != null) surface.Dispose(); if (backdrop != null) backdrop.Dispose(); }
            base.Dispose(disposing);
        }

        sealed class ThumbHost : Form
        {
            public ThumbHost()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black;
            }
            protected override CreateParams CreateParams
            {
                get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20; return cp; } // tool, no-activate, transparent to mouse
            }
            protected override bool ShowWithoutActivation { get { return true; } }
        }
    }
}
