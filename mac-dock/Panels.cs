// Floating glass panels for the status icons (Wi-Fi, Bluetooth, Sound, Battery, Control Center, Profile).
// One GlassPanel class draws every panel from a list of rows, so they all share material, radius and motion.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacDock
{
    enum RowKind { Title, Big, Info, Header, Item, Button, Slider, Separator, Tile, Chip, Media, Round }

    sealed class PanelRow
    {
        public RowKind Kind;
        public string Text;
        public string Sub;
        public bool Checked;
        public bool KeepOpen;            // item/button keeps the panel open after a click
        public bool? Toggle;             // non-null = draws a switch at the right edge
        public double Value;             // slider position, 0..1
        public Action Click;
        public Action<bool> Toggled;
        public Action<double> ValueChanged;
        public string Glyph;             // Tile icon: wifi, bluetooth, airplane, airdrop, dark, moon, warm
        public bool Active, Half, WhiteWhenOn, Sun, Card;
        public double Anim;              // 0..1 eased toward Active, drives the tile colours

        public string Key;               // glass layout: identity across rebuilds, so a row glides from its old place
        public string DragId;            // glass layout, edit mode: dragging this row onto another with a DragId reorders them
        public Action<int, int, bool> Resize; // edit mode resize grip: (dx, dy since press, released); (0,0,false) marks the start
        public Action<string> Drop;      // called with the DragId of the row it was dropped on
        public RectangleF Shown;         // animated position (eases toward Fixed)
        public bool HasShown;

        public Rectangle Rect;
        public Rectangle ToggleRect;
        public Rectangle Fixed;          // glass layout: position inside the panel (Rect = Fixed + gutter)
        public Rectangle ExtraRect;      // slider card: round button at the end
        public Action Extra;
        public readonly List<KeyValuePair<Rectangle, Action>> Hits = new List<KeyValuePair<Rectangle, Action>>();

        public int Height
        {
            get
            {
                switch (Kind)
                {
                    case RowKind.Title: return 38;
                    case RowKind.Big: return 66;
                    case RowKind.Info: return 24;
                    case RowKind.Header: return 26;
                    case RowKind.Item: return 38;
                    case RowKind.Button: return 32;
                    case RowKind.Slider: return Card ? 64 : 40;
                    case RowKind.Tile: return 64;
                    case RowKind.Chip: return 30;
                    case RowKind.Media: return 138;
                    case RowKind.Round: return 64;
                    default: return 12;
                }
            }
        }

        public bool Interactive
        {
            get { return Click != null || ValueChanged != null || Toggle.HasValue || Kind == RowKind.Media; }
        }

        public static PanelRow Title(string text, bool? toggle = null, Action<bool> toggled = null)
        {
            return new PanelRow { Kind = RowKind.Title, Text = text, Toggle = toggle, Toggled = toggled };
        }
        public static PanelRow Big(string text, string sub) { return new PanelRow { Kind = RowKind.Big, Text = text, Sub = sub }; }
        public static PanelRow Info(string label, string value) { return new PanelRow { Kind = RowKind.Info, Text = label, Sub = value }; }
        public static PanelRow Header(string text) { return new PanelRow { Kind = RowKind.Header, Text = text }; }
        public static PanelRow Item(string text, string sub, bool isChecked, Action click)
        {
            return new PanelRow { Kind = RowKind.Item, Text = text, Sub = sub, Checked = isChecked, Click = click };
        }
        public static PanelRow Button(string text, Action click) { return new PanelRow { Kind = RowKind.Button, Text = text, Click = click }; }
        public static PanelRow Slider(double value, Action<double> changed)
        {
            return new PanelRow { Kind = RowKind.Slider, Value = value, ValueChanged = changed };
        }
        public static PanelRow At(PanelRow r, int x, int y, int w, int h) { r.Fixed = new Rectangle(x, y, w, h); return r; }
        public static PanelRow Separator() { return new PanelRow { Kind = RowKind.Separator }; }
        public static PanelRow Tile(string title, string sub, string glyph, bool active, bool half, Action click)
        {
            return new PanelRow { Kind = RowKind.Tile, Text = title, Sub = sub, Glyph = glyph, Active = active, Anim = active ? 1 : 0, Half = half, Click = click };
        }
    }

    sealed class GlassPanel : Form
    {
        const int Gutter = 20, Radius = 18, PadX = 16, PadY = 12;

        readonly int panelWidth;
        readonly int barHeight;
        readonly Point anchor;
        readonly Action closed;
        readonly Timer timer = new Timer();
        // Opening is a slightly under-damped spring (a hint of overshoot); closing is critically damped so the
        // panel never swings back past zero.
        const double OpenOmega = 24, OpenZeta = 0.72, CloseOmega = 30, CloseZeta = 1.0;

        readonly Font body = Typography.Secondary();
        readonly Font semibold = Typography.Text(12.5f, Weight.Semibold);
        readonly Font header = Typography.Text(8.25f, Weight.Semibold);
        readonly Font big = Typography.Display(26f, Weight.Semibold);
        readonly Font small = Typography.Caption();
        readonly Font tileTitle = Typography.Heading();
        static readonly Font glyphFont = new Font("Segoe MDL2 Assets", 12f, FontStyle.Regular, GraphicsUnit.Point);
        readonly ImageAttributes fade = new ImageAttributes();
        readonly FrameClock clock = new FrameClock();
        readonly Stopwatch fadeWatch = Stopwatch.StartNew(); // times the menu-style fade; no blur window, the panel is opaque
        double fadeStart;

        List<PanelRow> rows;
        bool glass;                       // Control Center: transparent window, every tile is its own glass
        Bitmap backdrop;                  // blurred desktop snapshot behind the glass tiles
        Rectangle backdropRect;
        Bitmap content;
        Surface surface;
        int panelHeight;
        int hover = -1;
        PanelRow dragging;
        PanelRow dragRow;                 // edit mode: tile being dragged to a new place
        Point dragFrom, dragNow;
        bool dragActive;
        PanelRow resizeRow;               // edit mode: grip being dragged to resize its tile
        Point resizeFrom;

        // Spring state for open/close: runs 0 -> 1.
        Spring open = new Spring(0);
        double goal = 1;
        bool closing;
        int ticks;
        bool wasDown, wasEscape;

        public GlassPanel(int width, List<PanelRow> rows, Point anchor, int barHeight, Action closed)
        {
            this.panelWidth = width;
            this.rows = rows;
            this.anchor = anchor;
            this.barHeight = barHeight;
            this.closed = closed;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            CaptureBackdrop();
            Relayout(true);

            timer.Interval = 16;
            timer.Tick += (s, e) => Step();
            timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // layered, tool window, no-activate
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            fadeStart = fadeWatch.Elapsed.TotalMilliseconds;
            Compose();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
            MemTrim.Soon(400);
            if (closed != null) closed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                fade.Dispose();
                if (content != null) content.Dispose();
                if (surface != null) surface.Dispose();
                if (backdrop != null) backdrop.Dispose();
            }
            base.Dispose(disposing);
        }

        // Replaces the rows (used when async data such as networks arrives).
        public void SetRows(List<PanelRow> newRows)
        {
            if (IsDisposed || closing) return;
            if (glass)
            {
                var old = new Dictionary<string, RectangleF>();
                foreach (PanelRow o in rows) if (o.Key != null && o.HasShown) old[o.Key] = o.Shown;
                foreach (PanelRow n in newRows)
                {
                    RectangleF s;
                    n.Shown = n.Key != null && old.TryGetValue(n.Key, out s) ? s : (RectangleF)n.Fixed;
                    n.HasShown = true;
                }
            }
            rows = newRows;
            hover = -1;
            Relayout(false);
            Compose();
        }

        // Control Center look: no panel body. Grabs the desktop behind where the panel will sit and blurs it once;
        // each tile then shows its own slice of that blur (the window is per-pixel alpha, so DWM can't blur it).
        public void EnableGlass()
        {
            glass = true;
            Relayout(false);
        }

        void CaptureBackdrop()
        {
            try
            {
                Rectangle area = Screen.FromPoint(anchor).Bounds;
                int w = panelWidth + Gutter * 2, h = 760 + Gutter * 2; // tall enough for the LocalSend device list
                int x = Math.Max(area.Left, Math.Min(anchor.X - w / 2, area.Right - w));
                int y = anchor.Y - Gutter + 6;
                backdropRect = new Rectangle(x, y, w, Math.Min(h, area.Bottom - y));
                // Same as MenuPopup: capture a 64 px margin so the blur has real neighbours at the edges, then crop.
                Rectangle cap = Rectangle.Intersect(Rectangle.Inflate(backdropRect, 64, 64), SystemInformation.VirtualScreen);
                using (var shot = new Bitmap(cap.Width, cap.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(shot))
                        g.CopyFromScreen(cap.Location, Point.Empty, cap.Size);
                    using (Bitmap big = BlurBitmap(shot))
                    {
                        var crop = new Bitmap(backdropRect.Width, backdropRect.Height, PixelFormat.Format32bppPArgb);
                        using (Graphics cg = Graphics.FromImage(crop))
                            cg.DrawImage(big, new Rectangle(0, 0, crop.Width, crop.Height), new Rectangle(backdropRect.Left - cap.Left, backdropRect.Top - cap.Top, crop.Width, crop.Height), GraphicsUnit.Pixel);
                        backdrop = crop;
                    }
                }
            }
            catch { backdrop = null; }
        }

        internal static Bitmap BlurBitmap(Bitmap src, int divisor = 20, Rectangle? region = null)
        {
            // One pass: downscale in two steps (smooth), then a single bicubic upscale to full size.
            int sw = Math.Max(1, src.Width / divisor), sh = Math.Max(1, src.Height / divisor);
            int mw = Math.Max(sw, src.Width / 8), mh = Math.Max(sh, src.Height / 8);
            Bitmap cur = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var mid = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb))
            using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(mid)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(src, new Rectangle(0, 0, mw, mh)); }
                using (Graphics g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(mid, new Rectangle(0, 0, sw, sh)); }
                using (Graphics g = Graphics.FromImage(cur))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (region == null) g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height));
                    else
                    {   // only the part that will be seen: the glass panel
                        Rectangle rg = Rectangle.Intersect(region.Value, new Rectangle(0, 0, src.Width, src.Height));
                        float fx = (float)sw / src.Width, fy = (float)sh / src.Height;
                        g.SetClip(rg);
                        g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height), 0, 0, sw, sh, GraphicsUnit.Pixel);
                    }
                }
            }
            AcrylicFinish(cur, region);
            return cur;
        }

        // Windows acrylic's last steps after the blur: a saturation boost (~1.25) and a faint noise layer.
        static unsafe void AcrylicFinish(Bitmap bmp, Rectangle? region = null)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                IntPtr scan0 = d.Scan0; int stride = d.Stride, w = d.Width, h = d.Height;
                Rectangle rr = region.HasValue ? Rectangle.Intersect(region.Value, new Rectangle(0, 0, w, h)) : new Rectangle(0, 0, w, h);
                int fx0 = rr.Left, fx1 = rr.Right;
                System.Threading.Tasks.Parallel.For(rr.Top, rr.Bottom, y =>
                {
                    byte* p = (byte*)scan0 + (long)y * stride + fx0 * 4L;
                    uint seed = 2463534242u ^ ((uint)(y + 1) * 2654435761u); if (seed == 0) seed = 1;
                    for (int x = fx0; x < fx1; x++, p += 4)
                    {
                        if (p[3] != 255) continue;
                        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                        int n = (int)(seed & 7) - 3; // +-3 of 255: about 1% grain
                        int b = p[0], g = p[1], r = p[2];
                        int luma = (r * 54 + g * 183 + b * 19) >> 8;
                        int nr = luma + ((r - luma) * 5 >> 2) + n, ng = luma + ((g - luma) * 5 >> 2) + n, nb = luma + ((b - luma) * 5 >> 2) + n;
                        p[2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
                        p[1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                        p[0] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                    }
                });
            }
            finally { bmp.UnlockBits(d); }
        }

        public void BeginClose()
        {
            if (closing) return;
            closing = true;
            goal = 0;
            fadeStart = fadeWatch.Elapsed.TotalMilliseconds;
            clock.Reset();
            timer.Interval = 16;
        }

        // ---------- layout ----------

        void Relayout(bool initial)
        {
            // Half-width tiles sit in pairs (two in a row); everything else is full width.
            // Reference spacing: tiles and cards are 64 tall with a 12 gap between them, in both directions.
            const int TileGap = 12;
            if (glass)
            {
                int bottom = 0;
                foreach (PanelRow r in rows)
                {
                    if (r.Fixed.IsEmpty) continue;
                    if (r.HasShown) r.Rect = new Rectangle(Gutter + (int)Math.Round(r.Shown.X), Gutter + (int)Math.Round(r.Shown.Y), (int)Math.Round(r.Shown.Width), (int)Math.Round(r.Shown.Height));
                    else r.Rect = new Rectangle(Gutter + r.Fixed.X, Gutter + r.Fixed.Y, r.Fixed.Width, r.Fixed.Height);
                    bottom = Math.Max(bottom, r.Fixed.Bottom);
                }
                panelHeight = bottom;
                LayoutFinish();
                return;
            }
            int y = PadY;
            int half = (panelWidth - PadX - TileGap) / 2;
            for (int i = 0; i < rows.Count; i++)
            {
                PanelRow r = rows[i];
                int colX = Gutter + PadX / 2;
                if (r.Kind == RowKind.Tile && r.Half)
                {
                    r.Rect = new Rectangle(colX, Gutter + y, half, r.Height);
                    if (i + 1 < rows.Count && rows[i + 1].Kind == RowKind.Tile && rows[i + 1].Half)
                    {
                        i++;
                        rows[i].Rect = new Rectangle(colX + half + TileGap, Gutter + y, half, rows[i].Height);
                    }
                    y += r.Height + TileGap;
                    continue;
                }
                r.Rect = new Rectangle(colX, Gutter + y, panelWidth - PadX, r.Height);
                y += r.Height + (r.Kind == RowKind.Tile || (r.Kind == RowKind.Slider && r.Card) ? TileGap : 0);
            }
            panelHeight = y + PadY;
            LayoutFinish();
        }

        void LayoutFinish()
        {
            int width = panelWidth + Gutter * 2, height = panelHeight + Gutter * 2;
            Rectangle area = Screen.FromPoint(anchor).Bounds;
            int x = Math.Max(area.Left, Math.Min(anchor.X - width / 2, area.Right - width));
            int top = anchor.Y - Gutter + 6;

            if (content != null) content.Dispose();
            content = null;
            if (surface != null && (surface.Width != width || surface.Height != height)) { surface.Dispose(); surface = null; }

            Bounds = new Rectangle(x, top, width, height);
            RenderContent();
        }

        // ---------- drawing ----------

        // ---------- liquid-glass Control Center ----------

        static readonly Color Blue = Color.FromArgb(10, 132, 255);

        Rectangle Track(PanelRow r)
        {
            if (!glass) return SliderTrack(r.Rect);
            int right = r.Extra != null ? 92 : 44;
            return new Rectangle(r.Rect.Left + 40, r.Rect.Top + 41, r.Rect.Width - 40 - right, 4);
        }

        void GlassBase(Graphics g, Rectangle rc, float radius, bool hot, int boost = 0)
        {
            DrawGlass(g, rc, radius, hot, boost, backdrop, new Point(backdropRect.X - Left, backdropRect.Y - Top));
        }

        // Launchpad / All Apps glass: blurred backdrop, fixed dark tint (alpha 96), thin white rim. No sheen.
        internal static readonly Color LaunchpadTint = Color.FromArgb(96, 36, 38, 48);
        internal static void DrawLaunchpadGlass(Graphics g, Rectangle rc, float radius, Bitmap backdrop, Point backdropOrigin)
        {
            using (GraphicsPath pp = Layered.RoundedRect(rc, radius))
            {
                if (backdrop != null)
                    using (var tex = new TextureBrush(backdrop, WrapMode.Clamp))
                    {
                        tex.TranslateTransform(backdropOrigin.X, backdropOrigin.Y);
                        g.FillPath(tex, pp);
                    }
                using (var tint = new SolidBrush(backdrop != null ? LaunchpadTint : Color.FromArgb(235, 30, 32, 40))) g.FillPath(tint, pp);
                using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawPath(rim, pp);
            }
        }

        // Shared glass material (blurred backdrop, tint, sheen, rim); Spotlight uses it too.
        internal static void DrawGlass(Graphics g, Rectangle rc, float radius, bool hot, int boost, Bitmap backdrop, Point backdropOrigin)
        {
            bool light = Theme.Light;
            // Inset half a pixel so the rim stays fully inside the tile and edges anti-alias cleanly.
            var rf = new RectangleF(rc.Left + 0.5f, rc.Top + 0.5f, rc.Width - 1, rc.Height - 1);
            using (GraphicsPath path = Layered.RoundedRect(rf, radius))
            {
                // Shadow only outside the tile (so it never muddies the glass), built from faint widening rings.
                GraphicsState st = g.Save();
                g.ExcludeClip(Rectangle.Inflate(rc, -2, -2) == rc ? rc : new Rectangle(rc.Left + (int)(radius * 0.3f), rc.Top + (int)(radius * 0.3f), rc.Width - (int)(radius * 0.6f), rc.Height - (int)(radius * 0.6f)));
                for (int i = 6; i >= 1; i--)
                {
                    RectangleF s = rf; s.Inflate(i * 1.2f, i * 1.2f); s.Offset(0, i * 1.1f);
                    using (GraphicsPath sp = Layered.RoundedRect(s, radius + i * 1.2f))
                    using (var sb = new SolidBrush(Color.FromArgb(4, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }
                g.Restore(st);

                // Blurred wallpaper through an anti-aliased texture fill (a clip region would leave jagged edges).
                if (backdrop != null)
                    using (var tex = new TextureBrush(backdrop, WrapMode.Clamp))
                    {
                        tex.TranslateTransform(backdropOrigin.X, backdropOrigin.Y);
                        g.FillPath(tex, path);
                    }
                using (var tint = new SolidBrush(backdrop != null ? Color.FromArgb(Math.Min(255, LaunchpadTint.A + boost), LaunchpadTint) : Color.FromArgb(235, 30, 32, 40)))
                    g.FillPath(tint, path);
                if (hot)
                    using (var hl = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                        g.FillPath(hl, path);
                using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                    g.DrawPath(rim, path);
            }
        }

        void DrawGlassChip(Graphics g, PanelRow r)
        {
            GlassBase(g, r.Rect, r.Rect.Height / 2f, false);
            using (var b = new SolidBrush(Theme.Light ? Color.FromArgb(28, 28, 30) : Color.White))
            using (var f = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(r.Text ?? "", small, b, r.Rect, f);
        }

        void DrawGlassTile(Graphics g, PanelRow r, bool hot)
        {
            Rectangle rc = r.Rect;
            bool light = Theme.Light;
            double t = Math.Max(0, Math.Min(1, r.Anim));
            GlassBase(g, rc, rc.Height / 2f, hot);

            Color fg = light ? Color.FromArgb(28, 28, 30) : Color.White;
            Rectangle ic = new Rectangle(rc.Left + 14, rc.Top + (rc.Height - 36) / 2, 36, 36);
            Color iconOff = light ? Color.FromArgb(48, 0, 0, 0) : Color.FromArgb(62, 255, 255, 255);
            // As in the reference: an active toggle is a white circle with a blue glyph (the moon is dark on white);
            // an inactive one is a grey circle with a light glyph.
            Color iconFill = Lerp(iconOff, Color.White, t);
            Color glyphOn = r.Glyph == "moon" ? Color.FromArgb(40, 40, 46) : Blue;
            using (var b = new SolidBrush(iconFill)) g.FillEllipse(b, ic);
            DrawGlyph(g, r.Glyph, ic, Lerp(fg, glyphOn, t), iconFill);

            var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            bool noSub = string.IsNullOrEmpty(r.Sub);
            int textW = rc.Width - 66 - (r.Extra != null ? 22 : 0);
            using (var tb = new SolidBrush(fg))
                g.DrawString(r.Text, tileTitle, tb, new Rectangle(rc.Left + 58, rc.Top + (noSub ? 22 : 12), textW, 20), left);
            using (var sb = new SolidBrush(Color.FromArgb(light ? 150 : 175, fg)))
                g.DrawString(r.Sub ?? "", small, sb, new Rectangle(rc.Left + 58, rc.Top + 32, textW, 18), left);
            if (r.Extra != null)
            {
                // chevron at the right edge: opens this tile's own panel (networks / devices)
                r.ExtraRect = new Rectangle(rc.Right - 40, rc.Top, 40, rc.Height);
                float cx = rc.Right - 20, cy = rc.Top + rc.Height / 2f;
                using (var pen = new Pen(Color.FromArgb(light ? 150 : 190, fg), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                { g.DrawLine(pen, cx - 2.5f, cy - 5, cx + 2.5f, cy); g.DrawLine(pen, cx + 2.5f, cy, cx - 2.5f, cy + 5); }
            }
        }

        void DrawGlassRound(Graphics g, PanelRow r, bool hot)
        {
            Rectangle rc = r.Rect;
            GlassBase(g, rc, rc.Height / 2f, hot);
            Color fg = Theme.Light ? Color.FromArgb(28, 28, 30) : Color.White;
            float cx = rc.Left + rc.Width / 2f, cy = rc.Top + rc.Height / 2f;
            using (var pen = new Pen(fg, 1.9f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var b = new SolidBrush(fg))
                DrawRoundGlyph(g, r.Glyph, pen, b, cx, cy);
        }

        static void DrawRoundGlyph(Graphics g, string glyph, Pen pen, Brush b, float cx, float cy)
        {
            switch (glyph)
            {
                case "stage": // two overlapping windows
                    using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 4, cy - 12, 17, 17), 4)) g.DrawPath(pen, p);
                    using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 13, cy - 4, 17, 17), 4)) g.DrawPath(pen, p);
                    break;
                case "mirror": // screen with an up arrow
                    using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 12, cy - 10, 24, 20), 4.5f)) g.DrawPath(pen, p);
                    g.DrawLine(pen, cx, cy + 3, cx, cy - 4);
                    g.DrawLine(pen, cx - 4, cy - 1, cx, cy - 5);
                    g.DrawLine(pen, cx + 4, cy - 1, cx, cy - 5);
                    break;
                case "grid": // 2x2 outlined rounded squares
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 2; j++)
                            using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 11 + i * 12.5f, cy - 11 + j * 12.5f, 9.5f, 9.5f), 3f)) g.DrawPath(pen, p);
                    break;
                case "sidebar": // rounded window plus three dots on the left
                    using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 3, cy - 9, 15, 18), 4)) g.DrawPath(pen, p);
                    for (int i = 0; i < 3; i++) g.FillEllipse(b, cx - 12, cy - 8 + i * 6.5f, 3.2f, 3.2f);
                    break;
                case "airplay":
                    using (GraphicsPath p = Layered.RoundedRect(new RectangleF(cx - 8, cy - 7, 16, 11), 2f)) g.DrawPath(pen, p);
                    g.FillPolygon(b, new[] { new PointF(cx, cy + 1.5f), new PointF(cx + 5.5f, cy + 8), new PointF(cx - 5.5f, cy + 8) });
                    break;
            }
        }

        void DrawGlassMedia(Graphics g, PanelRow r, bool hot)
        {
            Rectangle rc = r.Rect;
            bool light = Theme.Light;
            GlassBase(g, rc, 32, hot);
            Color fg = light ? Color.FromArgb(28, 28, 30) : Color.White;
            MediaInfo m = Feed.Media;
            var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            // Artwork placeholder top-left, title in the middle-left, controls along the bottom.
            FillRound(g, new Rectangle(rc.Left + 18, rc.Top + 16, 44, 44), 10, Color.FromArgb(light ? 40 : 52, light ? 0 : 255, light ? 0 : 255, light ? 0 : 255));
            if (m != null && m.Art != null)
            {
                var ar = new Rectangle(rc.Left + 18, rc.Top + 16, 44, 44);
                using (var gp = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    gp.AddArc(ar.X, ar.Y, 20, 20, 180, 90); gp.AddArc(ar.Right - 20, ar.Y, 20, 20, 270, 90);
                    gp.AddArc(ar.Right - 20, ar.Bottom - 20, 20, 20, 0, 90); gp.AddArc(ar.X, ar.Bottom - 20, 20, 20, 90, 90);
                    gp.CloseFigure();
                    var oldClip = g.Clip; var oi = g.InterpolationMode;
                    g.SetClip(gp, System.Drawing.Drawing2D.CombineMode.Intersect);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    int sz = Math.Min(m.Art.Width, m.Art.Height);
                    g.DrawImage(m.Art, ar, (m.Art.Width - sz) / 2, (m.Art.Height - sz) / 2, sz, sz, GraphicsUnit.Pixel);
                    g.InterpolationMode = oi; g.Clip = oldClip;
                }
            }
            bool hasArtist = m != null && !string.IsNullOrEmpty(m.Artist);
            int tx = rc.Left + 72, tw = Math.Max(10, rc.Right - 18 - tx);
            using (var tb = new SolidBrush(fg))
            using (var ab = new SolidBrush(Color.FromArgb(150, fg)))
            {
                g.DrawString(m != null && !string.IsNullOrEmpty(m.Title) ? m.Title : "Not Playing", tileTitle, tb, new Rectangle(tx, rc.Top + (hasArtist ? 16 : 26), tw, 22), left);
                if (hasArtist) g.DrawString(m.Artist, tileTitle, ab, new Rectangle(tx, rc.Top + 38, tw, 22), left);
            }

            r.Hits.Clear();
            float by = rc.Bottom - 34, cx = rc.Left + rc.Width / 2f;
            bool playing = m != null && m.Playing;
            using (var b = new SolidBrush(fg))
            {
                // previous
                float px = cx - 42;
                g.FillPolygon(b, new[] { new PointF(px + 3, by - 8), new PointF(px + 3, by + 8), new PointF(px - 9, by) });
                g.FillPolygon(b, new[] { new PointF(px + 15, by - 8), new PointF(px + 15, by + 8), new PointF(px + 3, by) });
                // play / pause
                if (playing) { g.FillRectangle(b, cx - 7, by - 9, 5, 18); g.FillRectangle(b, cx + 2, by - 9, 5, 18); }
                else g.FillPolygon(b, new[] { new PointF(cx - 7, by - 10), new PointF(cx - 7, by + 10), new PointF(cx + 11, by) });
                // next
                float nx = cx + 42;
                g.FillPolygon(b, new[] { new PointF(nx - 3, by - 8), new PointF(nx - 3, by + 8), new PointF(nx + 9, by) });
                g.FillPolygon(b, new[] { new PointF(nx - 15, by - 8), new PointF(nx - 15, by + 8), new PointF(nx - 3, by) });
            }
            r.Hits.Add(new KeyValuePair<Rectangle, Action>(new Rectangle((int)cx - 62, (int)by - 18, 40, 36), () => MediaFeed.Previous()));
            r.Hits.Add(new KeyValuePair<Rectangle, Action>(new Rectangle((int)cx - 20, (int)by - 18, 40, 36), () => MediaFeed.TogglePlayPause()));
            r.Hits.Add(new KeyValuePair<Rectangle, Action>(new Rectangle((int)cx + 22, (int)by - 18, 40, 36), () => MediaFeed.Next()));
        }

        void DrawGlassSlider(Graphics g, PanelRow r, bool hot)
        {
            Rectangle rc = r.Rect;
            bool light = Theme.Light;
            GlassBase(g, rc, 32, false);
            Color fg = light ? Color.FromArgb(28, 28, 30) : Color.White;
            var lf = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            using (var lb = new SolidBrush(fg))
                g.DrawString(r.Text ?? "", tileTitle, lb, new Rectangle(rc.Left + 20, rc.Top + 8, rc.Width - 40, 20), lf);

            Rectangle tr = Track(r);
            FillRound(g, tr, tr.Height / 2f, light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
            int filled = (int)Math.Round(tr.Width * Math.Max(0, Math.Min(1, r.Value)));
            if (filled > 0)
                FillRound(g, new Rectangle(tr.Left, tr.Top, Math.Max(tr.Height, filled), tr.Height), tr.Height / 2f, Color.White);
            // Thin track as in the reference: small icon before it, large icon after it.
            float cy = tr.Top + tr.Height / 2f;
            using (var bl = new SolidBrush(fg))
            {
                if (r.Sun)
                {
                    g.FillEllipse(bl, rc.Left + 19, cy - 3.5f, 7, 7);
                    g.FillEllipse(bl, tr.Right + 12, cy - 7, 14, 14);
                }
                else
                {
                    var speaker = new[] { new PointF(0, 4), new PointF(3.5f, 4), new PointF(7, 0.5f), new PointF(7, 11.5f), new PointF(3.5f, 8), new PointF(0, 8) };
                    DrawPolygon(g, bl, speaker, rc.Left + 18, cy - 6);
                    DrawPolygon(g, bl, speaker, tr.Right + 12, cy - 7.5f, 1.25f);
                    using (var wp = new Pen(fg, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(wp, tr.Right + 24, cy - 4, 8, 8, -50, 100);
                        g.DrawArc(wp, tr.Right + 24, cy - 7, 14, 14, -50, 100);
                    }
                }
            }
            if (r.Extra != null)
            {
                r.ExtraRect = new Rectangle(rc.Right - 14 - 30, (int)cy - 15, 30, 30);
                using (var eb = new SolidBrush(light ? Color.FromArgb(48, 0, 0, 0) : Color.FromArgb(62, 255, 255, 255)))
                    g.FillEllipse(eb, r.ExtraRect);
                using (var pen = new Pen(fg, 1.5f) { LineJoin = LineJoin.Round })
                using (var b = new SolidBrush(fg))
                    DrawRoundGlyph(g, "airplay", pen, b, r.ExtraRect.Left + 15, r.ExtraRect.Top + 14);
            }
        }

        void RenderGlass()
        {
            using (Graphics g = Graphics.FromImage(content))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Typography.Prepare(g);
                for (int i = 0; i < rows.Count; i++)
                {
                    PanelRow r = rows[i];
                    bool hot = i == hover && r.Interactive;
                    switch (r.Kind)
                    {
                        case RowKind.Chip: DrawGlassChip(g, r); break;
                        case RowKind.Tile: DrawGlassTile(g, r, hot); break;
                        case RowKind.Round: DrawGlassRound(g, r, hot); break;
                        case RowKind.Media: DrawGlassMedia(g, r, hot); break;
                        case RowKind.Slider: DrawGlassSlider(g, r, hot); break;
                    }
                }
            }
        }

        void RenderContent()
        {
            if (content != null) content.Dispose();
            content = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); // premultiplied = fastest to blit
            if (glass) { RenderGlass(); return; }

            bool light = Theme.Light; // white in light mode, dark grey in dark mode
            Color text = light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(240, 240, 244);
            Color muted = light ? Color.FromArgb(125, 60, 60, 67) : Color.FromArgb(150, 235, 235, 245);
            Color accent = Color.FromArgb(10, 132, 255);
            Color hoverFill = light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(66, 255, 255, 255); // same grey as the menus, no blue

            using (Graphics g = Graphics.FromImage(content))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Typography.Prepare(g);

                var panel = new RectangleF(Gutter, Gutter, panelWidth, panelHeight);

                // Soft, short shadow: a few widening, very faint rounded rects.
                for (int i = 6; i >= 1; i--)
                {
                    RectangleF s = panel;
                    s.Inflate(i * 1.5f, i * 1.5f);
                    s.Offset(0, i * 0.8f);
                    using (GraphicsPath sp = Layered.RoundedRect(s, Radius + i * 1.5f))
                    using (var sb = new SolidBrush(Color.FromArgb(5, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }

                // Glass body: tinted fill, a soft sheen that fades from the top, and a thin rim that is brightest
                // along the top edge. The same recipe is used by the bar and the dock.
                using (GraphicsPath path = Layered.RoundedRect(panel, Radius))
                using (var fill = new SolidBrush(backdrop != null ? LaunchpadTint : Color.FromArgb(235, 30, 32, 40))) // Launchpad glass over the blurred desktop snapshot
                using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                {
                    if (backdrop != null)
                        using (var tex = new TextureBrush(backdrop, WrapMode.Clamp))
                        {
                            tex.TranslateTransform(backdropRect.X - Left, backdropRect.Y - Top);
                            g.FillPath(tex, path);
                        }
                    g.FillPath(fill, path);
                    g.DrawPath(rim, path);
                }

                var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                var right = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };

                using (var textBrush = new SolidBrush(text))
                using (var mutedBrush = new SolidBrush(muted))
                using (var accentBrush = new SolidBrush(accent))
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        PanelRow r = rows[i];
                        Rectangle rc = r.Rect;
                        bool hot = i == hover && r.Interactive;

                        switch (r.Kind)
                        {
                            case RowKind.Title:
                                g.DrawString(r.Text, semibold, textBrush, new Rectangle(rc.Left + 6, rc.Top, rc.Width - 70, rc.Height), left);
                                if (r.Toggle.HasValue)
                                {
                                    r.ToggleRect = new Rectangle(rc.Right - 50, rc.Top + (rc.Height - 24) / 2, 42, 24);
                                    DrawSwitch(g, r.ToggleRect, r.Toggle.Value, light);
                                }
                                break;

                            case RowKind.Big:
                                g.DrawString(r.Text, big, textBrush, new Rectangle(rc.Left + 6, rc.Top, rc.Width - 12, 40), left);
                                g.DrawString(r.Sub, small, mutedBrush, new Rectangle(rc.Left + 8, rc.Top + 40, rc.Width - 16, 20), left);
                                break;

                            case RowKind.Info:
                                g.DrawString(r.Text, body, mutedBrush, new Rectangle(rc.Left + 6, rc.Top, rc.Width / 2, rc.Height), left);
                                g.DrawString(r.Sub, body, textBrush, new Rectangle(rc.Left + rc.Width / 3, rc.Top, rc.Width * 2 / 3 - 6, rc.Height), right);
                                break;

                            case RowKind.Header:
                                g.DrawString((r.Text ?? "").ToUpperInvariant(), header, mutedBrush, new Rectangle(rc.Left + 6, rc.Top + 4, rc.Width - 12, rc.Height - 4), left);
                                break;

                            case RowKind.Item:
                                if (hot) FillRound(g, rc, 9, hoverFill);
                                if (r.Checked) g.DrawString("✓", body, textBrush, new Rectangle(rc.Left + 8, rc.Top, 20, rc.Height), left);
                                g.DrawString(r.Text, body, textBrush, new Rectangle(rc.Left + 30, rc.Top, rc.Width - 30 - (string.IsNullOrEmpty(r.Sub) ? 8 : 112), rc.Height), left);
                                if (!string.IsNullOrEmpty(r.Sub))
                                    g.DrawString(r.Sub, small, mutedBrush, new Rectangle(rc.Right - 110, rc.Top, 102, rc.Height), right);
                                break;

                            case RowKind.Button:
                                if (hot) FillRound(g, rc, 9, hoverFill);
                                g.DrawString(r.Text, body, textBrush, new Rectangle(rc.Left + 10, rc.Top, rc.Width - 20, rc.Height), left);
                                break;

                            case RowKind.Slider:
                                DrawSlider(g, r, accent, light);
                                break;

                            case RowKind.Tile:
                                DrawTile(g, r, light);
                                break;

                            default:
                                using (var pen = new Pen(light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255)))
                                    g.DrawLine(pen, rc.Left + 8, rc.Top + rc.Height / 2, rc.Right - 8, rc.Top + rc.Height / 2);
                                break;
                        }
                    }
                }
            }
        }

        static void FillRound(Graphics g, Rectangle r, float radius, Color c)
        {
            using (GraphicsPath p = Layered.RoundedRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        static void DrawSwitch(Graphics g, Rectangle r, bool on, bool light)
        {
            Color track = on ? Color.FromArgb(52, 199, 89) : (light ? Color.FromArgb(70, 120, 120, 128) : Color.FromArgb(90, 120, 120, 128));
            FillRound(g, r, r.Height / 2f, track);
            int knob = r.Height - 4;
            int kx = on ? r.Right - knob - 2 : r.Left + 2;
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillEllipse(shadow, kx, r.Top + 3, knob, knob);
            g.FillEllipse(Brushes.White, kx, r.Top + 2, knob, knob);
        }

        // Track runs between the two speaker glyphs.
        static Rectangle SliderTrack(Rectangle row)
        {
            return new Rectangle(row.Left + 34, row.Top + (row.Height - 8) / 2, row.Width - 68, 8);
        }

        void DrawSlider(Graphics g, PanelRow r, Color accent, bool light)
        {
            Rectangle track = SliderTrack(r.Rect);
            Color fillColor = accent;
            if (r.Card)
            {
                // Display / Sound card: rounded glass, label on top, thin white track underneath, no knob.
                FillRound(g, r.Rect, 24, light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255));
                using (var lb = new SolidBrush(light ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 245, 245, 250)))
                using (var lf = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center })
                    g.DrawString(r.Text ?? "", tileTitle, lb, new Rectangle(r.Rect.Left + 18, r.Rect.Top + 8, r.Rect.Width - 36, 20), lf);
                track = new Rectangle(track.Left, r.Rect.Top + 40, track.Width, 6);
                if (!light) fillColor = Color.White;
            }
            float rad = track.Height / 2f;
            FillRound(g, track, rad, light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255));

            int filled = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, r.Value)));
            if (filled > 0) FillRound(g, new Rectangle(track.Left, track.Top, Math.Max(track.Height, filled), track.Height), rad, fillColor);

            if (!r.Card)
            {
                int kx = track.Left + filled - 8;
                using (var shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                    g.FillEllipse(shadow, kx, track.Top - 3, 17, 17);
                g.FillEllipse(Brushes.White, kx, track.Top - 4, 17, 17);
            }

            float cy = track.Top + track.Height / 2f;
            Color glyph = light ? Color.FromArgb(120, 60, 60, 67) : Color.FromArgb(170, 235, 235, 245);
            using (var b = new SolidBrush(glyph))
            {
                if (r.Sun)
                {
                    // Brightness: small sun on the left, large sun on the right.
                    g.FillEllipse(b, r.Rect.Left + 10, cy - 3.5f, 7, 7);
                    g.FillEllipse(b, r.Rect.Right - 26, cy - 8, 16, 16);
                }
                else
                {
                    var speaker = new[] { new PointF(0, 4), new PointF(3.5f, 4), new PointF(7, 0.5f), new PointF(7, 11.5f), new PointF(3.5f, 8), new PointF(0, 8) };
                    DrawPolygon(g, b, speaker, r.Rect.Left + 8, cy - 6);
                    DrawPolygon(g, b, speaker, r.Rect.Right - 25, cy - 7, 1.35f);
                }
            }
        }

        static void DrawPolygon(Graphics g, Brush b, PointF[] pts, float ox, float oy, float scale = 1f)
        {
            g.FillPolygon(b, pts.Select(p => new PointF(ox + p.X * scale, oy + p.Y * scale)).ToArray());
        }

        // Draws the cached panel bitmap with the current spring scale/opacity and hands it to Windows.
        void Compose()
        {
            if (!IsHandleCreated || content == null) return;
            if (surface == null) surface = new Surface(Width, Height);

            float p = (float)Math.Max(0, open.Value);
            float alpha = Math.Max(0, Math.Min(1, p)); // menu-style fade: opacity only, no scaling
            float scale = 1f;
            float ox = anchor.X - Left, oy = Gutter;

            var m = new ColorMatrix { Matrix00 = 1, Matrix11 = 1, Matrix22 = 1, Matrix33 = alpha, Matrix44 = 1 };
            fade.SetColorMatrix(m);

            using (Graphics g = surface.Begin())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.TranslateTransform(ox, oy);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-ox, -oy);
                // Settled and fully opaque: a plain copy is much cheaper than a color-matrix blit.
                if (alpha >= 0.999f && Math.Abs(scale - 1f) < 0.0005f) g.DrawImageUnscaled(content, 0, 0);
                else g.DrawImage(content, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, fade);

                if (dragRow != null && dragActive)
                {
                    g.ResetTransform();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    PanelRow t = DragTarget(dragNow);
                    if (t != null)
                        using (var path = Layered.RoundedRect(Rectangle.Inflate(t.Rect, 2, 2), 22))
                        using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2.5f))
                            g.DrawPath(pen, path);
                    // Lifted preview of the tile under the cursor, slightly translucent.
                    Rectangle src = dragRow.Rect;
                    Rectangle dst = new Rectangle(dragNow.X - dragFrom.X + src.X, dragNow.Y - dragFrom.Y + src.Y, src.Width, src.Height);
                    var gm = new ColorMatrix { Matrix00 = 1, Matrix11 = 1, Matrix22 = 1, Matrix33 = 0.82f, Matrix44 = 1 };
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(gm);
                        g.DrawImage(content, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            surface.Present(Handle, Left, Top);
        }

        // ---------- animation + input polling ----------

        void Step()
        {
            ticks++;

            double dt = clock.Next();
            // Same motion as the menus: a 120 ms ease-out fade in, an 80 ms fade out.
            double fadeT = Math.Max(0, Math.Min(1, (fadeWatch.Elapsed.TotalMilliseconds - fadeStart) / (closing ? 80 : 120)));
            open.Value = closing ? 1 - fadeT : 1 - Math.Pow(1 - fadeT, 3);
            bool settled = fadeT >= 1;

            if (closing && open.Value < 0.04)
            {
                Close();
                return;
            }

            bool tilesMoved = StepTiles(dt);
            tilesMoved |= StepLayout(dt);
            if (tilesMoved) RenderContent();
            if (!settled || dragging != null || tilesMoved) Compose(); // nothing to redraw while the panel just sits there
            timer.Interval = settled && dragging == null && !tilesMoved ? 40 : 16;

            if (ticks % 2 == 0) PollInput();
        }

        // Glass layout: rows glide to their new place and size (soft ease-out) after a reorder, resize or add/remove.
        bool StepLayout(double dt)
        {
            if (!glass) return false;
            bool moved = false;
            double k = 1 - Math.Exp(-Math.Min(dt, 0.05) * 11); // gentle ease-out so resizes and moves settle softly
            foreach (PanelRow r in rows)
            {
                if (!r.HasShown || r.Fixed.IsEmpty) continue;
                RectangleF t = r.Fixed, s = r.Shown;
                if (Math.Abs(t.X - s.X) + Math.Abs(t.Y - s.Y) + Math.Abs(t.Width - s.Width) + Math.Abs(t.Height - s.Height) < 0.6f) s = t;
                else
                {
                    s = new RectangleF(s.X + (t.X - s.X) * (float)k, s.Y + (t.Y - s.Y) * (float)k, s.Width + (t.Width - s.Width) * (float)k, s.Height + (t.Height - s.Height) * (float)k);
                    moved = true;
                }
                r.Shown = s;
                r.Rect = new Rectangle(Gutter + (int)Math.Round(s.X), Gutter + (int)Math.Round(s.Y), (int)Math.Round(s.Width), (int)Math.Round(s.Height));
            }
            return moved;
        }

        // Eases each tile's colour toward its on/off state. Returns true while any tile is still moving.
        bool StepTiles(double dt)
        {
            bool moved = false;
            foreach (PanelRow r in rows)
            {
                if (r.Kind != RowKind.Tile) continue;
                double target = r.Active ? 1 : 0;
                if (Math.Abs(target - r.Anim) < 0.002) { r.Anim = target; continue; }
                r.Anim += (target - r.Anim) * (1 - Math.Exp(-dt * 14)); // about a quarter second
                moved = true;
            }
            return moved;
        }

        static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Control Center pill: rounded tile, round icon, title and subtitle. Colours cross-fade with Anim.
        void DrawTile(Graphics g, PanelRow r, bool light)
        {
            Rectangle rc = r.Rect;
            double t = Math.Max(0, Math.Min(1, r.Anim));

            Color offFill = light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255);
            // As in the reference, only Dark Mode turns the whole pill white; the others keep the glass and light up the icon.
            // Dark Mode "on" pill contrasts with the panel: dark on the white panel, light on the dark grey one.
            Color onFill = r.WhiteWhenOn ? (light ? Color.FromArgb(255, 44, 44, 46) : Color.FromArgb(255, 245, 245, 250)) : offFill;
            FillRound(g, rc, rc.Height / 2f, Lerp(offFill, onFill, t));

            Rectangle ic = new Rectangle(rc.Left + 14, rc.Top + (rc.Height - 36) / 2, 36, 36);
            Color iconOff = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255);
            Color iconOn = r.WhiteWhenOn && !light ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 255, 255, 255);
            Color glyphOff = light ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 245, 245, 250);
            Color glyphOn = r.WhiteWhenOn ? (light ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 255, 255, 255)) : Color.FromArgb(255, 10, 132, 255);
            Color textOff = light ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 245, 245, 250);
            Color textOn = r.WhiteWhenOn ? (light ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 28, 28, 30)) : textOff;
            Color subOff = light ? Color.FromArgb(150, 60, 60, 67) : Color.FromArgb(150, 235, 235, 245);
            Color subOn = r.WhiteWhenOn ? (light ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(170, 28, 28, 30)) : subOff;

            Color iconFill = Lerp(iconOff, iconOn, t);
            using (var b = new SolidBrush(iconFill)) g.FillEllipse(b, ic);
            DrawGlyph(g, r.Glyph, ic, Lerp(glyphOff, glyphOn, t), iconFill);

            var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var tb = new SolidBrush(Lerp(textOff, textOn, t)))
                g.DrawString(r.Text, tileTitle, tb, new Rectangle(rc.Left + 58, string.IsNullOrEmpty(r.Sub) ? rc.Top + (rc.Height - 20) / 2 : rc.Top + 12, rc.Width - 66, 20), left);
            using (var sb = new SolidBrush(Lerp(subOff, subOn, t)))
                g.DrawString(r.Sub ?? "", small, sb, new Rectangle(rc.Left + 58, rc.Top + 32, rc.Width - 66, 18), left);
        }

        static Bitmap lsLogo;

        static void DrawGlyph(Graphics g, string glyph, Rectangle ic, Color c, Color cut)
        {
            float cx = ic.Left + ic.Width / 2f, cy = ic.Top + ic.Height / 2f;
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, 1.8f))
            {
                switch (glyph)
                {
                    case "wifi":
                    case "bluetooth":
                    case "airplane":
                    {
                        string ch = glyph == "wifi" ? "" : glyph == "bluetooth" ? "" : "";
                        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(ch, glyphFont, b, new RectangleF(ic.Left, ic.Top, ic.Width, ic.Height), center);
                        break;
                    }
                    case "localsend":
                    {
                        // the real LocalSend logo (embedded resource), filling the icon circle
                        if (lsLogo == null)
                            try { using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("localsend.png")) lsLogo = new Bitmap(s); } catch { }
                        if (lsLogo != null)
                        {
                            var old = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            // white monochrome logo, tinted to the glyph colour like the other icons
                            var cm = new ColorMatrix(new float[][] {
                                new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                                new float[] { 0, 0, 0, c.A / 255f, 0 }, new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 } });
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetColorMatrix(cm);
                                const int S = 24;
                                g.DrawImage(lsLogo, new Rectangle(ic.Left + (ic.Width - S) / 2, ic.Top + (ic.Height - S) / 2, S, S), 0, 0, lsLogo.Width, lsLogo.Height, GraphicsUnit.Pixel, ia);
                            }
                            g.InterpolationMode = old;
                        }
                        break;
                    }
                    case "dark":
                        g.DrawEllipse(pen, cx - 8, cy - 8, 16, 16);
                        g.FillPie(b, cx - 8, cy - 8, 16, 16, 90, 180);
                        break;
                    case "moon":
                        g.FillEllipse(b, cx - 8, cy - 8, 16, 16);
                        using (var cutBrush = new SolidBrush(cut)) g.FillEllipse(cutBrush, cx - 4, cy - 10, 13, 13);
                        break;
                    case "warm":
                    {
                        // sun: disc with eight rays (Night light)
                        g.FillEllipse(b, cx - 4.5f, cy - 4.5f, 9, 9);
                        for (int k = 0; k < 8; k++)
                        {
                            double a = k * Math.PI / 4;
                            g.DrawLine(pen, cx + (float)Math.Cos(a) * 7.5f, cy + (float)Math.Sin(a) * 7.5f, cx + (float)Math.Cos(a) * 10f, cy + (float)Math.Sin(a) * 10f);
                        }
                        break;
                    }
                    case "shot":
                        g.DrawRectangle(pen, cx - 9, cy - 5, 18, 12);
                        g.DrawEllipse(pen, cx - 3.5f, cy - 2.5f, 7, 7);
                        g.FillRectangle(b, cx - 3, cy - 8, 6, 3);
                        break;
                    case "rec":
                        g.DrawEllipse(pen, cx - 9, cy - 9, 18, 18);
                        g.FillEllipse(b, cx - 4.5f, cy - 4.5f, 9, 9);
                        break;
                    case "project":
                        // two overlapping screens
                        g.DrawRectangle(pen, cx - 9, cy - 8, 12, 9);
                        g.FillRectangle(b, cx - 3, cy - 3, 12, 9);
                        break;
                    case "mute":
                        g.FillPolygon(b, new PointF[] { new PointF(cx - 9, cy - 3), new PointF(cx - 4, cy - 3), new PointF(cx + 1, cy - 8), new PointF(cx + 1, cy + 8), new PointF(cx - 4, cy + 3), new PointF(cx - 9, cy + 3) });
                        g.DrawLine(pen, cx + 4, cy - 4, cx + 10, cy + 4);
                        g.DrawLine(pen, cx + 10, cy - 4, cx + 4, cy + 4);
                        break;
                    case "lock":
                        g.FillRectangle(b, cx - 7, cy - 1, 14, 10);
                        g.DrawArc(pen, cx - 5, cy - 9, 10, 12, 180, 180);
                        g.DrawLine(pen, cx - 5, cy - 3, cx - 5, cy);
                        g.DrawLine(pen, cx + 5, cy - 3, cx + 5, cy);
                        break;
                    case "saver":
                    {
                        // battery with a plus
                        g.DrawRectangle(pen, cx - 9, cy - 5, 16, 10);
                        g.FillRectangle(b, cx + 8, cy - 2, 2, 4);
                        using (var cutBrush = new SolidBrush(cut))
                        { g.FillRectangle(cutBrush, cx - 6.5f, cy - 0.9f, 11, 1.8f); g.FillRectangle(cutBrush, cx - 1.9f, cy - 3.5f, 1.8f, 7); }
                        break;
                    }
                }
            }
        }

        void PollInput()
        {
            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            var screen = new Point(p.X, p.Y);

            bool escape = (Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (escape && !wasEscape) BeginClose();
            wasEscape = escape;

            bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (down && !wasDown && !Bounds.Contains(screen) && screen.Y >= barHeight) BeginClose();
            wasDown = down;

            if (dragging != null) return;
            int index = -1;
            if (Bounds.Contains(screen) && open.Value > 0.9)
            {
                Point local = PointToClient(screen);
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Interactive && rows[i].Rect.Contains(local)) { index = i; break; }
            }
            if (index != hover)
            {
                hover = index;
                RenderContent();
                Compose();
            }
        }

        // ---------- mouse ----------

        void SetSlider(PanelRow r, int x)
        {
            Rectangle track = Track(r);
            r.Value = Math.Max(0, Math.Min(1, (x - track.Left) / (double)track.Width));
            if (r.ValueChanged != null) r.ValueChanged(r.Value);
            RenderContent();
            Compose();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (PanelRow r in rows)
            {
                if (r.Kind == RowKind.Slider && r.Rect.Contains(e.Location) && !(r.Extra != null && r.ExtraRect.Contains(e.Location)))
                {
                    dragging = r;
                    Capture = true;
                    SetSlider(r, e.X);
                    return;
                }
            }
            // Edit mode: pressing a draggable tile (not one of its small buttons) may start a reorder drag.
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                PanelRow r = rows[i];
                if (!r.Rect.Contains(e.Location)) continue;
                if (r.Resize != null)
                {
                    resizeRow = r; resizeFrom = e.Location; Capture = true;
                    r.Resize(0, 0, false);
                    return;
                }
                if (r.DragId != null)
                {
                    dragRow = r; dragFrom = e.Location; dragNow = e.Location; dragActive = false;
                    Capture = true;
                }
                return;
            }
        }

        PanelRow DragTarget(Point p)
        {
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                PanelRow r = rows[i];
                if (r.DragId != null && r.DragId != dragRow.DragId && r.Rect.Contains(p)) return r;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != null) SetSlider(dragging, e.X);
            if (resizeRow != null) resizeRow.Resize(e.X - resizeFrom.X, e.Y - resizeFrom.Y, false);
            if (dragRow != null)
            {
                dragNow = e.Location;
                if (!dragActive && (Math.Abs(e.X - dragFrom.X) > 6 || Math.Abs(e.Y - dragFrom.Y) > 6)) dragActive = true;
                if (dragActive) Compose();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (resizeRow != null)
            {
                PanelRow rr = resizeRow;
                resizeRow = null; Capture = false;
                rr.Resize(e.X - resizeFrom.X, e.Y - resizeFrom.Y, true);
                return;
            }
            if (dragRow != null)
            {
                PanelRow src = dragRow;
                bool was = dragActive;
                dragRow = null; dragActive = false; Capture = false;
                if (was)
                {
                    PanelRow t = null;
                    for (int i = rows.Count - 1; i >= 0; i--)
                        if (rows[i].DragId != null && rows[i].DragId != src.DragId && rows[i].Rect.Contains(e.Location)) { t = rows[i]; break; }
                    if (t != null && src.Drop != null) src.Drop(t.DragId);
                    else { RenderContent(); Compose(); }
                    return;
                }
            }
            if (dragging != null)
            {
                dragging = null;
                Capture = false;
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            for (int ri = rows.Count - 1; ri >= 0; ri--) // topmost first: edit-mode buttons sit over their tile
            {
                PanelRow r = rows[ri];
                if (r.Extra != null && r.ExtraRect.Contains(e.Location)) { BeginClose(); r.Extra(); return; }
                foreach (var h in r.Hits)
                    if (h.Key.Contains(e.Location)) { h.Value(); RenderContent(); Compose(); return; }
                if (r.Toggle.HasValue && r.ToggleRect.Contains(e.Location))
                {
                    r.Toggle = !r.Toggle.Value;
                    if (r.Toggled != null) r.Toggled(r.Toggle.Value);
                    RenderContent();
                    Compose();
                    return;
                }
                if (r.Click != null && r.Rect.Contains(e.Location))
                {
                    Action click = r.Click;
                    if (r.KeepOpen)
                    {
                        click();
                        RenderContent();
                        Compose();
                    }
                    else
                    {
                        BeginClose();
                        click();
                    }
                    return;
                }
            }
        }
    }

    // Builds the content for each status icon and fills in slow data (networks, devices) in the background.
    static class Panels
    {
        const int Width = 330;

        // Raised when a panel asks for another panel (Control Center -> Wi-Fi, for example).
        public static event Action<string> Navigate;

        static Action Open(string target)
        {
            return () => { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } };
        }

        static Action Go(string id)
        {
            return () => { if (Navigate != null) Navigate(id); };
        }

        public static GlassPanel Create(string id, Point anchor, int barHeight, Action closed)
        {
            List<PanelRow> rows;
            switch (id)
            {
                case "wifi": rows = new List<PanelRow> { PanelRow.Title("Wi-Fi"), PanelRow.Info("Status", "Scanning...") }; break;
                case "bluetooth": rows = new List<PanelRow> { PanelRow.Title("Bluetooth"), PanelRow.Info("Status", "Checking...") }; break;
                case "sound": rows = SoundRows(null, null); break;
                case "battery": rows = BatteryRows(); break;
                case "control": rows = new List<PanelRow>(); break;
                case "localsend": rows = new List<PanelRow> { PanelRow.Title("LocalSend"), PanelRow.Info("Status", "Looking for devices...") }; break;
                case "nightlight": rows = NightLightRows(); break;
                default: rows = ProfileRows(); break;
            }

            var panel = new GlassPanel(Width, rows, anchor, barHeight, closed);
            if (id == "control") panel.EnableGlass();

            if (id == "wifi") Background(panel, () => WifiRows(panel));
            else if (id == "bluetooth") Background(panel, () => BluetoothRows(panel));
            else if (id == "nightlight" && NightLight.NeedsRead) Background(panel, () => { NightLight.ReadActual(); return NightLightRows(); });
            else if (id == "sound") Background(panel, () => { string def; List<string> outs = DeviceFeed.AudioOutputs(out def); return SoundRows(outs, def); });
            else if (id == "control") { ccEdit = false; Background(panel, () => ControlRows(panel)); }
            else if (id == "localsend") Background(panel, () => LocalSendRows(panel));
            return panel;
        }

        static void Background(GlassPanel panel, Func<List<PanelRow>> build)
        {
            Task.Run(() =>
            {
                List<PanelRow> rows;
                try { rows = build(); } catch { return; }
                try
                {
                    if (!panel.IsDisposed) panel.BeginInvoke(new Action(() => panel.SetRows(rows)));
                }
                catch { }
            });
        }

        // Quick pass (multicast, ~1 s) shows the list straight away; the subnet scan then runs behind it and refreshes the list.
        static List<PanelRow> LocalSendRows(GlassPanel panel, bool scan = false)
        {
            LocalSendNode n = LocalSend.Node;
            var rows = new List<PanelRow>();
            if (n == null) { rows.Add(PanelRow.Title("LocalSend")); rows.Add(PanelRow.Info("Status", LocalSend.Status)); return rows; }
            rows.Add(PanelRow.Title("LocalSend", n.ReceiveEnabled, on => n.ReceiveEnabled = on));
            if (!scan)
            {
                n.Discover(false);
                System.Threading.Thread.Sleep(1200);
                Task.Run(() =>
                {
                    try { n.Discover(true); } catch { }
                    try { if (!panel.IsDisposed) panel.BeginInvoke(new Action(() => panel.SetRows(LocalSendRows(panel, true)))); } catch { }
                });
            }
            List<LsPeer> peers = n.Peers();
            rows.Add(PanelRow.Header("↓ RECEIVE"));
            rows.Add(PanelRow.Info("This device", n.Alias));
            string leaf = System.IO.Path.GetFileName(n.SaveDir.TrimEnd('\\', '/'));
            rows.Add(PanelRow.Item("Save to  (change...)", string.IsNullOrEmpty(leaf) ? n.SaveDir : leaf, false, LocalSend.ChooseFolder));
            PanelRow auto = PanelRow.Item("Autosave incoming files", "no prompt", LocalSend.AutoAccept, null);
            auto.KeepOpen = true;
            auto.Click = () => { LocalSend.AutoAccept = !LocalSend.AutoAccept; Background(panel, () => LocalSendRows(panel, true)); };
            rows.Add(auto);
            if (!n.ReceiveEnabled) rows.Add(PanelRow.Info("Receiving", "Off (switch above)"));
            rows.Add(PanelRow.Separator());
            int pend; lock (LocalSend.Pending) pend = LocalSend.Pending.Count;
            rows.Add(PanelRow.Header(pend > 0 ? "↑ SEND " + pend + (pend == 1 ? " FILE TO" : " FILES TO") : "↑ SEND TO"));
            if (pend > 0)
            {
                PanelRow clr = PanelRow.Item("Clear selected files", "", false, null);
                clr.KeepOpen = true;
                clr.Click = () => { lock (LocalSend.Pending) LocalSend.Pending.Clear(); Background(panel, () => LocalSendRows(panel, true)); };
                rows.Add(clr);
            }
            if (peers.Count == 0) rows.Add(PanelRow.Info("None found", "Open LocalSend on another device"));
            foreach (LsPeer p in peers.Take(8))
            {
                LsPeer peer = p;
                rows.Add(PanelRow.Item(peer.Alias, peer.Ip + (string.IsNullOrEmpty(peer.Model) ? "" : "  " + peer.Model), false, () => { if (pend > 0) LocalSend.SendPending(peer); else LocalSend.PickAndSend(peer); }));
            }
            rows.Add(PanelRow.Separator());
            PanelRow rescan = PanelRow.Button("Rescan", null);
            rescan.KeepOpen = true;
            rescan.Click = () => Background(panel, () => LocalSendRows(panel));
            rows.Add(rescan);
            if (!string.IsNullOrEmpty(LocalSend.Status)) rows.Add(PanelRow.Info("Last", LocalSend.Status));
            return rows;
        }

        static List<PanelRow> WifiRows(GlassPanel panel)
        {
            bool? radio = DeviceFeed.RadioOn("WiFi");
            List<WifiNetwork> networks = radio == false ? new List<WifiNetwork>() : Wifi.Scan();
            WifiNetwork current = networks.FirstOrDefault(n => n.Connected);

            var rows = new List<PanelRow>();
            rows.Add(PanelRow.Title("Wi-Fi", radio, on => Task.Run(() =>
            {
                DeviceFeed.SetRadio("WiFi", on);
                System.Threading.Thread.Sleep(on ? 2500 : 300);
                Background(panel, () => WifiRows(panel));
            })));

            if (radio == null && SystemInfo.Net == NetKind.Ethernet)
                rows.Add(PanelRow.Info("Ethernet", "Connected"));
            else if (radio == null)
                rows.Add(PanelRow.Info("Wi-Fi", "Not available on this PC"));
            else
                rows.Add(PanelRow.Info("Status", current != null ? "Connected to " + current.Name : (radio == true ? "Not connected" : "Off")));

            if (radio == true)
            {
                rows.Add(PanelRow.Separator());
                rows.Add(PanelRow.Header("Networks"));
                foreach (WifiNetwork net in networks.Take(8))
                {
                    WifiNetwork n = net;
                    string detail = (n.Security ?? "") + (n.Signal > 0 ? "  " + n.Signal + "%" : "");
                    PanelRow item = PanelRow.Item(n.Name, n.Connected ? "Connected - click to disconnect" : detail, n.Connected, null);
                    item.KeepOpen = true;
                    item.Click = () =>
                    {
                        item.Sub = n.Connected ? "Disconnecting..." : "Connecting...";
                        Task.Run(() =>
                        {
                            string result = n.Connected ? Wifi.Disconnect() : Wifi.Connect(n);
                            System.Threading.Thread.Sleep(2000);
                            Background(panel, () =>
                            {
                                List<PanelRow> r2 = WifiRows(panel);
                                if (result != null) r2.Insert(2, PanelRow.Info("Wi-Fi", result));
                                return r2;
                            });
                        });
                    };
                    rows.Add(item);
                }
                if (networks.Count == 0) rows.Add(PanelRow.Info("Networks", "None found"));
            }
            rows.Add(PanelRow.Separator());
            PanelRow rescan = PanelRow.Button("Rescan", null);
            rescan.KeepOpen = true;
            rescan.Click = () => Background(panel, () => WifiRows(panel));
            rows.Add(rescan);
            return rows;
        }

        static List<PanelRow> BluetoothRows(GlassPanel panel)
        {
            bool? radio = DeviceFeed.RadioOn("Bluetooth");
            List<BtDevice> devices = radio == true ? BtClassic.Paired() : new List<BtDevice>();
            List<string> le = radio == true ? DeviceFeed.ConnectedBluetooth() : new List<string>();

            var rows = new List<PanelRow>();
            rows.Add(PanelRow.Title("Bluetooth", radio, on => Task.Run(() =>
            {
                DeviceFeed.SetRadio("Bluetooth", on);
                System.Threading.Thread.Sleep(on ? 2000 : 300);
                Background(panel, () => BluetoothRows(panel));
            })));
            if (radio == null)
            {
                rows.Add(PanelRow.Info("Bluetooth", "Not available on this PC"));
            }
            else
            {
                rows.Add(PanelRow.Info("Status", radio == true ? "On" : "Off"));
                if (radio == true)
                {
                    rows.Add(PanelRow.Separator());
                    rows.Add(PanelRow.Header("My devices"));
                    foreach (BtDevice dev in devices.Take(8))
                    {
                        BtDevice d = dev;
                        PanelRow item = PanelRow.Item(d.Name, d.Connected ? "Connected - click to disconnect" : "Not connected - click to connect", d.Connected, null);
                        item.KeepOpen = true;
                        item.Click = () =>
                        {
                            item.Sub = d.Connected ? "Disconnecting..." : "Connecting...";
                            Task.Run(() =>
                            {
                                BtClassic.SetConnected(d.Address, !d.Connected);
                                System.Threading.Thread.Sleep(1500);
                                Background(panel, () => BluetoothRows(panel));
                            });
                        };
                        rows.Add(item);
                    }
                    foreach (string n in le)
                        if (!devices.Any(x => x.Name == n)) rows.Add(PanelRow.Info(n, "Connected"));
                    if (devices.Count == 0 && le.Count == 0) rows.Add(PanelRow.Info("Devices", "None paired"));
                }
            }
            rows.Add(PanelRow.Separator());
            return rows;
        }

        static List<PanelRow> SoundRows(List<string> outputs, string defaultName)
        {
            float volume = Audio.Volume;
            var rows = new List<PanelRow>();
            rows.Add(PanelRow.Title("Sound"));

            if (volume < 0)
            {
                rows.Add(PanelRow.Info("Output", "No audio device"));
            }
            else
            {
                rows.Add(PanelRow.Slider(volume, v => { Audio.Volume = (float)v; if (Audio.Muted) Audio.Muted = false; }));
                PanelRow mute = PanelRow.Item("Mute", null, Audio.Muted, null);
                mute.KeepOpen = true;
                mute.Click = () => { Audio.Muted = !Audio.Muted; mute.Checked = Audio.Muted; };
                rows.Add(mute);
            }

            rows.Add(PanelRow.Separator());
            rows.Add(PanelRow.Header("Output"));
            if (outputs == null) rows.Add(PanelRow.Info("Devices", "Loading..."));
            else
            {
                foreach (string o in outputs.Take(6)) rows.Add(PanelRow.Item(o, null, o == defaultName, Open("ms-settings:sound")));
                if (outputs.Count == 0) rows.Add(PanelRow.Info("Devices", "None found"));
            }
            rows.Add(PanelRow.Separator());
            rows.Add(PanelRow.Button("Sound Settings...", Open("ms-settings:sound")));
            return rows;
        }

        static string FormatTime(int seconds)
        {
            if (seconds < 0) return "Calculating...";
            return (seconds / 3600) + " h " + (seconds % 3600) / 60 + " min";
        }

        static int prevSaverLevel = 0; // threshold to restore when Battery Saver is switched off
        static List<PanelRow> BatteryRows()
        {
            SystemInfo.UpdateFast();
            var rows = new List<PanelRow>();
            int saverLvl = SetSys.BatterySaverLevel();
            if (SystemInfo.HasBattery && saverLvl >= 0)
            {
                bool saverOn = SetSys.BatterySaverOn() || saverLvl >= 100;
                rows.Add(PanelRow.Title("Battery", saverOn, on => Task.Run(() =>
                {
                    if (on) { int cur = SetSys.BatterySaverLevel(); if (cur >= 0 && cur < 100) prevSaverLevel = cur; SetSys.SetBatterySaverLevel(100); }
                    else SetSys.SetBatterySaverLevel(prevSaverLevel);
                })));
                rows.Add(PanelRow.Info("Battery Saver", SetSys.BatterySaverOn() ? "On" : saverLvl >= 100 ? "Armed (on when unplugged)" : "Off"));
            }
            else rows.Add(PanelRow.Title("Battery"));

            if (!SystemInfo.HasBattery)
            {
                rows.Add(PanelRow.Info("Battery", "None detected"));
                rows.Add(PanelRow.Info("Power source", "AC power"));
            }
            else
            {
                string status = SystemInfo.Charging ? "Charging" : SystemInfo.OnAc ? "Plugged in, not charging" : "On battery";
                rows.Add(PanelRow.Big(SystemInfo.BatteryPercent + "%", status));
                rows.Add(PanelRow.Info("Power source", SystemInfo.OnAc ? "AC power" : "Battery"));
                if (!SystemInfo.OnAc) rows.Add(PanelRow.Info("Time remaining", FormatTime(SystemInfo.BatterySecondsLeft)));
            }
            rows.Add(PanelRow.Separator());
            rows.Add(PanelRow.Button("Battery Settings...", Open("ms-settings:batterysaver")));
            return rows;
        }

        // Control Center: tiles are picked from a catalog (see CcCatalog) and saved as "@cctiles" in dock.txt. "Edit Controls"
        // switches the panel to edit mode: tap a tile to remove it, tap a "+" chip to add one. Wi-Fi, Bluetooth, Dark Mode,
        // Mute, Night Light, Battery saver, Airplane and Do Not Disturb are set in code; Stage/Mirroring/Widgets/Project use
        // the Windows shortcuts and Lock calls LockWorkStation.
        sealed class CcTile
        {
            public string Id, Name;
            public bool Tall;                       // spans two rows (Now Playing)
            public Func<CcState, PanelRow[]> Build; // one tile, or two round buttons sharing a cell
        }

        sealed class CcState { public bool? Wifi, Bluetooth; public string Ssid; public bool Edit; }

        static readonly string[] CcDefault = { "wifi", "now", "bluetooth", "localsend", "stage", "focus", "widgets", "nightlight", "project", "saver", "airplane", "shot", "rec", "dark" };

        static readonly List<CcTile> CcCatalog = BuildCcCatalog();

        static List<CcTile> BuildCcCatalog()
        {
            Func<string, string, Action, PanelRow> round = (glyph, name, click) => new PanelRow { Kind = RowKind.Round, Glyph = glyph, Text = name, Click = click };
            var l = new List<CcTile>();
            l.Add(new CcTile { Id = "wifi", Name = "Wi-Fi", Build = s =>
            {
                PanelRow w = RadioTile("Wi-Fi", "WiFi", s.Wifi, "wifi", "ms-settings:network-wifi");
                if (s.Wifi == true && !string.IsNullOrWhiteSpace(s.Ssid)) w.Sub = s.Ssid.Trim();
                w.Extra = Go("wifi");
                return new[] { w };
            } });
            l.Add(new CcTile { Id = "now", Name = "Now Playing", Tall = true, Build = s => new[] { new PanelRow { Kind = RowKind.Media, Text = "Now Playing" } } });
            l.Add(new CcTile { Id = "bluetooth", Name = "Bluetooth", Build = s => { PanelRow b = RadioTile("Bluetooth", "Bluetooth", s.Bluetooth, "bluetooth", "ms-settings:bluetooth"); b.Extra = Go("bluetooth"); return new[] { b }; } });
            l.Add(new CcTile { Id = "localsend", Name = "LocalSend", Build = s => new[] { PanelRow.Tile("LocalSend", "", "localsend", false, false, Go("localsend")) } });
            l.Add(new CcTile { Id = "stage", Name = "Stage + Mirroring", Build = s => new[] {
                round("stage", "Stage", () => WinUtil.Combo(WinUtil.VK_LWIN, 0x09)),
                round("mirror", "Mirroring", () => WinUtil.Combo(WinUtil.VK_LWIN, (byte)'K')) } });
            l.Add(new CcTile { Id = "focus", Name = "Do Not Disturb", Build = s => new[] { DndTile() } });
            l.Add(new CcTile { Id = "widgets", Name = "Widgets + Sidebar", Build = s => new[] {
                round("grid", "Widgets", () => WinUtil.Combo(WinUtil.VK_LWIN, (byte)'A')),
                round("sidebar", "Sidebar", () => WinUtil.Combo(WinUtil.VK_LWIN, (byte)'W')) } });
            l.Add(new CcTile { Id = "nightlight", Name = "Night Light", Build = s => new[] { NightLightTile() } });
            l.Add(new CcTile { Id = "project", Name = "Project", Build = s => new[] { PanelRow.Tile("Project", "", "project", false, false, () => WinUtil.Combo(WinUtil.VK_LWIN, (byte)'P')) } });
            l.Add(new CcTile { Id = "saver", Name = "Battery Saver", Build = s => new[] { BatterySaverTile() } });
            l.Add(new CcTile { Id = "airplane", Name = "Airplane", Build = s => new[] { AirplaneTile(s.Wifi, s.Bluetooth) } });
            l.Add(new CcTile { Id = "shot", Name = "Screenshot", Build = s => new[] { CaptureTile(false) } });
            l.Add(new CcTile { Id = "rec", Name = "Screen Record", Build = s => new[] { CaptureTile(true) } });
            l.Add(new CcTile { Id = "dark", Name = "Dark Mode", Build = s => new[] { DarkModeTile() } });
            l.Add(new CcTile { Id = "mute", Name = "Mute", Build = s => new[] { MuteTile() } });
            l.Add(new CcTile { Id = "lock", Name = "Lock Screen", Build = s => new[] { PanelRow.Tile("Lock Screen", "", "lock", false, false, () => { try { Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); } catch { } }) } });
            return l;
        }

        static List<string> CcLoad()
        {
            string v = SetCfg.Get("cctiles");
            if (v == null) return new List<string>(CcDefault);
            var ids = new List<string>();
            foreach (string p in v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (CcCatalog.Any(c => c.Id == p) && !ids.Contains(p)) ids.Add(p);
            return ids;
        }

        static void CcSave(List<string> ids)
        {
            SetCfg.Set("cctiles", string.Join(",", ids.ToArray()), true);
        }

        // Adds/removes a tile and keeps the Screenshot / Screen Record Settings toggles in step with it.
        public static void CcSetMember(string id, bool member)
        {
            var cur = CcLoad();
            if (member) { if (!cur.Contains(id)) cur.Add(id); } else cur.Remove(id);
            CcSave(cur);
            if (id == "shot") ScreenCapture.TileShot = member;
            else if (id == "rec") ScreenCapture.TileRec = member;
        }

        // Per-tile size flags: bit0 = wide, bit1 = tall. Persisted as "id:n,id:n" in the "ccsizes" setting.
        static Dictionary<string, int> ccSize = CcLoadSizes();

        static Dictionary<string, int> CcLoadSizes()
        {
            var d = new Dictionary<string, int>();
            string v = SetCfg.Get("ccsizes");
            if (v == null) return d;
            foreach (string p in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = p.Split(':');
                int n;
                if (kv.Length == 2 && int.TryParse(kv[1], out n) && n > 0 && n < 4) d[kv[0]] = n;
            }
            return d;
        }

        static void CcCycleSize(string id)
        {
            CcTile t = CcCatalog.First(c => c.Id == id);
            int cur = ccSize.ContainsKey(id) ? ccSize[id] : 0;
            int next = t.Tall ? (cur & 1) ^ 1 : (cur + 1) & 3;
            if (next == 0) ccSize.Remove(id); else ccSize[id] = next;
            SetCfg.Set("ccsizes", string.Join(",", ccSize.Select(k => k.Key + ":" + k.Value).ToArray()), true);
        }

        static int ccResizeBase;
        // Drag-resize: past ~40px right/down the tile grows, past ~40px left/up it shrinks. Returns true when the size changed.
        static bool CcDragSize(string id, int dx, int dy, bool fin)
        {
            int cur = ccSize.ContainsKey(id) ? ccSize[id] : 0;
            if (dx == 0 && dy == 0 && !fin) { ccResizeBase = cur; return false; }
            CcTile t = CcCatalog.First(c => c.Id == id);
            int b = ccResizeBase, n = b;
            if (dx > 40) n |= 1; else if (dx < -40) n &= ~1;
            if (!t.Tall) { if (dy > 40) n |= 2; else if (dy < -40) n &= ~2; }
            else n &= 1;
            if (n == cur) return false;
            if (n == 0) ccSize.Remove(id); else ccSize[id] = n;
            SetCfg.Set("ccsizes", string.Join(",", ccSize.Select(k => k.Key + ":" + k.Value).ToArray()), true);
            return true;
        }

        // Moves the dragged tile into the slot of the tile it was dropped on.
        static void CcReorder(string id, string target)
        {
            var cur = CcLoad();
            int i = cur.IndexOf(id), j = cur.IndexOf(target);
            if (i < 0 || j < 0 || i == j) return;
            cur.RemoveAt(i);
            cur.Insert(j, id);
            CcSave(cur);
        }

        static bool ccEdit;

        static List<PanelRow> ControlRows(GlassPanel panel)
        {
            bool edit = ccEdit;
            List<string> ids = CcLoad();
            var st = new CcState { Edit = edit };
            if (ids.Contains("wifi") || ids.Contains("airplane")) st.Wifi = DeviceFeed.RadioOn("WiFi");
            if (ids.Contains("bluetooth") || ids.Contains("airplane")) st.Bluetooth = DeviceFeed.RadioOn("Bluetooth");
            if (!edit && st.Wifi == true && ids.Contains("wifi"))
                try { WifiNetwork cur = Wifi.Scan().FirstOrDefault(n => n.Connected); if (cur != null) st.Ssid = cur.Name; } catch { }
            int brightness = Brightness.Get();
            float volume = Audio.Volume;

            const int W = 330, Col = 160, Gap = 10, Row = 64;
            int xr = W - Col;
            var rows = new List<PanelRow>();
            Action refresh = () => Background(panel, () => ControlRows(panel));

            int yl = 0, yr = 0;
            foreach (string id in ids)
            {
                CcTile t = CcCatalog.First(c => c.Id == id);
                if (id == "shot" && !ScreenCapture.TileShot && !edit) continue;
                if (id == "rec" && !ScreenCapture.TileRec && !edit) continue;
                int size = ccSize.ContainsKey(id) ? ccSize[id] : 0;
                bool wide = (size & 1) != 0, tall = t.Tall || (size & 2) != 0;
                bool left = yl <= yr;
                int x0 = wide ? 0 : (left ? 0 : xr), y0 = wide ? Math.Max(yl, yr) : (left ? yl : yr);
                int h = tall ? Row * 2 + Gap : Row;
                int cellW = wide ? W : Col;
                PanelRow[] cells = t.Build(st);
                string removeId = id;
                for (int i = 0; i < cells.Length; i++)
                {
                    PanelRow c = cells[i];
                    int cx = x0, cw = cellW;
                    if (cells.Length == 2) { cw = Row; cx = x0 + (i == 0 ? 0 : cellW - Row); }
                    c.Key = id + "#" + i;
                    if (edit)
                    {
                        if (c.Kind == RowKind.Tile) { c.Sub = ""; c.Active = false; c.Anim = 0; }
                        c.Click = null; c.Extra = null; c.Toggle = null;
                        c.DragId = id;
                        c.Drop = to => { CcReorder(removeId, to); refresh(); };
                    }
                    rows.Add(PanelRow.At(c, cx, y0, cw, h));
                }
                if (edit)
                {
                    string sizeLabel = t.Tall ? ((size & 1) != 0 ? "W" : "T") : new[] { "S", "W", "T", "L" }[size & 3];
                    rows.Add(PanelRow.At(new PanelRow { Kind = RowKind.Chip, Key = id + "!x", Text = "×", KeepOpen = true,
                        Click = () => { CcSetMember(removeId, false); refresh(); } }, x0 + 6, y0 + 6, 26, 22));
                    rows.Add(PanelRow.At(new PanelRow { Kind = RowKind.Chip, Key = id + "!s", Text = sizeLabel, KeepOpen = true,
                        Click = () => { CcCycleSize(removeId); refresh(); } }, x0 + cellW - 32, y0 + 6, 26, 22));
                    // Grip in the bottom-right corner: drag right/left for wide/narrow, down/up for tall/short.
                    rows.Add(PanelRow.At(new PanelRow { Kind = RowKind.Chip, Key = id + "!g", Text = "◢", KeepOpen = true,
                        Resize = (dx, dy, fin) => { if (CcDragSize(removeId, dx, dy, fin)) refresh(); } }, x0 + cellW - 30, y0 + h - 28, 24, 22));
                }
                if (wide) { yl = yr = y0 + h + Gap; }
                else if (left) yl += h + Gap; else yr += h + Gap;
            }
            int y5 = Math.Max(yl, yr);
            if (brightness >= 0)
            {
                PanelRow sun = PanelRow.Slider(brightness / 100.0, v => Brightness.SetLatest((int)Math.Round(v * 100)));
                sun.Sun = true; sun.Card = true; sun.Text = "Display";
                rows.Add(PanelRow.At(sun, 0, y5, W, Row));
                y5 += Row + Gap;
            }
            if (volume >= 0)
            {
                PanelRow sound = PanelRow.Slider(volume, v => { Audio.Volume = (float)v; if (Audio.Muted) Audio.Muted = false; });
                sound.Card = true; sound.Text = "Sound";
                sound.Extra = Open("ms-settings:sound");
                rows.Add(PanelRow.At(sound, 0, y5, W, Row));
                y5 += Row + Gap;
            }
            if (edit)
            {
                var hidden = CcCatalog.Where(c => !ids.Contains(c.Id)).ToList();
                for (int i = 0; i < hidden.Count; i++)
                {
                    string addId = hidden[i].Id;
                    rows.Add(PanelRow.At(new PanelRow { Kind = RowKind.Chip, Text = "+ " + hidden[i].Name, KeepOpen = true,
                        Click = () => { CcSetMember(addId, true); refresh(); } },
                        (i % 2) * xr, y5 + (i / 2) * 38, Col, 30));
                }
                y5 += ((hidden.Count + 1) / 2) * 38;
            }
            rows.Add(PanelRow.At(new PanelRow { Kind = RowKind.Chip, Text = edit ? "Done" : "Edit Controls", KeepOpen = true,
                Click = () => { ccEdit = !ccEdit; refresh(); } }, 103, y5, 124, 30));
            return rows;
        }

        static PanelRow MuteTile()
        {
            bool on = Audio.Volume >= 0 && Audio.Muted;
            PanelRow tile = PanelRow.Tile("Mute", on ? "Muted" : "Off", "mute", on, true, null);
            tile.KeepOpen = true;
            tile.Click = () => { Audio.Muted = !Audio.Muted; tile.Active = Audio.Muted; tile.Sub = tile.Active ? "Muted" : "Off"; };
            return tile;
        }

        // Do Not Disturb: Windows 11 keeps it in NOC_GLOBAL_SETTING_TOASTS_ENABLED (0 = banners off).
        static PanelRow DndTile()
        {
            bool on = DoNotDisturb.IsOn();
            PanelRow tile = PanelRow.Tile("Focus", on ? "On" : "Off", "moon", on, true, null);
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "On" : "Off";
                bool want = tile.Active;
                Task.Run(() => DoNotDisturb.Set(want));
            };
            return tile;
        }


        static PanelRow RadioTile(string title, string kind, bool? on, string glyph, string settings)
        {
            if (on == null) return PanelRow.Tile(title, "Not available", glyph, false, true, null);
            PanelRow tile = PanelRow.Tile(title, on.Value ? "On" : "Off", glyph, on.Value, true, null);
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "On" : "Off";
                bool want = tile.Active;
                Task.Run(() => DeviceFeed.SetRadio(kind, want));
            };
            return tile;
        }

        // Night Light toggles through Windows' stored switch (see NightLight). If that value isn't present on this PC
        // the tile opens the Night Light settings page instead.
        static PanelRow NightLightTile()
        {
            bool? on = NightLight.IsOn();
            if (on == null) return PanelRow.Tile("Night Light", "Opens Settings", "warm", false, true, Open("ms-settings:nightlight"));
            return PanelRow.Tile("Night Light", on.Value ? "On" : "Off", "warm", on.Value, true, Go("nightlight"));
        }

        // Night Light menu: on/off switch and the strength slider (colour temperature, 6500 K = 0% down to 1200 K = 100%).
        static List<PanelRow> NightLightRows()
        {
            bool? on = NightLight.IsOn();
            var rows = new List<PanelRow>();
            rows.Add(PanelRow.Title("Night Light", on, v => Task.Run(() => NightLight.Set(v))));
            int pct = NightLight.Strength();
            if (pct >= 0)
            {
                PanelRow s = PanelRow.Slider(pct / 100.0, v => NightLight.SetStrengthLatest((int)Math.Round(v * 100)));
                rows.Add(PanelRow.Header("Strength"));
                rows.Add(s);
            }
            else rows.Add(PanelRow.Info("Strength", "Not available"));
            rows.Add(PanelRow.Separator());
            rows.Add(PanelRow.Button("Night Light Settings...", Open("ms-settings:nightlight")));
            return rows;
        }

        // Battery saver: state comes from Windows' power status; there is no public toggle, so a click opens its Settings page.
        static PanelRow BatterySaverTile()
        {
            // Windows has no direct switch. Setting the "turn on at" level to 100% makes it engage whenever the PC runs on
            // battery (immediately if already unplugged); 0 turns that off. The tile reads the real state plus that setting.
            bool live = SetSys.BatterySaverOn();
            bool armed = SetSys.BatterySaverLevel() >= 100;
            PanelRow tile = PanelRow.Tile("Battery", live ? "Saver on" : armed ? "Saver armed" : "Saver off", "saver", live || armed, true, null);
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "Saver on" : "Saver off";
                bool want = tile.Active;
                Task.Run(() => SetSys.SetBatterySaverLevel(want ? 100 : 0));
            };
            return tile;
        }

        // Airplane mode = every radio off. Uses the same Radio API as Windows' own switch for Wi-Fi and Bluetooth.
        static PanelRow AirplaneTile(bool? wifi, bool? bt)
        {
            if (!wifi.HasValue && !bt.HasValue) return PanelRow.Tile("Airplane", "Not available", "airplane", false, true, Open("ms-settings:network-airplanemode"));
            bool on = wifi != true && bt != true;
            PanelRow tile = PanelRow.Tile("Airplane", on ? "On" : "Off", "airplane", on, true, null);
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "On" : "Off";
                bool air = tile.Active;
                Task.Run(() =>
                {
                    if (wifi.HasValue) DeviceFeed.SetRadio("WiFi", !air);
                    if (bt.HasValue) DeviceFeed.SetRadio("Bluetooth", !air);
                });
            };
            return tile;
        }

        static PanelRow CaptureTile(bool record)
        {
            if (!record) return PanelRow.Tile("Screenshot", "Capture screen", "shot", false, false, () => ScreenCapture.Screenshot());
            bool on = ScreenCapture.Recording;
            PanelRow tile = PanelRow.Tile("Record", on ? "Recording" : "Start", "rec", on, true, null);
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "Recording" : "Start";
                ScreenCapture.ToggleRecord();
            };
            return tile;
        }

        static PanelRow DarkModeTile()
        {
            bool on = DarkMode.IsOn();
            PanelRow tile = PanelRow.Tile("Dark Mode", on ? "On" : "Off", "dark", on, true, null);
            tile.WhiteWhenOn = true;
            tile.KeepOpen = true;
            tile.Click = () =>
            {
                tile.Active = !tile.Active;
                tile.Sub = tile.Active ? "On" : "Off";
                bool want = tile.Active;
                Task.Run(() => DarkMode.Set(want));
            };
            return tile;
        }

        static List<PanelRow> ProfileRows()
        {
            string role = "Standard user";
            try { role = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) ? "Administrator" : "Standard user"; }
            catch { }

            var rows = new List<PanelRow>();
            rows.Add(PanelRow.Big(Environment.UserName, Environment.UserDomainName + "\\" + Environment.UserName));
            rows.Add(PanelRow.Info("Computer", Environment.MachineName));
            rows.Add(PanelRow.Info("Account type", role));
            rows.Add(PanelRow.Separator());
            rows.Add(PanelRow.Button("Account Settings...", Open("ms-settings:yourinfo")));
            rows.Add(PanelRow.Button("Lock Screen", () => Process.Start("rundll32.exe", "user32.dll,LockWorkStation")));
            rows.Add(PanelRow.Button("Sign Out...", () =>
            {
                if (MessageBox.Show("Sign out of Windows now?", "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Process.Start("shutdown", "/l");
            }));
            return rows;
        }
    }
}
