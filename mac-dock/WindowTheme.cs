// System-wide macOS-style window chrome, using only supported Windows APIs (no injection, no hooking of other processes).
//
//  1. Styling through DWM (DwmSetWindowAttribute works on other processes' windows): dark/light title bar, rounded corners,
//     a subtle border and a flat title-bar colour that matches the overlay below.
//  2. For ordinary windows with a standard Win32 title bar, a click-through overlay is laid exactly over that title bar. It
//     draws the title bar surface, the centred title, and the red / yellow / green buttons on the left (12 px circles, 8 px gap).
//     The native buttons on the right are covered. The overlay passes every mouse event through except those on the three
//     circles (and a dead zone over the old native buttons), so dragging, double-click maximise, snapping, resizing and the
//     system menu keep working natively. The circles send the standard SC_CLOSE / SC_MINIMIZE / SC_MAXIMIZE / SC_RESTORE
//     system commands. Minimising therefore also triggers the existing Genie effect.
//  3. Windows are tracked through WinEvent notifications (event driven, nothing polls). Only a window whose title bar actually
//     moved is repainted; hover fades run on the compositor-paced VsyncDriver, so they follow the monitor's refresh rate.
//
// Limits: apps that draw their own title bar (Chrome, Edge, Electron, Explorer tabs, UWP/WinUI, Office ribbons) are only
// styled through DWM, because covering their custom title bar would hide real controls. Windows can't be given new
// open/close/maximise animations from outside the process; those stay the system's own. Elevated windows ignore the
// buttons (UIPI blocks messages from a normal process). "@windowtheme off" in dock.txt disables everything;
// "@windowtheme exclude app.exe" skips one program.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    struct CaptionInfo
    {
        public int X, Y, W, H;       // screen rectangle of the title bar (the part that is covered)
        public int DeadFrom;         // x (relative) where the old native buttons start
        public int Dpi;
        public bool Active, Maximized, CanMin, CanMax, Dark, Square;
        public string Title;

        public bool SameAs(CaptionInfo o)
        {
            return X == o.X && Y == o.Y && W == o.W && H == o.H && DeadFrom == o.DeadFrom && Dpi == o.Dpi && Active == o.Active &&
                   Maximized == o.Maximized && CanMin == o.CanMin && CanMax == o.CanMax && Dark == o.Dark && Square == o.Square && Title == o.Title;
        }
    }

    static class WindowTheme
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WRect rect);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out WRect value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        public struct WRect { public int Left, Top, Right, Bottom; }

        const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
                   EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_NAMECHANGE = 0x800C, EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
        const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        const int WS_CAPTION = 0xC00000, WS_CHILD = 0x40000000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CORNER = 33, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
        const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

        public const int MaxOverlays = 32;

        static IntPtr hookSystem, hookObject;
        static WinEventProc proc;
        static readonly Dictionary<IntPtr, CaptionOverlay> overlays = new Dictionary<IntPtr, CaptionOverlay>();
        static readonly Dictionary<IntPtr, bool> styled = new Dictionary<IntPtr, bool>(); // hwnd -> was styled for dark?
        static readonly Dictionary<uint, string> exeCache = new Dictionary<uint, string>();
        static readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly uint self = (uint)Process.GetCurrentProcess().Id;
        static bool enabled, installed;

        // Programs that draw their own title bar (or aren't ordinary windows): DWM styling only.
        static readonly string[] CustomChrome =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "ApplicationFrameWindow", "Windows.UI.Core.CoreWindow",
            "CabinetWClass", "Chrome_WidgetWin", "MozillaWindowClass", "WinUIDesktopWin32WindowClass", "Microsoft.UI.Content",
            "XLMAIN", "OpusApp", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "Qt6QWindowIcon", "SDL_app", "GLFW"
        };

        // Fixed colours (the three macOS traffic lights) and the title-bar surfaces.
        public static readonly Color Red = Color.FromArgb(0xFF, 0x5F, 0x57), Yellow = Color.FromArgb(0xFE, 0xBC, 0x2E), Green = Color.FromArgb(0x28, 0xC8, 0x40);

        public static bool IsInstalled { get { return installed; } }
        public static int OverlayCount { get { return overlays.Count; } }
        public static CaptionOverlay Find(IntPtr hwnd) { CaptionOverlay o; return overlays.TryGetValue(hwnd, out o) ? o : null; }

        public static void Install()
        {
            if (installed) return;
            ReadConfig();
            if (!enabled) return;
            installed = true;
            proc = OnEvent;
            hookSystem = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            hookObject = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            EnumWindows((h, p) => { Update(h); return true; }, IntPtr.Zero);
        }

        public static void Uninstall()
        {
            if (!installed) return;
            installed = false;
            if (hookSystem != IntPtr.Zero) UnhookWinEvent(hookSystem);
            if (hookObject != IntPtr.Zero) UnhookWinEvent(hookObject);
            hookSystem = hookObject = IntPtr.Zero;
            foreach (CaptionOverlay o in overlays.Values.ToList()) { try { o.Dispose(); } catch { } }
            overlays.Clear();
            RestoreStyles();
        }

        // The user switched between light and dark: restyle everything.
        public static void ThemeChanged()
        {
            if (!installed) return;
            styled.Clear();
            RefreshAll();
        }

        static void ReadConfig()
        {
            enabled = true;
            excluded.Clear();
            try
            {
                if (!File.Exists(AppPaths.Config)) return;
                foreach (string raw in File.ReadAllLines(AppPaths.Config))
                {
                    string l = raw.Trim();
                    if (!l.ToLowerInvariant().StartsWith("@windowtheme")) continue;
                    string rest = l.Substring("@windowtheme".Length).Trim().TrimStart('=').Trim();
                    if (rest.Equals("off", StringComparison.OrdinalIgnoreCase)) enabled = false;
                    else if (rest.ToLowerInvariant().StartsWith("exclude ")) excluded.Add(rest.Substring(8).Trim());
                }
            }
            catch { }
        }

        static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (ev >= EVENT_OBJECT_CREATE && (idObject != 0 || idChild != 0)) return; // only the window itself, not its caret / cursor / children
                switch (ev)
                {
                    case EVENT_SYSTEM_FOREGROUND:
                        RefreshAll();
                        break;
                    case EVENT_OBJECT_DESTROY:
                    case EVENT_OBJECT_HIDE:
                    case EVENT_OBJECT_CLOAKED:
                        Remove(hwnd);
                        break;
                    case EVENT_SYSTEM_MINIMIZESTART:
                        Remove(hwnd);
                        break;
                    case EVENT_OBJECT_LOCATIONCHANGE:
                    case EVENT_OBJECT_NAMECHANGE:
                        if (overlays.ContainsKey(hwnd)) Update(hwnd);
                        else if (ev == EVENT_OBJECT_LOCATIONCHANGE && GetAncestor(hwnd, 2) == hwnd && IsWindowVisible(hwnd)) Update(hwnd);
                        break;
                    default: // create / show / uncloak / minimize end / move-size end
                        if (GetAncestor(hwnd, 2) == hwnd) Update(hwnd);
                        break;
                }
            }
            catch (Exception ex) { MacDockLog.Write(ex); }
        }

        static void RefreshAll()
        {
            IntPtr[] keys = overlays.Keys.ToArray();
            foreach (IntPtr h in keys) Update(h);
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && !overlays.ContainsKey(fg)) Update(fg);
        }

        static void Remove(IntPtr hwnd)
        {
            CaptionOverlay o;
            if (overlays.TryGetValue(hwnd, out o))
            {
                overlays.Remove(hwnd);
                try { o.Dispose(); } catch { }
            }
        }

        public static void Update(IntPtr hwnd)
        {
            if (!installed) return;
            CaptionKind kind = Classify(hwnd);
            if (kind == CaptionKind.None) { Remove(hwnd); return; }
            Style(hwnd);
            if (kind != CaptionKind.Standard) { Remove(hwnd); return; }

            CaptionInfo info;
            if (!Measure(hwnd, out info)) { Remove(hwnd); return; }
            CaptionOverlay ov;
            if (!overlays.TryGetValue(hwnd, out ov))
            {
                if (overlays.Count >= MaxOverlays) return;
                ov = new CaptionOverlay(hwnd);
                overlays[hwnd] = ov;
            }
            ov.Apply(info);
        }

        enum CaptionKind { None, Styled, Standard }

        static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        static string ExeOf(uint pid)
        {
            string name;
            if (exeCache.TryGetValue(pid, out name)) return name;
            name = "";
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(520);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) name = Path.GetFileName(sb.ToString());
                CloseHandle(h);
            }
            if (exeCache.Count > 512) exeCache.Clear();
            exeCache[pid] = name;
            return name;
        }

        static CaptionKind Classify(IntPtr hwnd)
        {
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return CaptionKind.None;
            if (GetAncestor(hwnd, 2) != hwnd || GetWindow(hwnd, 4) != IntPtr.Zero) return CaptionKind.None; // top-level, no owner (dialogs are left alone)
            int style = GetWindowLong(hwnd, -16), ex = GetWindowLong(hwnd, -20);
            if ((style & WS_CAPTION) != WS_CAPTION || (style & WS_CHILD) != 0) return CaptionKind.None;
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0 && (ex & WS_EX_APPWINDOW) == 0) return CaptionKind.None;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == self) return CaptionKind.None;
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return CaptionKind.None;
            string exe = ExeOf(pid);
            if (exe.Length > 0 && excluded.Contains(exe)) return CaptionKind.None;
            WRect r;
            if (!GetWindowRect(hwnd, out r) || r.Right - r.Left < 140 || r.Bottom - r.Top < 60) return CaptionKind.None;

            string cls = ClassOf(hwnd);
            foreach (string c in CustomChrome) if (cls.StartsWith(c, StringComparison.Ordinal)) return CaptionKind.Styled;
            return CaptionKind.Standard;
        }

        // Where the standard title bar is, in screen pixels.
        static bool Measure(IntPtr hwnd, out CaptionInfo info)
        {
            info = new CaptionInfo();
            WRect wr, fb, cb;
            if (!GetWindowRect(hwnd, out wr)) return false;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out fb, 16) != 0) fb = wr;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out cb, 16) != 0 || cb.Bottom <= 0) return false;

            bool max = IsZoomed(hwnd);
            int dpi = 96;
            try { dpi = (int)GetDpiForWindow(hwnd); } catch { }
            if (dpi < 48) dpi = 96;

            int inset = max ? 0 : 1; // keep the 1 px DWM border visible
            int captionBottom = wr.Top + cb.Bottom; // cb is relative to the window rectangle
            int x = fb.Left + inset, y = fb.Top + inset;
            int w = fb.Right - fb.Left - inset * 2, h = captionBottom - y;
            if (w < 100 || h < 16 || h > 120) return false;

            int style = GetWindowLong(hwnd, -16);
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);

            info.X = x; info.Y = y; info.W = w; info.H = h;
            info.DeadFrom = Math.Max(60, (wr.Left + cb.Left) - x);
            info.Dpi = dpi;
            info.Active = GetForegroundWindow() == hwnd;
            info.Maximized = max;
            info.CanMin = (style & WS_MINIMIZEBOX) != 0;
            info.CanMax = (style & WS_MAXIMIZEBOX) != 0;
            info.Dark = !Theme.Light;
            info.Square = max;
            info.Title = sb.ToString();
            return true;
        }

        public static Color Surface(bool dark, bool active)
        {
            if (dark) return active ? Color.FromArgb(0x2D, 0x2D, 0x30) : Color.FromArgb(0x26, 0x26, 0x29);
            return active ? Color.FromArgb(0xE9, 0xE9, 0xEC) : Color.FromArgb(0xF3, 0xF3, 0xF5);
        }

        public static Color TitleText(bool dark, bool active)
        {
            if (dark) return active ? Color.FromArgb(0xEC, 0xEC, 0xEE) : Color.FromArgb(0x8C, 0x8C, 0x91);
            return active ? Color.FromArgb(0x26, 0x26, 0x29) : Color.FromArgb(0x9A, 0x9A, 0x9F);
        }

        static int Rgb(Color c) { return c.R | (c.G << 8) | (c.B << 16); } // COLORREF

        // DWM attributes: dark/light title bar, rounded corners, flat caption colour, thin border.
        static void Style(IntPtr hwnd)
        {
            bool dark = !Theme.Light, was;
            if (styled.TryGetValue(hwnd, out was) && was == dark) return;
            if (styled.Count > 2048) styled.Clear();
            styled[hwnd] = dark;
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
            v = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_CORNER, ref v, 4);
            v = Rgb(dark ? Color.FromArgb(0x4A, 0x4A, 0x4E) : Color.FromArgb(0xD2, 0xD2, 0xD6));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref v, 4);
            v = Rgb(Surface(dark, true));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref v, 4);
            v = Rgb(TitleText(dark, true));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref v, 4);
        }

        // Hands the system defaults back (DWMWA_COLOR_DEFAULT) so quitting leaves nothing behind.
        static void RestoreStyles()
        {
            foreach (IntPtr h in styled.Keys.ToList())
            {
                if (!IsWindow(h)) continue;
                int def = unchecked((int)0xFFFFFFFF);
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref def, 4);
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref def, 4);
                DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref def, 4);
                int v = 0;
                DwmSetWindowAttribute(h, DWMWA_CORNER, ref v, 4);
            }
            styled.Clear();
        }

        // The three actions, as standard system commands (exactly what the native buttons send).
        public static void Perform(IntPtr hwnd, int button)
        {
            const int SC_CLOSE = 0xF060, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
            int cmd = button == 0 ? SC_CLOSE : button == 1 ? SC_MINIMIZE : (IsZoomed(hwnd) ? SC_RESTORE : SC_MAXIMIZE);
            PostMessage(hwnd, 0x0112, (IntPtr)cmd, IntPtr.Zero);
        }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    }

    static class MacDockLog
    {
        public static void Write(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock", "dock.log"), DateTime.Now + " WindowTheme: " + ex + "\r\n"); }
            catch { }
        }
    }

    // One overlay per themed window, laid over its title bar.
    sealed class CaptionOverlay : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);

        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);

        public readonly IntPtr Target;
        CaptionInfo info;
        bool have;
        Surface surface;
        int pressed = -1;
        bool groupHover;
        float symbol; // 0..1 fade of the x / minus / plus symbols
        VsyncDriver driver;
        bool shown;

        public CaptionOverlay(IntPtr target)
        {
            Target = target;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.Selectable, false);
            driver = new VsyncDriver(this, Step);
            IntPtr h = Handle; // creates the window
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // layered, tool window, no activate
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) // WM_NCHITTEST: only the circles and the dead zone catch the mouse; the rest falls through to the window
            {
                int x = unchecked((short)((long)m.LParam & 0xFFFF)), y = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                int lx = x - info.X, ly = y - info.Y;
                if (HitButton(lx, ly) >= 0 || InLightsBox(lx, ly) || (lx >= info.DeadFrom && lx < info.W && ly >= 0 && ly < info.H))
                {
                    m.Result = (IntPtr)1; // HTCLIENT
                    return;
                }
                m.Result = (IntPtr)(-1); // HTTRANSPARENT
                return;
            }
            if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        float Scale { get { return info.Dpi / 96f; } }

        // The button box (all three circles plus the gaps between them), used to keep the mouse "inside" while sliding between circles.
        bool InLightsBox(int lx, int ly)
        {
            float s = Scale;
            float d = 12 * s, gap = 8 * s;
            float left = 13 * s - 2 * s, right = 13 * s + 3 * d + 2 * gap + 2 * s;
            return lx >= left && lx <= right && Math.Abs(ly - info.H / 2f) <= d;
        }

        Rectangle Circle(int i)
        {
            float s = Scale;
            float d = 12 * s, gap = 8 * s;
            float x = 13 * s + i * (d + gap);
            float y = (info.H - d) / 2f;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(d), (int)Math.Round(d));
        }

        int HitButton(int lx, int ly)
        {
            for (int i = 0; i < 3; i++)
            {
                Rectangle c = Circle(i);
                c.Inflate((int)Math.Round(3 * Scale), (int)Math.Round(3 * Scale)); // a little forgiveness around each circle
                if (c.Contains(lx, ly)) return i;
            }
            return -1;
        }

        bool BtnEnabled(int i) { return i == 0 || (i == 1 ? info.CanMin : info.CanMax); }

        // Moves / resizes / repaints the overlay when the title bar changed.
        public void Apply(CaptionInfo next)
        {
            bool sameShape = have && info.X == next.X && info.Y == next.Y && info.W == next.W && info.H == next.H;
            bool same = have && info.SameAs(next);
            info = next;
            have = true;
            if (!same || !shown)
            {
                if (surface == null || surface.Width != info.W || surface.Height != info.H)
                {
                    if (surface != null) surface.Dispose();
                    surface = new Surface(info.W, info.H);
                }
                Render();
            }
            if (!shown || !sameShape || !same) Place();
        }

        // Directly above its window in z-order (so windows in front still cover it).
        void Place()
        {
            bool topmost = (GetWindowLong(Target, -20) & 0x8) != 0; // WS_EX_TOPMOST
            IntPtr above = topmost ? HWND_TOPMOST : GetWindow(Target, 3); // GW_HWNDPREV
            if (above == IntPtr.Zero) above = HWND_TOP;
            if (above == Handle) above = HWND_TOP;
            SetWindowPos(Handle, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
        }

        void Render()
        {
            if (surface == null) return;
            bool dark = info.Dark;
            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Typography.Prepare(g);
                float s = Scale;
                Color bg = WindowTheme.Surface(dark, info.Active);

                // Title bar surface. Top corners follow the window's rounded corners (8 px at 100%).
                float r = info.Square ? 0 : 8 * s;
                using (GraphicsPath p = new GraphicsPath())
                {
                    RectangleF rc = new RectangleF(0, 0, info.W, info.H);
                    if (r <= 0) p.AddRectangle(rc);
                    else
                    {
                        p.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
                        p.AddArc(info.W - 2 * r, 0, 2 * r, 2 * r, 270, 90);
                        p.AddLine(info.W, r, info.W, info.H);
                        p.AddLine(info.W, info.H, 0, info.H);
                    }
                    p.CloseFigure();
                    using (var b = new SolidBrush(bg)) g.FillPath(b, p);
                }
                // Hairline under the title bar.
                using (var pen = new Pen(Color.FromArgb(dark ? 70 : 48, dark ? 0 : 0, 0, 0), 1f))
                    g.DrawLine(pen, 0, info.H - 0.5f, info.W, info.H - 0.5f);

                // Title, centred in the bar (but never under the buttons).
                float leftEdge = Circle(2).Right + 14 * s, rightEdge = info.DeadFrom - 8 * s;
                if (!string.IsNullOrEmpty(info.Title) && rightEdge - leftEdge > 40)
                {
                    Font f = Typography.Text(9.5f * s, Weight.Semibold); // cached by Typography: never disposed here
                    using (var b =new SolidBrush(WindowTheme.TitleText(dark, info.Active)))
                    using (StringFormat fmt = Typography.Format(StringAlignment.Center))
                    {
                        float half = Math.Min(info.W / 2f - leftEdge, rightEdge - info.W / 2f);
                        if (half < 20) half = 20;
                        g.DrawString(info.Title, f, b, new RectangleF(info.W / 2f - half, 0, half * 2, info.H), fmt);
                    }
                }

                // Traffic lights.
                for (int i = 0; i < 3; i++) DrawLight(g, i);
            }
            surface.Present(Handle, info.X, info.Y);
        }

        void DrawLight(Graphics g, int i)
        {
            Rectangle c = Circle(i);
            bool active = info.Active || groupHover;       // macOS keeps the colours while hovering an inactive window
            bool enabled = BtnEnabled(i);
            Color baseColor = i == 0 ? WindowTheme.Red : i == 1 ? WindowTheme.Yellow : WindowTheme.Green;
            Color fill;
            Color edge;
            if (!enabled || !active)
            {
                fill = info.Dark ? Color.FromArgb(0x57, 0x57, 0x5B) : Color.FromArgb(0xCF, 0xCF, 0xD3);
                edge = info.Dark ? Color.FromArgb(0x6A, 0x6A, 0x6E) : Color.FromArgb(0xB9, 0xB9, 0xBD);
            }
            else
            {
                fill = baseColor;
                edge = Color.FromArgb(70, 0, 0, 0);
                if (pressed == i) fill = Mix(baseColor, Color.Black, 0.22f);
            }
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, c);
            using (var p = new Pen(Color.FromArgb(!enabled || !active ? 255 : 90, edge), Math.Max(1f, Scale * 0.75f)))
            {
                var inner = new RectangleF(c.X + 0.4f, c.Y + 0.4f, c.Width - 0.8f, c.Height - 0.8f);
                g.DrawEllipse(p, inner);
            }

            if (symbol > 0.02f && enabled && active)
            {
                int a = (int)Math.Round(symbol * 190);
                using (var p = new Pen(Color.FromArgb(a, 40, 14, 12), Math.Max(1.2f, Scale * 1.15f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = c.X + c.Width / 2f, cy = c.Y + c.Height / 2f, u = c.Width * 0.19f;
                    if (i == 0)
                    {
                        g.DrawLine(p, cx - u, cy - u, cx + u, cy + u);
                        g.DrawLine(p, cx - u, cy + u, cx + u, cy - u);
                    }
                    else if (i == 1)
                    {
                        g.DrawLine(p, cx - u * 1.25f, cy, cx + u * 1.25f, cy);
                    }
                    else if (info.Maximized)
                    {
                        // restore: two arrows pointing in
                        g.DrawLine(p, cx - u * 1.1f, cy + u * 1.1f, cx - u * 1.1f + u * 1.2f, cy + u * 1.1f - u * 1.2f);
                        g.DrawLine(p, cx + u * 1.1f, cy - u * 1.1f, cx + u * 1.1f - u * 1.2f, cy - u * 1.1f + u * 1.2f);
                        FillArrow(g, p.Color, cx - u * 0.1f, cy + u * 0.1f, -1, u);
                        FillArrow(g, p.Color, cx + u * 0.1f, cy - u * 0.1f, 1, u);
                    }
                    else
                    {
                        // zoom: two arrows pointing out (diagonal corners)
                        FillArrow(g, p.Color, cx + u * 1.35f, cy - u * 1.35f, 1, u);
                        FillArrow(g, p.Color, cx - u * 1.35f, cy + u * 1.35f, -1, u);
                        g.DrawLine(p, cx - u * 0.8f, cy + u * 0.8f, cx + u * 0.8f, cy - u * 0.8f);
                    }
                }
            }
        }

        // A small filled right triangle at (x, y) pointing up-right (dir 1) or down-left (dir -1).
        static void FillArrow(Graphics g, Color color, float x, float y, int dir, float u)
        {
            float k = u * 1.15f;
            PointF[] pts = dir > 0
                ? new[] { new PointF(x + k * 0.3f, y - k * 0.3f), new PointF(x - k * 0.9f, y - k * 0.3f), new PointF(x + k * 0.3f, y + k * 0.9f) }
                : new[] { new PointF(x - k * 0.3f, y + k * 0.3f), new PointF(x + k * 0.9f, y + k * 0.3f), new PointF(x - k * 0.3f, y - k * 0.9f) };
            using (var b = new SolidBrush(color)) g.FillPolygon(b, pts);
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Test hook: shows the symbols as if the mouse hovered the buttons.
        public void SetHoverForTest(bool hover) { groupHover = hover; symbol = hover ? 1f : 0f; Render(); }
        public CaptionInfo Info { get { return info; } }
        public Rectangle CircleForTest(int i) { return Circle(i); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = InLightsBox(e.X, e.Y);
            if (over != groupHover) { groupHover = over; driver.Wake(); Render(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (groupHover || pressed >= 0) { groupHover = false; pressed = -1; driver.Wake(); Render(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int b = HitButton(e.X, e.Y);
            if (b >= 0 && BtnEnabled(b)) { pressed = b; Render(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int was = pressed;
            pressed = -1;
            if (e.Button == MouseButtons.Left && was >= 0 && HitButton(e.X, e.Y) == was) WindowTheme.Perform(Target, was);
            Render();
        }

        // Fades the symbols in / out at the display's refresh rate; sleeps once settled.
        void Step()
        {
            float goal = groupHover ? 1f : 0f;
            float k = (float)Math.Min(1.0, driver.Dt / 0.08); // ~80 ms ease
            symbol += (goal - symbol) * Math.Max(k, 0.2f);
            if (Math.Abs(goal - symbol) < 0.02f) { symbol = goal; driver.Sleep(); }
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (driver != null) { try { driver.Dispose(); } catch { } driver = null; }
                if (surface != null) { surface.Dispose(); surface = null; }
            }
            base.Dispose(disposing);
        }
    }
}
