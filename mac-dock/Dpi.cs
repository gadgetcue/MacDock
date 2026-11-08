using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    // Per-monitor DPI helpers. The process is PerMonitorV2 (app.manifest); custom-painted windows scale their pixel
    // constants with Dpi.Of(screen) and re-layout on WM_DPICHANGED.
    static class Dpi
    {
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        // Scale factor (1.0 = 96 dpi) of the monitor that holds the centre of r.
        public static float Of(Rectangle r)
        {
            try
            {
                IntPtr m = MonitorFromPoint(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2), 2);
                uint x, y;
                if (m != IntPtr.Zero && GetDpiForMonitor(m, 0, out x, out y) == 0 && x >= 48) return x / 96f;
            }
            catch { }
            return 1f;
        }
        public static float Of(Screen s) { return s == null ? 1f : Of(s.Bounds); }
        public static float Of(IntPtr hwnd)
        {
            try
            {
                IntPtr m = MonitorFromWindow(hwnd, 2);
                uint x, y;
                if (m != IntPtr.Zero && GetDpiForMonitor(m, 0, out x, out y) == 0 && x >= 48) return x / 96f;
            }
            catch { }
            return 1f;
        }
        public static int S(int v, float scale) { return (int)Math.Round(v * scale); }
    }
}
