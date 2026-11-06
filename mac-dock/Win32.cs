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
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

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
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    }

    static class WinUtil
    {
        public const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;
        public const uint WM_CLOSE = 0x10;

        // Brings a window to the foreground, restoring it first if minimized.
        public static void Focus(IntPtr h)
        {
            if (!NativeMethods.IsWindow(h)) return;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);
            // A synthetic Alt tap lets a background process take the foreground.
            NativeMethods.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(VK_MENU, 0, 2, UIntPtr.Zero);
            NativeMethods.SetForegroundWindow(h);
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

        public static bool Active { get; private set; }

        // A normal app window is in front (not the desktop, the shell or an empty foreground): drives the dock's auto-hide.
        public static bool AppInFront { get; private set; }

        static readonly uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        // Fullscreen = the foreground window covers its whole monitor. A normally maximized window doesn't:
        // the top bar reserves screen space, so a maximized window starts below it.
        public static void Update()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) { Active = false; AppInFront = false; return; }

            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            if (pid == self) return; // our own menus or search: keep the current state

            var cls = new StringBuilder(64);
            NativeMethods.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd")
            {
                Active = false;
                AppInFront = false;
                return;
            }

            if (!NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg)) { Active = false; AppInFront = false; return; }
            AppInFront = true;

            Win32.RECT r;
            GetWindowRect(fg, out r);
            Rectangle bounds = System.Windows.Forms.Screen.FromHandle(fg).Bounds;
            Active = r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
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
