// Shared Win32 helpers for the top bar, island and Spotlight.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MacDock
{
    static class Win32
    {

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    }

    static class WinUtil
    {
        public const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;
        public const uint WM_CLOSE = 0x10;

        // Brings a window to the foreground, restoring it first if minimized.
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Restores the window if minimized, forces it above other windows (even a topmost one) without leaving it
        // topmost, and works around the foreground lock.
        public static void Focus(IntPtr h)
        {
            if (!NativeMethods.IsWindow(h)) return;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);
            // A synthetic Alt tap lets a background process take the foreground.
            NativeMethods.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(VK_MENU, 0, 2, UIntPtr.Zero);

            uint pid;
            uint me = GetCurrentThreadId();
            uint tgtThread = GetWindowThreadProcessId(h, out pid), fgThread = 0;
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero) fgThread = GetWindowThreadProcessId(fg, out pid);
            bool a1 = false, a2 = false;
            try
            {
                if (fgThread != 0 && fgThread != me) a1 = AttachThreadInput(me, fgThread, true);
                if (tgtThread != 0 && tgtThread != me && tgtThread != fgThread) a2 = AttachThreadInput(me, tgtThread, true);
                BringWindowToTop(h);
                const uint NOSIZE = 1, NOMOVE = 2, SHOW = 0x40;
                SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, NOMOVE | NOSIZE | SHOW); // above any topmost window...
                SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, NOMOVE | NOSIZE | SHOW); // ...then back to a normal window
                NativeMethods.SetForegroundWindow(h);
            }
            finally
            {
                if (a2) AttachThreadInput(me, tgtThread, false);
                if (a1) AttachThreadInput(me, fgThread, false);
            }
        }

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            Win32.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // Presses the keys in order and releases them in reverse, e.g. Combo(VK_CONTROL, 'C').
        public static void Combo(params byte[] keys)
        {
            foreach (byte k in keys) NativeMethods.keybd_event(k, 0, 0, UIntPtr.Zero);
            for (int i = keys.Length - 1; i >= 0; i--) NativeMethods.keybd_event(keys[i], 0, 2, UIntPtr.Zero);
        }
    }

    static class Layered
    {
        // Hands an ARGB bitmap to Windows as the content of a layered window.
        // Fully transparent pixels let clicks fall through to whatever is underneath.
        public static void Present(IntPtr hwnd, Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeMethods.SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new NativeMethods.POINT();
                var dst = new NativeMethods.POINT { X = x, Y = y };
                var blend = new NativeMethods.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                NativeMethods.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
            float d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Detects a fullscreen app (video, game, F11 in a browser) so the dock and bar can step aside.
    static class Fullscreen
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);

        // True while a fullscreen app owns the primary display (where the dock, top bar and island sit).
        public static bool Active { get; private set; }

        // A normal app window is in front (not the desktop, the shell or an empty foreground): drives the dock's auto-hide.
        public static bool AppInFront { get; private set; }

        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        static readonly uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        static int lastTick = Environment.TickCount - 1000;
        static int streak; // consecutive samples that disagree with Active (debounce: a transition never flickers)

        static bool Quns()
        {
            try { int st; return SHQueryUserNotificationState(out st) == 0 && (st == 2 || st == 3 || st == 4); } // busy, D3D fullscreen, presentation
            catch { return false; }
        }

        // Fullscreen = the foreground window covers its whole (primary) monitor. A normally maximized window (caption + frame,
        // zoomed) isn't, unless Windows itself reports a fullscreen state. Throttled, so every timer may call it.
        public static void Update()
        {
            int now = Environment.TickCount;
            if (now - lastTick < 120) return;
            lastTick = now;

            bool fs = false;
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) AppInFront = false;
            else
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(fg, out pid);
                if (pid == self) return; // our own menus or search: keep the current state

                var cls = new StringBuilder(64);
                NativeMethods.GetClassName(fg, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") AppInFront = false;
                else if (!NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg)) AppInFront = false;
                else
                {
                    AppInFront = true;
                    // Task View / Alt-Tab cover the screen but aren't apps.
                    if (c != "MultitaskingViewFrame" && c != "XamlExplorerHostIslandWindow" && c != "TaskSwitcherWnd" && c != "ForegroundStaging")
                    {
                        Win32.RECT r;
                        GetWindowRect(fg, out r);
                        System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.FromHandle(fg);
                        Rectangle bounds = scr.Bounds;
                        bool covers = r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
                        if (covers && scr.Primary)
                        {
                            bool framed = (GetWindowLong(fg, -16) & 0x00C00000) != 0; // WS_CAPTION
                            fs = !(framed && IsZoomed(fg)) || Quns();
                        }
                    }
                }
            }

            if (fs == Active) { streak = 0; return; }
            if (++streak >= 2) { streak = 0; Active = fs; if (fs) HideStrays(); }
        }

        // Our topmost popups (menus, panels, previews) would otherwise float over the fullscreen app.
        static void HideStrays()
        {
            try
            {
                foreach (System.Windows.Forms.Form f in new System.Collections.ArrayList(System.Windows.Forms.Application.OpenForms))
                {
                    string n = f.GetType().Name;
                    if (f.Visible && (n == "GlassPanel" || n == "MenuPopup" || n == "WindowPreview" || n == "DownloadsFan")) f.Hide();
                }
            }
            catch { }
        }
    }

    // Follows the Windows light/dark app theme for the menu popups.
    static class Theme
    {
        public static bool Light;

        public static void Refresh()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    Light = v is int && (int)v == 1;
                }
            }
            catch { Light = false; }
        }
    }
}
