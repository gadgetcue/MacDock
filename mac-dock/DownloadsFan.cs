// macOS-style "fan" stack for the Downloads folder: the newest files fan out in an arc above the dock icon.
// A layered, per-pixel-alpha window (no glass panel, like the Dock's stacks); click an item to open it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    sealed class DownloadsFan : Form
    {
        const int MaxItems = 10, IconSize = 60, Step = 66, LabelH = 22, WinW = 900, Margin = 16;

        sealed class FanItem
        {
            public string Path, Name; public bool IsDir;
            public Bitmap Icon; public Bitmap Thumb; public double Hot;
            public Rectangle Rect; // final icon rect (window coordinates)
            public double Angle; public string Label;   // label tilt in degrees (follows the arc, like the Dock)
        }

        readonly List<FanItem> items = new List<FanItem>();
        readonly Func<Point, bool> keepOpen;
        readonly Action closed;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Font font = Typography.Text(9.5f);
        DateTime born = DateTime.UtcNow;
        Surface surface;
        Point origin;      // dock icon centre in window coordinates
        int hover = -1;
        bool wasDown = true, dirty = true;
        volatile bool thumbsReady;
        DateTime leftSince = DateTime.MaxValue;

        public static bool IsDownloads(string target)
        {
            try
            {
                string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)).TrimEnd('\\'), dl.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Items (with their shell icons) are cached per folder and re-read only when the folder changes, so reopening is instant.
        sealed class Loaded { public DateTime Stamp; public List<FanItem> List; public volatile bool ThumbsDone; }
        static readonly Dictionary<string, Loaded> cache = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
        volatile List<FanItem> arrived;
        volatile bool loading;
        const int Cols = 5, CellW = 104, GPad = 14;
        int CellH { get { return IconSize + 34; } }
        int StepFor { get { return style == "list" ? 64 : Step; } }

        string folder; Loaded cur; int thumbTick;
        static readonly HashSet<string> inflight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static Loaded Cached(string folder)
        {
            DateTime stamp = DateTime.MinValue;
            try { stamp = Directory.GetLastWriteTimeUtc(folder); } catch { }
            Loaded hit;
            lock (cache) cache.TryGetValue(folder, out hit);
            return hit != null && hit.Stamp == stamp ? hit : null;
        }

        // Reads the folder's newest items and their icons on a background thread. Called early (when the cursor nears a
        // folder icon, and at startup) so the fan usually has everything ready the moment it opens.
        public static void Prefetch(string folder)
        {
            if (Cached(folder) != null) return;
            lock (inflight) { if (!inflight.Add(folder)) return; }
            new Thread(() =>
            {
                try
                {
                    DateTime stamp = DateTime.MinValue;
                    try { stamp = Directory.GetLastWriteTimeUtc(folder); } catch { }
                    var list = new List<FanItem>();
                    try
                    {
                        foreach (FileSystemInfo f in new DirectoryInfo(folder).EnumerateFileSystemInfos()
                            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                            .Where(f => { string e = f.Extension.ToLowerInvariant(); return e != ".crdownload" && e != ".tmp" && e != ".part" && e != ".partial"; })
                            .OrderByDescending(f => f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc)
                            .Take(MaxItems))
                            list.Add(new FanItem { Path = f.FullName, Name = f.Name, IsDir = f is DirectoryInfo });
                    }
                    catch { }
                    foreach (FanItem it in list) it.Icon = DockForm.LoadShellIcon(it.Path, 0, 0);
                    var ld = new Loaded { Stamp = stamp, List = list };
                    lock (cache) cache[folder] = ld;
                    foreach (FanItem it in list.Where(x => !x.IsDir && IsImage(x.Path)).ToList())
                    { try { it.Thumb = MakeThumb(it.Path); } catch { } }
                    ld.ThumbsDone = true;
                }
                finally { lock (inflight) inflight.Remove(folder); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        public DownloadsFan(string folder, Point anchorTopCenter, Func<Point, bool> keepOpen, Action closed)
        {
            t0 = clk.ElapsedTicks; this.keepOpen = keepOpen; this.closed = closed;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            style = GetStyle();
            labelsLeft = true;

            // The window is sized for a full stack up front, so it can appear before any file has been read.
            int height = style == "grid" ? 2 * CellH + GPad * 2 + 12 : MaxItems * StepFor + IconSize + 40;
            Rectangle area = Screen.FromPoint(anchorTopCenter).WorkingArea;
            int left = Math.Max(area.Left, Math.Min(anchorTopCenter.X - 580, area.Right - WinW));
            int top = Math.Max(area.Top, anchorTopCenter.Y - height);
            Bounds = new Rectangle(left, top, WinW, anchorTopCenter.Y - top);
            height = Bounds.Height;
            origin = new Point(anchorTopCenter.X - left, height);

            this.folder = folder;
            Loaded hit = Cached(folder);
            if (hit != null) { cur = hit; items.AddRange(hit.List); Layout(); }
            else { loading = true; Prefetch(folder); }

            Log("ctor done " + ((clk.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + "ms cached=" + (hit != null));
        }

        // Places the icons: the Dock's curved fan, a straight list, or a grid panel.
        void Layout()
        {
            int n = Math.Max(1, items.Count), height = Height;
            bool grid = style == "grid";
            int step = StepFor, rowsN = (n + Cols - 1) / Cols;
            int pw = Math.Min(Cols, n) * CellW + GPad * 2;
            panel = new Rectangle(Math.Max(6, Math.Min(WinW - pw - 6, origin.X - pw / 2)), height - rowsN * CellH - GPad * 2 - 6, pw, rowsN * CellH + GPad * 2);
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Hot = 0;
                if (grid)
                {
                    int r = i / Cols, c = i % Cols;
                    int cx = panel.Left + GPad + c * CellW + CellW / 2;
                    int ty = panel.Bottom - GPad - (r + 1) * CellH;
                    items[i].Rect = new Rectangle(cx - IconSize / 2, ty + 2, IconSize, IconSize);
                }
                else
                {
                    // Newest (index 0) sits lowest, closest to the dock; the stack bends to the right.
                    double curve = style == "list" ? 0 : 2.5;
                    int cx = origin.X + (int)(Math.Pow(i, 1.6) * curve);
                    cx = Math.Max(IconSize / 2 + 4, Math.Min(WinW - IconSize / 2 - 4, cx));
                    int cy = height - IconSize / 2 - 6 - i * step;
                    items[i].Rect = new Rectangle(cx - IconSize / 2, cy - IconSize / 2, IconSize, IconSize);
                    // Labels lean along the arc: flat near the dock, tilting up toward the top (about 17 degrees at the 10th item).
                    double dx = style == "list" ? 0 : 1.6 * curve * Math.Pow(Math.Max(i, 0.01), 0.6);
                    items[i].Angle = style == "list" ? 0 : Math.Min(18.0, 1.0 + 1.9 * i);
                    items[i].Label = FitName(items[i].Name, (items[i].Rect.Left - 14 - 10) / Math.Cos(items[i].Angle * Math.PI / 180) - 16 - 6);
                }
            }
            moreRect = Rectangle.Empty;
            if (!grid && items.Count > 0)
            {
                // "Open in Explorer" button on the next slot above the stack (the Dock's "More in Finder" arrow circle).
                int i = items.Count;
                double curve = style == "list" ? 0 : 2.5;
                int cx = Math.Max(IconSize / 2 + 4, Math.Min(WinW - IconSize / 2 - 4, origin.X + (int)(Math.Pow(i, 1.6) * curve)));
                int cy = height - IconSize / 2 - 6 - i * step;
                if (cy >= 26) moreRect = new Rectangle(cx - 22, cy - 22, 44, 44);
            }
        }
        Rectangle moreRect; bool moreHot, pressMore;

        bool labelsLeft;
        string style;
        Rectangle panel;

        static string StyleFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanstyle.txt"); } }
        public static string GetStyle()
        {
            try { string s = File.ReadAllText(StyleFile).Trim().ToLowerInvariant(); if (s == "grid" || s == "list") return s; } catch { }
            return "fan";
        }
        public static void SetStyle(string s) { try { File.WriteAllText(StyleFile, s); } catch { } }

        static bool IsImage(string p)
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif";
        }

        static Bitmap MakeThumb(string path)
        {
            if (new FileInfo(path).Length > 40 * 1024 * 1024) return null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image img = Image.FromStream(fs, false, false))
            {
                double s = Math.Min((double)IconSize / img.Width, (double)IconSize / img.Height);
                int w = Math.Max(1, (int)(img.Width * s)), h = Math.Max(1, (int)(img.Height * s));
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, w, h);
                }
                return bmp;
            }
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public new void Show()
        {
            Log("Show after ctor " + ((clk.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + "ms");
            born = DateTime.UtcNow; // restart the clock: building a big folder (Downloads) can take longer than the animation itself
            base.Show();
            Draw();
            if (vsync == null) { vsync = new VsyncDriver(this, Poll); vsync.Wake(); } // one step per display refresh, steady frame pacing
        }

        // 0..1 progress of item i's fan-out (staggered), with a little overshoot.
        // 0..1 how far open the fan is (drives the dock tile's fade and scale, so both move together).
        public double Presence
        {
            get
            {
                if (closeStart != DateTime.MinValue) return Math.Max(0, Math.Min(1, Progress(0)));
                double t = Math.Max(0, Math.Min(1, (DateTime.UtcNow - born).TotalMilliseconds / 240.0));
                return t * t * (3 - 2 * t);
            }
        }

        double Progress(int i)
        {
            if (closeStart != DateTime.MinValue)
            {
                double cu = Math.Max(0, Math.Min(1, (DateTime.UtcNow - closeStart).TotalMilliseconds / 110.0));
                return 1 - cu * cu * (3 - 2 * cu); // folds back into the dock icon
            }
            double t = ((DateTime.UtcNow - born).TotalMilliseconds - i * 6) / 240.0;
            t = Math.Max(0, Math.Min(1, t));
            double cu3 = 1 - t;
            return 1 - cu3 * cu3 * cu3; // easeOutCubic: fast launch from the folder, soft landing, no bounce (as in the recording)
        }

        bool Animating { get { if (closeStart != DateTime.MinValue) return true; return (DateTime.UtcNow - born).TotalMilliseconds < items.Count * 6 + 260; } }

        RectangleF CurrentRect(int i)
        {
            double p = Progress(i);
            Rectangle r = items[i].Rect;
            double ox = origin.X - IconSize / 2.0, oy = origin.Y - IconSize / 2.0;
            // Like the Dock: icons grow out of the dock icon while sliding up the arc.
            double sc = 0.35 + 0.65 * Math.Max(0, Math.Min(1, p));
            float sz = (float)(IconSize * sc);
            double cx = ox + (r.Left - ox) * p + IconSize / 2.0, cy = oy + (r.Top - oy) * p + IconSize / 2.0;
            return new RectangleF((float)(cx - sz / 2.0), (float)(cy - sz / 2.0), sz, sz);
        }

        VsyncDriver vsync;
        Font arrowFont;

        void Poll()
        {
            if (IsDisposed) return;
            if (closeStart != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - closeStart).TotalMilliseconds >= 120) { closeDone = true; Close(); return; }
                Draw(); return;
            }
            if (menuOpen || dragging) return;
            NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
            Point sp = new Point(p.X, p.Y), c = new Point(p.X - Left, p.Y - Top);

            int idx = -1;
            if (!Animating)
                for (int i = 0; i < items.Count; i++)
                    if (HitRect(i).Contains(c)) { idx = i; break; }
            if (idx != hover) { hover = idx; dirty = true; }
            bool mh = !Animating && !moreRect.IsEmpty && moreRect.Contains(c);
            if (mh != moreHot) { moreHot = mh; dirty = true; }
            for (int k = 0; k < items.Count; k++) { double tgt = k == hover ? 1 : 0, d = tgt - items[k].Hot; if (Math.Abs(d) > 0.01) { items[k].Hot += d * 0.3; dirty = true; } else items[k].Hot = tgt; }

            bool near = keepOpen(sp) || idx >= 0 || moreHot || Animating;
            if (!near && items.Any((it) => Rectangle.Inflate(HitRect(items.IndexOf(it)), 6, 6).Contains(c))) near = true;
            if (!near && !Pinned)
            {
                if (leftSince == DateTime.MaxValue) leftSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - leftSince).TotalMilliseconds > 250) { Close(); return; }
            }
            else leftSince = DateTime.MaxValue;

            bool rdown = (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (rdown && !wasRDown && idx >= 0) { wasRDown = true; ShowItemMenu(items[idx], sp); return; }
            wasRDown = rdown;
            bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !wasDown)
            {
                if (idx >= 0) { pressIdx = idx; pressPt = sp; }
                else if (moreHot) { pressMore = true; pressIdx = -1; }
                else { pressIdx = -1; if (!Pinned && !keepOpen(sp)) { Close(); return; } }
            }
            else if (down && pressIdx >= 0 && (Math.Abs(sp.X - pressPt.X) > 6 || Math.Abs(sp.Y - pressPt.Y) > 6))
            {
                // Dragging an item out: drop it on the Recycle Bin icon, Explorer, the desktop...
                string path = items[pressIdx].Path;
                pressIdx = -1; dragging = true;
                try { DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); } catch { }
                dragging = false;
                Close();
                return;
            }
            else if (!down && wasDown && pressMore)
            {
                pressMore = false;
                if (moreHot) { Close(); Open(folder); return; }
            }
            else if (!down && wasDown && pressIdx >= 0)
            {
                int pi = pressIdx; pressIdx = -1;
                if (pi == idx) { string path = items[pi].Path; Close(); Open(path); return; }
            }
            wasDown = down;
            if (!Pinned && (Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0) { Close(); return; }

            if (loading) { Loaded ld = Cached(folder); if (ld != null) { cur = ld; arrived = ld.List; } }
            var got = arrived;
            if (got != null) { Log("items arrived +" + ((clk.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + "ms"); arrived = null; if (cur == null) cur = Cached(folder); items.AddRange(got); Layout(); loading = false; born = DateTime.UtcNow; hover = -1; dirty = true; }
            if (cur != null && !cur.ThumbsDone && (++thumbTick % 6) == 0) dirty = true; // thumbnails are still arriving
            if (dirty || Animating) Draw();
        }

        // Icon plus its label pill.
        Rectangle HitRect(int i)
        {
            Rectangle r = items[i].Rect;
            if (style == "grid") return new Rectangle(r.Left - 22, r.Top - 4, r.Width + 44, r.Height + 28);
            int lw = LabelWidth(items[i]);
            return labelsLeft ? Rectangle.Union(r, new Rectangle(r.Left - lw - 12, r.Top + (r.Height - LabelH) / 2, lw + 12, LabelH))
                              : Rectangle.Union(r, new Rectangle(r.Right, r.Top + (r.Height - LabelH) / 2, lw + 12, LabelH));
        }

        // In the reference each label sits a little above its item's centre, more so up the arc (about 2 px at the bottom to 18 px at the top for a 58 px item).
        int LabelLift(int i) { return style != "fan" ? 0 : (int)Math.Round(IconSize * (i <= 5 ? 0.0073 * i * i : 0.18 + 0.045 * (i - 5))); }

        string FitName(string name, double maxW)
        {
            if (TextRenderer.MeasureText(name, font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width <= maxW) return name;
            int n = name.Length;
            while (n > 1 && TextRenderer.MeasureText(name.Substring(0, n) + "…", font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width > maxW) n--;
            return name.Substring(0, n).TrimEnd() + "…";
        }

        string Display(FanItem it)
        {
            return it.Label ?? it.Name;
        }

        int LabelWidth(FanItem it)
        {
            return TextRenderer.MeasureText(Display(it), font).Width + 16;
        }

        DateTime closeStart = DateTime.MinValue; bool closeDone;
        public bool Pinned; // opened by a click: stays until the arrow tile (or the folder) is clicked
        bool wasRDown = true, menuOpen, dragging;
        int pressIdx = -1;
        Point pressPt;

        void ShowItemMenu(FanItem it, Point at)
        {
            string path = it.Path;
            bool isDir = it.IsDir;
            menuOpen = true;
            var list = new List<MenuEntry>
            {
                MenuEntry.Item(it.Name.Length > 28 ? it.Name.Substring(0, 25) + "..." : it.Name, null, null), // header
                MenuEntry.Sep(),
                MenuEntry.Item("Open", null, () => Open(path)),
                MenuEntry.Item("Show in Folder", null, () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } }),
                MenuEntry.Item("Copy Path", null, () => { try { Clipboard.SetText(path); } catch { } }),
                MenuEntry.Sep(),
                MenuEntry.Item("Move to Recycle Bin", null, () =>
                {
                    try
                    {
                        if (isDir) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    catch { }
                })
            };
            // Same frosted popup as the dock's own menu; it is shown after the fan, so it stacks above it.
            MenuPopup menu = null;
            menu = new MenuPopup(list, at, 0, () => { menuOpen = false; if (!IsDisposed && !Pinned) BeginInvoke((Action)(() => Close())); });
            menu.launchpadGlass = true;
            Rectangle area = Screen.FromPoint(at).Bounds;
            menu.Top = Math.Max(area.Top + 4, at.Y - menu.Height - 8);
            menu.Show();
        }

        static void Open(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        static readonly System.Diagnostics.Stopwatch clk = System.Diagnostics.Stopwatch.StartNew();
        long t0, drawN; double drawSum, drawMax;
        static readonly bool Dbg = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan.debug"));
        static void Log(string s) { if (!Dbg) return; try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { } }
        void Draw()
        {
            if (!IsHandleCreated) return;
            dirty = false; long dt0 = clk.ElapsedTicks;
            if (surface == null) surface = new Surface(Width, Height);
            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center, Trimming = StringTrimming.None, FormatFlags = StringFormatFlags.NoWrap };
                if (style == "grid" && items.Count > 0)
                {
                    double pp = Math.Max(0, Math.Min(1, Progress(0)));
                    int pa = (int)(235 * Math.Min(1, pp * 2));
                    // The panel grows out of the dock icon.
                    float k = (float)(0.2 + 0.8 * Math.Min(1, pp));
                    var pr2 = new RectangleF(origin.X + (panel.Left - origin.X) * k, panel.Bottom - panel.Height * k, panel.Width * k, panel.Height * k);
                    using (GraphicsPath gp = Layered.RoundedRect(pr2, 18))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(pa * 250 / 255, 28, 28, 34))) g.FillPath(b, gp);
                        using (var pen = new Pen(Color.FromArgb(pa / 3, 255, 255, 255))) g.DrawPath(pen, gp);
                    }
                }
                for (int n = items.Count - 1; n >= 0; n--) // far ones first so near ones overlap them; hovered one is drawn last (on top)
                {
                    int i = n;
                    if (hover >= 0 && hover < items.Count)
                    {
                        if (n == 0) i = hover;
                        else if (n <= hover) i = n - 1;
                    }
                    FanItem it = items[i];
                    double pr = Math.Max(0, Math.Min(1, Progress(i)));
                    RectangleF rf = CurrentRect(i);
                    bool hot = i == hover;
                    { float gI = (float)(IconSize * 0.34 * it.Hot); rf.Inflate(gI, gI); }
                    Rectangle r = Rectangle.Round(rf);
                    int alpha = (int)(255 * Math.Min(1, pr * 1.6));

                    Bitmap img = it.Thumb ?? it.Icon;
                    if (img != null)
                    {
                        RectangleF dst = rf;
                        if (it.Thumb != null) dst = new RectangleF(rf.Left + (rf.Width - it.Thumb.Width) / 2f, rf.Top + (rf.Height - it.Thumb.Height) / 2f, it.Thumb.Width, it.Thumb.Height);
                        var rotSaved = g.Save();
                        double rot = style == "fan" ? it.Angle * 0.6 : 0; // thumbnails lean a few degrees like the Dock's, following the arc
                        if (rot != 0) { g.TranslateTransform(dst.Left + dst.Width / 2f, dst.Top + dst.Height / 2f); g.RotateTransform((float)rot); g.TranslateTransform(-(dst.Left + dst.Width / 2f), -(dst.Top + dst.Height / 2f)); }
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f };
                            ia.SetColorMatrix(cm);
                            if (it.Thumb != null)
                            {
                                float rad = Math.Max(3f, 0.1f * Math.Max(dst.Width, dst.Height));
                                using (GraphicsPath tp = Layered.RoundedRect(new RectangleF(dst.Left, dst.Top, dst.Width, dst.Height), rad))
                                using (GraphicsPath sp2 = Layered.RoundedRect(new RectangleF(dst.Left + 1, dst.Top + 3, dst.Width, dst.Height), rad))
                                using (var sh = new SolidBrush(Color.FromArgb(alpha / 4, 0, 0, 0)))
                                using (var pen = new Pen(Color.FromArgb(alpha / 3, 255, 255, 255)))
                                using (var tbr = new TextureBrush(img, new Rectangle(0, 0, img.Width, img.Height), ia))
                                {
                                    tbr.WrapMode = WrapMode.Clamp;
                                    tbr.TranslateTransform(dst.Left, dst.Top); tbr.ScaleTransform(dst.Width / (float)img.Width, dst.Height / (float)img.Height);
                                    g.FillPath(sh, sp2); g.FillPath(tbr, tp); g.DrawPath(pen, tp);
                                }
                            }
                            else g.DrawImage(img, new[] { new PointF(dst.Left, dst.Top), new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) }, new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
                        }
                        g.Restore(rotSaved);
                    }

                    if (style == "grid")
                    {
                        if (pr > 0.5)
                        {
                            int la = (int)(255 * Math.Min(1, (pr - 0.5) / 0.5));
                            var gf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                            var lr = new Rectangle(r.Left + r.Width / 2 - 48, r.Bottom + 3, 96, 20);
                            if (hot)
                                using (GraphicsPath gp = Layered.RoundedRect(lr, 6))
                                using (var hb = new SolidBrush(Color.FromArgb(la * 150 / 255, 10, 110, 230))) g.FillPath(hb, gp);
                            using (var tb = new SolidBrush(Color.FromArgb(la, 250, 250, 252)))
                                g.DrawString(it.Name, font, tb, lr, gf);
                        }
                    }
                    else if (pr > 0.55)
                    {
                        int lw = LabelWidth(it);
                        int la = (int)(255 * Math.Min(1, (pr - 0.55) / 0.45));
                        Rectangle lr = labelsLeft ? new Rectangle(r.Left - lw - 14, r.Top + (r.Height - LabelH) / 2 - LabelLift(i), lw, LabelH)
                                                  : new Rectangle(r.Right + 10, r.Top + (r.Height - LabelH) / 2 - LabelLift(i), lw, LabelH);
                        // Rotate about the label's end next to the icon, so each label leans along the arc.
                        var saved = g.Save();
                        if (it.Angle != 0)
                        {
                            float px = labelsLeft ? lr.Right : lr.Left, py = lr.Top + lr.Height / 2f;
                            g.TranslateTransform(px, py); g.RotateTransform((float)(labelsLeft ? it.Angle : -it.Angle)); g.TranslateTransform(-px, -py);
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        }
                        using (GraphicsPath gp = Layered.RoundedRect(lr, LabelH / 2f))
                        using (var b = new SolidBrush(Color.FromArgb(hot ? la * 215 / 255 : la * 190 / 255, hot ? 52 : 30, hot ? 52 : 30, hot ? 58 : 34)))
                        using (var rim = new Pen(Color.FromArgb(la * 55 / 255, 255, 255, 255), 1f))
                            { g.FillPath(b, gp); g.DrawPath(rim, gp); }
                        using (var tb = new SolidBrush(Color.FromArgb(la, 250, 250, 252)))
                            g.DrawString(Display(it), font, tb, lr, fmt);
                        g.Restore(saved);
                    }
                }
                if (!moreRect.IsEmpty && items.Count > 0)
                {
                    // The arrow button grows out of the folder icon with the file icons, and shrinks back on close.
                    double mp = Math.Max(0, Math.Min(1, Progress(items.Count)));
                    if (mp > 0.01)
                    {
                        float ms = (float)(0.35 + 0.65 * mp);
                        float mcx = (float)(origin.X + (moreRect.Left + moreRect.Width / 2f - origin.X) * mp), mcy = (float)(origin.Y + (moreRect.Top + moreRect.Height / 2f - origin.Y) * mp);
                        int mal = (int)(255 * Math.Min(1, mp * 1.6));
                        var mst = g.Save();
                        g.TranslateTransform(mcx, mcy); g.ScaleTransform(ms, ms);
                        var mr = new Rectangle(-moreRect.Width / 2, -moreRect.Height / 2, moreRect.Width, moreRect.Height);
                        using (var b = new SolidBrush(Color.FromArgb((moreHot ? 225 : 190) * mal / 255, 70, 70, 78)))
                        using (var pen = new Pen(Color.FromArgb(70 * mal / 255, 255, 255, 255)))
                        { g.FillEllipse(b, mr); g.DrawEllipse(pen, mr); }
                        if (arrowFont == null) arrowFont = new Font("Segoe UI Symbol", 17f, FontStyle.Bold);
                        using (var tb = new SolidBrush(Color.FromArgb(235 * mal / 255, 255, 255, 255)))
                            g.DrawString("➦", arrowFont, tb, new RectangleF(mr.Left, mr.Top, mr.Width, mr.Height), fmt);
                        g.Restore(mst);
                    }
                    if (moreHot)
                    {
                        int mw = TextRenderer.MeasureText("Open in Explorer", font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width + 28;
                        double ma = style == "list" ? 0 : Math.Min(18.0, 1.0 + 1.9 * items.Count);
                        int rise = (int)(mw * Math.Sin(ma * Math.PI / 180)); // how far the tilt lifts the pill's left end
                        var lr = new Rectangle(Math.Max(4, moreRect.Left - 14 - mw), Math.Max(rise + 2, moreRect.Top + (moreRect.Height - LabelH) / 2 - 10), mw, LabelH); // left of the button, nudged up clear of the top file label
                        var ms = g.Save();
                        float mpx = lr.Right, mpy = lr.Top + lr.Height / 2f;
                        g.TranslateTransform(mpx, mpy); g.RotateTransform((float)ma); g.TranslateTransform(-mpx, -mpy);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        using (GraphicsPath gp = Layered.RoundedRect(lr, LabelH / 2f)) using (var b = new SolidBrush(Color.FromArgb(235, 60, 60, 66))) g.FillPath(b, gp);
                        using (var tb = new SolidBrush(Color.FromArgb(250, 250, 252))) g.DrawString("Open in Explorer", font, tb, lr, fmt);
                        g.Restore(ms);
                    }
                }
                if (items.Count == 0 && !loading)
                {
                    using (var b = new SolidBrush(Color.FromArgb(220, 38, 38, 42)))
                    using (GraphicsPath gp = Layered.RoundedRect(new RectangleF(origin.X - 80, Height - 70, 160, 28), 8))
                        g.FillPath(b, gp);
                    using (var tb = new SolidBrush(Color.White))
                        g.DrawString("Folder is empty", font, tb, new RectangleF(origin.X - 80, Height - 70, 160, 28), fmt);
                }
            }
            long te = clk.ElapsedTicks; surface.Present(Handle, Left, Top, 255);
            double dd = (clk.ElapsedTicks - dt0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, ppr = (clk.ElapsedTicks - te) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            drawN++; drawSum += dd; if (dd > drawMax) drawMax = dd;
            if (drawN <= 3 || drawN % 20 == 0) Log("frame " + drawN + " draw=" + dd.ToString("0.0") + "ms (present " + ppr.ToString("0.0") + "ms) avg=" + (drawSum / drawN).ToString("0.0") + " max=" + drawMax.ToString("0.0"));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closeDone && !IsDisposed && IsHandleCreated && Visible && items.Count > 0 && style != "grid" && e.CloseReason == CloseReason.UserClosing)
            {
                if (closeStart == DateTime.MinValue) { closeStart = DateTime.UtcNow; hover = -1; moreHot = false; for (int k = 0; k < items.Count; k++) items[k].Hot = 0; }
                e.Cancel = true; return;
            }
            if (Dbg) { string st = Environment.StackTrace; int k = st.IndexOf("OnFormClosing"); Log("closing from: " + st.Substring(Math.Max(0, k), Math.Min(600, st.Length - Math.Max(0, k))).Replace(Environment.NewLine, " | ")); }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (vsync != null) { vsync.Dispose(); vsync = null; }
            timer.Stop();
            base.OnFormClosed(e);
            if (closed != null) closed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); if (arrowFont != null) arrowFont.Dispose(); if (surface != null) surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
