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
    sealed class OnboardingForm : Form
    {
        const string Flag = "onboarded";
        static OnboardingForm open;

        public static void ShowIfFirstRun()
        {
            try { if (SetCfg.GetBool(Flag, false)) return; SetCfg.SetBool(Flag, true); Open(); }
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
                "Closing apps|Closing or force-quitting any app never closes MacDock." }
        };

        int page;
        readonly List<Tuple<Rectangle, Action>> hits = new List<Tuple<Rectangle, Action>>();
        Rectangle hot = Rectangle.Empty;
        Bitmap logo;
        readonly string face;

        OnboardingForm()
        {
            SetUi.Apply(SetUi.WantDark());
            face = SetUi.HasFont("Inter") ? "Inter" : (SetUi.HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI");
            Text = "Welcome to MacDock";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(560, 580);
            BackColor = SetUi.Content;
            DoubleBuffered = true;
            try { Icon = DockForm.LogoIcon(); } catch { }
            try { logo = DockForm.Logo(); } catch { }
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Enter) Next(); else if (e.KeyCode == Keys.Left && page > 0) { page--; Invalidate(); } };
            FormClosed += (s, e) => { if (logo != null) logo.Dispose(); open = null; };
        }

        void Next() { if (page >= Pages.Length - 1) Close(); else { page++; Invalidate(); } }

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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            hits.Clear();
            string[] p = Pages[page];
            int W = ClientSize.Width, y = 36;
            var cc = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
            var lf = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
            using (Font title = new Font(face, 22f, FontStyle.Bold, GraphicsUnit.Pixel), head = new Font(face, 15f, FontStyle.Bold, GraphicsUnit.Pixel),
                   body = new Font(face, 15f, FontStyle.Regular, GraphicsUnit.Pixel), small = new Font(face, 13f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                if (page == 0 && logo != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logo, (W - 96) / 2, y, 96, 96); y += 112; }
                TextRenderer.DrawText(g, p[0], title, new Rectangle(24, y, W - 48, 34), SetUi.Text, cc);
                y += 50;
                if (page == 0)
                {
                    TextRenderer.DrawText(g, p[1], body, new Rectangle(60, y, W - 120, 44), SetUi.Text, cc); y += 48;
                    TextRenderer.DrawText(g, p[2], body, new Rectangle(60, y, W - 120, 44), SetUi.Dim, cc);
                }
                else
                {
                    for (int i = 1; i < p.Length; i++)
                    {
                        string[] kv = p[i].Split('|');
                        Rectangle card = new Rectangle(32, y, W - 64, 56);
                        using (var gp = SetUi.Round(card, 10))
                        {
                            using (var b = new SolidBrush(SetUi.Card)) g.FillPath(b, gp);
                            using (var pen = new Pen(SetUi.CardLine)) g.DrawPath(pen, gp);
                        }
                        using (var b = new SolidBrush(SetUi.Accent)) g.FillEllipse(b, card.X + 14, card.Y + 22, 10, 10);
                        TextRenderer.DrawText(g, kv[0], head, new Rectangle(card.X + 36, card.Y + 8, card.Width - 48, 20), SetUi.Text, lf);
                        TextRenderer.DrawText(g, kv[1], small, new Rectangle(card.X + 36, card.Y + 30, card.Width - 48, 20), SetUi.Dim, lf);
                        y += 62;
                    }
                }
                // credits
                int cy = ClientSize.Height - 118;
                if (page == Pages.Length - 1)
                {
                    TextRenderer.DrawText(g, Credits.Line, small, new Rectangle(24, cy, W - 48, 18), SetUi.Dim, cc);
                    string[][] L = Credits.Links;
                    int tot = 0; int[] ws = new int[L.Length];
                    for (int i = 0; i < L.Length; i++) { ws[i] = TextRenderer.MeasureText(L[i][0], small).Width; tot += ws[i] + (i > 0 ? 22 : 0); }
                    int x = (W - tot) / 2;
                    TextRenderer.DrawText(g, "Follow us", small, new Rectangle(24, cy + 22, W - 48, 18), SetUi.Dim, cc);
                    for (int i = 0; i < L.Length; i++)
                    {
                        Rectangle r = new Rectangle(x, cy + 44, ws[i], 18);
                        string url = L[i][1];
                        hits.Add(Tuple.Create(r, (Action)(() => SetUi.OpenTarget(url))));
                        TextRenderer.DrawText(g, L[i][0], small, r, SetUi.Accent, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        if (r == hot) using (var pen = new Pen(SetUi.Accent)) g.DrawLine(pen, r.X, r.Bottom, r.Right, r.Bottom);
                        x += ws[i] + 22;
                    }
                }
                else TextRenderer.DrawText(g, Credits.Line, small, new Rectangle(24, cy + 22, W - 48, 18), SetUi.Dim, cc);

                // page dots
                int dx = (W - (Pages.Length * 16 - 6)) / 2, dy = ClientSize.Height - 66;
                for (int i = 0; i < Pages.Length; i++)
                    using (var b = new SolidBrush(i == page ? SetUi.Accent : SetUi.Track)) g.FillEllipse(b, dx + i * 16, dy, 10, 10);

                // buttons
                bool last = page == Pages.Length - 1;
                Rectangle next = new Rectangle(W - 24 - 112, ClientSize.Height - 54, 112, 34);
                Rectangle back = new Rectangle(24, ClientSize.Height - 54, 80, 34);
                Btn(g, next, last ? "Get Started" : "Next", true, head);
                hits.Add(Tuple.Create(next, (Action)Next));
                if (page > 0) { Btn(g, back, "Back", false, head); hits.Add(Tuple.Create(back, (Action)(() => { page--; Invalidate(); }))); }
                else
                {
                    Rectangle skip = new Rectangle(24, ClientSize.Height - 54, 60, 34);
                    TextRenderer.DrawText(g, "Skip", small, skip, SetUi.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    hits.Add(Tuple.Create(skip, (Action)Close));
                }
            }
        }

        void Btn(Graphics g, Rectangle r, string text, bool primary, Font f)
        {
            bool h = r == hot;
            using (var gp = SetUi.Round(r, 17))
            {
                Color fill = primary ? (h ? Color.FromArgb(40, 150, 255) : SetUi.Accent) : (h ? SetUi.HoverFill : SetUi.PillFill);
                using (var b = new SolidBrush(fill)) g.FillPath(b, gp);
                if (!primary) using (var pen = new Pen(SetUi.PillLine)) g.DrawPath(pen, gp);
            }
            TextRenderer.DrawText(g, text, f, r, primary ? Color.White : SetUi.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
