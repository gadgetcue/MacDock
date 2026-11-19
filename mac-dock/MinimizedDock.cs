// Minimized windows as their own dock items (like macOS "minimize into a separate tile"): a snapshot taken as the window
// minimizes, with the app's icon in the corner. Click restores the window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacDock
{
    static class MinimizedDock
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        static readonly Dictionary<IntPtr, Bitmap> shots = new Dictionary<IntPtr, Bitmap>();
        static readonly Dictionary<IntPtr, Bitmap> composed = new Dictionary<IntPtr, Bitmap>();

        public static bool On { get { return SetCfg.GetBool("minimize-to-dock", true); } }

        // Called from the minimize hook, before the window is gone. The capture runs on a worker so a hung app can't stall the dock.
        public static void Capture(IntPtr hwnd) { Capture(hwnd, false); }
        static void Capture(IntPtr hwnd, bool quiet)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RECT r; if (!GetWindowRect(hwnd, out r)) return;
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w < 40 || h < 40 || w > 10000 || h > 10000) { if (!quiet) Program.Log("MINDOCK capture: window already minimized, keeping the last snapshot"); return; }
                    var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        IntPtr dc = g.GetHdc();
                        bool ok; try { ok = PrintWindow(hwnd, dc, 2 /*PW_RENDERFULLCONTENT*/); } finally { g.ReleaseHdc(dc); }
                        if (!ok) { full.Dispose(); if (!quiet) Program.Log("MINDOCK capture: PrintWindow failed"); return; }
                    }
                    if (Blank(full)) { full.Dispose(); if (!quiet) Program.Log("MINDOCK capture: blank frame"); return; } // already minimized: Windows gave an empty frame
                    float s = Math.Min(160f / w, 160f / h);
                    var small = new Bitmap(Math.Max(1, (int)(w * s)), Math.Max(1, (int)(h * s)), PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, small.Width, small.Height); }
                    full.Dispose(); if (!quiet) Program.Log("MINDOCK capture ok " + w + "x" + h);
                    lock (shots)
                    {
                        Bitmap old; if (shots.TryGetValue(hwnd, out old)) old.Dispose(); else if (quiet) Program.Log("MINDOCK snapshot of front window " + hwnd + " " + w + "x" + h);
                        shots[hwnd] = small;
                        composed.Remove(hwnd); // rebuilt on next use; the old tile may still be on screen, so it is not disposed here
                    }
                    if (!quiet) { var d = DockForm.Instance; if (d != null && d.IsHandleCreated) d.BeginInvoke((Action)d.RebuildSoon); }
                }
                catch { }
            });
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        static IntPtr lastFg; static long lastAt; static readonly uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        // By the time Windows reports a minimize the window is often already gone, so the window in front is snapshotted
        // now and then (on a worker); a minimize then has a recent picture to show.
        public static void Track()
        {
            try
            {
                if (!On) return;
                IntPtr fg = GetForegroundWindow(); uint pid;
                if (fg == IntPtr.Zero || IsIconic(fg) || !IsWindowVisible(fg)) return;
                GetWindowThreadProcessId(fg, out pid); if (pid == self) return;
                long now = Environment.TickCount;
                if (fg == lastFg && now - lastAt < 6000) return;
                lastFg = fg; lastAt = now;
                Capture(fg, true);
            }
            catch { }
        }

        static bool Blank(Bitmap b)
        {
            int dark = 0, n = 0;
            for (int y = b.Height / 8; y < b.Height; y += Math.Max(1, b.Height / 8))
                for (int x = b.Width / 8; x < b.Width; x += Math.Max(1, b.Width / 8))
                { Color c = b.GetPixel(x, y); n++; if (c.A == 0 || (c.R + c.G + c.B) < 6) dark++; }
            return n > 0 && dark == n;
        }

        // 96 px dock tile: the snapshot fitted and rounded, the app icon at the bottom right. Falls back to the app icon alone.
        public static Bitmap Tile(IntPtr hwnd, Bitmap appIcon)
        {
            lock (shots)
            {
                Bitmap c; if (composed.TryGetValue(hwnd, out c)) return c;
                Bitmap shot; shots.TryGetValue(hwnd, out shot);
                if (shot == null) return appIcon;
                const int S = 96;
                c = new Bitmap(S, S, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float sc = Math.Min((S - 8f) / shot.Width, (S - 8f) / shot.Height);
                    var r = new RectangleF((S - shot.Width * sc) / 2, (S - shot.Height * sc) / 2, shot.Width * sc, shot.Height * sc);
                    using (var path = new GraphicsPath())
                    {
                        float d = 12;
                        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
                        g.SetClip(path); g.DrawImage(shot, r); g.ResetClip();
                    }
                    if (appIcon != null) g.DrawImage(appIcon, S - 40, S - 40, 38, 38);
                }
                composed[hwnd] = c;
                return c;
            }
        }

        public static void Prune()
        {
            lock (shots)
                foreach (IntPtr h in new List<IntPtr>(shots.Keys))
                    if (!IsWindow(h)) { shots[h].Dispose(); shots.Remove(h); composed.Remove(h); }
        }
    }
}
