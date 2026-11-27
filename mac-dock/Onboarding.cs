using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacDock
{
    // Credits + social links. PLACEHOLDER URLs: confirm the real channel addresses and edit them here only.
    static class Credits
    {
        public const string Handle = "@gadgetcue";
        public const string Line = "This app is brought to you by " + Handle;
        public const string YouTube = "https://www.youtube.com/@gadgetcue";
        public const string Instagram = "https://www.instagram.com/gadgetcue";
        public const string Facebook = "https://www.facebook.com/gadgetcue1";
        public static readonly string[][] Links =
        {
            new[] { "YouTube", YouTube },
            new[] { "Instagram", Instagram },
            new[] { "Facebook", Facebook },
        };
    }

    // First-run welcome tour. Shown once (flag @onboarded in dock.txt), re-openable from Settings > General.
    // Fills the work area vertically; every pixel constant scales with the monitor DPI and the content is
    // measured, centred and (if the screen is short) shrunk so nothing overlaps.
    sealed class OnboardingForm : Form
    {
        const string Flag = "onboarded";
        static OnboardingForm open;

        // Bump for every release build, also when the version number stays the same: the "What's new" page shows once per value.
        const string ReleaseBuild = "20261127.2";
        const string SeenKey = "whatsnew-seen";

        // Fresh install: the whole welcome tour. After an update: only the "What's new" page. Each at most once.
        public static void ShowIfFirstRun()
        {
            try
            {
                string build = Application.ProductVersion + "+" + ReleaseBuild;
                bool first = !SetCfg.GetBool(Flag, false);
                if (!first && SetCfg.Get(SeenKey) == build) return;
                SetCfg.SetBool(Flag, true);
                SetCfg.Set(SeenKey, build, false);
                Program.Log((first ? "welcome tour" : "whats-new popup") + " shown for " + build);
                if (first) Open();
                else if (open == null || open.IsDisposed) { open = new OnboardingForm(); open.pages = new[] { Pages[1], Pages[4] }; open.Show(); }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static void Open()
        {
            if (open != null && !open.IsDisposed) { open.Activate(); return; }
            open = new OnboardingForm();
            open.Show();
        }

        
        static readonly string[][] Pages =
        {
            new[] { "Welcome to MacDock", "A macOS-style Dock, menu bar and Launchpad for Windows.", "Take a quick look around. This takes under a minute." },
            new[] { "What's new in 3.1",
                "Window previews|Close a window from its preview, and choose whether previews open on hover or on click.",
                "Settings, regrouped|Clearer categories, search that jumps to the setting, and macOS style pop-up menus.",
                "Sound and Night Light|Switch the output device from the Sound panel. Night Light now changes the screen as you drag.",
                "Faster and steadier|Apps open at normal speed from the dock, and Send with LocalSend also takes folders." },
            new[] { "What you get",
                "Dock|Magnifying icons, genie minimize, folders that fan open.",
                "Launchpad|Every app in one grid. Swipe with four fingers or use the shortcut.",
                "Spotlight|Search apps and files from anywhere.",
                "Menu bar & island|Control Center, notifications, tray icons and live status.",
                "Mission Control|See every window at once and switch spaces." },
            new[] { "Quick setup tips",
                "Pin apps|Drag any app or folder onto the Dock. Right-click an icon for options.",
                "Make it yours|Open MacDock Settings from the menu bar for size, theme and icons.",
                "Start with Windows|Turn on Open MacDock at login in Settings > General.",
                "Closing apps|Closing or force-quitting any app never closes MacDock." },
            new[] { SupportTitle, "MacDock is free, with no ads and no tracking. If it saves you time, you can buy me a coffee.", DonationCard.Hint }
        };
        const string SupportTitle = "Follow and support";
        string[][] pages;

        const int BaseW = 560;
        int page;
        readonly List<Tuple<Rectangle, Action>> hits = new List<Tuple<Rectangle, Action>>();
        Rectangle hot = Rectangle.Empty;
        Bitmap logo;
        readonly float testScale;

        OnboardingForm(float forceScale = 0f, int forceWorkH = 0)
        {
            SetUi.Apply(SetUi.WantDark());
            testScale = forceScale;
            pages = Pages;
            Text = "Welcome to MacDock";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = SetUi.Content;
            DoubleBuffered = true;
            // Full work-area height on the screen under the cursor; width follows that monitor's DPI.
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            float s = forceScale > 0 ? forceScale : Dpi.Of(wa);
            int wh = forceWorkH > 0 ? forceWorkH : wa.Height;
            ClientSize = new Size(Math.Min((int)Math.Round(BaseW * s), wa.Width), 300);
            Size = new Size(Width, wh);
            if (forceScale <= 0) Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y);
            try { Icon = DockForm.LogoIcon(); } catch { }
            try { logo = DockForm.Logo(); } catch { }
            KeyPreview = true;
            KeyDown += (a, e) => { if (e.KeyCode == Keys.Escape) Close(); else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Enter) Next(); else if (e.KeyCode == Keys.Left && page > 0) { page--; Invalidate(); } };
            FormClosed += (a, e) => { if (logo != null) logo.Dispose(); open = null; };
        }

        // Debug hook: MacDock.exe --tour-shot <scale> <workAreaHeightPx> <page> <out.png> renders the tour offscreen.
        public static int Shot(string[] a)
        {
            try
            {
                float sc = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture);
                int wh = int.Parse(a[1]); int pg = int.Parse(a[2]);
                using (var f = new OnboardingForm(sc, wh))
                {
                    f.page = pg; f.CreateControl();
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(a[3], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return 0;
            }
            catch (Exception ex) { Program.Log(ex); return 1; }
        }

        float Scale() { return testScale > 0 ? testScale : (IsHandleCreated ? Dpi.Of(Handle) : 1f); }
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        void Next() { if (page >= pages.Length - 1) Close(); else { page++; Invalidate(); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Rectangle h = Rectangle.Empty;
            foreach (var t in hits) if (t.Item1.Contains(e.Location)) { h = t.Item1; break; }
            Cursor = h.IsEmpty ? Cursors.Default : Cursors.Hand;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            foreach (var t in hits) if (t.Item1.Contains(e.Location)) { t.Item2(); break; }
            base.OnMouseUp(e);
        }

        const TextFormatFlags CC = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
        const TextFormatFlags LF = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

        static int TH(string t, Font f, int w, TextFormatFlags fl) { return TextRenderer.MeasureText(t, f, new Size(Math.Max(10, w), 0), fl).Height; }
        float ptk = 0.75f; // points per pixel for the current graphics DPI
        static int I(float v) { return (int)Math.Round(v); }

        static bool IsSplash(string[] p) { return p == Pages[0]; }

        // Natural (unstretched) content height at unit scale u; cardH gets each card's height.
        int Measure(float u, int W, out int[] cardH)
        {
            Font title = Typography.Text(22f * ptk * u, Weight.Semibold), head = Typography.Text(15f * ptk * u, Weight.Semibold),
                 body = Typography.Text(15f * ptk * u), small = Typography.Text(13f * ptk * u);
            string[] p = pages[page];
            cardH = new int[Math.Max(0, p.Length - 1)];
            int h = 0;
            if (p[0] == SupportTitle)
                return TH(p[0], title, W - I(48 * u), CC) + I(10 * u) + TH(p[1], small, W - I(96 * u), CC) + I(18 * u) + I(DonationCard.Height * u) + I(12 * u) + TH(p[2], small, W - I(96 * u), CC);
            if (IsSplash(p))
            {
                if (logo != null) h += I(96 * u) + I(16 * u);
                h += TH(p[0], title, W - I(48 * u), CC) + I(14 * u);
                h += TH(p[1], body, W - I(120 * u), CC) + I(8 * u);
                h += TH(p[2], body, W - I(120 * u), CC);
                return h;
            }
            h += TH(p[0], title, W - I(48 * u), CC) + I(16 * u);
            for (int i = 1; i < p.Length; i++)
            {
                string[] kv = p[i].Split('|');
                int tw = W - I(64 * u) - I(48 * u);
                cardH[i - 1] = Math.Max(I(56 * u), I(10 * u) + TH(kv[0], head, tw, LF) + I(4 * u) + TH(kv[1], small, tw, LF) + I(10 * u));
                h += cardH[i - 1] + (i > 1 ? I(8 * u) : 0);
            }
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            hits.Clear(); ptk = 72f / g.DpiY;
            string[] p = pages[page];
            float s = Scale();
            int W = ClientSize.Width, H = ClientSize.Height;
            int top = I(28 * s), bottomZone = I(150 * s);
            int avail = H - top - bottomZone;
            int[] ch;
            float u = s;
            int nat = Measure(u, W, out ch);
            if (nat > avail && nat > 0) { u = s * Math.Max(0.6f, (float)avail / nat); nat = Measure(u, W, out ch); }
            Font title = Typography.Text(22f * ptk * u, Weight.Semibold), head = Typography.Text(15f * ptk * u, Weight.Semibold),
                 body = Typography.Text(15f * ptk * u), small = Typography.Text(13f * ptk * u);
            Font bf = Typography.Text(15f * ptk * s, Weight.Semibold), sf = Typography.Text(13f * ptk * s);
            int extra = Math.Max(0, avail - nat);
            int y;
            if (p[0] == SupportTitle)
            {
                y = top + extra / 2;
                int th = TH(p[0], title, W - I(48 * u), CC);
                TextRenderer.DrawText(g, p[0], title, new Rectangle(I(24 * u), y, W - I(48 * u), th), SetUi.Text, CC); y += th + I(10 * u);
                th = TH(p[1], small, W - I(96 * u), CC);
                TextRenderer.DrawText(g, p[1], small, new Rectangle(I(48 * u), y, W - I(96 * u), th), SetUi.Dim, CC); y += th + I(18 * u);
                DonationCard.Draw(g, (W - I(DonationCard.Width * u)) / 2, y, u); y += I(DonationCard.Height * u) + I(12 * u);
                th = TH(p[2], small, W - I(96 * u), CC);
                TextRenderer.DrawText(g, p[2], small, new Rectangle(I(48 * u), y, W - I(96 * u), th), SetUi.Dim, CC);
            }
            else if (IsSplash(p))
            {
                y = top + extra / 2;
                if (logo != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; int ls = I(96 * u); g.DrawImage(logo, (W - ls) / 2, y, ls, ls); y += ls + I(16 * u); }
                int hh = TH(p[0], title, W - I(48 * u), CC);
                TextRenderer.DrawText(g, p[0], title, new Rectangle(I(24 * u), y, W - I(48 * u), hh), SetUi.Text, CC); y += hh + I(14 * u);
                hh = TH(p[1], body, W - I(120 * u), CC);
                TextRenderer.DrawText(g, p[1], body, new Rectangle(I(60 * u), y, W - I(120 * u), hh), SetUi.Text, CC); y += hh + I(8 * u);
                hh = TH(p[2], body, W - I(120 * u), CC);
                TextRenderer.DrawText(g, p[2], body, new Rectangle(I(60 * u), y, W - I(120 * u), hh), SetUi.Dim, CC);
            }
            else
            {
                int n = p.Length - 1;
                int grow = n > 0 ? Math.Min(extra / 2 / n, I(40 * u)) : 0; // stretch cards a little, centre the rest
                int used = nat + grow * n;
                y = top + Math.Max(0, (avail - used) / 2);
                int hh = TH(p[0], title, W - I(48 * u), CC);
                TextRenderer.DrawText(g, p[0], title, new Rectangle(I(24 * u), y, W - I(48 * u), hh), SetUi.Text, CC); y += hh + I(16 * u);
                int pad = I(32 * u);
                for (int i = 1; i < p.Length; i++)
                {
                    string[] kv = p[i].Split('|');
                    int cardHt = ch[i - 1] + grow;
                    Rectangle card = new Rectangle(pad, y, W - 2 * pad, cardHt);
                    using (var gp = SetUi.Round(card, I(10 * u)))
                    {
                        using (var b = new SolidBrush(SetUi.Card)) g.FillPath(b, gp);
                        using (var pen = new Pen(SetUi.CardLine)) g.DrawPath(pen, gp);
                    }
                    int tx = card.X + I(36 * u), tw = card.Width - I(48 * u);
                    int h1 = TH(kv[0], head, tw, LF), h2 = TH(kv[1], small, tw, LF);
                    int ty = card.Y + (cardHt - (h1 + I(4 * u) + h2)) / 2;
                    int dot = I(10 * u);
                    using (var b = new SolidBrush(SetUi.Accent)) g.FillEllipse(b, card.X + I(14 * u), ty + (h1 - dot) / 2, dot, dot);
                    TextRenderer.DrawText(g, kv[0], head, new Rectangle(tx, ty, tw, h1), SetUi.Text, LF);
                    TextRenderer.DrawText(g, kv[1], small, new Rectangle(tx, ty + h1 + I(4 * u), tw, h2), SetUi.Dim, LF);
                    y += cardHt + I(8 * u);
                }
            }

            // bottom zone: credits, page dots, buttons, anchored to the bottom and scaled by the DPI
            int btnH = I(34 * s), m = I(24 * s);
            int btnY = H - m - btnH;
            int dotY = btnY - I(26 * s);
            int dsz = I(10 * s), dstep = I(16 * s);
            int lh = I(24 * s);
            int cy = dotY - I(70 * s);
            if (page == pages.Length - 1)
            {
                TextRenderer.DrawText(g, Credits.Line, sf, new Rectangle(m, cy, W - 2 * m, lh), SetUi.Dim, CC);
                string[][] L = Credits.Links;
                int tot = 0; int[] ws = new int[L.Length]; int sp = I(22 * s);
                for (int i = 0; i < L.Length; i++) { ws[i] = TextRenderer.MeasureText(L[i][0], sf).Width; tot += ws[i] + (i > 0 ? sp : 0); }
                int x = (W - tot) / 2;
                TextRenderer.DrawText(g, "Follow us", sf, new Rectangle(m, cy + I(24 * s), W - 2 * m, lh), SetUi.Dim, CC);
                for (int i = 0; i < L.Length; i++)
                {
                    Rectangle r = new Rectangle(x, cy + I(48 * s), ws[i], lh);
                    string url = L[i][1];
                    hits.Add(Tuple.Create(r, (Action)(() => SetUi.OpenTarget(url))));
                    TextRenderer.DrawText(g, L[i][0], sf, r, SetUi.Accent, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    if (r == hot) using (var pen = new Pen(SetUi.Accent)) g.DrawLine(pen, r.X, r.Bottom, r.Right, r.Bottom);
                    x += ws[i] + sp;
                }
            }
            else TextRenderer.DrawText(g, Credits.Line, sf, new Rectangle(m, cy + I(24 * s), W - 2 * m, lh), SetUi.Dim, CC);

            int dx = (W - (pages.Length * dstep - (dstep - dsz))) / 2;
            for (int i = 0; i < pages.Length; i++)
                using (var b = new SolidBrush(i == page ? SetUi.Accent : SetUi.Track)) g.FillEllipse(b, dx + i * dstep, dotY, dsz, dsz);

            bool last = page == pages.Length - 1;
            int nw = I(112 * s);
            Rectangle next = new Rectangle(W - m - nw, btnY, nw, btnH);
            Rectangle back = new Rectangle(m, btnY, I(80 * s), btnH);
            Btn(g, next, !last ? "Next" : pages[0] != Pages[0] ? "Done" : "Get Started", true, bf, s);
            hits.Add(Tuple.Create(next, (Action)Next));
            if (page > 0) { Btn(g, back, "Back", false, bf, s); hits.Add(Tuple.Create(back, (Action)(() => { page--; Invalidate(); }))); }
            else if (!last)
            {
                Rectangle skip = new Rectangle(m, btnY, I(60 * s), btnH);
                TextRenderer.DrawText(g, "Skip", sf, skip, SetUi.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                hits.Add(Tuple.Create(skip, (Action)Close));
            }
        }

        void Btn(Graphics g, Rectangle r, string text, bool primary, Font f, float s)
        {
            bool h = r == hot;
            using (var gp = SetUi.Round(r, I(17 * s)))
            {
                Color fill = primary ? (h ? Color.FromArgb(40, 150, 255) : SetUi.Accent) : (h ? SetUi.HoverFill : SetUi.PillFill);
                using (var b = new SolidBrush(fill)) g.FillPath(b, gp);
                if (!primary) using (var pen = new Pen(SetUi.PillLine)) g.DrawPath(pen, gp);
            }
            TextRenderer.DrawText(g, text, f, r, primary ? Color.White : SetUi.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
