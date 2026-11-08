// The macOS "genie" effect: when a window is minimized it is sucked down into its dock icon, and when it is restored it
// flows back out. Windows tells us when any window starts minimizing or restoring (a WinEvent hook). We then show a
// click-through overlay that draws the window as a stack of thin horizontal slices (Windows' own live window thumbnails,
// one per slice, so no screenshots are taken). Each slice is moved and narrowed toward the dock icon, the bottom first,
// which bends the window into the funnel shape.
//
// Limits: the system's own minimize animation still plays underneath (foreign windows cannot switch it off), and windows
// that Windows will not give us a thumbnail for (for example elevated ones) are simply not animated. The windows
// themselves are never touched, so nothing can be left hidden or stuck; the overlay is removed after at most two seconds.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    static class Genie
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }

        const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_CAPTION = 0xC00000;
        const int WPF_RESTORETOMAXIMIZED = 2;

        static IntPtr hook;
        static WinEventProc proc; // kept alive for as long as the hook is installed
        static GenieOverlay overlay; // one overlay window, created once and reused, so an effect starts without delay
        static readonly uint self = (uint)Process.GetCurrentProcess().Id;

        public static void Install()
        {
            if (hook != IntPtr.Zero) return;
            proc = OnEvent;
            hook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            GenieOverlay o = overlay;
            overlay = null;
            if (o != null) { try { o.Dispose(); } catch { } }
        }

        // Stops any animation in progress (display change, errors).
        public static void Abort()
        {
            GenieOverlay o = overlay;
            if (o != null) { try { o.Cancel(); } catch { } }
        }

        // Creates the overlay window ahead of time (on the UI thread), so the first effect is as quick as the rest.
        public static void Warm()
        {
            try { if (overlay == null) overlay = new GenieOverlay(); } catch { }
        }

        static void OnEvent(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (idObject != 0 || hwnd == IntPtr.Zero) return;
                if (!SetCfg.GetBool("genie", true) || Fullscreen.Active) return;
                if (!IsAnimatable(hwnd)) return;

                Rectangle rect;
                if (!RestRect(hwnd, out rect)) return;

                // Restoring needs the real window out of sight while the effect plays. Store (UWP) windows cannot be made see-through,
                // so they would show twice: they get the minimize effect only.
                if (ev == EVENT_SYSTEM_MINIMIZEEND)
                {
                    var cls = new System.Text.StringBuilder(64);
                    GetClassName(hwnd, cls, cls.Capacity);
                    if (cls.ToString() == "ApplicationFrameWindow") return;
                }

                if (overlay == null || overlay.IsDisposed) overlay = new GenieOverlay();
                overlay.Cancel();
                if (!overlay.Begin(hwnd, rect, IconTarget(hwnd), ev == EVENT_SYSTEM_MINIMIZESTART)) overlay.Cancel();
            }
            catch { Abort(); }
        }
        // An ordinary top-level app window: not ours, not a tool window, not the shell, not cloaked.
        static bool IsAnimatable(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd) return false; // 2 = GA_ROOT
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE), style = GetWindowLong(hwnd, GWL_STYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
            if ((style & WS_CAPTION) != WS_CAPTION && (ex & WS_EX_APPWINDOW) == 0) return false;
            if (GetWindow(hwnd, 4) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false; // 4 = GW_OWNER: dialogs and the like

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == self) return false;

            int cloaked;
            if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return false;

            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            return c != "Progman" && c != "WorkerW" && c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd" && c != "XamlExplorerHostIslandWindow";
        }

        // Where the window sits when it is open (a minimized window reports a tiny off-screen rect, so the saved placement is used).
        static bool RestRect(IntPtr hwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(hwnd, ref wp)) return false;
            RECT r = wp.rcNormalPosition;
            if ((wp.flags & WPF_RESTORETOMAXIMIZED) != 0)
            {
                Rectangle wa = Screen.FromHandle(hwnd).WorkingArea;
                rect = new Rectangle(wa.Left, wa.Top, wa.Width, wa.Height);
            }
            else rect = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return rect.Width > 40 && rect.Height > 40;
        }

        // The window's dock icon (its centre), or the middle of the dock's area when it has none.
        static Point IconTarget(IntPtr hwnd)
        {
            Point p;
            DockForm dock = DockForm.Instance;
            if (dock != null && dock.TryIconCenter(hwnd, out p)) return p;
            Rectangle s = Screen.PrimaryScreen.Bounds;
            return new Point(s.Left + s.Width / 2, s.Bottom - 44);
        }
    }

    // A transparent, click-through, full-screen window that holds the slices and animates them. It is created once and
    // reused: showing it and registering the slices takes a few milliseconds, creating it would take over a hundred.
    sealed class GenieOverlay : Form
    {
        const int MaxSlices = 44;        // every slice costs one DWM call per frame: too many and the frames stutter
        const double MinimizeMs = 500, RestoreMs = 420;
        const double Delay = 0.44;       // how much later the top starts than the bottom (0 = whole window at once)
        const double FadeFrom = 0.90;    // the last tenth of the way fades out, so nothing pops off at the icon

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct ThumbProps
        {
            public uint flags;
            public RECT dest, source;
            public byte opacity;
            public int visible, clientAreaOnly;
        }

        const uint TNP_DEST = 0x1, TNP_SOURCE = 0x2, TNP_OPACITY = 0x4, TNP_VISIBLE = 0x8;

        readonly IntPtr[] thumbs = new IntPtr[MaxSlices];
        readonly Stopwatch clock = new Stopwatch();
        readonly VsyncDriver vsync;
        readonly Timer watchdog = new Timer { Interval = 2000 }; // whatever happens, an effect does not outlive this
        Rectangle screen;
        Rectangle rest;      // the window's rectangle when it is open (screen coordinates)
        Point target;        // the dock icon's centre (screen coordinates)
        bool minimizing, running;
        int srcW, srcH;
        readonly long[] last = new long[MaxSlices * 3]; // what each slice was last given, so unchanged slices cost no DWM call
        double frameT;        // animation time actually shown (ms): follows the clock, but a late or early tick cannot make a big jump
        int slices = MaxSlices; // chosen per window: about one slice per 14 px of height
        IntPtr hiddenWindow;  // the real window, made see-through while it is being drawn by the effect (restore only)
        int hiddenStyle;

        public GenieOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black; // glass: black client area is see-through once the frame is extended
            screen = Screen.PrimaryScreen.Bounds;
            Bounds = screen;
            CreateHandle();
            var margins = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
            vsync = new VsyncDriver(this, Step);
            watchdog.Tick += (s, e) => Cancel();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x8; // tool window, no-activate, click-through, topmost
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // Registers the slices and starts the animation. False when Windows will not give us a thumbnail.
        public bool Begin(IntPtr source, Rectangle rest, Point target, bool minimizing)
        {
            this.rest = rest;
            this.target = target;
            this.minimizing = minimizing;

            Rectangle now = Screen.PrimaryScreen.Bounds;
            if (now != screen) { screen = now; Bounds = now; }

            IntPtr first;
            if (DwmRegisterThumbnail(Handle, source, out first) != 0 || first == IntPtr.Zero) return false;
            SIZE size;
            if (DwmQueryThumbnailSourceSize(first, out size) != 0 || size.cx < 40 || size.cy < 40) { DwmUnregisterThumbnail(first); return false; }
            srcW = size.cx;
            srcH = size.cy;
            slices = Math.Max(28, Math.Min(MaxSlices, rest.Height / 22));
            Array.Clear(last, 0, last.Length);
            frameT = 0;
            thumbs[0] = first;
            for (int i = 1; i < slices; i++)
            {
                IntPtr t;
                if (DwmRegisterThumbnail(Handle, source, out t) != 0 || t == IntPtr.Zero) return false;
                thumbs[i] = t;
            }

            running = true;
            if (!minimizing) HideReal(source);
            Layout(0); // first frame: in place when minimizing, collapsed in the icon when restoring (Layout maps t = 0 to both)
            if (!Visible) Show();
            // Under the dock, so the window disappears into its icon.
            DockForm dock = DockForm.Instance;
            if (dock != null && dock.IsHandleCreated) SetWindowPos(Handle, dock.Handle, 0, 0, 0, 0, 0x0010 | 0x0002 | 0x0001 | 0x0040);

            clock.Restart();
            vsync.Wake();
            watchdog.Stop();
            watchdog.Start();
            return true;
        }

        // Ends the animation (or does nothing if none is running): slices released, window hidden.
        public void Cancel()
        {
            watchdog.Stop();
            vsync.Sleep();
            running = false;
            ShowReal();
            for (int i = 0; i < MaxSlices; i++)
                if (thumbs[i] != IntPtr.Zero) { try { DwmUnregisterThumbnail(thumbs[i]); } catch { } thumbs[i] = IntPtr.Zero; }
            if (Visible && !IsDisposed) Hide();
        }

        void Step()
        {
            if (!running || IsDisposed) return;
            double total = minimizing ? MinimizeMs : RestoreMs;
            // Even pacing: each frame advances by about one refresh interval. A tick the message loop delivered late (or early)
            // would otherwise jump the animation by that error, which reads as stutter. Within 0.75..1.35 refreshes of the
            // real elapsed gap it is smoothed; beyond that the clock wins (a real stall must not slow the whole effect).
            double now = clock.Elapsed.TotalMilliseconds;
            double dtMs = Math.Max(4, Math.Min(34, vsync.Dt * 1000));
            double adv = now - frameT;
            if (adv > dtMs * 2.5) frameT = now;
            else frameT += Math.Max(dtMs * 0.75, Math.Min(dtMs * 1.35, adv));
            frameT = Math.Min(frameT, now + dtMs);
            double t = Math.Min(1.0, frameT / total);
            Layout(t);
            if (frameT >= total) Cancel();
        }

        // While a window is restored, the window itself is made fully transparent so only the effect is seen. It is put back
        // in Cancel(), which every path (end of the animation, the 2 second watchdog, exit, display change) goes through.
        void HideReal(IntPtr window)
        {
            try
            {
                int ex = GetWindowLong(window, -20);
                if ((ex & 0x80000) != 0) return; // already a layered window: leave it alone
                hiddenStyle = ex;
                SetWindowLong(window, -20, ex | 0x80000);           // WS_EX_LAYERED
                SetLayeredWindowAttributes(window, 0, 0, 2);        // LWA_ALPHA, fully transparent
                hiddenWindow = window;
            }
            catch { hiddenWindow = IntPtr.Zero; }
        }

        void ShowReal()
        {
            IntPtr w = hiddenWindow;
            hiddenWindow = IntPtr.Zero;
            if (w == IntPtr.Zero) return;
            try
            {
                if (!IsWindow(w)) return;
                SetLayeredWindowAttributes(w, 0, 255, 2);
                SetWindowLong(w, -20, hiddenStyle);                 // the original style, not layered
                RedrawWindow(w, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); // INVALIDATE | UPDATENOW? | ALLCHILDREN | FRAME
            }
            catch { }
        }

        static double Ease(double x)
        {
            x = Math.Max(0, Math.Min(1, x));
            return x * x * (3 - 2 * x); // smoothstep
        }

        // Time curve of the whole effect. macOS sucks the window in with an accelerating pull: a gentle start, fast into the
        // icon. A symmetric smoothstep would crawl at the end, so blend it with an ease-in; f(0) = 0, f(1) = 1, f'(1) > 0.
        static double Pull(double x)
        {
            x = Math.Max(0, Math.Min(1, x));
            return 0.35 * x * x * (3 - 2 * x) + 0.65 * Math.Pow(x, 2.2);
        }

        // t runs 0 -> 1 in time. Minimizing: the window goes from its place to the icon. Restoring: the reverse.
        void Layout(double t)
        {
            // 0 = window in place, 1 = collapsed into the icon. Restoring is the exact reverse (leaves the icon fast, settles gently).
            double progress = Pull(minimizing ? t : 1 - t);
            double iconW = 54, neckY = target.Y;
            int n = slices;
            byte alpha = progress <= FadeFrom ? (byte)255 : (byte)Math.Round(255 * Math.Max(0, 1 - (progress - FadeFrom) / (1 - FadeFrom)));

            // Slice boundaries 0..n: each has its own progress, the bottom starting first and the top following
            // later, which is what bends the window into a funnel.
            var x0 = new double[n + 1];
            var x1 = new double[n + 1];
            var y = new double[n + 1];
            for (int k = 0; k <= n; k++)
            {
                double row = k / (double)n;                      // 0 = top, 1 = bottom
                double delay = (1 - row) * Delay;
                double u = Ease((progress - delay) / (1 - Delay));
                double yRest = rest.Top + row * rest.Height;
                double cx = rest.Left + rest.Width / 2.0;
                double halfRest = rest.Width / 2.0;
                double centre = cx + (target.X - cx) * u;
                double half = halfRest + (iconW / 2 - halfRest) * u;
                x0[k] = centre - half;
                x1[k] = centre + half;
                y[k] = yRest + (neckY - yRest) * u;
            }

            for (int i = 0; i < n; i++)
            {
                if (thumbs[i] == IntPtr.Zero) continue;
                int top = (int)Math.Round(y[i]) - screen.Top;
                int bottom = (int)Math.Round(y[i + 1]) - screen.Top;
                // Rows that have fully reached the icon sit on one line: leave them out instead of stacking them there.
                bool folded = y[i + 1] - y[i] < 0.5;
                if (bottom <= top) bottom = top + 1;
                var props = new ThumbProps
                {
                    flags = TNP_DEST | TNP_SOURCE | TNP_VISIBLE | TNP_OPACITY,
                    dest = new RECT
                    {
                        Left = (int)Math.Round((x0[i] + x0[i + 1]) / 2) - screen.Left,
                        Top = top,
                        Right = (int)Math.Round((x1[i] + x1[i + 1]) / 2) - screen.Left,
                        Bottom = bottom
                    },
                    source = new RECT { Left = 0, Top = srcH * i / n, Right = srcW, Bottom = srcH * (i + 1) / n },
                    opacity = alpha,
                    visible = folded ? 0 : 1
                };
                if (props.dest.Right <= props.dest.Left) props.dest.Right = props.dest.Left + 1;
                long a = ((long)props.dest.Left << 32) | (uint)props.dest.Right;
                long b = ((long)props.dest.Top << 32) | (uint)props.dest.Bottom;
                long c = ((long)props.opacity << 8) | (uint)props.visible | 0x10000;
                if (last[i * 3] == a && last[i * 3 + 1] == b && last[i * 3 + 2] == c) continue;
                last[i * 3] = a; last[i * 3 + 1] = b; last[i * 3 + 2] = c;
                DwmUpdateThumbnailProperties(thumbs[i], ref props);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Cancel(); } catch { }
                try { vsync.Dispose(); } catch { }
                try { watchdog.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}