// Windows 11 snap layouts: windows tiled side by side (halves, thirds, quarters) are shown in the dock as one combined item.
// Windows exposes no public API for snap groups, so a group is recognised by geometry: visible, non-maximized windows on
// one monitor that do not overlap and together fill most of its work area.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    static class SnapGroups
    {
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Win32.RECT r, int size);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        static Rectangle Bounds(IntPtr h)
        {
            Win32.RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(Win32.RECT))) != 0) return Rectangle.Empty; // DWMWA_EXTENDED_FRAME_BOUNDS
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        static long Area(Rectangle r) { return (long)Math.Max(0, r.Width) * Math.Max(0, r.Height); }

        // 'ordered' is the visible windows, front-most first. Returns groups of 2 to 4 snapped windows.
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

        public static List<List<IntPtr>> FindAmong(ICollection<IntPtr> all)
        {
            var ordered = new List<IntPtr>();
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && ordered.Count < all.Count; h = GetWindow(h, 2))
                if (all.Contains(h)) ordered.Add(h);
            var found = Find(ordered);
            // A group whose windows were minimized stays in the dock (so a click can restore it) until a window closes.
            var next = new List<List<IntPtr>>(found);
            foreach (var k in known)
            {
                bool alive = true, anyMin = false, overlap = false;
                foreach (IntPtr h in k) { if (!IsWindow(h) || !all.Contains(h)) alive = false; else if (IsIconic(h)) anyMin = true; }
                foreach (var f in found) foreach (IntPtr h in f) if (k.Contains(h)) overlap = true;
                if (alive && anyMin && !overlap) next.Add(k);
            }
            known = next;
            return next;
        }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        static List<List<IntPtr>> known = new List<List<IntPtr>>();

        public static List<List<IntPtr>> Find(List<IntPtr> ordered)
        {
            var groups = new List<List<IntPtr>>();
            var byScreen = new Dictionary<string, List<KeyValuePair<IntPtr, Rectangle>>>();
            foreach (IntPtr h in ordered)
            {
                try
                {
                    if (IsIconic(h) || IsZoomed(h)) continue;
                    Rectangle b = Bounds(h);
                    if (b.IsEmpty) continue;
                    Screen s = Screen.FromRectangle(b);
                    Rectangle wa = s.WorkingArea;
                    if (Area(b) < Area(wa) * 0.18) continue;
                    // A snapped window touches the edge of the work area.
                    const int tol = 20;
                    bool edge = Math.Abs(b.Left - wa.Left) <= tol || Math.Abs(b.Right - wa.Right) <= tol || Math.Abs(b.Top - wa.Top) <= tol || Math.Abs(b.Bottom - wa.Bottom) <= tol;
                    if (!edge) continue;
                    List<KeyValuePair<IntPtr, Rectangle>> l;
                    if (!byScreen.TryGetValue(s.DeviceName, out l)) byScreen[s.DeviceName] = l = new List<KeyValuePair<IntPtr, Rectangle>>();
                    l.Add(new KeyValuePair<IntPtr, Rectangle>(h, b));
                }
                catch { }
            }
            foreach (var kv in byScreen)
            {
                Rectangle wa = Screen.AllScreens.Length == 0 ? Rectangle.Empty : Rectangle.Empty;
                foreach (Screen s in Screen.AllScreens) if (s.DeviceName == kv.Key) wa = s.WorkingArea;
                if (wa.IsEmpty) continue;
                var chosen = new List<KeyValuePair<IntPtr, Rectangle>>();
                foreach (var c in kv.Value)
                {
                    bool clash = false;
                    foreach (var o in chosen)
                    {
                        Rectangle i = Rectangle.Intersect(c.Value, o.Value);
                        if (Area(i) > Area(wa) * 0.03) { clash = true; break; }
                    }
                    if (!clash && chosen.Count < 4) chosen.Add(c);
                }
                if (chosen.Count < 2) continue;
                long sum = 0;
                foreach (var c in chosen) sum += Area(c.Value);
                if (sum < Area(wa) * 0.80) continue;
                var g = new List<IntPtr>();
                foreach (var c in chosen) g.Add(c.Key);
                groups.Add(g);
            }
            return groups;
        }

        // One tile showing each member's icon (two side by side, three or four in a grid).
        public static Bitmap Compose(List<Bitmap> icons)
        {
            const int S = 96;
            var bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var path = new GraphicsPath())
                {
                    int r = 22;
                    path.AddArc(0, 0, r, r, 180, 90); path.AddArc(S - r - 1, 0, r, r, 270, 90);
                    path.AddArc(S - r - 1, S - r - 1, r, r, 0, 90); path.AddArc(0, S - r - 1, r, r, 90, 90);
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, S, S), Color.FromArgb(235, 70, 74, 84), Color.FromArgb(235, 38, 40, 48), 90f))
                        g.FillPath(br, path);
                    using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.5f)) g.DrawPath(pen, path);
                }
                int n = icons.Count;
                if (n == 2)
                {
                    int sz = 42;
                    g.DrawImage(icons[0], new Rectangle(4, (S - sz) / 2, sz, sz));
                    g.DrawImage(icons[1], new Rectangle(S - sz - 4, (S - sz) / 2, sz, sz));
                    using (var p = new Pen(Color.FromArgb(110, 255, 255, 255), 2f)) g.DrawLine(p, S / 2, 20, S / 2, S - 20);
                }
                else
                {
                    int sz = 38;
                    for (int i = 0; i < n && i < 4; i++)
                        g.DrawImage(icons[i], new Rectangle(8 + (i % 2) * (sz + 4), 8 + (i / 2) * (sz + 4), sz, sz));
                }
            }
            return bmp;
        }
    }
}
