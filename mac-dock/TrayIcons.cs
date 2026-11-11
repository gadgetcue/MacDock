// Windows notification-area (system tray) icons in the menu bar.
//
// Windows 11's tray is XAML, so the old ToolbarWindow32 buttons no longer exist. Instead the tray buttons
// (SystemTray.NormalButton) are read through UI Automation: the visible ones from the taskbar, the hidden ones from
// the overflow flyout (TopLevelWindowForOverflowXamlIsland), which Scan() opens with Win+B, reads and closes again.
// Each icon's name and a snapshot of its image are kept in %APPDATA%\MacDock\tray. Clicking a bar icon opens the
// flyout for an instant and sends a real left/right click to the original button, so the app gets a genuine tray click.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace MacDock
{
    sealed class TrayItem
    {
        public string Key;      // stable id saved in config (first words of the tooltip, lower case)
        public string Title;    // readable name
        public bool Hidden;     // lives in the overflow flyout
        public Bitmap Icon;
    }

    static class TrayIcons
    {
        [DllImport("user32")] static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);
        [DllImport("user32")] static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
        [DllImport("user32")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32")] static extern bool GetCursorPos(out Pt p);
        [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }

        static readonly object gate = new object();
        static readonly object busy = new object();
        static List<TrayItem> cache;
        static string Dir { get { return Path.Combine(AppPaths.DataDir, "tray"); } }
        static string Marker { get { return Path.Combine(Dir, "v2-sharp"); } }
        static string IndexFile { get { return Path.Combine(Dir, "index.txt"); } }

        // ---------- key / persistence ----------

        static string Clean(string name)
        {
            if (name == null) return "";
            name = name.Replace("\r", "").Split('\n')[0].Trim();
            int i = name.IndexOf(':'); if (i > 0) name = name.Substring(0, i);
            i = name.IndexOf(" - "); if (i > 0) name = name.Substring(0, i);
            var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Any(char.IsDigit)).Take(2);
            return string.Join(" ", words).Trim();
        }

        static string Key(string clean) { return clean.ToLowerInvariant(); }
        static string FileFor(string key) { return Path.Combine(Dir, new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".png"); }

        public static List<TrayItem> Cached
        {
            get
            {
                lock (gate)
                {
                    if (cache == null) cache = LoadIndex();
                    return cache;
                }
            }
        }

        static List<TrayItem> LoadIndex()
        {
            var list = new List<TrayItem>();
            try
            {
                if (!File.Exists(IndexFile)) return list;
                foreach (string line in File.ReadAllLines(IndexFile))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 3 || p[0].Length == 0) continue;
                    Bitmap bmp = null;
                    try
                    {
                        string f = FileFor(p[0]);
                        if (File.Exists(f)) using (var raw = new Bitmap(f)) bmp = new Bitmap(raw);
                    }
                    catch { }
                    list.Add(new TrayItem { Key = p[0], Title = p[1], Hidden = p[2] == "1", Icon = bmp });
                }
            }
            catch { }
            return list;
        }

        // ---------- selection (config: @tray-bar on/off, @tray-icons key|key|...) ----------

        public static bool Arrow { get { return SetCfg.GetBool("tray-arrow", true); } set { SetCfg.SetBool("tray-arrow", value); } }

        static bool? collapsible, collapsed;
        static DateTime cfgAt = DateTime.MinValue;
        static void Fresh() { if ((DateTime.UtcNow - cfgAt).TotalMilliseconds > 1000) { collapsible = null; collapsed = null; cfgAt = DateTime.UtcNow; } }
        public static bool Collapsible
        {
            get { Fresh(); if (collapsible == null) collapsible = SetCfg.GetBool("tray-collapsible", false); return collapsible.Value; }
            set { collapsible = value; SetCfg.SetBool("tray-collapsible", value); }
        }
        public static bool Collapsed
        {
            get { Fresh(); if (collapsed == null) collapsed = SetCfg.GetBool("tray-collapsed", true); return collapsed.Value; }
            set { collapsed = value; SetCfg.SetBool("tray-collapsed", value); }
        }

        public static bool BarOn        {
            get { return SetCfg.GetBool("tray-bar", false); }
            set
            {
                SetCfg.SetBool("tray-bar", value);
                if (value && Cached.Count == 0) Scan(null);
            }
        }

        public static HashSet<string> Selected()
        {
            var set = new HashSet<string>();
            string v = SetCfg.Get("tray-icons");
            if (!string.IsNullOrEmpty(v)) foreach (string k in v.Split('|')) if (k.Trim().Length > 0) set.Add(k.Trim());
            return set;
        }

        public static void SetSelected(string key, bool on)
        {
            var set = Selected();
            if (on) set.Add(key); else set.Remove(key);
            SetCfg.Set("tray-icons", set.Count == 0 ? null : string.Join("|", set), false);
        }

        // ---------- UI Automation helpers ----------

        static void Press(byte k) { keybd_event(k, 0, 0, UIntPtr.Zero); keybd_event(k, 0, 2, UIntPtr.Zero); }

        static AutomationElement Top(string cls)
        {
            try { return AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, cls)); }
            catch { return null; }
        }

        static List<AutomationElement> Buttons(AutomationElement top)
        {
            var list = new List<AutomationElement>();
            if (top == null) return list;
            try
            {
                foreach (AutomationElement e in top.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton")))
                    list.Add(e);
            }
            catch { }
            return list;
        }

        // Opens the overflow flyout (Win+B, Enter, like clicking the ^) and returns it, or null.
        static AutomationElement OpenFlyout()
        {
            AutomationElement fly = Top("TopLevelWindowForOverflowXamlIsland");
            if (fly != null && Buttons(fly).Count > 0) return fly;
            keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event((byte)'B', 0, 0, UIntPtr.Zero);
            keybd_event((byte)'B', 0, 2, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero);
            Thread.Sleep(250);
            Press(0x0D);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(100);
                fly = Top("TopLevelWindowForOverflowXamlIsland");
                if (fly != null && Buttons(fly).Count > 0) return fly;
            }
            return null;
        }

        static Bitmap Snapshot(AutomationElement button, AutomationElement host)
        {
            try
            {
                var r = button.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 8) return null;
                Point c = Center(button, host);
                float sc = Dpi.Of(new Rectangle(c.X, c.Y, 1, 1)); // capture exactly the icon's physical pixels (16 DIP), centred, no padding
                int size = Math.Max(16, (int)Math.Round(16 * sc)), x = c.X - size / 2, y = c.Y - size / 2;
                var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(size, size));
                return bmp;
            }
            catch { return null; }
        }

        // Scans the taskbar tray and the overflow flyout, then stores names and image snapshots.
        // Opens the flyout briefly, so run it on request (when the option is turned on or Rescan is pressed).
        public static void Scan(Action done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (busy)
                {
                    try
                    {
                        Directory.CreateDirectory(Dir);
                        var found = new List<KeyValuePair<AutomationElement, bool>>();
                        AutomationElement bar = Top("Shell_TrayWnd"); foreach (AutomationElement b in Buttons(bar)) found.Add(new KeyValuePair<AutomationElement, bool>(b, false));
                        AutomationElement fly = OpenFlyout();
                        bool opened = fly != null;
                        if (opened) foreach (AutomationElement b in Buttons(fly)) found.Add(new KeyValuePair<AutomationElement, bool>(b, true));

                        var lines = new List<string>();
                        var seen = new HashSet<string>();
                        foreach (var kv in found)
                        {
                            string title = Clean(kv.Key.Current.Name);
                            if (title.Length == 0) continue;
                            string key = Key(title);
                            if (!seen.Add(key)) continue;
                            Bitmap bmp = Snapshot(kv.Key, kv.Value ? fly : bar);
                            if (bmp != null) { try { bmp.Save(FileFor(key), System.Drawing.Imaging.ImageFormat.Png); } catch { } bmp.Dispose(); }
                            lines.Add(key + "|" + title + "|" + (kv.Value ? "1" : "0"));
                        }
                        if (opened) Press(0x1B);
                        if (lines.Count > 0) { File.WriteAllLines(IndexFile, lines.ToArray()); File.WriteAllText(Marker, "2"); }
                        lock (gate) cache = null;
                    }
                    catch { }
                }
                if (done != null) done();
            });
        }

        // ---------- clicks ----------

        // Sends a real click to the original tray button (opens the flyout for an instant when the icon is hidden).
        // mode 0 left, 1 right, 2 double. The button is re-located by name on every attempt (positions change when the
        // flyout re-lays out), the point is checked with FromPoint, and the search is retried if another icon is there.
        public static void Click(TrayItem t, bool right) { Click(t, right ? 1 : 0); }
        public static void Click(TrayItem t, int mode)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (busy)
                {
                    bool flyout = false;
                    try
                    {
                        for (int attempt = 0; attempt < 4; attempt++)
                        {
                            AutomationElement host = Top("Shell_TrayWnd"), button = Find(host, t.Key);
                            if (button == null)
                            {
                                AutomationElement fly = OpenFlyout();
                                if (fly != null) flyout = true;
                                host = fly; button = Find(fly, t.Key);
                            }
                            if (button == null) { Thread.Sleep(200); continue; }
                            Point c = Center(button, host);
                            // wait until the layout is stable (the flyout animates / re-lays out while it opens)
                            for (int s = 0; s < 8; s++)
                            {
                                Thread.Sleep(120);
                                AutomationElement b2 = Find(host, t.Key);
                                if (b2 == null) { button = null; break; }
                                Point c2 = Center(b2, host);
                                bool same = Math.Abs(c2.X - c.X) < 2 && Math.Abs(c2.Y - c.Y) < 2;
                                button = b2; c = c2;
                                if (same) break;
                            }
                            if (button == null || (c.X <= 0 && c.Y <= 0)) { Thread.Sleep(200); continue; }
                            if (!UnderPointIs(c, t.Key)) { Thread.Sleep(250); continue; }
                            Pt old; GetCursorPos(out old);
                            SetCursorPos(c.X, c.Y);
                            Thread.Sleep(80);
                            uint dn = mode == 1 ? 0x08u : 0x02u, up = mode == 1 ? 0x10u : 0x04u;
                            for (int n = 0; n < (mode == 2 ? 2 : 1); n++)
                            {
                                mouse_event(dn, 0, 0, 0, UIntPtr.Zero);
                                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                                if (n == 0 && mode == 2) Thread.Sleep(60);
                            }
                            Thread.Sleep(120);
                            SetCursorPos(old.X, old.Y);
                            return;
                        }
                        if (flyout) Press(0x1B);
                    }
                    catch { }
                }
            });
        }

        // Centre of the button. Some buttons report an oversized rectangle (Monitorian: 222x172) overlapping the
        // neighbouring cells; then only the top-left cell, sized like the sibling buttons, is the real icon.
        static Point Center(AutomationElement b, AutomationElement host)
        {
            var r = b.Current.BoundingRectangle;
            if (r.IsEmpty || double.IsInfinity(r.X)) return new Point(0, 0);
            double w = r.Width, h = r.Height, cell = 0;
            foreach (AutomationElement o in Buttons(host))
            {
                try { var q = o.Current.BoundingRectangle; if (!q.IsEmpty && q.Width >= 8 && q.Width <= 80 && (cell == 0 || q.Width < cell)) cell = q.Width; }
                catch { }
            }
            if (cell == 0) cell = 40;
            if (w > cell * 1.5 || h > cell * 1.5) { w = cell; h = cell; }
            return new Point((int)(r.X + w / 2), (int)(r.Y + h / 2));
        }

        // False only when the element under the point is a different tray button.
        static bool UnderPointIs(Point p, string key)
        {
            try
            {
                AutomationElement e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
                for (int i = 0; e != null && i < 4; i++)
                {
                    if (e.Current.ClassName == "SystemTray.NormalButton")
                    {
                        if (e.Current.BoundingRectangle.Width > 80) return true; // oversized rect (Monitorian) overlaps neighbours
                        string k = Key(Clean(e.Current.Name));
                        return k == key || (k.Length > 0 && k.Split(' ')[0] == key.Split(' ')[0]);
                    }
                    e = TreeWalker.ControlViewWalker.GetParent(e);
                }
            }
            catch { }
            return true;
        }

        static AutomationElement Find(AutomationElement top, string key)
        {
            if (top == null) return null;
            string first = key.Split(' ')[0];
            AutomationElement loose = null;
            foreach (AutomationElement b in Buttons(top))
            {
                try
                {
                    string k = Key(Clean(b.Current.Name));
                    if (k == key) return b;
                    if (loose == null && k.Length > 0 && k.Split(' ')[0] == first) loose = b;
                }
                catch { }
            }
            return loose;
        }
    }
}
