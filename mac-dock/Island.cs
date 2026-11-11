// Dynamic island at the top-center of the screen. It stays a small black pill and springs open for
// notifications, power events, now-playing media, a hover peek, and the notification list.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MacDock
{
    sealed class IslandForm : Form
    {
        enum Mode { Idle, Notif, Power, Panel, Copy }

        sealed class Hit
        {
            public Rectangle Rect;
            public Action Click;
        }

        const int CanvasW = 520, CanvasH = 340, TopY = 0;
        // Idle: a round dot exactly as tall as the status bar.
        static readonly int IdleW = TopBar.BarHeight - 10, IdleH = TopBar.BarHeight - 10;
        const int HeaderH = 70, MediaH = 56, RowH = 46, FooterH = 44, TabH = 36, ClipRowH = 34, ClipMax = 5;

        readonly Timer timer = new Timer();
        // The pill is an under-damped spring, so it overshoots slightly when it opens, like the real island.
        const double Omega = 19, Zeta = 0.62;

        readonly Font nameFont = Typography.Text(9f, Weight.Medium);
        readonly Font bodyFont = Typography.Secondary();
        readonly Font boldFont = Typography.Text(11f, Weight.Semibold);
        readonly Font bigFont = Typography.Display(24f, Weight.Semibold);
        // Swipe a notification row sideways to dismiss it; the rows below slide up to fill the gap.
        struct RowRect { public Rectangle Rect; public NotificationInfo N; public int Index; }
        readonly List<RowRect> rowRects = new List<RowRect>();
        NotificationInfo swipeN;
        int swipeIndex, swipeDir, shiftFrom = 99;
        Spring swipeX = new Spring(0), rowShift = new Spring(0);
        bool swipeDown, swipeDragging, swipeOut, swipeWheel;
        long lastWheelT;
        float swipeStartX, swipeRowW, swipeLastX;
        long swipeLastT;
        double swipeFlick; // px/s, smoothed
        StringFormat fmtLeft, fmtRight;
        bool noPresent;
        Rectangle prevDirty;
        readonly List<Hit> hits = new List<Hit>();
        readonly FrameClock clock = new FrameClock();
        readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>();

        // 120 Hz: a worker waits on the compositor (DwmFlush returns once per display refresh) and posts a
        // Step to the UI thread, so animation runs at the monitor's native rate instead of the ~64 Hz WinForms timer.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();
        readonly System.Threading.ManualResetEventSlim gate = new System.Threading.ManualResetEventSlim(false);
        volatile bool animating, closing;
        int pendingFrame;

        Surface surface;
        bool wasBusy;
        Mode mode = Mode.Idle;
        DateTime until, lastHover = DateTime.MinValue;
        NotificationInfo current;
        string powerText;
        Color powerColor;
        bool powerInitialized, lastOnAc, lowShown;
        string copyText;
        // Clipboard history (text), newest first; shown on the panel's Clipboard tab. Click an item to copy it back.
        readonly List<string> clipHistory = new List<string>();
        bool clipTab;

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        void OnClipboardChanged()
        {
            try
            {
                uint pid; GetWindowThreadProcessId(GetClipboardOwner(), out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                string text = null;
                for (int i = 0; i < 3 && text == null; i++)
                {
                    try { if (!Clipboard.ContainsText()) return; text = Clipboard.GetText(); }
                    catch { System.Threading.Thread.Sleep(25); }
                }
                if (string.IsNullOrWhiteSpace(text)) return;
                string full = text.Trim();
                if (full.Length > 2000) full = full.Substring(0, 2000);
                clipHistory.Remove(full);
                clipHistory.Insert(0, full);
                if (clipHistory.Count > 20) clipHistory.RemoveAt(clipHistory.Count - 1);
                text = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
                if (text.Length > 120) text = text.Substring(0, 120);
                if (mode == Mode.Panel || mode == Mode.Notif) return;
                copyText = text;
                mode = Mode.Copy;
                until = DateTime.Now.AddSeconds(2.2);
                Wake();
            }
            catch { }
        }

        // Spring-driven size of the pill.
        Spring springW = new Spring(IdleW), springH = new Spring(IdleH);
        double targetW = IdleW, targetH = IdleH;
        double curW { get { return springW.Value; } }
        double curH { get { return springH.Value; } }
        int frame;

        // User toggle (Settings > Menu Bar & Island, "@island on|off"). Off hides the pill and stops its timer; the vsync thread just idles.
        public static volatile bool Enabled = true;
        public static event Action EnabledChanged;
        public static void SetEnabled(bool on) { Enabled = on; var h = EnabledChanged; if (h != null) h(); }

        public void ApplyEnabled()
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (Enabled)
            {
                if (!Visible) Show();
                Reposition(); Raise();
                if (!animating) { timer.Start(); }
            }
            else
            {
                mode = Mode.Idle; animating = false; timer.Stop();
                if (Visible) Hide();
            }
        }

        public IslandForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            Rectangle screen = Screen.PrimaryScreen.Bounds;
            Bounds = new Rectangle(screen.Left + (screen.Width - CanvasW) / 2, screen.Top, CanvasW, CanvasH);

            Feed.Arrived += OnNotification;
            Feed.Changed += () => { frame = 0; };

            timer.Interval = 50;
            timer.Tick += (s, e) => Step();
            timer.Start();

            var vsync = new System.Threading.Thread(VsyncLoop) { IsBackground = true, Name = "island-vsync", Priority = System.Threading.ThreadPriority.AboveNormal };
            vsync.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; // layered, tool window, no-activate, topmost
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { AddClipboardFormatListener(Handle); } catch { }
            // Warm up fonts, JIT and icon cache with an off-screen panel frame so the first hover doesn't hitch.
            try
            {
                Mode saved = mode; double w = springW.Value, h = springH.Value, tw = targetW, th = targetH;
                mode = Mode.Panel; springW.Value = 420; springH.Value = PanelHeight(); targetW = 420; targetH = springH.Value;
                noPresent = true; Redraw(); noPresent = false;
                mode = saved; springW.Value = w; springH.Value = h; targetW = tw; targetH = th;
                hits.Clear();
            }
            catch { noPresent = false; }
            Redraw();
            Raise();
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        volatile float vsyncDtF = 1f / 60;
        double vsyncDt { get { return vsyncDtF; } }

        void VsyncLoop()
        {
            timeBeginPeriod(1);
            long prev = 0;
            while (!closing)
            {
                if (!animating) { prev = 0; gate.Wait(250); continue; }
                if (DwmFlush() != 0) System.Threading.Thread.Sleep(4); // DWM off: fall back to ~250 Hz polling
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                if (prev != 0)
                {
                    double d = (t - prev) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (d > 1.0 / 300 && d < 1.0 / 30) vsyncDtF = (float)(vsyncDtF * 0.85 + d * 0.15); // smoothed refresh interval
                }
                prev = t;
                if (closing || !IsHandleCreated) continue;
                if (System.Threading.Interlocked.Exchange(ref pendingFrame, 1) == 0)
                {
                    try { BeginInvoke(new Action(() => { System.Threading.Interlocked.Exchange(ref pendingFrame, 0); Step(); })); }
                    catch { }
                }
            }
        }

        // Switches to vsync-driven frames until the motion settles.
        void Wake()
        {
            animating = true;
            timer.Stop();
            gate.Set();
        }

        protected override void Dispose(bool disposing)
        {
            closing = true;
            gate.Set();
            if (disposing)
            {
                timer.Dispose();
                if (surface != null) surface.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- events ----------

        void OnNotification(NotificationInfo n)
        {
            if (!Enabled || mode == Mode.Panel) return;
            current = n;
            mode = Mode.Notif;
            until = DateTime.Now.AddSeconds(5.5);
            Wake();
        }

        public void TogglePanel()
        {
            if (!Enabled) return;
            if (mode == Mode.Panel)
            {
                mode = Mode.Idle;
            }
            else
            {
                mode = Mode.Panel;
                Feed.Unread = 0;
                lastHover = DateTime.Now.AddSeconds(4); // stays open a while even if the cursor never enters
            }
            Wake();
        }

        void ShowPower(string text, Color color, double seconds)
        {
            if (mode == Mode.Panel || mode == Mode.Notif) return;
            powerText = text;
            powerColor = color;
            mode = Mode.Power;
            until = DateTime.Now.AddSeconds(seconds);
            Wake();
        }

        void CheckPower()
        {
            if (!SystemInfo.HasBattery) return;
            if (!powerInitialized)
            {
                powerInitialized = true;
                lastOnAc = SystemInfo.OnAc;
                return;
            }

            if (SystemInfo.OnAc != lastOnAc)
            {
                lastOnAc = SystemInfo.OnAc;
                ShowPower(SystemInfo.OnAc ? "Charging  " + SystemInfo.BatteryPercent + "%" : "On Battery  " + SystemInfo.BatteryPercent + "%",
                    SystemInfo.OnAc ? Color.FromArgb(52, 199, 89) : Color.White, 3);
            }

            if (!SystemInfo.OnAc && SystemInfo.BatteryPercent <= 20 && !lowShown)
            {
                lowShown = true;
                ShowPower("Battery Low  " + SystemInfo.BatteryPercent + "%", Color.FromArgb(255, 69, 58), 4);
            }
            else if (SystemInfo.OnAc || SystemInfo.BatteryPercent > 25) lowShown = false;
        }

        // ---------- state + spring ----------

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        DateTime lastRaise = DateTime.MinValue;
        Mode lastRaiseMode = Mode.Idle;

        // Keeps the island above every other window, including other topmost ones (the bar, panels, menus, fullscreen apps).
        void Raise()
        {
            if (!IsHandleCreated) return;
            lastRaise = DateTime.Now;
            if (GetWindow(Handle, 3) == IntPtr.Zero) return; // already above every other window: no z-order churn
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0200); // NOMOVE|NOSIZE|NOACTIVATE|NOOWNERZORDER
        }

        // Centred at the top of whichever display is the main one now.
        internal void Reposition()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int left = screen.Left + (screen.Width - CanvasW) / 2;
            if (Left != left || Top != screen.Top) Location = new Point(left, screen.Top);
        }

        void Step()
        {
            frame++;
            Fullscreen.Update();
            if (Fullscreen.Active)
            {
                if (Visible) Hide(); // step aside for the fullscreen app; the slow timer keeps polling
                animating = false;
                timer.Interval = 250;
                timer.Start();
                return;
            }
            if (!Visible) { Show(); Raise(); }
            if (frame % 20 == 0) Reposition();
            if (mode != lastRaiseMode || (DateTime.Now - lastRaise).TotalMilliseconds > 40) { lastRaiseMode = mode; Raise(); }
            CheckPower();

            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            Point local = PointToClient(new Point(p.X, p.Y));
            var pill = new RectangleF((CanvasW - (float)curW) / 2f - 6, TopY - 4, (float)curW + 12, (float)curH + 8);
            bool hover = pill.Contains(local.X, local.Y);
            if (hover || swipeN != null) lastHover = DateTime.Now; // stay open while a row is being dragged
            bool wasIdle = mode == Mode.Idle;

            DateTime now = DateTime.Now;
            switch (mode)
            {
                case Mode.Panel:
                    if (!hover && (now - lastHover).TotalSeconds > 0.3) mode = Mode.Idle;
                    break;
                case Mode.Notif:
                case Mode.Power:
                case Mode.Copy:
                    if (now > until && !hover) mode = Mode.Idle;
                    break;
                case Mode.Idle:
                    if (hover)
                    {
                        mode = Mode.Panel;
                        Feed.Unread = 0;
                        lastHover = now;
                    }
                    break;
            }

            bool media = Feed.Media != null;
            switch (mode)
            {
                case Mode.Idle: targetW = IdleW; targetH = IdleH; break;
                case Mode.Notif: targetW = 440; targetH = 80; break;
                case Mode.Power: targetW = 280; targetH = 48; break;
                case Mode.Copy: targetW = 340; targetH = 48; break;
                default:
                    targetW = 420; targetH = PanelHeight();
                    break;
            }

            // Frame time: the measured vsync interval, unless a frame was genuinely dropped (then the real gap).
            double vs = vsyncDt, dt;
            if (wasBusy)
            {
                double real = clock.Next();
                dt = real < vs * 1.6 ? vs : real;
            }
            else { clock.Reset(); dt = vs; } // first frame after idling

            springW.Step(targetW, Omega, Zeta, dt);
            springH.Step(targetH, Omega, Zeta, dt);
            // The under-damped bounce must never shrink the pill below its idle size.
            if (springW.Value < IdleW) { springW.Value = IdleW; if (springW.Velocity < 0) springW.Velocity = 0; }
            if (springH.Value < IdleH) { springH.Value = IdleH; if (springH.Velocity < 0) springH.Velocity = 0; }

            bool swipeBusy = StepSwipe(dt);
            bool settled = springW.IsSettled(targetW, 0.3) && springH.IsSettled(targetH, 0.3) && !swipeBusy;
            if (settled) { springW.Snap(targetW); springH.Snap(targetH); }
            wasBusy = !settled;

            // Only repaint when something changes: while moving, for the equalizer, or while content is live.
            bool equalizer = mode == Mode.Idle && media && Feed.Media.Playing;
            bool live = mode != Mode.Idle;
            if (!settled || equalizer || frame < 3 || (live && frame % 8 == 0)) Redraw();
            if (!settled)
            {
                if (!animating) Wake(); // hover/notification started motion from the slow timer: hand over to vsync
                return;
            }
            animating = false;
            // Resolve icons for the rows the panel will show while nothing is moving, so opening never stalls on a shell lookup.
            if (mode == Mode.Idle)
                for (int i = 0, n = Math.Min(3, Feed.History.Count); i < n; i++)
                    if (!iconCache.ContainsKey((Feed.History[i].AppId ?? Feed.History[i].App) ?? "")) { IconFor(Feed.History[i]); break; }
            timer.Interval = equalizer ? 33 : (mode == Mode.Idle ? 40 : 60);
            timer.Start();
        }

        bool StepSwipe(double dt)
        {
            bool busy = false;
            if (swipeDown && swipeWheel && (System.Diagnostics.Stopwatch.GetTimestamp() - lastWheelT) / (double)System.Diagnostics.Stopwatch.Frequency > 0.12)
                ReleaseSwipe(); // no wheel events for a moment: the fingers lifted
            if (swipeN != null)
            {
                if (swipeDown) busy = swipeDragging; // following the cursor
                else
                {
                    swipeX.Step(swipeOut ? swipeDir * swipeRowW * 1.15 : 0, 24, 1.0, dt);
                    if (swipeOut && Math.Abs(swipeX.Value) >= swipeRowW * 0.95)
                    {
                        NotificationInfo n = swipeN;
                        int index = swipeIndex;
                        swipeN = null;
                        swipeX.Snap(0);
                        Feed.Remove(n);
                        shiftFrom = index; // rows that were below it start one row lower and glide up
                        rowShift.Value = RowH;
                        rowShift.Velocity = 0;
                        busy = true;
                    }
                    else if (!swipeOut && swipeX.IsSettled(0, 0.3)) { swipeN = null; swipeX.Snap(0); }
                    else busy = true;
                }
            }
            if (!rowShift.IsSettled(0, 0.3)) { rowShift.Step(0, 22, 1.0, dt); busy = true; }
            else rowShift.Snap(0);
            return busy;
        }

        float IdleYOff() { float open = (float)Math.Max(0, Math.Min(1, (curW - IdleW) / 100.0)); return (float)Math.Round((TopBar.BarHeight - 1 - IdleH) / 2.0 * (1 - open)); }

        int PanelHeight()
        {
            int h = HeaderH + (Feed.Media != null ? MediaH : 0) + TabH;
            if (clipTab)
            {
                int cr = Math.Min(ClipMax, clipHistory.Count);
                return h + (cr > 0 ? cr * ClipRowH + FooterH : 30) + 6;
            }
            int rows = Math.Min(3, Feed.History.Count);
            h += rows > 0 ? rows * RowH + FooterH : 30;
            return h + 6;
        }

        // ---------- drawing ----------

        void Redraw()
        {
            if (!IsHandleCreated) return;
            if (surface == null) surface = new Surface(CanvasW, CanvasH);

            hits.Clear();
            rowRects.Clear();
            // Clear only where the pill is now or was last frame (plus shadow margin), not the whole canvas.
            var now = Rectangle.Ceiling(new RectangleF((CanvasW - (float)curW) / 2f - 12, TopY - 4, (float)curW + 24, (float)curH + 20 + IdleYOff()));
            Rectangle dirty = prevDirty.IsEmpty ? new Rectangle(0, 0, CanvasW, CanvasH) : Rectangle.Union(prevDirty, now);
            prevDirty = now;
            using (Graphics g = surface.BeginRegion(dirty))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Typography.Prepare(g);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var pill = new RectangleF((CanvasW - (float)curW) / 2f, TopY + IdleYOff(), (float)curW, (float)curH);
                if (mode == Mode.Idle && curW < IdleW + 1.5 && curH < IdleH + 1.5) pill = new RectangleF((CanvasW - IdleW) / 2, TopY + IdleYOff(), IdleW, IdleH); // exact pixel-aligned circle
                float radius = Math.Min(pill.Height / 2f, mode == Mode.Idle || mode == Mode.Power || mode == Mode.Copy ? 20f : 28f);

                using (GraphicsPath path = Layered.RoundedRect(pill, radius))
                {
                    // Faint shadow so the pill separates from dark wallpapers.
                    // Strength follows how open the pill is, so it fades in/out instead of popping.
                    float open = (float)Math.Max(0, Math.Min(1, (curW - IdleW) / 100.0));
                    int shadowA = (int)(22 * open);
                    if (shadowA > 0)
                        using (var sb = new SolidBrush(Color.FromArgb(shadowA, 0, 0, 0)))
                            for (int i = 2; i >= 2; i--)
                            {
                                RectangleF s = pill;
                                s.Inflate(i * 2f, i * 2f);
                                s.Offset(0, i);
                                using (GraphicsPath sp = Layered.RoundedRect(s, radius + i * 2f))
                                    g.FillPath(sb, sp);
                            }

                    using (var fill = new SolidBrush(Color.Black))
                        g.FillPath(fill, path);
                    using (var rim = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
                        g.DrawPath(rim, path);

                    // Content fades in as the pill opens, and is clipped to the pill.
                    float alpha = mode == Mode.Idle ? 1f - open
                        : (float)Math.Max(0, Math.Min(1, (curW - IdleW) / Math.Max(1, targetW - IdleW)));
                    alpha *= alpha;

                    // Lay content out on the final (target) rect and let the moving pill reveal it: text no longer
                    // slides and re-rasterises at fractional offsets every frame.
                    float lw = mode == Mode.Idle ? (float)curW : (float)targetW, lh = mode == Mode.Idle ? (float)curH : (float)targetH;
                    var layout = new RectangleF((float)Math.Round((CanvasW - lw) / 2f), TopY + IdleYOff(), (float)Math.Round(lw), (float)Math.Round(lh));
                    g.SetClip(RectangleF.Inflate(pill, -1f, -1f));
                    DrawContent(g, layout, (int)(alpha * 255));
                    g.ResetClip();
                }
            }
            if (!noPresent) surface.Present(Handle, Left, Top);
        }

        static Color A(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, c.A * alpha / 255)), c); }

        void DrawContent(Graphics g, RectangleF pill, int alpha)
        {
            if (alpha < 6) return;
            if (fmtLeft == null)
            {
                fmtLeft = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                fmtRight = new StringFormat(fmtLeft) { Alignment = StringAlignment.Far };
            }
            StringFormat left = fmtLeft, right = fmtRight;
            // Grayscale grid-fit AA: ClearType showed colour fringes on the layered window.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Color white = A(Color.White, alpha), gray = A(Color.FromArgb(150, 255, 255, 255), alpha);

            using (var wb = new SolidBrush(white))
            using (var gb = new SolidBrush(gray))
            {
                switch (mode)
                {
                    case Mode.Idle:
                        if (Feed.Media != null) DrawEqualizer(g, pill, wb, left);
                        break;
                    case Mode.Notif:
                        if (current != null) DrawNotification(g, pill, current, wb, gb, left, 0, alpha, true);
                        break;
                    case Mode.Power:
                        DrawPower(g, pill, alpha, left);
                        break;
                    case Mode.Copy:
                        DrawCopy(g, pill, wb, gb, alpha);
                        break;
                    default:
                        DrawPanel(g, pill, wb, gb, left, right, alpha);
                        break;
                }
            }
        }

        void DrawEqualizer(Graphics g, RectangleF pill, Brush brush, StringFormat left)
        {
            bool playing = Feed.Media.Playing;
            double t = Environment.TickCount / 180.0;
            float x0 = pill.Left + (pill.Width - 15) / 2f;
            for (int i = 0; i < 4; i++)
            {
                float h = playing ? 4 + (float)(8 * Math.Abs(Math.Sin(t + i * 1.4))) : 4;
                g.FillRectangle(Brushes.White, x0 + i * 4, pill.Top + (pill.Height - h) / 2, 3, h);
            }
        }

        // Date and time on the left, battery ring on the right.
        void DrawHeader(Graphics g, RectangleF pill, Brush white, Brush gray, StringFormat left, int alpha)
        {
            g.DrawString(DateTime.Now.ToString("h:mm"), bigFont, white, new RectangleF(pill.Left + 22, pill.Top + 8, 150, 36), left);
            g.DrawString(DateTime.Now.ToString("ddd, d MMM"), nameFont, gray, new RectangleF(pill.Left + 24, pill.Top + 44, 150, 18), left);

            float d = 46, rx = pill.Right - 24 - d, ry = pill.Top + (HeaderH - d) / 2f;
            int pct = SystemInfo.HasBattery ? SystemInfo.BatteryPercent : 100;
            Color ring = (SystemInfo.HasBattery && !SystemInfo.OnAc && pct <= 20) ? Color.FromArgb(255, 69, 58) : Color.FromArgb(52, 199, 89);
            using (var track = new Pen(A(Color.FromArgb(60, 255, 255, 255), alpha), 4f))
            using (var arc = new Pen(A(ring, alpha), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var r = new RectangleF(rx + 2, ry + 2, d - 4, d - 4);
                g.DrawEllipse(track, r);
                if (pct > 0) g.DrawArc(arc, r, -90, pct >= 100 ? 359.9f : 360f * pct / 100f);
            }
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            string label = SystemInfo.HasBattery ? pct.ToString() : "AC";
            g.DrawString(label, nameFont, white, new RectangleF(rx - 2, ry, d + 4, d + 1), center);
            if (SystemInfo.HasBattery && SystemInfo.Charging)
                g.DrawString("Charging", nameFont, gray, new RectangleF(rx - 90, ry + 14, 84, 18), new StringFormat(left) { Alignment = StringAlignment.Far });
        }

        void DrawMediaPeek(Graphics g, RectangleF pill, Brush white, Brush gray, StringFormat left, int alpha)
        {
            MediaInfo m = Feed.Media;
            pill.Y += HeaderH; pill.Height = MediaH;
            g.DrawString(m.Title, boldFont, white, new RectangleF(pill.Left + 22, pill.Top + 6, pill.Width - 160, 22), left);
            g.DrawString(m.Artist, nameFont, gray, new RectangleF(pill.Left + 22, pill.Top + 30, pill.Width - 160, 20), left);

            float bx = pill.Right - 132, by = pill.Top + pill.Height / 2f;
            DrawMediaButton(g, bx, by, 0, alpha, MediaFeed.Previous);
            DrawMediaButton(g, bx + 42, by, m.Playing ? 1 : 2, alpha, MediaFeed.TogglePlayPause);
            DrawMediaButton(g, bx + 84, by, 3, alpha, MediaFeed.Next);
        }

        // kind: 0 previous, 1 pause, 2 play, 3 next
        void DrawMediaButton(Graphics g, float cx, float cy, int kind, int alpha, Action click)
        {
            hits.Add(new Hit { Rect = new Rectangle((int)cx, (int)cy - 18, 36, 36), Click = click });
            float x = cx + 18;
            using (var b = new SolidBrush(A(Color.White, alpha)))
            {
                switch (kind)
                {
                    case 1:
                        g.FillRectangle(b, x - 6, cy - 8, 4.5f, 16);
                        g.FillRectangle(b, x + 1.5f, cy - 8, 4.5f, 16);
                        break;
                    case 2:
                        g.FillPolygon(b, new[] { new PointF(x - 5, cy - 9), new PointF(x + 8, cy), new PointF(x - 5, cy + 9) });
                        break;
                    case 0:
                        g.FillPolygon(b, new[] { new PointF(x + 6, cy - 7), new PointF(x - 4, cy), new PointF(x + 6, cy + 7) });
                        g.FillRectangle(b, x - 8, cy - 7, 2.5f, 14);
                        break;
                    default:
                        g.FillPolygon(b, new[] { new PointF(x - 6, cy - 7), new PointF(x + 4, cy), new PointF(x - 6, cy + 7) });
                        g.FillRectangle(b, x + 5.5f, cy - 7, 2.5f, 14);
                        break;
                }
            }
        }

        // The app's own icon (icon pack first, then the shell's icon for its AppUserModelID); cached.
        Bitmap IconFor(NotificationInfo n)
        {
            string key = (n.AppId ?? n.App) ?? "";
            Bitmap bmp;
            if (iconCache.TryGetValue(key, out bmp)) return bmp;
            bmp = null;
            try
            {
                if (IconPack.Enabled) bmp = IconPack.ForName(n.App);
                if (bmp == null && !string.IsNullOrEmpty(n.AppId)) bmp = ShellIcons.Load(ShellIcons.AppsFolderPrefix + n.AppId, 64);
            }
            catch { bmp = null; }
            iconCache[key] = bmp;
            return bmp;
        }

        static Color AppColor(string app)
        {
            int hash = 17;
            foreach (char c in app ?? "") hash = hash * 31 + c;
            Color[] palette =
            {
                Color.FromArgb(10, 132, 255), Color.FromArgb(52, 199, 89), Color.FromArgb(255, 159, 10),
                Color.FromArgb(191, 90, 242), Color.FromArgb(255, 69, 58), Color.FromArgb(100, 210, 255)
            };
            return palette[Math.Abs(hash) % palette.Length];
        }

        // Draws one notification: colored initial badge, app name, title, body.

        // GDI+ DrawString does no font fallback, so emoji came out as blanks/boxes. Draw emoji runs with Segoe UI Emoji.
        readonly System.Collections.Generic.Dictionary<float, Font> emojiFonts = new System.Collections.Generic.Dictionary<float, Font>();
        static bool IsEmojiAt(string s, int i)
        {
            char c = s[i];
            if (char.IsSurrogate(c)) return true;
            return c == '\u200D' || c == '\uFE0F' || (c >= '\u2190' && c <= '\u2BFF') || c == '\u00A9' || c == '\u00AE' || c == '\u3030' || c == '\u303D' || c == '\u3297' || c == '\u3299' || c == '\u203C' || c == '\u2049' || c == '\u2122' || c == '\u2139';
        }
        void DrawRich(Graphics g, string text, Font font, Brush brush, RectangleF r, StringFormat fmt)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool any = false;
            for (int i = 0; i < text.Length; i++) if (IsEmojiAt(text, i)) { any = true; break; }
            if (!any) { g.DrawString(text, font, brush, r, fmt); return; }
            Font ef;
            if (!emojiFonts.TryGetValue(font.Size, out ef)) { ef = new Font("Segoe UI Emoji", font.Size, FontStyle.Regular, font.Unit); emojiFonts[font.Size] = ef; }
            float x = r.Left;
            int pos = 0;
            while (pos < text.Length && x < r.Right)
            {
                bool em = IsEmojiAt(text, pos);
                int end = pos;
                while (end < text.Length && IsEmojiAt(text, end) == em) end++;
                string run = text.Substring(pos, end - pos);
                Font f = em ? ef : font;
                float w = g.MeasureString(run, f, PointF.Empty, StringFormat.GenericTypographic).Width + (em ? 2 : 0);
                g.DrawString(run, f, brush, new RectangleF(x, r.Top, r.Right - x, r.Height), fmt);
                x += w;
                pos = end;
            }
        }

        void DrawNotification(Graphics g, RectangleF pill, NotificationInfo n, Brush white, Brush gray, StringFormat left, float yOffset, int alpha, bool large)
        {
            float d = large ? 44 : 34;
            float cx = pill.Left + (large ? 18 : 16), cy = pill.Top + yOffset + (large ? (pill.Height - d) / 2 : 6);
            Bitmap icon = IconFor(n);
            if (icon != null)
            {
                var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                g.DrawImage(icon, new Rectangle((int)cx, (int)cy, (int)d, (int)d), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                using (var badge = new SolidBrush(A(AppColor(n.App), alpha)))
                    g.FillEllipse(badge, cx, cy, d, d);
                string initial = string.IsNullOrEmpty(n.App) ? "!" : n.App.Substring(0, 1).ToUpperInvariant();
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(initial, boldFont, white, new RectangleF(cx, cy, d, d + 1), center);
            }

            float tx = cx + d + 12, tw = pill.Right - tx - 16;
            if (large)
            {
                g.DrawString((n.App ?? "").ToUpperInvariant(), nameFont, gray, new RectangleF(tx, pill.Top + 8, tw, 16), left);
                DrawRich(g, n.Title, boldFont, white, new RectangleF(tx, pill.Top + 24, tw, 22), left);
                DrawRich(g, n.Body, bodyFont, gray, new RectangleF(tx, pill.Top + 48, tw, 22), left);
            }
            else
            {
                DrawRich(g, n.Title, boldFont, white, new RectangleF(tx, pill.Top + yOffset + 3, tw - 60, 22), left);
                DrawRich(g, n.Body.Length > 0 ? n.Body : n.App, nameFont, gray, new RectangleF(tx, pill.Top + yOffset + 25, tw - 60, 18), left);
            }
        }

        void DrawCopy(Graphics g, RectangleF pill, Brush white, Brush gray, int alpha)
        {
            var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            float x = pill.Left + 18, cy = pill.Top + pill.Height / 2f;
            using (var pen = new Pen(A(Color.FromArgb(10, 132, 255), alpha), 1.4f))
            {
                g.DrawRectangle(pen, x + 3, cy - 5, 9, 11);
                g.DrawRectangle(pen, x, cy - 8, 9, 11);
            }
            g.DrawString("Copied", boldFont, white, new RectangleF(x + 22, pill.Top, 54, pill.Height), fmt);
            g.DrawString(copyText ?? "", bodyFont, gray, new RectangleF(x + 80, pill.Top, pill.Width - 80 - 36, pill.Height), fmt);
        }

        void DrawPower(Graphics g, RectangleF pill, int alpha, StringFormat left)
        {
            float x = pill.Left + 20, y = pill.Top + pill.Height / 2f - 6;
            Color c = A(powerColor, alpha);
            using (var pen = new Pen(c, 1.3f))
            using (var fill = new SolidBrush(c))
            {
                g.DrawRectangle(pen, x, y, 22, 12);
                g.FillRectangle(fill, x + 23, y + 3.5f, 2, 5);
                g.FillRectangle(fill, x + 2, y + 2, Math.Max(2, 18f * SystemInfo.BatteryPercent / 100f), 8);
                g.DrawString(powerText, boldFont, fill, new RectangleF(x + 38, pill.Top, pill.Width - 80, pill.Height), left);
            }
        }

        // Expanded island: date/time + battery ring, media controls, notifications with icons, Clear pill.
        void DrawPanel(Graphics g, RectangleF pill, Brush white, Brush gray, StringFormat left, StringFormat right, int alpha)
        {
            DrawHeader(g, pill, white, gray, left, alpha);
            float y = HeaderH;
            if (Feed.Media != null) { DrawMediaPeek(g, pill, white, gray, left, alpha); y += MediaH; }

            DrawTabs(g, pill, y, white, gray, alpha);
            y += TabH;
            if (clipTab) { DrawClipboard(g, pill, y, white, gray, alpha); return; }

            if (Feed.History.Count == 0)
            {
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("No notifications", bodyFont, gray, new RectangleF(pill.Left, pill.Top + y, pill.Width, 28), center);
                return;
            }

            int rows = Math.Min(3, Feed.History.Count);
            for (int i = 0; i < rows; i++)
            {
                NotificationInfo n = Feed.History[i];
                float shift = i >= shiftFrom ? (float)rowShift.Value : 0;
                float ry = y + i * RowH + shift;
                rowRects.Add(new RowRect { Rect = new Rectangle((int)pill.Left, (int)(pill.Top + ry), (int)pill.Width, RowH), N = n, Index = i });

                if (n != swipeN)
                {
                    DrawNotification(g, pill, n, white, gray, left, ry, alpha, false);
                    g.DrawString(n.Time.ToString("h:mm tt"), nameFont, gray, new RectangleF(pill.Right - 90, pill.Top + ry + 4, 72, 18), right);
                    continue;
                }

                // The row being swiped follows the cursor and fades as it leaves.
                float ox = (float)swipeX.Value;
                int ra = (int)(alpha * Math.Max(0, 1 - Math.Abs(ox) / (pill.Width * 0.7f)));
                RectangleF moved = pill;
                moved.X += ox;
                using (var rw = new SolidBrush(A(Color.White, ra)))
                using (var rg = new SolidBrush(A(Color.FromArgb(150, 255, 255, 255), ra)))
                {
                    DrawNotification(g, moved, n, rw, rg, left, ry, ra, false);
                    g.DrawString(n.Time.ToString("h:mm tt"), nameFont, rg, new RectangleF(moved.Right - 90, pill.Top + ry + 4, 72, 18), right);
                }
            }
            y += rows * RowH + (shiftFrom <= rows ? (float)rowShift.Value : 0);

            // Clear pill
            var clear = new Rectangle((int)(pill.Left + pill.Width / 2 - 45), (int)(pill.Top + y + 8), 90, 26);
            hits.Add(new Hit { Rect = clear, Click = Feed.ClearAll });
            using (var path = Layered.RoundedRect(clear, 13))
            using (var fillB = new SolidBrush(A(Color.FromArgb(46, 255, 255, 255), alpha)))
                g.FillPath(fillB, path);
            var mid = new StringFormat(left) { Alignment = StringAlignment.Center };
            g.DrawString("Clear", bodyFont, white, new RectangleF(clear.Left, clear.Top, clear.Width, clear.Height), mid);
        }

        // Segmented switch: Notifications | Clipboard.
        void DrawTabs(Graphics g, RectangleF pill, float y, Brush white, Brush gray, int alpha)
        {
            float w = 112, h = 24, x0 = pill.Left + pill.Width / 2 - w, ty = pill.Top + y + 2;
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                bool on = (i == 1) == clipTab;
                var r = new Rectangle((int)(x0 + i * w), (int)ty, (int)w, (int)h);
                bool toClip = i == 1;
                hits.Add(new Hit { Rect = r, Click = () => { clipTab = toClip; } });
                if (on)
                    using (var path = Layered.RoundedRect(r, 12))
                    using (var fb = new SolidBrush(A(Color.FromArgb(56, 255, 255, 255), alpha)))
                        g.FillPath(fb, path);
                g.DrawString(i == 0 ? "Notifications" : "Clipboard", nameFont, on ? white : gray, r, center);
            }
        }

        void DrawClipboard(Graphics g, RectangleF pill, float y, Brush white, Brush gray, int alpha)
        {
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            if (clipHistory.Count == 0)
            {
                g.DrawString("Nothing copied yet", bodyFont, gray, new RectangleF(pill.Left, pill.Top + y, pill.Width, 28), center);
                return;
            }
            int rows = Math.Min(ClipMax, clipHistory.Count);
            var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            Point cur = PointToClient(Cursor.Position);
            for (int i = 0; i < rows; i++)
            {
                string text = clipHistory[i];
                var r = new Rectangle((int)pill.Left + 10, (int)(pill.Top + y + i * ClipRowH), (int)pill.Width - 20, ClipRowH - 2);
                hits.Add(new Hit { Rect = r, Click = () => CopyBack(text) });
                if (r.Contains(cur))
                    using (var path = Layered.RoundedRect(r, 10))
                    using (var fb = new SolidBrush(A(Color.FromArgb(34, 255, 255, 255), alpha)))
                        g.FillPath(fb, path);
                string line = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
                g.DrawString(line, bodyFont, white, new RectangleF(r.Left + 12, r.Top, r.Width - 24, r.Height), fmt);
            }
            y += rows * ClipRowH;
            var clear = new Rectangle((int)(pill.Left + pill.Width / 2 - 45), (int)(pill.Top + y + 8), 90, 26);
            hits.Add(new Hit { Rect = clear, Click = () => clipHistory.Clear() });
            using (var path = Layered.RoundedRect(clear, 13))
            using (var fillB = new SolidBrush(A(Color.FromArgb(46, 255, 255, 255), alpha)))
                g.FillPath(fillB, path);
            g.DrawString("Clear", bodyFont, white, clear, center);
        }

        // Puts a history item back on the clipboard (moved to the top), and confirms with the Copied pill.
        void CopyBack(string text)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
            catch { return; }
            clipHistory.Remove(text);
            clipHistory.Insert(0, text);
            copyText = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            if (copyText.Length > 120) copyText = copyText.Substring(0, 120);
            mode = Mode.Copy;
            until = DateTime.Now.AddSeconds(1.4);
        }

        // ---------- clicks ----------

        // Fingers (or the mouse button) lifted: dismiss if dragged far enough or flicked, otherwise spring back.
        void ReleaseSwipe()
        {
            swipeDown = swipeDragging = swipeWheel = false;
            float dx = (float)swipeX.Value;
            bool flick = Math.Abs(swipeFlick) > 900 && Math.Sign(swipeFlick) == Math.Sign(dx) && Math.Abs(dx) > 12;
            swipeOut = Math.Abs(dx) > swipeRowW * 0.35f || flick;
            swipeDir = dx < 0 ? -1 : 1;
        }

        // Two-finger horizontal touchpad swipe (or a tilt wheel) over a row drags it like the mouse does.
        // The fingers' release is the end of the wheel events: none for ~120 ms.
        void OnHorizontalWheel(int delta)
        {
            if (mode != Mode.Panel) return;
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (swipeN == null)
            {
                Point p = PointToClient(Cursor.Position);
                foreach (RowRect r in rowRects)
                {
                    if (!r.Rect.Contains(p)) continue;
                    swipeN = r.N;
                    swipeIndex = r.Index;
                    swipeRowW = r.Rect.Width;
                    swipeDown = swipeDragging = swipeWheel = true;
                    swipeOut = false;
                    swipeFlick = 0;
                    swipeX.Snap(0);
                    lastWheelT = t;
                    break;
                }
                if (swipeN == null) return;
            }
            else if (!swipeDown || !swipeWheel) return; // mouse drag, or a release animation, is in progress

            float px = delta / 120f * 60f;
            double secs = Math.Max(0.004, (t - lastWheelT) / (double)System.Diagnostics.Stopwatch.Frequency);
            swipeFlick = swipeFlick * 0.5 + px / secs * 0.5;
            lastWheelT = t;
            swipeX.Value += px;
            swipeX.Velocity = 0;
            Wake();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) { OnClipboardChanged(); m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x020E) // WM_MOUSEHWHEEL
            {
                OnHorizontalWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // Brings the app that sent this notification forward (restoring it if minimized), or starts it. False when the
        // sender can't be worked out, so the notification is kept.
        bool ActivateOwner(NotificationInfo n)
        {
            string id = n == null ? null : n.AppId;
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                // A toast that carries a link opens it (WhatsApp, Telegram and Teams style deep links); a toast that carries
                // an activation argument has the app launched with that same argument, so it opens the right chat.
                if (!string.IsNullOrEmpty(n.Launch))
                {
                    Uri link;
                    bool protocol = string.Equals(n.ActivationType, "protocol", StringComparison.OrdinalIgnoreCase);
                    if ((protocol || Uri.IsWellFormedUriString(n.Launch, UriKind.Absolute)) && Pwa.IsSafeLink(n.Launch, out link))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); return true; }
                        catch { }
                    }
                    else if (!protocol)
                    {
                        bool installed;
                        lock (AppDirectory.Names) installed = AppDirectory.Names.ContainsKey(id);
                        // Only Store apps (their id has "!") take an argument this way; others just get opened below.
                        if (installed && id.IndexOf('!') > 0 && Pwa.ActivateWithArguments(id, n.Launch)) return true;
                    }
                }

                IntPtr window = Pwa.FindWindow(id, true); // windows carry the AppUserModelID their toasts use
                if (window != IntPtr.Zero) { WinUtil.Focus(window); return true; }

                string target = null;
                lock (AppDirectory.Names) if (AppDirectory.Names.ContainsKey(id)) target = ShellIcons.AppsFolderPrefix + id;
                if (target == null && (id.IndexOf('\\') >= 0 || id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) && System.IO.File.Exists(id)) target = id;
                return target != null && Pwa.Launch(target);
            }
            catch { return false; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || mode != Mode.Panel || swipeN != null) return;
            foreach (Hit h in hits)
                if (h.Rect.Contains(e.Location)) return; // buttons keep their plain click
            foreach (RowRect r in rowRects)
            {
                if (!r.Rect.Contains(e.Location)) continue;
                swipeN = r.N;
                swipeIndex = r.Index;
                swipeRowW = r.Rect.Width;
                swipeStartX = swipeLastX = e.X;
                swipeLastT = System.Diagnostics.Stopwatch.GetTimestamp();
                swipeFlick = 0;
                swipeDown = true;
                swipeDragging = swipeOut = false;
                swipeX.Snap(0);
                break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!swipeDown || swipeWheel) return;
            float dx = e.X - swipeStartX;
            if (!swipeDragging && Math.Abs(dx) > 4) swipeDragging = true; // below that it's still a click
            if (!swipeDragging) return;

            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            double secs = (t - swipeLastT) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (secs > 0.0005) { swipeFlick = swipeFlick * 0.5 + (e.X - swipeLastX) / secs * 0.5; swipeLastT = t; swipeLastX = e.X; }
            swipeX.Value = dx;
            swipeX.Velocity = 0;
            Wake();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (swipeDown && !swipeWheel)
            {
                if (swipeDragging)
                {
                    ReleaseSwipe();
                    Wake();
                    return; // a drag is never also a click
                }
                swipeDown = false;
                swipeN = null;
            }

            foreach (Hit h in hits)
            {
                if (h.Rect.Contains(e.Location))
                {
                    h.Click();
                    Wake();
                    return;
                }
            }

            // A click on a notification opens the app it came from, and the notification goes away.
            if (mode == Mode.Panel)
            {
                foreach (RowRect r in rowRects)
                {
                    if (!r.Rect.Contains(e.Location)) continue;
                    if (ActivateOwner(r.N)) Feed.Remove(r.N);
                    Wake();
                    return;
                }
            }

            switch (mode)
            {
                case Mode.Notif:
                    if (current != null && ActivateOwner(current)) Feed.Remove(current);
                    mode = Mode.Idle;
                    break;
                case Mode.Idle:
                    mode = Mode.Panel;
                    Feed.Unread = 0;
                    lastHover = DateTime.Now;
                    break;
                case Mode.Panel:
                    break;
            }
            Wake();
        }
    }
}
