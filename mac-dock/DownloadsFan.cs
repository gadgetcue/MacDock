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
        const int MaxItems = 10, IconSize = 60, Step = 66, LabelH = 22, WinW = 560, Margin = 16;

        sealed class FanItem
        {
            public string Path, Name; public bool IsDir;
            public Bitmap Icon; public Bitmap Thumb; public double Hot;
            public Rectangle Rect; // final icon rect (window coordinates)
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
            int left = Math.Max(area.Left, Math.Min(anchorTopCenter.X - 300, area.Right - WinW));
            int top = Math.Max(area.Top, anchorTopCenter.Y - height);
            Bounds = new Rectangle(left, top, WinW, anchorTopCenter.Y - top);
            height = Bounds.Height;
            origin = new Point(anchorTopCenter.X - left, height);

            this.folder = folder;
            Loaded hit = Cached(folder);
            if (hit != null) { cur = hit; items.AddRange(hit.List); Layout(); }
            else { loading = true; Prefetch(folder); }

            Log("ctor done " + ((clk.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + "ms cached=" + (hit != null));
            timer.Interval = 15;
            timer.Tick += (s, e) => Poll();
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
                    double curve = style == "list" ? 0 : 8.0;
                    int cx = origin.X + (int)(Math.Pow(i, 1.6) * curve);
                    cx = Math.Max(IconSize / 2 + 4, Math.Min(WinW - IconSize / 2 - 4, cx));
                    int cy = height - IconSize / 2 - 6 - i * step;
                    items[i].Rect = new Rectangle(cx - IconSize / 2, cy - IconSize / 2, IconSize, IconSize);
                }
            }
        }

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
            timer.Start();
        }

        // 0..1 progress of item i's fan-out (staggered), with a little overshoot.
        double Progress(int i)
        {
            double t = ((DateTime.UtcNow - born).TotalMilliseconds - i * 22) / 260.0;
            t = Math.Max(0, Math.Min(1, t));
            double c1 = 1.2, c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u; // easeOutBack
        }

        bool Animating { get { return (DateTime.UtcNow - born).TotalMilliseconds < items.Count * 22 + 320; } }

        Rectangle CurrentRect(int i)
        {
            double p = Progress(i);
            Rectangle r = items[i].Rect;
            double ox = origin.X - IconSize / 2.0, oy = origin.Y - IconSize / 2.0;
            // Like the Dock: icons grow out of the dock icon while sliding up the arc.
            double sc = 0.15 + 0.85 * Math.Max(0, Math.Min(1, p));
            int sz = (int)(IconSize * sc);
            double cx = ox + (r.Left - ox) * p + IconSize / 2.0, cy = oy + (r.Top - oy) * p + IconSize / 2.0;
            return new Rectangle((int)(cx - sz / 2.0), (int)(cy - sz / 2.0), sz, sz);
        }

        void Poll()
        {
            if (menuOpen || dragging) return;
            NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
            Point sp = new Point(p.X, p.Y), c = new Point(p.X - Left, p.Y - Top);

            int idx = -1;
            if (!Animating)
                for (int i = 0; i < items.Count; i++)
                    if (HitRect(i).Contains(c)) { idx = i; break; }
            if (idx != hover) { hover = idx; dirty = true; }
            for (int k = 0; k < items.Count; k++) { double tgt = k == hover ? 1 : 0, d = tgt - items[k].Hot; if (Math.Abs(d) > 0.01) { items[k].Hot += d * 0.3; dirty = true; } else items[k].Hot = tgt; }

            bool near = keepOpen(sp) || idx >= 0 || Animating;
            if (!near && items.Any((it) => Rectangle.Inflate(HitRect(items.IndexOf(it)), 6, 6).Contains(c))) near = true;
            if (!near)
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
                else { pressIdx = -1; if (!keepOpen(sp)) { Close(); return; } }
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
            else if (!down && wasDown && pressIdx >= 0)
            {
                int pi = pressIdx; pressIdx = -1;
                if (pi == idx) { string path = items[pi].Path; Close(); Open(path); return; }
            }
            wasDown = down;
            if ((Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0) { Close(); return; }

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

        string Display(FanItem it)
        {
            return it.Name.Length > 38 ? it.Name.Substring(0, 35) + "..." : it.Name;
        }

        int LabelWidth(FanItem it)
        {
            return TextRenderer.MeasureText(Display(it), font).Width + 16;
        }

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
            menu = new MenuPopup(list, at, 0, () => { menuOpen = false; if (!IsDisposed) BeginInvoke((Action)(() => Close())); });
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
                    Rectangle r = CurrentRect(i);
                    bool hot = i == hover;
                    { int gI = (int)(IconSize * 0.34 * it.Hot); r.Inflate(gI, gI); }
                    int alpha = (int)(255 * Math.Min(1, pr * 1.6));

                    Bitmap img = it.Thumb ?? it.Icon;
                    if (img != null)
                    {
                        Rectangle dst = r;
                        if (it.Thumb != null) dst = new Rectangle(r.Left + (r.Width - it.Thumb.Width) / 2, r.Top + (r.Height - it.Thumb.Height) / 2, it.Thumb.Width, it.Thumb.Height);
                        if (it.Thumb != null)
                        {
                            using (var sh = new SolidBrush(Color.FromArgb(alpha / 4, 0, 0, 0))) g.FillRectangle(sh, dst.Left + 1, dst.Top + 3, dst.Width, dst.Height);
                            using (var pen = new Pen(Color.FromArgb(alpha / 3, 255, 255, 255))) g.DrawRectangle(pen, dst.Left, dst.Top, dst.Width - 1, dst.Height - 1);
                        }
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f };
                            ia.SetColorMatrix(cm);
                            g.DrawImage(img, dst, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                        }
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
                        Rectangle lr = labelsLeft ? new Rectangle(r.Left - lw - 10, r.Top + (r.Height - LabelH) / 2, lw, LabelH)
                                                  : new Rectangle(r.Right + 10, r.Top + (r.Height - LabelH) / 2, lw, LabelH);
                        using (GraphicsPath gp = Layered.RoundedRect(lr, 7))
                        using (var b = new SolidBrush(Color.FromArgb(hot ? la * 235 / 255 : la * 250 / 255, hot ? 60 : 38, hot ? 60 : 38, hot ? 66 : 42)))
                            g.FillPath(b, gp);
                        using (var tb = new SolidBrush(Color.FromArgb(la, 250, 250, 252)))
                            g.DrawString(Display(it), font, tb, lr, fmt);
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
            if (Dbg) { string st = Environment.StackTrace; int k = st.IndexOf("OnFormClosing"); Log("closing from: " + st.Substring(Math.Max(0, k), Math.Min(600, st.Length - Math.Max(0, k))).Replace(Environment.NewLine, " | ")); }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
            if (closed != null) closed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); if (surface != null) surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
