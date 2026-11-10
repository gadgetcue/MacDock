// macOS-style menu bar along the top of the screen, with working dropdown menus.
// The logo, File, Edit, View and Window menus act on the app that was in front when the menu opened.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    sealed class MenuEntry
    {
        public string Text;
        public string Shortcut;
        public Action Action;
        public bool Separator;
        public bool Disabled;
        public bool Checked;
        public Color? Dot; // colour swatch drawn at the right edge

        public static MenuEntry Sep() { return new MenuEntry { Separator = true }; }

        public static MenuEntry Item(string text, string shortcut, Action action)
        {
            return new MenuEntry { Text = text, Shortcut = shortcut, Action = action, Disabled = action == null };
        }
    }

    // The frosted dropdown. Never takes focus, so the app underneath keeps receiving the menu's keystrokes.
    sealed class MenuPopup : Form
    {
        const int ItemHeight = 24, SepHeight = 9, PadY = 6, PadX = 14, Radius = 10;

        readonly List<MenuEntry> entries;
        readonly Action closed;
        readonly Rectangle[] rows;
        readonly Timer timer = new Timer();
        readonly Font font = Typography.Text(9.75f);
        readonly int barHeight;
        Surface surface;
        Bitmap backdrop;
        public bool launchpadGlass; // dock menu: Launchpad-style glass (set before the window is shown)
        int hover = -1;
        bool wasDown;
        int blurDivisor;

        // Blurred snapshot of the desktop behind the menu, taken at its FINAL position (callers move it after
        // construction) and before it becomes visible.
        void CaptureBackdrop()
        {
            if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
            try
            {
                // Capture a margin around the menu so the blur has real neighbours at the edges (like Launchpad's
                // full-screen capture), then crop back to the menu.
                Rectangle vs = SystemInformation.VirtualScreen;
                Rectangle cap = Rectangle.Intersect(Rectangle.Inflate(Bounds, 64, 64), vs);
                using (var shot = new Bitmap(cap.Width, cap.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (Graphics sg = Graphics.FromImage(shot)) sg.CopyFromScreen(cap.Location, Point.Empty, cap.Size);
                    using (Bitmap big = GlassPanel.BlurBitmap(shot, blurDivisor))
                    {
                        var crop = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                        using (Graphics cg = Graphics.FromImage(crop)) cg.DrawImage(big, new Rectangle(0, 0, Width, Height), new Rectangle(Left - cap.Left, Top - cap.Top, Width, Height), GraphicsUnit.Pixel);
                        backdrop = crop;
                    }
                }
            }
            catch { backdrop = null; }
        }

        // Open: a ~120 ms ease-out fade of the whole window. Dismiss: an ~80 ms fade. Only the window opacity changes
        // per frame (the bitmap and the blur window stay put), so it costs next to nothing.
        const double FadeInMs = 120, FadeOutMs = 80;
        readonly Stopwatch clock = Stopwatch.StartNew();
        double animStart;
        bool closing, faded;

        public MenuPopup(List<MenuEntry> entries, Point anchor, int barHeight, Action closed, int blurDivisor = 20, int bottomLimit = int.MaxValue)
        {
            this.entries = entries;
            this.closed = closed;
            this.barHeight = barHeight;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            float textW = 0, shortcutW = 0;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                foreach (MenuEntry e in entries.Where(x => !x.Separator))
                {
                    textW = Math.Max(textW, g.MeasureString(e.Text, font).Width);
                    if (!string.IsNullOrEmpty(e.Shortcut)) shortcutW = Math.Max(shortcutW, g.MeasureString(e.Shortcut, font).Width);
                }
            }

            int width = Math.Max(210, (int)(PadX * 2 + 18 + textW + (shortcutW > 0 ? 28 + shortcutW : 0)));
            rows = new Rectangle[entries.Count];
            int y = PadY;
            for (int i = 0; i < entries.Count; i++)
            {
                int h = entries[i].Separator ? SepHeight : ItemHeight;
                rows[i] = new Rectangle(0, y, width, h);
                y += h;
            }
            int height = y + PadY;

            Rectangle area = Screen.FromPoint(anchor).Bounds;
            int left = Math.Max(area.Left + 4, Math.Min(anchor.X, area.Right - width - 4));
            // Keep the whole menu above the screen edge (and above the dock when given): flip upward, else shift.
            int top = anchor.Y;
            int limit = Math.Min(area.Bottom - 4, bottomLimit);
            if (top + height > limit)
            {
                int up = anchor.Y - height - 4;
                top = (up >= area.Top + 4 && up + height <= limit) ? up : Math.Max(area.Top + 4, limit - height);
            }
            Bounds = new Rectangle(left, top, width, height);

            this.blurDivisor = blurDivisor;

            timer.Interval = 16;
            timer.Tick += (s, e) => Poll();
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
            CaptureBackdrop();
            animStart = clock.Elapsed.TotalMilliseconds;
            if (vsync == null) vsync = new VsyncDriver(this, AnimateFade);
            Redraw();
            vsync.Wake(); // fade in at the display's refresh rate
        }

        VsyncDriver vsync;

        // Fades the menu out, then closes it. Picking an item still closes at once.
        void Dismiss()
        {
            if (closing) return;
            closing = true;
            animStart = clock.Elapsed.TotalMilliseconds;
            vsync.Wake();
        }

        byte Opacity255()
        {
            double t = (clock.Elapsed.TotalMilliseconds - animStart) / (closing ? FadeOutMs : FadeInMs);
            t = Math.Max(0, Math.Min(1, t));
            if (closing) return (byte)Math.Round(255 * (1 - t));
            double eased = 1 - Math.Pow(1 - t, 3);
            return (byte)Math.Round(255 * eased);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            int index = RowAt(e.Location);
            if (index < 0 || entries[index].Separator || entries[index].Disabled) return;

            Action action = entries[index].Action;
            Close();
            if (action != null) action();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            vsync.Dispose();
            base.OnFormClosed(e);
            MemTrim.Soon(400);
            if (closed != null) closed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (surface != null) surface.Dispose();
                if (backdrop != null) backdrop.Dispose();
            }
            base.Dispose(disposing);
        }

        int RowAt(Point p)
        {
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].Contains(p)) return i;
            return -1;
        }

        // Tracks hover and closes the menu on a click outside it. Clicks on the bar are left to the bar.
        void Poll()
        {
            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            Point screen = new Point(p.X, p.Y);

            int index = Bounds.Contains(screen) ? RowAt(PointToClient(screen)) : -1;
            if (index >= 0 && (entries[index].Separator || entries[index].Disabled)) index = -1;
            if (index != hover)
            {
                hover = index;
                Redraw();
            }

            bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (down && !wasDown && !Bounds.Contains(screen) && screen.Y >= barHeight) Dismiss();
            wasDown = down;
            if ((Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0) Dismiss(); // Esc
        }

        // One fade step per display refresh; the driver goes back to sleep once the fade ends.
        void AnimateFade()
        {
            if (IsDisposed || !IsHandleCreated) return;
            byte opacity = Opacity255();
            if (surface != null) surface.Present(Handle, Left, Top, opacity);
            if (closing && opacity == 0) { vsync.Sleep(); Close(); return; }
            if (!closing && opacity == 255) { faded = true; vsync.Sleep(); }
        }

        void Redraw()
        {
            if (!IsHandleCreated) return;
            if (surface == null) surface = new Surface(Width, Height);

            bool light = Theme.Light; // white in light mode, dark grey in dark mode
            Color fill = light ? Color.FromArgb(backdrop != null ? 150 : 255, 250, 250, 253) : Color.FromArgb(backdrop != null ? 150 : 255, 34, 36, 44); // translucent over the blurred snapshot
            Color border = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(44, 255, 255, 255);
            Color text = light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(240, 240, 244);
            Color muted = light ? Color.FromArgb(120, 60, 60, 67) : Color.FromArgb(150, 235, 235, 245);
            Color separator = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255);
            Color highlight = light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(66, 255, 255, 255); // translucent, no blue

            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Typography.Prepare(g);

                GlassPanel.DrawLaunchpadGlass(g, new Rectangle(0, 0, Width - 1, Height - 1), Radius, backdrop, Point.Empty);

                var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                var right = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };

                for (int i = 0; i < entries.Count; i++)
                {
                    MenuEntry e = entries[i];
                    Rectangle row = rows[i];

                    if (e.Separator)
                    {
                        using (var pen = new Pen(separator))
                            g.DrawLine(pen, PadX, row.Top + row.Height / 2, Width - PadX, row.Top + row.Height / 2);
                        continue;
                    }

                    bool hot = i == hover;
                    if (hot)
                    {
                        var hl = new RectangleF(6, row.Top, Width - 12, row.Height);
                        using (GraphicsPath hp = Layered.RoundedRect(hl, 5))
                        using (var b = new SolidBrush(highlight))
                            g.FillPath(b, hp);
                    }

                    Color main = hot ? (light ? text : Color.White) : (e.Disabled ? muted : text);
                    Color sub = hot ? (light ? muted : Color.FromArgb(210, 255, 255, 255)) : muted;
                    using (var mb = new SolidBrush(main))
                    using (var sb = new SolidBrush(sub))
                    {
                        if (e.Checked) g.DrawString("✓", font, mb, new RectangleF(PadX - 4, row.Top, 18, row.Height), left);
                        g.DrawString(e.Text, font, mb, new RectangleF(PadX + 18, row.Top, Width - PadX * 2 - 18, row.Height), left);
                        if (!string.IsNullOrEmpty(e.Shortcut))
                            g.DrawString(e.Shortcut, font, sb, new RectangleF(PadX, row.Top, Width - PadX * 2, row.Height), right);
                        if (e.Dot.HasValue)
                        {
                            float d = 12, dx = Width - PadX - d + 2, dy = row.Top + (row.Height - d) / 2;
                            using (var db = new SolidBrush(e.Dot.Value)) g.FillEllipse(db, dx, dy, d, d);
                            using (var dp = new Pen(Color.FromArgb(70, 128, 128, 128))) g.DrawEllipse(dp, dx, dy, d, d);
                        }
                    }
                }
            }
            surface.Present(Handle, Left, Top, faded ? (byte)255 : Opacity255());
        }
    }

    sealed class TopBar : Form
    {
        public const int BarHeight = 30;
        const int LogoMenu = 0, AppMenu = 1;

        sealed class Hit
        {
            public RectangleF Rect;
            public Action Click;
        }

        readonly Timer timer = new Timer();
        readonly Font regular = Typography.Text(9.25f, Weight.Medium);
        readonly Font bold = Typography.Text(9.25f, Weight.Semibold);
        readonly Font small = Typography.Text(8.5f, Weight.Medium);
        readonly Font badgeFont = Typography.Text(6.5f, Weight.Semibold);
        readonly List<Hit> hits = new List<Hit>();
        readonly Dictionary<string, string> appNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly RectangleF[] titleRects = new RectangleF[7];
        readonly Dictionary<string, RectangleF> iconRects = new Dictionary<string, RectangleF>();
        readonly uint self;

        static int volumePercent;

        Surface surface;
        MenuPopup popup;
        GlassPanel panel;
        string panelId;
        int openMenu = -1;
        int hoverTitle = -1;
        int tick;
        string lastKey;

        IntPtr target = IntPtr.Zero;
        string targetExe;
        string appName = "Desktop";
        bool appBarRegistered;

        public event Action BellClicked;
        public event Action SearchClicked;

        public TopBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            using (Process me = Process.GetCurrentProcess()) self = (uint)me.Id;

            Rectangle screen = Screen.PrimaryScreen.Bounds;
            Bounds = new Rectangle(screen.Left, screen.Top, screen.Width, BarHeight);

            Theme.Refresh();
            SystemInfo.UpdateFast();
            SystemInfo.UpdateNet();

            Panels.Navigate += id => { if (iconRects.ContainsKey(id) || id == "localsend" || id == "nightlight") TogglePanel(id); };

            timer.Interval = 100;
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        // Always (re)opens the panel, e.g. when files arrive from Explorer's right-click menu.
        public void OpenPanel(string id)
        {
            if (panel == null || panelId != id) { TogglePanel(id); return; }
            ClosePanel();
            var t = new Timer { Interval = 450 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (panel == null || panelId != id) TogglePanel(id); };
            t.Start();
        }

        // Opens the panel under its status icon, or closes it if it is already open.
        void TogglePanel(string id)
        {
            bool same = panel != null && panelId == id;
            ClosePanel();
            CloseMenu();
            if (same) return;

            RectangleF r = iconRects.ContainsKey(id) ? iconRects[id] : (id == "localsend" || id == "nightlight") && iconRects.ContainsKey("control") ? iconRects["control"] : new RectangleF(Width - 100, 0, 30, BarHeight);
            panelId = id;
            panel = Panels.Create(id, new Point(Left + (int)(r.Left + r.Width / 2), Top + BarHeight), BarHeight, () =>
            {
                panel = null;
                panelId = null;
            });
            panel.Show();
        }

        void ClosePanel()
        {
            if (panel != null) panel.BeginClose();
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
            ReserveScreenSpace();
            Redraw();
        }

        // Registers the bar as a screen-edge toolbar so maximized windows start below it.
        void ReserveScreenSpace()
        {
            try
            {
                Rectangle screen = Screen.PrimaryScreen.Bounds;
                var data = new Win32.APPBARDATA();
                data.cbSize = Marshal.SizeOf(typeof(Win32.APPBARDATA));
                data.hWnd = Handle;
                data.uCallbackMessage = Win32.RegisterWindowMessage("MacDockAppBar");
                Win32.SHAppBarMessage(0, ref data); // ABM_NEW

                data.uEdge = 1; // top
                data.rc = new Win32.RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Top + BarHeight };
                Win32.SHAppBarMessage(2, ref data); // ABM_QUERYPOS
                data.rc.Bottom = data.rc.Top + BarHeight;
                Win32.SHAppBarMessage(3, ref data); // ABM_SETPOS
                appBarRegistered = true;
            }
            catch { }
        }

        void ReleaseScreenSpace()
        {
            if (!appBarRegistered) return;
            appBarRegistered = false;
            var data = new Win32.APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(Win32.APPBARDATA));
            data.hWnd = Handle;
            Win32.SHAppBarMessage(1, ref data); // ABM_REMOVE
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (popup != null) popup.Close();
                if (panel != null) panel.Close();
                ReleaseScreenSpace();
                if (surface != null) surface.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- polling ----------

        // Spans the top of whichever display is the main one now (it can change: a laptop panel, an external monitor, a new
        // resolution), and moves the screen-edge reservation with it.
        internal void Reposition()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            var want = new Rectangle(screen.Left, screen.Top, screen.Width, BarHeight);
            if (Bounds == want) return;
            ReleaseScreenSpace();
            Bounds = want;
            ReserveScreenSpace();
            Redraw();
        }

        void Tick()
        {
            tick++;
            if (Screen.PrimaryScreen.Bounds.Width != Width || Screen.PrimaryScreen.Bounds.Left != Left) Reposition();

            // Step aside while a fullscreen app is in front (the dock does the same).
            bool away = Fullscreen.Active;
            if (away && Visible)
            {
                CloseMenu();
                ClosePanel();
                Hide();
            }
            else if (!away && !Visible)
            {
                Show();
                lastKey = null;
                Redraw();
            }
            if (!Visible) return;

            if (tick % 600 == 0)
            {
                bool wasLight = Theme.Light;
                Theme.Refresh();
                if (wasLight != Theme.Light) WindowTheme.ThemeChanged();
                if (wasLight != Theme.Light && DockForm.Instance != null) { IconPack.Reset(); MissionControl.ResetIcons(); DockForm.Instance.RefreshIcons(); }
            }
            if (tick % 5 == 0)
            {
                SystemInfo.UpdateFast();
                float volume = Audio.Volume;
                volumePercent = volume < 0 ? 0 : (int)Math.Round(volume * 100);
            }
            if (tick % 50 == 0) SystemInfo.UpdateNet();
            UpdateForeground();

            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            Point local = PointToClient(new Point(p.X, p.Y));
            int hover = -1;
            if (local.Y >= 0 && local.Y < BarHeight)
            {
                for (int i = 0; i < titleRects.Length; i++)
                    if (titleRects[i].Contains(local.X, local.Y)) { hover = i; break; }
            }
            hoverTitle = hover;
            if (popup != null && hover >= 0 && hover != openMenu) OpenMenu(hover); // slide between menus like macOS

            string key = string.Join("|", DateTime.Now.ToString("ddd d MMM h:mm tt"), appName, hoverTitle, openMenu,
                SystemInfo.Cpu, SystemInfo.Ram, SystemInfo.BatteryPercent, SystemInfo.Charging, SystemInfo.OnAc,
                SystemInfo.Net, Feed.Unread, volumePercent);
            if (key != lastKey)
            {
                lastKey = key;
                Redraw();
            }
        }

        void UpdateForeground()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == target) return;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            if (pid == self) return; // our own menus, island and search

            var cls = new System.Text.StringBuilder(64);
            NativeMethods.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd")
            {
                target = IntPtr.Zero;
                targetExe = null;
                appName = "Desktop";
                return;
            }

            target = fg;
            targetExe = DockForm.ProcessPath(pid);
            appName = FriendlyName(targetExe);
        }

        string FriendlyName(string exe)
        {
            if (exe == null) return "App";
            string name;
            if (appNames.TryGetValue(exe, out name)) return name;

            try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { name = null; }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
            name = name.Trim();
            if (name.Length > 28) name = name.Substring(0, 27) + "…";
            appNames[exe] = name;
            return name;
        }

        // ---------- drawing ----------

        void Redraw()
        {
            if (!IsHandleCreated || Width <= 0) return;
            if (surface == null || surface.Width != Width)
            {
                if (surface != null) surface.Dispose();
                surface = new Surface(Width, BarHeight);
            }

            hits.Clear();
            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Typography.Prepare(g);
                DrawBar(g);
            }
            surface.Present(Handle, Left, Top);
            // (blur companion window removed: the bar is drawn alone)
        }

        void DrawBar(Graphics g)
        {
            // Glass strip: the same cool tint as the dock and panels, slightly denser at the top so the text stays
            // readable on bright wallpapers, with a faint hairline along the bottom edge.
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, BarHeight), Color.FromArgb(100, 40, 48, 62), Color.FromArgb(80, 40, 48, 62), LinearGradientMode.Vertical))
                g.FillRectangle(bg, 0, 0, Width, BarHeight);
            using (var line = new Pen(Color.FromArgb(24, 255, 255, 255)))
                g.DrawLine(line, 0, BarHeight - 1, Width, BarHeight - 1);

            var white = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            var dim = new SolidBrush(Color.FromArgb(190, 255, 255, 255));
            var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };

            // Left: logo + menu titles.
            string[] names = { null, appName, "File", "Edit", "View", "Window", "Help" };
            float x = 10;
            for (int i = 0; i < names.Length; i++)
            {
                float w = i == LogoMenu ? 18 : g.MeasureString(names[i], i == AppMenu ? bold : regular, 1000, StringFormat.GenericTypographic).Width + 18;
                titleRects[i] = new RectangleF(x, 3, w, BarHeight - 7);

                if (i == hoverTitle || i == openMenu)
                {
                    using (GraphicsPath hp = Layered.RoundedRect(titleRects[i], 6))
                    using (var hb = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                        g.FillPath(hb, hp);
                }

                if (i == LogoMenu) DrawWindowsLogo(g, x + 2, 8, 14, white);
                else g.DrawString(names[i], i == AppMenu ? bold : regular, white,
                    new RectangleF(x + 9, 0, w, BarHeight - 1), format);
                x += w + 2;
            }

            // Right: laid out from the edge inward, like the macOS menu extras.
            float right = Width - 14;

            string clock = DateTime.Now.ToString("ddd d MMM  h:mm tt");
            float clockW = Typography.MeasureTabular(g, clock, regular);
            right -= clockW;
            Typography.DrawTabular(g, clock, regular, white, right, 0, BarHeight - 1);
            AddHit("clock", right - 6, clockW + 12, () => WinUtil.Combo(WinUtil.VK_LWIN, (byte)'N'));
            right -= 12;

            // Profile avatar.
            right -= 18;
            var avatar = new RectangleF(right, 6, 18, 18);
            using (var ab = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                g.FillEllipse(ab, avatar);
            var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Environment.UserName.Substring(0, 1).ToUpperInvariant(), small, white, new RectangleF(avatar.X, avatar.Y, avatar.Width, avatar.Height + 1), centered);
            AddHit("profile", right - 5, 28, () => TogglePanel("profile"));
            right -= 12;

            // Notification bell with an unread badge.
            right -= 15;
            DrawBell(g, right, 8, white);
            if (Feed.Unread > 0)
            {
                string count = Feed.Unread > 9 ? "9+" : Feed.Unread.ToString();
                var badge = new RectangleF(right + 8, 3, 10, 10);
                using (var red = new SolidBrush(Color.FromArgb(255, 69, 58)))
                    g.FillEllipse(red, badge);
                g.DrawString(count, badgeFont, Brushes.White, new RectangleF(badge.X - 2, badge.Y, badge.Width + 4, badge.Height + 1), centered);
            }
            AddHit("bell", right - 6, 27, () => { ClosePanel(); if (BellClicked != null) BellClicked(); });
            right -= 12;

            // Screenshot and screen record (Settings > Menu bar & island).
            bool recOn = ScreenCapture.Recording;
            if (ScreenCapture.BarShot)
            {
                right -= 16;
                using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.5f))
                {
                    g.DrawRectangle(pen, right, 11, 16, 11);
                    g.DrawRectangle(pen, right + 5, 8, 6, 3);
                    g.DrawEllipse(pen, right + 5, 13, 6, 6);
                }
                AddHit("screenshot", right - 6, 28, () => { ClosePanel(); ScreenCapture.Screenshot(); });
                right -= 12;
            }
            if (ScreenCapture.BarRec)
            {
                right -= 16;
                using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.5f))
                using (var red = new SolidBrush(Color.FromArgb(255, 69, 58)))
                {
                    g.DrawEllipse(pen, right, 7, 14, 14);
                    if (recOn) g.FillRectangle(red, right + 4, 11, 6, 6); else g.FillEllipse(red, right + 3.5f, 10.5f, 7, 7);
                }
                AddHit("screenrecord", right - 6, 26, () => { ClosePanel(); ScreenCapture.ToggleRecord(); });
                right -= 12;
            }

            // Control Center.
            right -= 16;
            DrawControlCenter(g, right, 8);
            AddHit("control", right - 6, 28, () => TogglePanel("control"));
            right -= 12;

            // Search (opens Spotlight).
            right -= 14;
            DrawMagnifier(g, right, 8);
            AddHit("search", right - 6, 26, () => { ClosePanel(); if (SearchClicked != null) SearchClicked(); });
            right -= 12;

            if (SystemInfo.HasBattery)
            {
                string pct = SystemInfo.BatteryPercent + "%";
                float pctW = Typography.MeasureTabular(g, pct, small);
                right -= pctW;
                Typography.DrawTabular(g, pct, small, dim, right, 0, BarHeight - 1);
                right -= 30;
                DrawBattery(g, right, 9);
                AddHit("battery", right - 6, 30 + pctW + 12, () => TogglePanel("battery"));
                right -= 12;
            }

            right -= 16;
            DrawNetwork(g, right, 7, dim);
            AddHit("wifi", right - 6, 28, () => TogglePanel("wifi"));
            right -= 12;

            right -= 16;
            DrawVolume(g, right, 8);
            AddHit("sound", right - 6, 28, () => TogglePanel("sound"));
            right -= 12;

            right -= 12;
            DrawBluetooth(g, right, 8);
            AddHit("bluetooth", right - 6, 24, () => TogglePanel("bluetooth"));
            right -= 18;

            string stats = "CPU " + SystemInfo.Cpu + "%   RAM " + SystemInfo.Ram + "%";
            float statsW = Typography.MeasureTabular(g, stats, small);
            right -= statsW;
            Typography.DrawTabular(g, stats, small, dim, right, 0, BarHeight - 1);
            AddHit("stats", right - 6, statsW + 12, () => Process.Start("taskmgr.exe"));
            right -= 12;

            // Chevron: opens Windows' own tray overflow (the hidden icons), up while it is open.
            right -= 12;
            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cy = trayOpen ? 17 : 13, d = trayOpen ? -4 : 4;
                g.DrawLines(pen, new[] { new PointF(right, cy), new PointF(right + 6, cy + d), new PointF(right + 12, cy) });
            }
            AddHit("tray", right - 6, 24, ToggleTrayOverflow);
            right -= 12;

            // App launchers to the left of the tray chevron: each shows the app's own icon and starts it (or brings it
            // forward) on click. They launch the apps; they are not mirrors of any tray icon.
            foreach (BarLauncher l in launchers)
            {
                if (l.Vantage)
                {
                    right -= 18;
                    PaintVantage(g, new RectangleF(right, 6, 18, 18));
                    AddHit(l.Id, right - 5, 28, OpenVantageWidget);
                    right -= 12;
                    continue;
                }
                EnsureLauncherIcon(l);
                if (l.Icon == null) continue;
                right -= 18;
                g.DrawImage(l.Icon, new RectangleF(right, 6, 18, 18));
                BarLauncher launch = l;
                AddHit(l.Id, right - 5, 28, () => LaunchApp(launch));
                right -= 12;
            }
            white.Dispose();
            dim.Dispose();
        }

        bool trayOpen;

        sealed class BarLauncher
        {
            public string Id, Target, Exe; // Exe, when set, must exist for the launcher to show
            public Bitmap Icon;
            public bool Loading;
            public bool Vantage;           // a lookalike drawn in code; its click finds the real tray icon (see OpenVantageWidget)
        }

        readonly BarLauncher[] launchers =
        {
            new BarLauncher { Id = "vantage", Vantage = true },
            new BarLauncher { Id = "monitorian", Target = @"shell:AppsFolder\10186emoacht.Monitorian_0q7myvhtpbc7w!Monitorian" }
        };

        // Loads an app's icon once, off the UI thread. An app that isn't installed has no icon, so it simply isn't shown.
        void EnsureLauncherIcon(BarLauncher l)
        {
            if (l.Icon != null || l.Loading || (l.Exe != null && !File.Exists(l.Exe))) return;
            l.Loading = true;
            var loader = new System.Threading.Thread(() =>
            {
                Bitmap b = null;
                try { b = ShellIcons.Load(l.Target, 96); } catch { }
                if (b == null) return;
                l.Icon = b;
                try { BeginInvoke(new Action(Redraw)); } catch { }
            }) { IsBackground = true, Name = "Bar icon " + l.Id };
            loader.SetApartmentState(System.Threading.ApartmentState.STA);
            loader.Start();
        }

        // A lookalike of the red Lenovo Vantage widget icon (a red disc with a white L), drawn in code so it stays sharp.
        static void PaintVantage(Graphics g, RectangleF r)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var red = new SolidBrush(Color.FromArgb(227, 30, 36))) g.FillEllipse(red, r);
            var font = Typography.Text(8.5f, Weight.Semibold); // cached by Typography: not disposed here
            
            using (var white = new SolidBrush(Color.White))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("L", font, white, new RectangleF(r.X, r.Y + 0.5f, r.Width, r.Height), fmt);
            g.SmoothingMode = old;
        }

        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

        // This is not the live tray icon. The bar shows a lookalike; clicking it finds the real red icon on screen and
        // clicks that. It looks in the notification area first, then opens the hidden-icons flyout (Win+B, Enter) and
        // looks there. If it isn't found, the flyout is closed and nothing else happens. Runs off the UI thread.
        void OpenVantageWidget()
        {
            ClosePanel();
            if (vantageBusy) return;
            vantageBusy = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Point restore = Cursor.Position;
                    Rectangle area = Screen.PrimaryScreen.Bounds;
                    Rectangle search = new Rectangle(area.Right - 700, area.Bottom - 520, 700, 520); // bottom-right: taskbar tray and its flyout
                    Point? hit = FindRedDisc(search);
                    bool openedFlyout = false;
                    if (hit == null)
                    {
                        WinUtil.Combo(WinUtil.VK_LWIN, (byte)'B');
                        System.Threading.Thread.Sleep(200);
                        WinUtil.Combo(0x0D);
                        openedFlyout = true;
                        System.Threading.Thread.Sleep(260);
                        hit = FindRedDisc(search);
                    }
                    if (hit != null)
                    {
                        SetCursorPos(hit.Value.X, hit.Value.Y);
                        System.Threading.Thread.Sleep(40);
                        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // left down
                        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // left up
                    }
                    else if (openedFlyout) WinUtil.Combo(0x1B); // not found: close the flyout again
                    System.Threading.Thread.Sleep(60);
                    SetCursorPos(restore.X, restore.Y);
                }
                catch (Exception ex) { Program.Log(ex); }
                finally { vantageBusy = false; }
            });
        }

        volatile bool vantageBusy;

        // The centre of the biggest strongly-red, roughly round blob 14-30 px wide inside region (screen coordinates).
        static Point? FindRedDisc(Rectangle region)
        {
            using (var shot = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(shot)) g.CopyFromScreen(region.Location, Point.Empty, region.Size);
                var data = shot.LockBits(new Rectangle(0, 0, shot.Width, shot.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    int w = shot.Width, h = shot.Height, stride = data.Stride;
                    var px = new byte[stride * h];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    var seen = new bool[w * h];
                    Point? best = null; int bestArea = 0;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            if (seen[y * w + x] || !IsRed(px, y * stride + x * 4)) continue;
                            // flood fill this blob
                            var stack = new Stack<int>(); stack.Push(y * w + x); seen[y * w + x] = true;
                            int minX = x, maxX = x, minY = y, maxY = y, count = 0;
                            while (stack.Count > 0)
                            {
                                int i = stack.Pop(), cx = i % w, cy = i / w;
                                count++;
                                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx; if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                                for (int d = 0; d < 4; d++)
                                {
                                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[ny * w + nx]) continue;
                                    if (!IsRed(px, ny * stride + nx * 4)) continue;
                                    seen[ny * w + nx] = true;
                                    stack.Push(ny * w + nx);
                                }
                            }
                            int bw = maxX - minX + 1, bh = maxY - minY + 1;
                            bool round = bw >= 14 && bw <= 30 && bh >= 14 && bh <= 30 && Math.Abs(bw - bh) <= 4;
                            if (round && count > bestArea) { bestArea = count; best = new Point(region.Left + (minX + maxX) / 2, region.Top + (minY + maxY) / 2); }
                        }
                    return best;
                }
                finally { shot.UnlockBits(data); }
            }
        }

        static bool IsRed(byte[] px, int i) { return px[i + 2] > 190 && px[i + 1] < 75 && px[i] < 75; } // BGRA: R high, G and B low

        void LaunchApp(BarLauncher l)
        {
            ClosePanel();
            try { Process.Start(new ProcessStartInfo(l.Target) { UseShellExecute = true }); }
            catch { if (l.Exe != null) { try { Process.Start(new ProcessStartInfo(l.Exe) { UseShellExecute = true }); } catch { } } }
        }
        // Win+B moves keyboard focus to the notification area; Enter then opens the overflow flyout, exactly like
        // clicking the ^ on the taskbar. Nothing is injected and the taskbar itself is left alone.
        void ToggleTrayOverflow()
        {
            ClosePanel();
            trayOpen = !trayOpen;
            if (trayOpen)
            {
                WinUtil.Combo(WinUtil.VK_LWIN, (byte)'B');
                System.Threading.Tasks.Task.Delay(220).ContinueWith(t => WinUtil.Combo(0x0D));
            }
            else WinUtil.Combo(0x1B); // Esc closes the flyout
            Redraw();
        }

        void AddHit(string id, float x, float w, Action click)
        {
            var rect = new RectangleF(x, 0, w, BarHeight);
            iconRects[id] = rect;
            hits.Add(new Hit { Rect = rect, Click = click });
        }

        static void DrawVolume(Graphics g, float x, float y)
        {
            bool muted = Audio.Muted || volumePercent == 0;
            Color c = Color.FromArgb(240, 255, 255, 255);
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.FillPolygon(b, new[] { new PointF(x, y + 5), new PointF(x + 3.5f, y + 5), new PointF(x + 7.5f, y + 1.5f), new PointF(x + 7.5f, y + 12.5f), new PointF(x + 3.5f, y + 9), new PointF(x, y + 9) });
                if (muted)
                {
                    g.DrawLine(pen, x + 10, y + 4.5f, x + 15, y + 9.5f);
                    g.DrawLine(pen, x + 15, y + 4.5f, x + 10, y + 9.5f);
                    return;
                }
                g.DrawArc(pen, x + 4, y + 3, 8, 8, -50, 100);
                if (volumePercent > 40) g.DrawArc(pen, x + 2, y + 0.5f, 13, 13, -55, 110);
            }
        }

        static void DrawBluetooth(Graphics g, float x, float y)
        {
            using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(x, y + 3.5f), new PointF(x + 8, y + 10.5f), new PointF(x + 4, y + 14),
                    new PointF(x + 4, y), new PointF(x + 8, y + 3.5f), new PointF(x, y + 10.5f)
                });
            }
        }

        static void DrawControlCenter(Graphics g, float x, float y)
        {
            Color c = Color.FromArgb(240, 255, 255, 255);
            using (var pen = new Pen(c, 1.4f))
            using (var b = new SolidBrush(c))
            {
                using (GraphicsPath top = Layered.RoundedRect(new RectangleF(x, y, 15, 6), 3)) g.DrawPath(pen, top);
                g.FillEllipse(b, x + 9.5f, y + 1.5f, 3, 3);
                using (GraphicsPath bottom = Layered.RoundedRect(new RectangleF(x, y + 8, 15, 6), 3)) g.DrawPath(pen, bottom);
                g.FillEllipse(b, x + 2.5f, y + 9.5f, 3, 3);
            }
        }

        static void DrawWindowsLogo(Graphics g, float x, float y, float size, Brush brush)
        {
            float pane = (size - 2) / 2f;
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    g.FillRectangle(brush, x + c * (pane + 2), y + r * (pane + 2), pane, pane);
        }

        static void DrawBell(Graphics g, float x, float y, Brush brush)
        {
            using (var path = new GraphicsPath())
            {
                path.AddArc(x + 2, y, 10, 10, 180, 180);
                path.AddLine(x + 12, y + 5, x + 14, y + 11);
                path.AddLine(x + 14, y + 11, x, y + 11);
                path.AddLine(x, y + 11, x + 2, y + 5);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
            g.FillEllipse(brush, x + 5, y + 12, 4, 3);
        }

        static void DrawMagnifier(Graphics g, float x, float y)
        {
            using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.6f) { EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, x, y, 9, 9);
                g.DrawLine(pen, x + 8, y + 8, x + 13, y + 13);
            }
        }

        void DrawBattery(Graphics g, float x, float y)
        {
            Color fillColor = SystemInfo.Charging || SystemInfo.OnAc ? Color.FromArgb(52, 199, 89)
                : SystemInfo.BatteryPercent <= 20 ? Color.FromArgb(255, 69, 58)
                : Color.FromArgb(240, 255, 255, 255);

            using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
            using (GraphicsPath body = Layered.RoundedRect(new RectangleF(x, y, 22, 11), 3))
            using (var fill = new SolidBrush(fillColor))
            using (var tip = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.DrawPath(pen, body);
                float w = Math.Max(1.5f, 18f * SystemInfo.BatteryPercent / 100f);
                using (GraphicsPath inner = Layered.RoundedRect(new RectangleF(x + 2, y + 2, w, 7), 1.5f))
                    g.FillPath(fill, inner);
                g.FillRectangle(tip, x + 23, y + 3.5f, 1.8f, 4);
            }
        }

        static void DrawNetwork(Graphics g, float x, float y, Brush brush)
        {
            Color c = SystemInfo.Net == NetKind.None ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(240, 255, 255, 255);
            using (var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var dot = new SolidBrush(c))
            {
                if (SystemInfo.Net == NetKind.Ethernet)
                {
                    g.DrawRectangle(pen, x + 1, y + 2, 12, 8);
                    g.DrawLine(pen, x + 7, y + 10, x + 7, y + 13);
                    g.DrawLine(pen, x + 3, y + 13, x + 11, y + 13);
                    return;
                }
                float cx = x + 7, bottom = y + 13;
                foreach (float r in new[] { 4f, 8f, 12f })
                    g.DrawArc(pen, cx - r, bottom - r, r * 2, r * 2, 225, 90);
                g.FillEllipse(dot, cx - 1.5f, bottom - 2.5f, 3, 3);
            }
        }

        // ---------- clicks ----------

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < titleRects.Length; i++)
            {
                if (!titleRects[i].Contains(e.X, e.Y)) continue;
                if (popup != null && openMenu == i) CloseMenu(); else OpenMenu(i);
                return;
            }

            foreach (Hit h in hits)
            {
                if (h.Rect.Contains(e.X, e.Y))
                {
                    CloseMenu();
                    try { h.Click(); } catch { }
                    return;
                }
            }
        }

        void CloseMenu()
        {
            if (popup != null) popup.Close();
        }

        void OpenMenu(int index)
        {
            CloseMenu();
            ClosePanel();
            openMenu = index;
            Redraw();

            RectangleF r = titleRects[index];
            popup = new MenuPopup(BuildMenu(index), new Point(Left + (int)r.Left, Top + BarHeight + 2), BarHeight, () =>
            {
                popup = null;
                openMenu = -1;
                lastKey = null;
            });
            popup.Show();
        }

        // ---------- menu content ----------

        static Action Keys(params byte[] keys) { return () => WinUtil.Combo(keys); }

        static Action Open(string target)
        {
            return () => { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } };
        }

        static Action Confirm(string question, Action action)
        {
            return () =>
            {
                if (MessageBox.Show(question, "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) action();
            };
        }

        List<MenuEntry> BuildMenu(int index)
        {
            const byte CTRL = WinUtil.VK_CONTROL;
            var m = new List<MenuEntry>();

            switch (index)
            {
                case LogoMenu:
                    m.Add(MenuEntry.Item("About This PC", null, Open("ms-settings:about")));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("All Apps", null, () => MissionControl.Toggle(true)));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("MacDock Settings...", null, () => SettingsForm.ShowSettings()));
                    m.Add(MenuEntry.Item(DockForm.ShowDockOn ? "Hide Dock" : "Show Dock", null, () =>
                    {
                        SetCfg.SetBool("show-dock", !DockForm.ShowDockOn);
                        if (DockForm.Instance != null) DockForm.Instance.ApplyDockMode();
                    }));
                    m.Add(MenuEntry.Item("System Settings...", null, Open("ms-settings:")));
                    m.Add(MenuEntry.Item("Task Manager...", "Ctrl+Shift+Esc", () => Process.Start("taskmgr.exe")));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Add Folder to Dock...", null, () => DockForm.Instance.PromptAddFolder()));
                    m.Add(MenuEntry.Item("Add App or File to Dock...", null, () => DockForm.Instance.PromptAddApp()));
                    m.Add(MenuEntry.Item("Add Website to Dock...", null, () => DockForm.Instance.PromptAddWebsite()));
                    m.Add(MenuEntry.Item("Find macOS Icons for Dock Apps...", null, () => IconFetcher.FindIcons(DockForm.Instance.AppsWithoutMacIcon())));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Sleep", null, () => Application.SetSuspendState(PowerState.Suspend, false, false)));
                    m.Add(MenuEntry.Item("Restart...", null, Confirm("Restart this PC now?", () => Process.Start("shutdown", "/r /t 0"))));
                    m.Add(MenuEntry.Item("Shut Down...", null, Confirm("Shut down this PC now?", () => Process.Start("shutdown", "/s /t 0"))));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Lock Screen", "Win+L", () => Process.Start("rundll32.exe", "user32.dll,LockWorkStation")));
                    m.Add(MenuEntry.Item("Log Out...", null, Confirm("Sign out of Windows now?", () => Process.Start("shutdown", "/l"))));
                    break;

                case AppMenu:
                    bool hasApp = target != IntPtr.Zero;
                    IntPtr win = target;
                    string exe = targetExe;
                    m.Add(MenuEntry.Item("Show " + appName + " in Folder", null, !hasApp || exe == null ? null : (Action)(() => Process.Start("explorer.exe", "/select,\"" + exe + "\""))));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Hide " + appName, "Win+Down", !hasApp ? null : (Action)(() => NativeMethods.ShowWindow(win, 6))));
                    m.Add(MenuEntry.Item("Hide Others", null, !hasApp ? null : (Action)(() => HideOthers(exe))));
                    m.Add(MenuEntry.Item("Show All", null, ShowAll));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Quit " + appName, "Alt+F4", !hasApp ? null : (Action)(() => Quit(exe))));
                    break;

                case 2:
                    m.Add(MenuEntry.Item("New Window", "Ctrl+N", Keys(CTRL, (byte)'N')));
                    m.Add(MenuEntry.Item("New Tab", "Ctrl+T", Keys(CTRL, (byte)'T')));
                    m.Add(MenuEntry.Item("Open...", "Ctrl+O", Keys(CTRL, (byte)'O')));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Close Window", "Ctrl+W", Keys(CTRL, (byte)'W')));
                    m.Add(MenuEntry.Item("Save", "Ctrl+S", Keys(CTRL, (byte)'S')));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Print...", "Ctrl+P", Keys(CTRL, (byte)'P')));
                    break;

                case 3:
                    m.Add(MenuEntry.Item("Undo", "Ctrl+Z", Keys(CTRL, (byte)'Z')));
                    m.Add(MenuEntry.Item("Redo", "Ctrl+Y", Keys(CTRL, (byte)'Y')));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Cut", "Ctrl+X", Keys(CTRL, (byte)'X')));
                    m.Add(MenuEntry.Item("Copy", "Ctrl+C", Keys(CTRL, (byte)'C')));
                    m.Add(MenuEntry.Item("Paste", "Ctrl+V", Keys(CTRL, (byte)'V')));
                    m.Add(MenuEntry.Item("Select All", "Ctrl+A", Keys(CTRL, (byte)'A')));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Find...", "Ctrl+F", Keys(CTRL, (byte)'F')));
                    break;

                case 4:
                    m.Add(MenuEntry.Item("Enter Full Screen", "F11", Keys(0x7A)));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Zoom In", "Ctrl++", Keys(CTRL, 0xBB)));
                    m.Add(MenuEntry.Item("Zoom Out", "Ctrl+-", Keys(CTRL, 0xBD)));
                    m.Add(MenuEntry.Item("Actual Size", "Ctrl+0", Keys(CTRL, (byte)'0')));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("Reload", "F5", Keys(0x74)));
                    break;

                case 5:
                    IntPtr w = target;
                    bool any = w != IntPtr.Zero;
                    m.Add(MenuEntry.Item("Minimize", "Win+Down", !any ? null : (Action)(() => NativeMethods.ShowWindow(w, 6))));
                    m.Add(MenuEntry.Item(any && Win32.IsZoomed(w) ? "Restore" : "Zoom", "Win+Up",
                        !any ? null : (Action)(() => NativeMethods.ShowWindow(w, Win32.IsZoomed(w) ? 9 : 3))));
                    m.Add(MenuEntry.Item("Close Window", "Alt+F4", !any ? null : (Action)(() => Win32.PostMessage(w, WinUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))));
                    m.Add(MenuEntry.Sep());
                    int listed = 0;
                    foreach (KeyValuePair<string, List<IntPtr>> app in DockForm.EnumerateRunningWindows())
                    {
                        foreach (IntPtr h in app.Value)
                        {
                            if (listed >= 12) break;
                            string title = WinUtil.TitleOf(h);
                            if (title.Length > 46) title = title.Substring(0, 45) + "…";
                            IntPtr handle = h;
                            var entry = MenuEntry.Item(title, null, () => WinUtil.Focus(handle));
                            entry.Checked = handle == w;
                            m.Add(entry);
                            listed++;
                        }
                    }
                    if (listed == 0) m.Add(MenuEntry.Item("No open windows", null, null));
                    break;

                default:
                    m.Add(MenuEntry.Item("Search", "Win+Space", () => { if (SearchClicked != null) SearchClicked(); }));
                    m.Add(MenuEntry.Item("Windows Help", null, Open("https://support.microsoft.com/windows")));
                    m.Add(MenuEntry.Sep());
                    m.Add(MenuEntry.Item("MacDock Settings...", null, () => SettingsForm.ShowSettings()));
                    m.Add(MenuEntry.Item("Edit Dock Items...", null, () => Process.Start("notepad.exe", AppPaths.Config)));
                    break;
            }
            return m;
        }

        static void HideOthers(string exe)
        {
            foreach (KeyValuePair<string, List<IntPtr>> app in DockForm.EnumerateRunningWindows())
            {
                if (string.Equals(app.Key, exe, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (IntPtr h in app.Value) NativeMethods.ShowWindow(h, 6);
            }
        }

        static void ShowAll()
        {
            foreach (KeyValuePair<string, List<IntPtr>> app in DockForm.EnumerateRunningWindows())
                foreach (IntPtr h in app.Value)
                    if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);
        }

        // Asks every window of the app to close, like Cmd+Q.
        static void Quit(string exe)
        {
            List<IntPtr> handles;
            if (exe != null && DockForm.EnumerateRunningWindows().TryGetValue(exe, out handles))
                foreach (IntPtr h in handles) Win32.PostMessage(h, WinUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
