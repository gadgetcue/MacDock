// Mission Control: a full-screen overview of every open window, plus an "All Apps" page (like Launchpad)
// where any installed app can be launched or added to the dock with its + button or by dragging it there.
//
// The dock, top bar and island are always-on-top, so they stay visible above this overview and an app
// tile can be dropped straight onto the dock.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    sealed class MissionControl : Form
    {
        enum Page { Windows, Apps }

        sealed class Card
        {
            public IntPtr Handle;
            public string Title;
            public string Exe;
            public bool Minimized;
            public Bitmap Thumb;
            public Rectangle Rect;      // the thumbnail
            public Rectangle CloseRect; // the x button shown on hover
        }

        sealed class Tile
        {
            public string Shown { get { return ItemNames.Display(Target, Name); } }
            public string Name;
            public string Target;       // shell:AppsFolder\<id>
            public Bitmap Icon;
            public Rectangle Rect;      // whole cell
            public Rectangle IconRect;
            public Rectangle AddRect;   // the + / check button
            public string Folder;       // set on a folder tile (Name is the folder's name, Target is null)
            public List<Tile> Members;  // the apps inside that folder
            public bool IsFile;         // a file or folder from the Windows Search index (Spotlight results)
        public bool PinRow;         // a copy of an app or folder shown in the Pinned row at the top
        }

        Rectangle pinBand;              // the Pinned row's backdrop in grid space (empty when nothing is pinned)

        // The key a tile is pinned under: the app's id, or "folder:" plus the folder's name.
        static string PinKey(Tile t) { return t.Folder != null ? "folder:" + t.Folder : t.Target; }

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out Win32.RECT rect);

        const int BarClearance = 40, DockClearance = 150, SideMargin = 64;
        const int TileH = 144, TileIcon = 92;

        static MissionControl current;

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly FrameClock clock = new FrameClock();
        VsyncDriver vsync; // motion runs one step per display refresh; the 50 ms timer covers idle
        readonly Backdrop backdrop = new Backdrop(Blur.Tint(0x78, 8, 10, 18)); // desktop blurred behind the overview
        readonly Font titleFont = Typography.Text(10.5f, Weight.Medium);
        readonly Font smallFont = Typography.Caption();
        readonly Font segmentFont = Typography.Text(10.5f, Weight.Semibold);
        readonly Font searchFont = Typography.Text(12f);
        readonly Font pillFont = Typography.Text(26f);
        readonly System.Windows.Forms.Timer caretTimer = new System.Windows.Forms.Timer();
        bool caretOn = true;
        IntPtr previousForeground; bool hold; long spareBorn; static MissionControl spare; static System.Windows.Forms.Timer spareTimer;

        readonly List<Card> cards = new List<Card>();
        readonly List<Tile> allTiles = new List<Tile>();
        List<Tile> tiles = new List<Tile>();
        // App-tile icons live for the whole session, so reopening All Apps shows them instantly instead of reloading.
        static readonly Dictionary<string, Bitmap> tileIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> iconsFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> iconsLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bitmap> exeIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly Rectangle[] segments = new Rectangle[2];

        Surface surface;
        Page page;
        string search = "";
        List<Tile> fileTiles = new List<Tile>(); // Spotlight file results for the current search
        int fileSeq;
        // Open/close: a short, subtle genie-style grow from the dock (scale + fade), time-based and eased via Tokens.
        // Windows "Animation effects" off (Motion.Reduced) makes both instant.
        const double OpenSecs = 0.16, CloseSecs = 0.10; // Windows 11 Start: ~250 ms decelerate slide-up + fade in, quicker drop out
        double prog;                     // 0 = hidden, 1 = fully open
        double fadeGoal = 1;
        // Start-menu motion: opening decelerates hard (quartic ease-out), closing accelerates away (ease-in); alpha leads the slide.
        double Eased { get { return closing ? 1 - Tokens.EaseIn(1 - prog) : Quart(prog); } }
        double FadeEased { get { double t = Math.Min(1.0, prog * (closing ? 1.0 : 2.2)); return closing ? 1 - Tokens.EaseIn(1 - t) : Quart(t); } }
        static double Quart(double t) { t = 1 - Math.Max(0, Math.Min(1, t)); return 1 - t * t * t * t; }
        long tTrig, tShot, tBuilt, tTiles, tLay, tRend, tShow; bool firstLogged;
        int riseMax = -1;                     // pixels the frame travels while fading in
        // Frame-time log (open and close): min/avg/max/1%-low of the vsync interval plus present cost.
        readonly double[] animDt = new double[128]; int animN; 
        Spring scroll = new Spring(0);
        double scrollGoal, maxScroll;
        string openFolder;         // the folder being viewed on the All Apps page, or null for the main grid
        bool renaming;             // editing the open folder's name
        string renameBuffer = "";
        bool closing, dirty = true, suppressRestore, disposedFlag;
        int hover = -1;            // card or tile index under the cursor
        bool hoverAction;          // cursor is on that item's x / + button
        int selected = -1;
        int columns = 1;
        Tile dragTile;
        Point dragStart;
        int segmentHover = -1;

        // In-overview drag and drop: while a tile is being dragged, the drop target is highlighted and a ghost follows the cursor.
        enum DropKind { None, IntoFolder, MakeFolder, Before, After, Remove, Pin, PinBefore, PinAfter, Unpin }
        Tile dragSource, dropTile;
        DropKind dropKind;
        Point dragPoint;
        readonly System.Drawing.Imaging.ImageAttributes ghostAttr = MakeGhostAttributes();

        static System.Drawing.Imaging.ImageAttributes MakeGhostAttributes()
        {
            var attr = new System.Drawing.Imaging.ImageAttributes();
            attr.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.85f }); // 85% opaque
            return attr;
        }

        // ---------- opening and closing ----------

        // Opens the overview, or switches page / closes it if it is already open.
        public static void Toggle(bool apps)
        {
            if (current != null && !current.IsDisposed)
            {
                if (current.closing) return;
                current.BeginClose();
                return;
            }

            long tt = System.Diagnostics.Stopwatch.GetTimestamp();
            MissionControl sp = spare; spare = null;
            if (sp != null && (sp.IsDisposed || Environment.TickCount - sp.spareBorn > 300000 || sp.Bounds != DockForm.DockScreen.Bounds)) { try { sp.Close(); } catch { } sp = null; }
            if (sp != null) { current = sp; sp.WakeFromSpare(tt); }
            else current = new MissionControl(Page.Apps);
            current.tTrig = tt; current.Show(); long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
            WinUtil.Focus(current.Handle); long q2 = System.Diagnostics.Stopwatch.GetTimestamp();
            current.Activate(); long q3 = System.Diagnostics.Stopwatch.GetTimestamp();
            RaiseChrome(); long q4 = System.Diagnostics.Stopwatch.GetTimestamp(); double qf = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            current.stepLog = string.Format(" T[show {0:0} focus {1:0} activate {2:0} chrome {3:0}]", (q1 - tt) / qf, (q2 - q1) / qf, (q3 - q2) / qf, (q4 - q3) / qf);
        }

        // The dock, top bar and island stay above the overview; being topmost too, it would otherwise cover them.
        static void RaiseChrome()
        {
            foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
                if (f is TopBar || f is IslandForm) DockForm.KeepOnTop(f);
            if (DockForm.Instance != null) DockForm.Instance.Reassert();
        }

        // Like Start: clicking into another app dismisses it. Our own popups / the dock (same process) don't.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (closing || disposedFlag) return;
            BeginInvoke(new Action(() =>
            {
                if (closing || disposedFlag) return;
                IntPtr fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return;
                uint pid; GetWindowThreadProcessId(fg, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                suppressRestore = true; BeginClose();
            }));
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!closing) BeginInvoke(new Action(RaiseChrome)); // activating a topmost window lifts it over the other topmost ones
        }

        public static void Dismiss()
        {
            if (current != null && !current.IsDisposed) current.BeginClose();
        }

        public static bool IsOpen { get { return current != null && !current.IsDisposed && !current.closing; } }

        // A dock item was clicked while the overview is open: fade it out without handing focus back to the app that was
        // in front before, so the item that is about to open or come forward keeps the focus.
        public static void DismissForLaunch()
        {
            if (current == null || current.IsDisposed || current.closing) return;
            current.suppressRestore = true;
            current.BeginClose();
        }

        // A second swipe up while open goes on to All Apps.
        public static void ShowApps()
        {
        }

        MissionControl(Page startPage, bool prewarm = false)
        {
            page = startPage; hold = prewarm;
            previousForeground = prewarm ? IntPtr.Zero : Win32.GetForegroundWindow();
            // Topmost, so no other window can cover it (e.g. after a menu popup closes and focus moves on); its blur
            // sits in the same layer, just below it. The dock and bars are raised back above it by RaiseChrome.
            backdrop.AlwaysOnTop = true;
            TopMost = true;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = DockForm.DockScreen.Bounds;

            // Grab the screen now (must be before we show), but blur it on a worker while the tiles are built.
            blurTask = null;
            if (!prewarm) try
            {
                Bitmap shot = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using (Graphics sg = Graphics.FromImage(shot)) sg.CopyFromScreen(Bounds.Location, Point.Empty, Bounds.Size);
                pendingShot = shot;
            }
            catch { blurTask = null; }

            tShot = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildTiles(); tTiles = System.Diagnostics.Stopwatch.GetTimestamp();
            LayoutAll(); tLay = System.Diagnostics.Stopwatch.GetTimestamp();
            deskBlur = null; // first frame is tint-only; the real blur is swapped in from Step() when the worker finishes
            if (glassComp != null) { glassComp.Dispose(); glassComp = null; }
            tBuilt = System.Diagnostics.Stopwatch.GetTimestamp();

            caretTimer.Interval = 55;
            caretTimer.Tick += (s, e) => PulseCaret();
            if (!prewarm) caretTimer.Start();

            AllowDrop = true; // tiles can be dropped on each other, on folders and back out of a folder
            vsync = new VsyncDriver(this, Step);
            timer.Interval = 16;
            timer.Tick += (s, e) => { if (!hold) Step(); };
            clock.Reset();
            if (prewarm) { spareBorn = Environment.TickCount; var hh = Handle; dirty = false; } else timer.Start(); // prewarm: create the window and render it once, hidden
        }

        // A hidden, rendered copy is kept ready so opening only has to show it.
        public static void Prewarm()
        {
            if (spare != null || current != null) return;
            try { spare = new MissionControl(Page.Apps, true); } catch { spare = null; return; }
            // Let the icons arrive, redraw the hidden frame once with them, then hand its memory back to Windows.
            var rt = new System.Windows.Forms.Timer { Interval = 2500 };
            rt.Tick += (s, e) => { rt.Stop(); rt.Dispose(); MissionControl sp = spare; if (sp == null || sp.IsDisposed || sp.disposedFlag || current != null) return; try { sp.lastStaticKey = int.MinValue; sp.Render(); } catch { } MemTrim.Soon(500); };
            rt.Start();
        }
        public static void PrewarmSoon(int ms)
        {
            if (spareTimer != null) { spareTimer.Stop(); spareTimer.Dispose(); }
            spareTimer = new System.Windows.Forms.Timer { Interval = ms };
            spareTimer.Tick += (s, e) => { spareTimer.Stop(); if (current == null) Prewarm(); };
            spareTimer.Start();
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr a, IntPtr b);
        static System.Windows.Forms.Timer trimT;
        static void TrimSoon()
        {
            if (trimT != null) { trimT.Stop(); trimT.Dispose(); }
            trimT = new System.Windows.Forms.Timer { Interval = 1500 };
            trimT.Tick += (s, e) => { trimT.Stop(); try { GC.Collect(2, GCCollectionMode.Forced, true); SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { } };
            trimT.Start();
        }
        void WakeFromSpare(long tt)
        {
            previousForeground = Win32.GetForegroundWindow();
            try { Bitmap shot = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); using (Graphics sg = Graphics.FromImage(shot)) sg.CopyFromScreen(Bounds.Location, Point.Empty, Bounds.Size); pendingShot = shot; } catch { }
            tShot = tTiles = tLay = tBuilt = System.Diagnostics.Stopwatch.GetTimestamp();
            hold = false; prog = 0; fadeGoal = 1; tTrig = tt; caretOn = true;
            clock.Reset(); caretTimer.Start(); timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80; // layered; tool window so it stays out of Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Render();
        }

        void BeginClose()
        {
            if (closing) return;
            closing = true;
            fadeGoal = 0;
            clock.Reset();
            Kick();
        }

        // Motion starts or continues: hand frame pacing to the display's vsync instead of the idle timer.
        void Kick()
        {
            if (vsync.Active) return;
            timer.Stop();
            vsync.Wake();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            disposedFlag = true;
            timer.Stop(); caretTimer.Stop();
            vsync.Dispose();
            base.OnFormClosed(e);

            foreach (Card c in cards) if (c.Thumb != null) c.Thumb.Dispose();
            foreach (Bitmap b in exeIcons.Values) b.Dispose();
            if (surface != null) surface.Dispose();
            if (gridCache != null) { if (sGrid != null) sGrid.Dispose(); sGrid = gridCache; sGridStamp = gridStamp; sGridIcons = new HashSet<string>(); foreach (Tile gt in gridHasIcon) if (gt.Target != null) sGridIcons.Add(gt.Target); gridCache = null; }
            backdrop.Dispose();
            if (current == this) current = null;
            if (!hold) PrewarmSoon(1200);
            MemTrim.Soon(2500);

            if (!suppressRestore) WinUtil.Focus(previousForeground);
        }

        // ---------- data ----------

        static List<IntPtr> ZOrder()
        {
            var order = new List<IntPtr>();
            NativeMethods.EnumWindows((h, l) => { order.Add(h); return true; }, IntPtr.Zero);
            return order;
        }

        void BuildCards()
        {
            List<IntPtr> order = ZOrder();
            var found = new List<KeyValuePair<IntPtr, string>>();
            foreach (KeyValuePair<string, List<IntPtr>> app in DockForm.EnumerateRunningWindows())
                foreach (IntPtr h in app.Value) found.Add(new KeyValuePair<IntPtr, string>(h, app.Key));

            foreach (KeyValuePair<IntPtr, string> f in found.OrderBy(x => order.IndexOf(x.Key)))
            {
                cards.Add(new Card
                {
                    Handle = f.Key,
                    Exe = f.Value,
                    Title = WinUtil.TitleOf(f.Key),
                    Minimized = NativeMethods.IsIconic(f.Key)
                });
            }
        }

        void BuildTiles()
        {
            List<KeyValuePair<string, string>> apps;
            lock (AppDirectory.Names) apps = AppDirectory.Apps.ToList();

            if (apps.Count == 0)
            {
                // The app list is still being read; fill the page in when it is ready.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    AppDirectory.Build();
                    try { BeginInvoke(new Action(() => { allTiles.Clear(); BuildTiles(); LayoutAll(); dirty = true; })); }
                    catch { }
                });
                return;
            }

            // Apps installed since MacDock started (a new web app, a program) are picked up on a worker thread, and the page refills.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!AppDirectory.Refresh()) return;
                try { BeginInvoke(new Action(() => { if (disposedFlag) return; allTiles.Clear(); BuildTiles(); LayoutAll(); dirty = true; Kick(); })); }
                catch { }
            });

            foreach (KeyValuePair<string, string> app in apps.OrderBy(a => a.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                if (app.Key.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var tile = new Tile { Name = app.Key, Target = ShellIcons.AppsFolderPrefix + app.Value };
                lock (tileIconCache) tileIconCache.TryGetValue(tile.Target, out tile.Icon);
                allTiles.Add(tile);
            }
            SortAllTiles();
            ApplyFilter();

            // Icons come from a worker thread; they pop in as they are ready.
            // Loading keeps going after the overview closes, so the next open finds every icon already cached.
            List<Tile> work;
            lock (tileIconCache)
                work = allTiles.Where(t => t.Icon == null && !iconsFailed.Contains(t.Target) && iconsLoading.Add(t.Target)).ToList();
            if (work.Count == 0) return;
            var loader = new Thread(() =>
            {
                foreach (Tile t in work)
                {
                    Bitmap cust = IconChoice.Custom(t.Target, t.Name);
                    if (cust != null) { Bitmap ct = DockForm.Tile(cust); lock (tileIconCache) tileIconCache[t.Target] = ct; t.Icon = ct; dirty = true; continue; }
                    bool webApp = Pwa.IsWebAppId(Pwa.IdOfTarget(t.Target)); // a Brave / Edge / Chrome web app keeps its own icon
                    Bitmap packed = webApp ? null : IconPack.ForName(t.Name); // macOS icon, if the pack has one for this app
                    if (packed != null) { lock (tileIconCache) tileIconCache[t.Target] = packed; t.Icon = packed; dirty = true; continue; }

                    Bitmap raw = (webApp ? Pwa.IconFor(t.Name) : null) ?? ShellIcons.Load(t.Target, 96); if (raw == null) raw = ShellIcons.Generic(t.Name, 96);
                    if (raw == null) { lock (tileIconCache) iconsFailed.Add(t.Target); continue; }
                    Bitmap tiled = DockForm.Tile(raw);
                    lock (tileIconCache) tileIconCache[t.Target] = tiled;
                    t.Icon = tiled;
                    dirty = true;
                }
            }) { IsBackground = true, Name = "MissionControl icons" };
            loader.SetApartmentState(ApartmentState.STA);
            loader.Start();
        }

        // Loads every app icon into the session cache at startup, so All Apps never has to load them when opened.
        // IconPack.Reset disposes the pack bitmaps this cache shares, so drop them and reload.
        public static void ResetIcons()
        {
            Preload();
        }

        // The icon choice changed: forget every tile icon, reload, and refill any open or pre-built overview.
        public static void IconsChanged()
        {
            lock (tileIconCache) { tileIconCache.Clear(); iconsFailed.Clear(); iconsLoading.Clear(); }
            foreach (MissionControl mc in new[] { current, spare })
            {
                if (mc == null || mc.IsDisposed || mc.disposedFlag) continue;
                try { mc.allTiles.Clear(); mc.BuildTiles(); mc.LayoutAll(); mc.dirty = true; mc.lastStaticKey = int.MinValue; } catch { }
            }
            Preload();
        }

        public static void Preload()
        {
            var t = new Thread(() =>
            {
                List<KeyValuePair<string, string>> apps;
                lock (AppDirectory.Names) apps = AppDirectory.Apps.ToList();
                foreach (KeyValuePair<string, string> app in apps)
                {
                    if (app.Key.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string target = ShellIcons.AppsFolderPrefix + app.Value;
                    lock (tileIconCache) if (tileIconCache.ContainsKey(target) || iconsFailed.Contains(target) || !iconsLoading.Add(target)) continue;
                    bool webApp = Pwa.IsWebAppId(app.Value);
                    Bitmap custom = IconChoice.Custom(target, app.Key);
                    if (custom != null) { lock (tileIconCache) tileIconCache[target] = DockForm.Tile(custom); continue; }
                    Bitmap bmp = webApp ? null : IconPack.ForName(app.Key);
                    if (bmp == null)
                    {
                        Bitmap raw = (webApp ? Pwa.IconFor(app.Key) : null) ?? ShellIcons.Load(target, 96); if (raw == null) raw = ShellIcons.Generic(app.Key, 96);
                        if (raw != null) bmp = DockForm.Tile(raw);
                    }
                    lock (tileIconCache) { if (bmp != null) tileIconCache[target] = bmp; else iconsFailed.Add(target); }
                }
            }) { IsBackground = true, Name = "Icon preload" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // The base order of every app: A-Z by default, or the user's dragged order in Custom mode (apps that are not in
        // it yet go after, A-Z). Name Z-A is applied to what is displayed, in SortTiles.
        void SortAllTiles()
        {
            List<Tile> sorted;
            if (LaunchpadOrder.Mode == "custom")
            {
                Dictionary<string, int> index = LaunchpadOrder.IndexMap();
                sorted = allTiles.OrderBy(t => { int i; return t.Target != null && index.TryGetValue(t.Target, out i) ? i : int.MaxValue; })
                    .ThenBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            else if (LaunchpadOrder.Mode == "date-new" || LaunchpadOrder.Mode == "date-old") sorted = SortTiles(allTiles.ToList());
            else sorted = allTiles.OrderBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            allTiles.Clear();
            allTiles.AddRange(sorted);
        }

        // Applies the name sort modes to what the grid shows (apps and folder tiles together). Custom keeps the base order.
        static List<Tile> SortTiles(List<Tile> list)
        {
            string mode = LaunchpadOrder.Mode;
            if (mode == "name-asc") return list.OrderBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (mode == "name-desc") return list.OrderByDescending(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (mode == "date-new") return list.OrderByDescending(t => TileDate(t)).ThenBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (mode == "date-old") return list.OrderBy(t => TileDate(t) == DateTime.MinValue ? DateTime.MaxValue : TileDate(t)).ThenBy(t => t.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
            return list;
        }

        // Install date of an app tile; a folder tile takes its newest member. Unknown dates are MinValue (last in both date sorts).
        static DateTime TileDate(Tile t)
        {
            if (t.Members != null) return t.Members.Count == 0 ? DateTime.MinValue : t.Members.Max(m => TileDate(m));
            return InstallDates.For(t.Name, t.Target);
        }

        static bool InFolder(Tile t, string folder)
        {
            return string.Equals(LaunchpadFolders.Of(t.Target), folder, StringComparison.OrdinalIgnoreCase);
        }

        // The main grid: apps not in a folder, with each folder shown as one tile where its first app would be.
        // With a folder open, just that folder's apps.
        List<Tile> MainTiles()
        {
            if (openFolder != null)
            {
                string open = openFolder;
                List<Tile> inside = allTiles.Where(t => InFolder(t, open)).ToList();
                if (inside.Count > 0) return SortTiles(inside);
                openFolder = null;
                renaming = false;
            }

            var result = new List<Tile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Tile t in allTiles)
            {
                string f = LaunchpadFolders.Of(t.Target);
                if (f == null) { if (!LaunchpadPins.Contains(t.Target)) result.Add(t); continue; } // pinned apps live in the top row
                if (!seen.Add(f)) continue;
                if (LaunchpadPins.Contains("folder:" + f)) continue;
                result.Add(new Tile { Name = f, Folder = f, Members = allTiles.Where(m => InFolder(m, f)).ToList() });
            }
            return SortTiles(result);
        }

        // The Pinned row's tiles, in the order they were pinned: copies of the app or folder tiles, marked PinRow.
        List<Tile> PinnedTiles()
        {
            var list = new List<Tile>();
            foreach (string key in LaunchpadPins.Keys())
            {
                if (key.StartsWith("folder:", StringComparison.Ordinal))
                {
                    string f = key.Substring(7);
                    List<Tile> members = allTiles.Where(m => InFolder(m, f)).ToList();
                    if (members.Count > 0) list.Add(new Tile { Name = f, Folder = f, Members = members, PinRow = true });
                }
                else
                {
                    Tile app = allTiles.FirstOrDefault(t => string.Equals(t.Target, key, StringComparison.OrdinalIgnoreCase));
                    if (app != null) list.Add(new Tile { Name = app.Name, Target = app.Target, Icon = app.Icon, PinRow = true });
                }
            }
            return list;
        }

        void ApplyFilter()
        {
            if (search.Length == 0 && category != "All" && openFolder == null)
            {
                string cat = category;
                tiles = SortTiles(allTiles.Where(t => Categorize(t.Name) == cat).ToList());
                return;
            }
            if (search.Length == 0)
            {
                tiles = MainTiles();
                if (openFolder == null) tiles.InsertRange(0, PinnedTiles());
                return;
            }
            tiles = allTiles
                .Select(t => new { t, rank = t.Shown.StartsWith(search, StringComparison.CurrentCultureIgnoreCase) ? 0
                    : t.Shown.IndexOf(" " + search, StringComparison.CurrentCultureIgnoreCase) >= 0 ? 1
                    : t.Shown.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ? 2 : 3 })
                .Where(x => x.rank < 3)
                .OrderBy(x => x.rank).ThenBy(x => x.t.Shown.Length)
                .Select(x => x.t)
                .ToList();
            tiles.AddRange(fileTiles.Take(Math.Max(0, 28 - tiles.Count)));
        }

        void SwitchPage(Page next)
        {
            page = next;
            selected = -1;
            hover = -1;
            scroll = new Spring(0);
            scrollGoal = 0;
            if (openFolder != null) { openFolder = null; renaming = false; ApplyFilter(); }
            LayoutAll();
            dirty = true;
        }

        Bitmap ExeIcon(string exe)
        {
            Bitmap b;
            if (exe == null) return null;
            if (!exeIcons.TryGetValue(exe, out b))
            {
                b = DockForm.LoadShellIcon(exe, 0, 0);
                exeIcons[exe] = b; // may be null; remembered so it isn't retried
            }
            return b;
        }

        // ---------- layout ----------

        Rectangle ContentArea
        {
            get
            {
                if (page == Page.Apps)
                {
                    Rectangle pn = PanelRect;
                    int ctop = pn.Top + 74;
                    return new Rectangle(pn.Left + 24, ctop, pn.Width - 48, pn.Bottom - ctop);
                }
                return new Rectangle(SideMargin, 110, Width - SideMargin * 2, Height - 110 - DockClearance);
            }
        }

        // An open folder fills the page: its editable name takes the search box's place, with a back chip at the left.
        Rectangle FolderNameRect
        {
            get { Rectangle pn = PanelRect; return new Rectangle(pn.Left + (pn.Width - 380) / 2, pn.Top + 18, 380, 38); }
        }

        // The floating search pill, the frosted panel under it, and the "..." menu button inside the pill.
        Rectangle PillRect { get { int pw = Math.Max(560, (int)(Width * 0.345)), ph = Math.Max(84, (int)(Width * 0.057)); return new Rectangle((Width - pw) / 2, BarClearance + 36, pw, ph); } }
        Rectangle PanelRect
        {
            get
            {
                int w = Math.Max(760, Math.Min(1120, (int)(Width * 0.58)));
                int top = PillRect.Bottom + 18;
                return new Rectangle((Width - w) / 2, top, w, Math.Max(200, Height - DockClearance + 14 - top));
            }
        }
        Rectangle ChipRow { get { Rectangle pn = PanelRect; return new Rectangle(pn.Left + 24, pn.Top + 18, pn.Width - 48, 38); } }
        Rectangle SortRect { get { Rectangle pl = PillRect; return new Rectangle(pl.Right - 70, pl.Top + (pl.Height - 32) / 2, 32, 32); } }
        Rectangle PanelClip { get { Rectangle pn = PanelRect, a = ContentArea; return new Rectangle(pn.Left, a.Top - 10, pn.Width, pn.Bottom - a.Top + 10); } }

        static readonly string[] Categories = { "All", "Productivity & Finance", "Creativity", "Utilities", "Information & Reading", "Games", "Entertainment", "Social" };
        string category = "All";
        int chipScroll, chipHover = -1, chipTotal;
        readonly List<Rectangle> chipRects = new List<Rectangle>();

        static string Categorize(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            Func<string[], bool> has = ks => ks.Any(k => n.Contains(k));
            if (has(new[] { "game", "steam", "epic", "xbox", "minecraft", "valorant", "riot", "battle.net", "ubisoft", "gog", "roblox", "fortnite", "league of", "genshin", "origin", "ea app", "solitaire" })) return "Games";
            if (has(new[] { "discord", "whatsapp", "telegram", "slack", "teams", "zoom", "skype", "messenger", "signal", "instagram", "facebook", "twitter", "reddit", "linkedin", "snapchat", "wechat", "viber", "tiktok", "threads" })) return "Social";
            if (has(new[] { "spotify", "netflix", "vlc", "youtube", "music", "media", "player", "prime video", "disney", "twitch", "movies", "tv", "kodi", "itunes", "groove", "podcast", "plex" })) return "Entertainment";
            if (has(new[] { "photoshop", "illustrator", "premiere", "after effects", "lightroom", "figma", "blender", "canva", "davinci", "obs", "audacity", "gimp", "inkscape", "krita", "paint", "photos", "camera", "clipchamp", "capcut", "adobe", "indesign", "sketch", "studio one", "fl studio", "ableton", "design", "draw" })) return "Creativity";
            if (has(new[] { "word", "excel", "powerpoint", "outlook", "onenote", "office", "notion", "obsidian", "todo", "to do", "calendar", "mail", "sticky", "code", "visual studio", "pycharm", "intellij", "docs", "sheets", "bank", "finance", "wallet", "money", "tax", "trello", "asana", "evernote", "onedrive", "dropbox", "drive", "pdf", "acrobat", "github", "git", "access", "publisher", "project", "planner", "claude", "chatgpt", "copilot" })) return "Productivity & Finance";
            if (has(new[] { "edge", "chrome", "firefox", "brave", "opera", "browser", "news", "reader", "kindle", "books", "weather", "maps", "wikipedia", "pocket", "calibre", "dictionary", "tips", "get help", "feedback" })) return "Information & Reading";
            return "Utilities";
        }

        void LayoutChips()
        {
            chipRects.Clear();
            int x = 0;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                foreach (string c in Categories)
                {
                    int w = (int)Math.Ceiling(g.MeasureString(c, titleFont, 600, StringFormat.GenericTypographic).Width) + 36;
                    chipRects.Add(new Rectangle(x, 0, w, 34));
                    x += w + 10;
                }
            chipTotal = x - 10;
            int max = Math.Max(0, chipTotal - ChipRow.Width);
            chipScroll = Math.Max(0, Math.Min(max, chipScroll));
        }

        Rectangle ChipScreen(int i) { Rectangle r = chipRects[i], row = ChipRow; return new Rectangle(row.Left + r.Left - chipScroll, row.Top + 2, r.Width, r.Height); }
        int ChipAt(Point p)
        {
            if (page != Page.Apps || openFolder != null || !ChipRow.Contains(p)) return -1;
            for (int i = 0; i < chipRects.Count; i++) if (ChipScreen(i).Contains(p)) return i;
            return -1;
        }

        Rectangle FolderBackRect
        {
            get { Rectangle pn = PanelRect; return new Rectangle(pn.Left + 24, pn.Top + 18, 128, 38); }
        }

        void LayoutAll()
        {
            int w = 260;
            segments[0] = Rectangle.Empty;
            segments[1] = Rectangle.Empty;
            LayoutChips();

            if (page == Page.Windows) LayoutCards();
            else LayoutTiles();
        }

        void LayoutCards()
        {
            int n = cards.Count;
            columns = 1;
            if (n == 0) return;

            Rectangle area = ContentArea;
            double bestSize = -1;
            int bestCols = 1;
            for (int c = 1; c <= n; c++)
            {
                int rows = (int)Math.Ceiling(n / (double)c);
                double cellW = area.Width / (double)c - 40, cellH = area.Height / (double)rows - 64;
                double size = Math.Min(cellW, cellH * 1.6);
                if (size > bestSize) { bestSize = size; bestCols = c; }
            }
            columns = bestCols;

            int rowCount = (int)Math.Ceiling(n / (double)columns);
            int cw = area.Width / columns, ch = area.Height / rowCount;
            for (int i = 0; i < n; i++)
            {
                Card card = cards[i];
                int col = i % columns, row = i / columns;
                int availW = cw - 40, availH = ch - 64;

                Win32.RECT r;
                GetWindowRect(card.Handle, out r);
                double ww = Math.Max(1, r.Right - r.Left), wh = Math.Max(1, r.Bottom - r.Top);
                if (card.Minimized) { ww = 16; wh = 10; }
                double scale = Math.Min(Math.Min(availW / ww, availH / wh), card.Minimized ? 100 : 1.0);
                int tw = (int)(ww * scale), th = (int)(wh * scale);

                int cx = area.Left + col * cw + cw / 2, cy = area.Top + row * ch + (ch - 40) / 2;
                card.Rect = new Rectangle(cx - tw / 2, cy - th / 2, tw, th);
                card.CloseRect = new Rectangle(card.Rect.Left - 10, card.Rect.Top - 10, 24, 24);

                // Capture at the size it will be shown, off the UI thread.
                if (!card.Minimized && card.Thumb == null) QueueCapture(card, tw, th);
            }
        }

        void QueueCapture(Card card, int width, int height)
        {
            IntPtr handle = card.Handle;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap thumb = null;
                try
                {
                    Win32.RECT r;
                    GetWindowRect(handle, out r);
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w <= 0 || h <= 0) return;

                    using (var full = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics g = Graphics.FromImage(full))
                        {
                            IntPtr hdc = g.GetHdc();
                            try { PrintWindow(handle, hdc, 2); } // 2 = also capture GPU-rendered content
                            finally { g.ReleaseHdc(hdc); }
                        }

                        thumb = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(full, 0, 0, thumb.Width, thumb.Height);
                        }
                    }
                }
                catch { if (thumb != null) { thumb.Dispose(); thumb = null; } }
                if (thumb == null) return;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (disposedFlag) { thumb.Dispose(); return; }
                        card.Thumb = thumb;
                        dirty = true;
                    }));
                }
                catch { thumb.Dispose(); }
            });
        }

        void LayoutTiles()
        {
            Rectangle area = ContentArea;
            columns = Math.Max(1, Math.Min(7, area.Width / 110)); // seven columns, like the Tahoe launcher
            int TileW = area.Width / columns;
            int left = area.Left + (area.Width - columns * TileW) / 2;

            // Pinned tiles come first in the list and get their own band at the top of the grid; the rest follow below it.
            int pinned = 0;
            while (pinned < tiles.Count && tiles[pinned].PinRow) pinned++;
            int pinRows = (int)Math.Ceiling(pinned / (double)columns);
            const int PinHeader = 0, PinGap = 30;
            int mainTop = area.Top;
            pinBand = Rectangle.Empty;
            if (pinned > 0)
            {
                pinBand = new Rectangle(left, area.Top, columns * TileW, PinHeader + pinRows * TileH);
                mainTop = pinBand.Bottom + PinGap;
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile t = tiles[i];
                if (i < pinned)
                    t.Rect = new Rectangle(left + (i % columns) * TileW, area.Top + PinHeader + (i / columns) * TileH, TileW, TileH);
                else
                {
                    int j = i - pinned;
                    t.Rect = new Rectangle(left + (j % columns) * TileW, mainTop + (j / columns) * TileH, TileW, TileH);
                }
                t.IconRect = new Rectangle(t.Rect.Left + (TileW - TileIcon) / 2, t.Rect.Top + 6, TileIcon, TileIcon);
                t.AddRect = new Rectangle(t.IconRect.Right - 12, t.IconRect.Top - 8, 26, 26);
            }
            int rows = (int)Math.Ceiling((tiles.Count - pinned) / (double)columns);
            maxScroll = Math.Max(0, mainTop - area.Top + rows * TileH - area.Height + 10);
            scrollGoal = Math.Max(0, Math.Min(maxScroll, scrollGoal));
        }

        // ---------- animation ----------

        // Frame-time logs write files from inside the frame loop, which itself drops frames; keep off outside profiling.
        const bool PerfLog = false;
        void FlushAnimLog()
        {
            int n = animN; animN = 0;
            if (n < 2) return;
            if (PerfLog) try
            {
                double min = double.MaxValue, max = 0, sum = 0;
                for (int i = 0; i < n; i++) { double v = animDt[i] * 1000; if (v < min) min = v; if (v > max) max = v; sum += v; }
                Array.Sort(animDt, 0, n);
                double p99 = animDt[Math.Min(n - 1, (int)Math.Ceiling(n * 0.99) - 1)] * 1000;
                System.IO.File.AppendAllText(@"D:\#AI workflows\Claude work\Windows Apps\MacDock\mac-dock\launchpad_anim.txt",
                    string.Format("{0:s} {1} frames {2} dt ms min {3:0.0} avg {4:0.0} p99 {5:0.0} max {6:0.0}\r\n", DateTime.Now, closing ? "close" : "open", n, min, sum / n, p99, max));
            }
            catch { }
        }

        int perfN; double perfSum, perfDt; long pBg, pBlit, pPresent, pMask, pTiles, pFoot;
        void Step()
        {
            if (disposedFlag) return;
            long sT0 = System.Diagnostics.Stopwatch.GetTimestamp(); double dt = vsync.Frame(clock.Next()); if (stepLog.Length < 200 && tTrig != 0) stepLog += string.Format(" S@{0:0}+{1:0}", (sT0 - tTrig) / (System.Diagnostics.Stopwatch.Frequency / 1000.0), (System.Diagnostics.Stopwatch.GetTimestamp() - sT0) / (System.Diagnostics.Stopwatch.Frequency / 1000.0));
            double dur = Tokens.Duration(closing ? CloseSecs : OpenSecs);
            if (dur <= 0.0001) prog = fadeGoal;
            else if (prog < fadeGoal) { if (closing || (pendingShot == null && blurTask == null) || prog > 0) prog = Math.Min(fadeGoal, prog + Math.Min(dt, 0.017) / dur); } // nothing is shown until the blurred backdrop is ready
            else if (prog > fadeGoal) prog = Math.Max(fadeGoal, prog - Math.Min(dt, 0.017) / dur);
            bool moving = prog != fadeGoal;

            if (!scroll.IsSettled(scrollGoal, 0.3))
            {
                scroll.Step(scrollGoal, 16, 0.92, dt); // slight glide: eases in, coasts out
                moving = true;
                dirty = true;
            }

            if (closing && prog <= 0.001)
            {
                FlushAnimLog();
                Close();
                return;
            }

            if (pendingShot != null && !closing)
            {
                Bitmap shot2 = pendingShot; pendingShot = null;
                Rectangle? rgn = null; if (page == Page.Apps) { Rectangle pr0 = PanelRect; rgn = Rectangle.Union(Rectangle.Inflate(pr0, 24, 24), new Rectangle(pr0.Left, Math.Max(0, pr0.Top - 220), pr0.Width, 230)); }
                blurTask = System.Threading.Tasks.Task.Run(() => { try { Bitmap bl = GlassPanel.BlurBitmap(shot2, 20, rgn); glassPre = ComposeGlass(bl, rgn); return bl; } finally { shot2.Dispose(); } });
            }
            if (blurTask != null && blurTask.IsCompleted && !closing)
            {
                try { deskBlur = blurTask.Result; glassComp = glassPre; glassPre = null; } catch { deskBlur = null; }
                blurTask = null; backdrop.HideBackdrop(); lastStaticKey = int.MinValue; dirty = true;
            }
            if (dirty)
            {
                dirty = false;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Render();
                perfN++; perfSum += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; perfDt += dt;
                if (perfN == 60) { if (PerfLog) try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "launchpad_perf.txt"), string.Format("mask {6:0.0} tiles {7:0.0} foot {8:0.0} bg {3:0.0} blit {4:0.0} present {5:0.0} | render avg {0:0.0} ms, frame dt avg {1:0.0} ms, vsync dt {2:0.0} ms\r\n", perfSum / 60, perfDt / 60 * 1000, vsync.Dt * 1000, pBg * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60, pBlit * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60, pPresent * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60, pMask * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60, pTiles * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60, pFoot * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60)); pBg = pBlit = pPresent = pMask = pTiles = pFoot = 0; } catch { } perfN = 0; perfSum = 0; perfDt = 0; }
            }
            else if (moving)
            {
                // Only the fade is running: reuse the last frame and just change the window's opacity.
                if (surface == null) Render();
                else PresentFrame(Rectangle.Empty);
            }
            if (!moving && !closing && Environment.TickCount - lastTrimTick > 700) { lastTrimTick = Environment.TickCount; MemTrim.Now(false); }
            if (moving && animN < animDt.Length) animDt[animN++] = dt;
            if (!moving && animN > 0) FlushAnimLog();
            if (moving) Kick();
            else { if (vsync.Active) vsync.Sleep(); timer.Interval = 50; if (!timer.Enabled) timer.Start(); }
        }

        // ---------- drawing ----------

        void Render()
        {
            if (!IsHandleCreated || disposedFlag) return;
            if (tRend == 0) tRend = System.Diagnostics.Stopwatch.GetTimestamp(); nRender++;
            if (surface == null) surface = new Surface(Width, Height);

            // While only the tile grid moves (scrolling, hover, icons arriving) the header, search box and background
            // are already on the surface: redraw just the content band and tell Windows only that band changed.
            Rectangle band = page == Page.Apps ? PanelClip : ContentArea;
            if (page != Page.Apps) band = new Rectangle(0, band.Top - 10, Width, band.Height + 20);
            int key;
            unchecked
            {
                key = ((int)page * 31 + segmentHover) * 31 + (search != null ? search.GetHashCode() : 0);
                key = key * 31 + Width * 7 + Height + (openFolder != null ? openFolder.GetHashCode() : 0) + (tiles.Count == 0 ? 1 : 0) + (int)dropKind * 101 + LaunchpadOrder.Mode.GetHashCode() + category.GetHashCode() * 13 + chipHover * 7 + chipScroll * 3;
            }
            bool partial = page == Page.Apps && openFolder == null && tiles.Count > 0 && !closing && key == lastStaticKey;
            lastStaticKey = key;
            if (partial)
            {
                using (Graphics g = surface.BeginRegion(band))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    Typography.Prepare(g);
                    g.SetClip(band);
                    long d0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    DrawTiles(g);
                    pTiles += System.Diagnostics.Stopwatch.GetTimestamp() - d0; d0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    g.SetClip(band); // DrawTiles resets the clip; the footer text sits inside the band
                    DrawFooter(g);
                    pFoot += System.Diagnostics.Stopwatch.GetTimestamp() - d0;
                }
                long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
                PresentFrame(band);
                pPresent += System.Diagnostics.Stopwatch.GetTimestamp() - q1;
                return;
            }

            using (Graphics g = surface.Begin())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                Typography.Prepare(g);

                using (var dim = new SolidBrush(page == Page.Apps ? Color.FromArgb(2, 0, 0, 0) : Color.FromArgb(204, 6, 8, 14))) // Windows: 80% dim; All Apps: clear (a hair of alpha keeps it clickable), the panel carries the glass
                {
                    g.CompositingMode = CompositingMode.SourceCopy; // overwrite the cleared pixels instead of blending
                    g.FillRectangle(dim, 0, 0, Width, Height);
                    g.CompositingMode = CompositingMode.SourceOver;
                }

                long r1 = System.Diagnostics.Stopwatch.GetTimestamp(); DrawHeader(g); long r2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (page == Page.Windows) DrawCards(g);
                else DrawTiles(g);
                long r3 = System.Diagnostics.Stopwatch.GetTimestamp();
                DrawFooter(g);
                if (rdbg == null) { double Fq = System.Diagnostics.Stopwatch.Frequency / 1000.0; rdbg = string.Format(" pre {0:0.0} header {1:0.0} tiles {2:0.0} footer {3:0.0}", (r1 - tRend) / Fq, (r2 - r1) / Fq, (r3 - r2) / Fq, (System.Diagnostics.Stopwatch.GetTimestamp() - r3) / Fq); }
            }

            PresentFrame(Rectangle.Empty);
            bool appsBlur = page == Page.Apps;
            if (appsBlur != backdropForApps) { backdrop.HideBackdrop(); backdropForApps = appsBlur; }
            if (hold || (appsBlur && (deskBlur != null || pendingShot != null || blurTask != null))) { }
            else if (appsBlur) { Rectangle pr = PanelRect; backdrop.Place(new Rectangle(Left + pr.Left, Top + pr.Top, pr.Width, pr.Height), 28, Handle); }
            else backdrop.Place(Bounds, 0, Handle);
        }
        // One UpdateLayeredWindow per frame. While animating, the frame is drawn scaled (anchored bottom-centre, where the
        // dock is) into a scratch surface; at rest it goes straight out.
        Rectangle caretRect, caretBox; float caretCx; Bitmap caretPatch;
        static float CaretPulse() { double t = (Environment.TickCount & 0x7fffffff) % 900 / 900.0; return (float)(0.22 + 0.78 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t))); }
        static void DrawCaretMark(Graphics g, Rectangle box, float cx)
        {
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var caret = new Pen(Color.FromArgb((int)(235 * CaretPulse()), 255, 255, 255), 1.8f))
                g.DrawLine(caret, cx, box.Top + box.Height * 0.3f, cx, box.Bottom - box.Height * 0.3f);
            g.SmoothingMode = sm;
        }
        // Repaints just the caret over the saved pill pixels and presents only that rectangle.
        void PulseCaret()
        {
            if (hold || dirty || closing || prog < 1 || page != Page.Apps || openFolder != null || surface == null || caretPatch == null || caretRect.IsEmpty) return;
            try
            {
                using (Graphics g = Graphics.FromImage(surface.Bitmap))
                {
                    g.SetClip(caretRect);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(caretPatch, caretRect.Location);
                    g.CompositingMode = CompositingMode.SourceOver;
                    DrawCaretMark(g, caretBox, caretCx);
                }
                PresentFrame(caretRect);
            }
            catch { }
        }

        void PresentFrame(Rectangle band)
        {
            if (tFirstPres == 0) tFirstPres = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!firstLogged && tTrig != 0 && !closing && prog > 0)
            {
                firstLogged = true; double F = System.Diagnostics.Stopwatch.Frequency / 1000.0; long n = System.Diagnostics.Stopwatch.GetTimestamp();
                if (PerfLog) try { System.IO.File.AppendAllText(@"D:\#AI workflows\Claude work\Windows Apps\MacDock\mac-dock\launchpad_anim.txt", string.Format("{0:s} trigger->first frame {1:0.0} ms (ctor shot+blur {2:0.0}, tiles+layout {3:0.0}, rest {4:0.0}) [tiles {5:0.0}, layout {6:0.0}, blurwait {7:0.0}, show->render {8:0.0}, render {9:0.0}{10} renders {11} firstPres->now {12:0.0} prog {13:0.00}{14}]\r\n", DateTime.Now, (n - tTrig) / F, (tShot - tTrig) / F, (tBuilt - tShot) / F, (n - tBuilt) / F, (tTiles - tShot) / F, (tLay - tTiles) / F, (tBuilt - tLay) / F, (tRend - tBuilt) / F, (n - tRend) / F, rdbg, nRender, (n - tFirstPres) / F, prog, stepLog)); } catch { }
            }
            double e = Eased;
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(FadeEased * 255)));
            if (prog >= 1 || Motion.Reduced) { if (band.IsEmpty) surface.Present(Handle, Left, Top, a); else surface.Present(Handle, Left, Top, a, band); return; }
            // No redraw or scaling: the cached frame only fades and rises a few pixels (position + alpha in one call).
            if (riseMax < 0) riseMax = 0; // Start slides up ~36 px
            surface.Present(Handle, Left, Top + (int)Math.Round((1 - e) * riseMax), a);
        }
        int lastTrimTick;
        string stepLog = ""; int nRender; long tFirstPres; string rdbg; bool backdropForApps;
        Bitmap glassComp;
        Bitmap deskBlur; // the desktop, blurred once when the overview opens (same glass as Control Center)
        static readonly Color PanelTint = Color.FromArgb(96, 36, 38, 48);

        Bitmap pendingShot; Bitmap glassPre; System.Threading.Tasks.Task<Bitmap> blurTask; static Bitmap sGrid; static int sGridStamp; static HashSet<string> sGridIcons;
        static Bitmap ComposeGlass(Bitmap blur, Rectangle? region = null)
        {
            var c = new Bitmap(blur.Width, blur.Height, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(c))
            {
                if (region.HasValue) cg.SetClip(region.Value);
                cg.CompositingMode = CompositingMode.SourceCopy; cg.DrawImage(blur, 0, 0, blur.Width, blur.Height);
                cg.CompositingMode = CompositingMode.SourceOver;
                using (var tint = new SolidBrush(PanelTint)) cg.FillRectangle(tint, 0, 0, c.Width, c.Height);
            }
            return c;
        }
        void EnsureGlassComp()
        {
            if (glassComp != null && glassComp.Width == deskBlur.Width && glassComp.Height == deskBlur.Height) return;
            if (glassComp != null) glassComp.Dispose();
            glassComp = new Bitmap(deskBlur.Width, deskBlur.Height, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(glassComp))
            {
                cg.CompositingMode = CompositingMode.SourceCopy; cg.DrawImage(deskBlur, 0, 0, deskBlur.Width, deskBlur.Height);
                cg.CompositingMode = CompositingMode.SourceOver;
                using (var tint = new SolidBrush(PanelTint)) cg.FillRectangle(tint, 0, 0, glassComp.Width, glassComp.Height);
            }
        }

        // One pass per scroll frame: result = cached tiles over the glass, written straight into the window's surface.
        // Replaces "copy glass, then GDI+ alpha-blend the tile cache" (~3.4 ms) with a single multi-threaded integer loop.
        unsafe static void BlendGrid(Bitmap dst, Rectangle dstRect, Bitmap cache, int cacheY, Bitmap glass)
        {
            var rd = new Rectangle(dstRect.Left, dstRect.Top, dstRect.Width, dstRect.Height);
            var rc = new Rectangle(dstRect.Left, cacheY, dstRect.Width, dstRect.Height);
            BitmapData dd = dst.LockBits(rd, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            BitmapData cd = cache.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            BitmapData gd = glass.LockBits(rd, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte* d0 = (byte*)dd.Scan0, c0 = (byte*)cd.Scan0, g0 = (byte*)gd.Scan0;
                int ds = dd.Stride, cs = cd.Stride, gs = gd.Stride, w = dstRect.Width, h = dstRect.Height;
                IntPtr pd = (IntPtr)d0, pc = (IntPtr)c0, pg = (IntPtr)g0;
                int chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
                System.Threading.Tasks.Parallel.For(0, chunks, k =>
                {
                    int ya = h * k / chunks, yb = h * (k + 1) / chunks;
                    for (int y = ya; y < yb; y++)
                    {
                        uint* d = (uint*)((byte*)pd + (long)y * ds);
                        uint* c = (uint*)((byte*)pc + (long)y * cs);
                        uint* gl = (uint*)((byte*)pg + (long)y * gs);
                        for (int x = 0; x < w; x++)
                        {
                            uint s = c[x], sa = s >> 24;
                            if (sa == 255) { d[x] = s; continue; }
                            uint g = gl[x];
                            if (sa == 0) { d[x] = g; continue; }
                            uint inv = 255 - sa;
                            uint rb = (g & 0x00FF00FFu) * inv + 0x00800080u; rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
                            uint ag = ((g >> 8) & 0x00FF00FFu) * inv + 0x00800080u; ag = (ag + ((ag >> 8) & 0x00FF00FFu)) & 0xFF00FF00u;
                            d[x] = s + (rb | ag);
                        }
                    }
                });
            }
            finally { dst.UnlockBits(dd); cache.UnlockBits(cd); glass.UnlockBits(gd); }
        }

        void DrawPanelBg(Graphics g, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Width, Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.CompositingMode = CompositingMode.SourceCopy;
            if (deskBlur != null)
            {
                EnsureGlassComp();
                g.DrawImage(glassComp, r, r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel); // glass + tint pre-blended: one copy per frame
            }
            else using (var b = new SolidBrush(Color.FromArgb(235, 30, 32, 40))) g.FillRectangle(b, r);
            g.CompositingMode = CompositingMode.SourceOver;
            g.Restore(st);
        }


        // The grid cache is square-cornered; cut its bottom corners back to the panel's rounded shape and redraw the rim.
        void MaskPanelCorners(Graphics g)
        {
            Rectangle pn = PanelRect;
            using (var path = new GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate))
            {
                path.AddRectangle(pn);
                path.AddPath(Layered.RoundedRect(pn, 28), false);
                GraphicsState st = g.Save();
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // only the bottom corners differ from the square panel; keep the AA fill off the rest
                using (var corners = new Region(new Rectangle(pn.Left - 1, pn.Bottom - 30, 32, 31)))
                {
                    corners.Union(new Rectangle(pn.Right - 31, pn.Bottom - 30, 32, 31));
                    g.IntersectClip(corners);
                    using (var clear = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillPath(clear, path);
                }
                g.Restore(st);
            }
            using (GraphicsPath rp = Layered.RoundedRect(pn, 28))
            using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255)))
                g.DrawPath(rim, rp);
        }

        void DrawHeader(Graphics g)
        {
            if (page == Page.Apps)
            {
                Rectangle pn = PanelRect;
                for (int k = 6; k >= 1; k--) // soft shadow
                {
                    Rectangle sr = Rectangle.Inflate(pn, k * 3, k * 3); sr.Offset(0, k * 3);
                    using (GraphicsPath sp = Layered.RoundedRect(sr, 28 + k * 3))
                    using (var sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }
                GlassPanel.DrawLaunchpadGlass(g, pn, 28, deskBlur, Point.Empty);
            }

            if (page == Page.Apps && openFolder == null)
            {
                // Category chips: a horizontally scrolling row of pills.
                GraphicsState st = g.Save();
                g.SetClip(ChipRow);
                for (int i = 0; i < chipRects.Count; i++)
                {
                    Rectangle r = ChipScreen(i);
                    bool on = Categories[i] == category;
                    using (GraphicsPath cp = Layered.RoundedRect(r, 17))
                    using (var fill = new SolidBrush(on ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(i == chipHover ? 58 : 34, 255, 255, 255)))
                    using (var rim = new Pen(Color.FromArgb(on ? 0 : 40, 255, 255, 255)))
                    using (var text = new SolidBrush(on ? Color.FromArgb(255, 20, 22, 30) : Color.FromArgb(235, 255, 255, 255)))
                    {
                        g.FillPath(fill, cp);
                        if (!on) g.DrawPath(rim, cp);
                        g.DrawString(Categories[i], titleFont, text, r, Typography.Format(StringAlignment.Center));
                    }
                }
                g.Restore(st);

                // Floating search pill: wide dark glass, "A" glyph, large light placeholder, blinking caret, "..." button.
                Rectangle box = PillRect;
                using (GraphicsPath sh = Layered.RoundedRect(Rectangle.Inflate(box, 3, 6), (Rectangle.Inflate(box, 3, 6).Height) / 2))
                using (var shb = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                    g.FillPath(shb, sh);
                using (GraphicsPath p = Layered.RoundedRect(box, box.Height / 2))
                {
                    if (deskBlur != null)
                        using (var tex = new TextureBrush(deskBlur, WrapMode.Clamp)) g.FillPath(tex, p);
                    int a0 = deskBlur != null ? 168 : 235;
                    using (var grad = new LinearGradientBrush(box, Color.FromArgb(a0, 34, 34, 40), Color.FromArgb(a0, 4, 4, 8), LinearGradientMode.Vertical))
                        g.FillPath(grad, p);
                    using (var sheen = new LinearGradientBrush(box, Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        g.FillPath(sheen, p);
                    var rr = new RectangleF(box.Left + 0.5f, box.Top + 0.5f, box.Width - 1f, box.Height - 1f);
                    SmoothingMode rimSm = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath rp = Layered.RoundedRect(rr, rr.Height / 2f))
                    using (var rim = new Pen(Color.FromArgb(95, 255, 255, 255), 1f))
                        g.DrawPath(rim, rp);
                    g.SmoothingMode = rimSm;
                }
                float gs = box.Height * 0.34f / 440f, gx = box.Left + box.Height * 0.66f, gy = box.Top + box.Height / 2f;
                using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 72f * gs) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, gx - 207 * gs, gy + 220 * gs, gx + 48 * gs, gy - 220 * gs);   // left leg
                    g.DrawLine(pen, gx + 213 * gs, gy + 220 * gs, gx - 52 * gs, gy - 220 * gs);   // right leg
                    g.DrawLine(pen, gx - 262 * gs, gy + 105 * gs, gx + 263 * gs, gy + 105 * gs);  // crossbar
                }
                bool empty = search.Length == 0;
                int tx = box.Left + (int)(box.Height * 1.33f);
                Rectangle tr = new Rectangle(tx, box.Top, SortRect.Left - (int)(box.Height * 0.12f) - tx, box.Height);
                using (var text = new SolidBrush(empty ? Color.FromArgb(190, 255, 255, 255) : Color.White))
                    g.DrawString(empty ? "Applications" : search, pillFont, text, tr, Typography.Format());
                float cx = tx + (empty ? 0 : g.MeasureString(search, pillFont, 900, StringFormat.GenericTypographic).Width + 2);
                {
                    float chh = box.Height * 0.4f;
                    caretBox = box; caretCx = cx;
                    caretRect = Rectangle.Intersect(new Rectangle((int)cx - 6, (int)(box.Top + (box.Height - chh) / 2f) - 4, 12, (int)chh + 8), new Rectangle(0, 0, surface.Width, surface.Height));
                    try { if (caretPatch != null) caretPatch.Dispose(); caretPatch = caretRect.Width > 0 && caretRect.Height > 0 ? surface.Bitmap.Clone(caretRect, PixelFormat.Format32bppPArgb) : null; } catch { caretPatch = null; }
                    DrawCaretMark(g, caretBox, caretCx);
                }
                Rectangle sort = SortRect;
                using (var fill = new SolidBrush(Color.FromArgb(LaunchpadOrder.Mode == "custom" ? 70 : 40, 255, 255, 255)))
                using (var ink = new Pen(Color.FromArgb(200, 255, 255, 255), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    SmoothingMode oldSm = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (LaunchpadOrder.Mode == "custom") g.FillEllipse(fill, sort);
                    float sx = sort.Left + 16f, sy = sort.Top + 16f;
                    // Sort glyph: three lines shrinking downward with an up/down arrow pair beside them.
                    g.DrawLine(ink, sx - 9f, sy - 5f, sx + 1f, sy - 5f);
                    g.DrawLine(ink, sx - 9f, sy,      sx - 2f, sy);
                    g.DrawLine(ink, sx - 9f, sy + 5f, sx - 5f, sy + 5f);
                    g.DrawLine(ink, sx + 6f, sy - 5f, sx + 6f, sy + 5f);
                    g.DrawLine(ink, sx + 3.5f, sy + 2.5f, sx + 6f, sy + 5f);
                    g.DrawLine(ink, sx + 8.5f, sy + 2.5f, sx + 6f, sy + 5f);
                    g.SmoothingMode = oldSm;
                }
            }
        }

        void DrawFooter(Graphics g)
        {
            if (page == Page.Apps) return;
            string hint = cards.Count == 0 ? "No open windows" : "Click a window to bring it forward. Type to search your apps.";
            using (var b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.DrawString(hint, smallFont, b, new Rectangle(0, Height - DockClearance - 28, Width, 24), Typography.Format(StringAlignment.Center));
        }

        void DrawCards(Graphics g)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                Card c = cards[i];
                bool hot = i == hover || i == selected;

                using (GraphicsPath path = Layered.RoundedRect(c.Rect, 10))
                {
                    // Soft shadow under the thumbnail.
                    for (int k = 4; k >= 1; k--)
                    {
                        Rectangle s = Rectangle.Inflate(c.Rect, k * 2, k * 2);
                        s.Offset(0, k * 2);
                        using (GraphicsPath sp = Layered.RoundedRect(s, 10 + k * 2))
                        using (var sb = new SolidBrush(Color.FromArgb(10, 0, 0, 0)))
                            g.FillPath(sb, sp);
                    }

                    if (c.Thumb != null)
                    {
                        g.SetClip(path);
                        g.DrawImage(c.Thumb, c.Rect);
                        g.ResetClip();
                    }
                    else
                    {
                        using (var plate = new SolidBrush(Color.FromArgb(c.Minimized ? 60 : 36, 255, 255, 255)))
                            g.FillPath(plate, path);
                        Bitmap icon = ExeIcon(c.Exe);
                        if (icon != null)
                        {
                            int size = Math.Min(Math.Min(c.Rect.Width, c.Rect.Height) - 24, 96);
                            if (size > 8)
                                g.DrawImage(icon, c.Rect.Left + (c.Rect.Width - size) / 2, c.Rect.Top + (c.Rect.Height - size) / 2, size, size);
                        }
                    }

                    using (var rim = new Pen(hot ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(70, 255, 255, 255), hot ? 3f : 1f))
                        g.DrawPath(rim, path);
                }

                // Label: app icon and window title.
                Bitmap small = ExeIcon(c.Exe);
                int labelY = c.Rect.Bottom + 12;
                string title = string.IsNullOrEmpty(c.Title) ? System.IO.Path.GetFileNameWithoutExtension(c.Exe) : c.Title;
                float textWidth = Math.Min(g.MeasureString(title, titleFont, 600, StringFormat.GenericTypographic).Width, Math.Max(40, c.Rect.Width - 30));
                float total = textWidth + (small != null ? 26 : 0);
                float x = c.Rect.Left + (c.Rect.Width - total) / 2f;
                if (small != null) g.DrawImage(small, x, labelY, 20, 20);
                using (var b = new SolidBrush(Color.White))
                    g.DrawString(title, titleFont, b, new RectangleF(x + (small != null ? 26 : 0), labelY - 1, textWidth + 2, 22), Typography.Format());

                if (hot) DrawRoundButton(g, c.CloseRect, Color.FromArgb(40, 42, 52), "x", hoverAction && i == hover);
            }
        }

        void DrawTiles(Graphics g)
        {
            Rectangle area = ContentArea;
            if (openFolder != null) DrawFolderPanel(g);
            g.SetClip(PanelClip);
            int offset = (int)Math.Round(scroll.Value);

            // The grid (icons and names) is painted once into a tall cached bitmap; a scroll frame is then one blit.
            EnsureGridCache(area);

            if (gridCache != null)
            {
                // A straight 1:1 copy of just the visible rows. The cache has the page background baked in, so this
                // is a plain overwrite (no per-pixel blending), the cheapest way to move that many pixels.
                InterpolationMode oldInterp = g.InterpolationMode;
                PixelOffsetMode oldPixel = g.PixelOffsetMode;
                CompositingQuality oldQuality = g.CompositingQuality;
                long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int fRow = Math.Max(0, offset - 10);
                int fShown = Math.Min(gridCache.Height - fRow, area.Height + 20 - (fRow - (offset - 10)));
                Rectangle pc2 = PanelClip; pc2.Intersect(new Rectangle(0, 0, Width, Height));
                int fy0 = Math.Max(area.Top - offset + fRow, pc2.Top), fy1 = Math.Min(area.Top - offset + fRow + fShown, pc2.Bottom);
                if (deskBlur != null && surface != null && fShown > 0 && fy1 > fy0 && pc2.Width > 0 && gridCache.Width >= pc2.Right)
                {
                    EnsureGlassComp();
                    if (glassComp.Width < pc2.Right || glassComp.Height < fy1) goto fallback;
                    if (fy0 > pc2.Top) DrawPanelBg(g, new Rectangle(pc2.Left, pc2.Top, pc2.Width, fy0 - pc2.Top));
                    if (fy1 < pc2.Bottom) DrawPanelBg(g, new Rectangle(pc2.Left, fy1, pc2.Width, pc2.Bottom - fy1));
                    BlendGrid(surface.Bitmap, new Rectangle(pc2.Left, fy0, pc2.Width, fy1 - fy0), gridCache, fy0 - (area.Top - offset + fRow) + fRow, glassComp);
                    pBlit += System.Diagnostics.Stopwatch.GetTimestamp() - q0;
                    goto blitDone;
                }
            fallback:
                DrawPanelBg(g, PanelClip);
                pBg += System.Diagnostics.Stopwatch.GetTimestamp() - q0; q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                int firstRow = Math.Max(0, offset - 10);
                int rowsShown = Math.Min(gridCache.Height - firstRow, area.Height + 20 - (firstRow - (offset - 10)));
                if (rowsShown > 0)
                {
                    Rectangle pcl = PanelClip; // only the panel's columns are visible; don't blend the rest of the screen width
                    g.DrawImage(gridCache, new Rectangle(pcl.Left, area.Top - offset + firstRow, pcl.Width, rowsShown), pcl.Left, firstRow, pcl.Width, rowsShown, GraphicsUnit.Pixel);
                }
                pBlit += System.Diagnostics.Stopwatch.GetTimestamp() - q0;
            blitDone:
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = oldInterp;
                g.PixelOffsetMode = oldPixel;
                g.CompositingQuality = oldQuality;
            }

            // The hovered or selected tile is repainted live: clear its cell, draw the highlight, then the tile again.
            for (int i = 0; i < tiles.Count; i++)
            {
                if (i != hover && i != selected) continue;
                Tile t = tiles[i];
                Rectangle cell = t.Rect, icon = t.IconRect;
                cell.Offset(0, -offset);
                icon.Offset(0, -offset);
                if (cell.Bottom < area.Top - 20 || cell.Top > area.Bottom + 20) continue;
                GraphicsState state = g.Save();
                g.IntersectClip(cell); // nothing of the repaint may spill outside the cell the cache painted
                DrawPanelBg(g, cell);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Layered.RoundedRect(Rectangle.Inflate(cell, -6, -2), 16))
                using (var fill = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                    g.FillPath(fill, p);
                using (StringFormat nf = Typography.Format(StringAlignment.Center))
                using (var white = new SolidBrush(Color.White))
                using (var placeholder = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                {
                    nf.LineAlignment = StringAlignment.Near;
                    PaintTile(g, t, cell, icon, nf, white, placeholder);
                }
                g.Restore(state);
            }

            for (int i = 0; i < tiles.Count; i++)
            {
                if (i != hover && i != selected) continue;
                Tile t = tiles[i];
                if (t.Folder != null) continue;
                Rectangle add = t.AddRect;
                add.Offset(0, -offset);
                if (add.Bottom < area.Top - 20 || add.Top > area.Bottom + 20) continue;
                bool pinned = IsPinned(t);
                DrawRoundButton(g, add, pinned ? Color.FromArgb(52, 199, 89) : Color.FromArgb(10, 132, 255), pinned ? "check" : "+", hoverAction && i == hover);
            }
            DrawDragOverlay(g, offset);
            g.SetClip(PanelClip);
            long m0 = System.Diagnostics.Stopwatch.GetTimestamp();
            MaskPanelCorners(g);
            pMask += System.Diagnostics.Stopwatch.GetTimestamp() - m0;
            g.ResetClip();

            if (tiles.Count == 0)
            {
                string message = allTiles.Count == 0 ? "Loading apps..." : "No apps match \"" + search + "\"";
                using (var b = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                    g.DrawString(message, titleFont, b, new Rectangle(0, area.Top + 40, Width, 30), Typography.Format(StringAlignment.Center));
            }
        }

        int lastStaticKey = int.MinValue; // identifies what the header, search box and background currently show
        Bitmap gridCache;
        int gridStamp;
        readonly HashSet<Tile> gridHasIcon = new HashSet<Tile>(); // tiles painted into the cache with their real icon

        // Keeps the cached grid current. Structure changes (tiles, names, layout, folder contents) rebuild it; an icon
        // arriving later only repaints that one tile, a few per frame, so loading icons never costs a full redraw.
        void EnsureGridCache(Rectangle area)
        {
            int stamp = tiles.Count * 31 + Width;
            unchecked
            {
                foreach (Tile t in tiles)
                {
                    stamp = stamp * 31 + (t.Shown != null ? t.Shown.GetHashCode() : 0);
                    stamp = stamp * 31 + t.Rect.X * 7 + t.Rect.Y;
                    if (t.Folder != null)
                    {
                        int loaded = 0;
                        foreach (Tile m in t.Members)
                        {
                            if (m.Icon == null) lock (tileIconCache) tileIconCache.TryGetValue(m.Target, out m.Icon);
                            if (m.Icon != null) loaded++;
                        }
                        stamp = stamp * 31 + t.Members.Count * 17 + loaded;
                    }
                }
            }

            if (gridCache == null && sGrid != null && stamp == sGridStamp && sGrid.Width == Width)
            {
                gridCache = sGrid; sGrid = null; gridStamp = stamp; gridHasIcon.Clear();
                foreach (Tile t in tiles) { if (t.Folder == null && t.Target != null && sGridIcons != null && sGridIcons.Contains(t.Target)) { if (t.Icon == null) lock (tileIconCache) tileIconCache.TryGetValue(t.Target, out t.Icon); if (t.Icon != null) gridHasIcon.Add(t); } }
            }
            if (gridCache == null || stamp != gridStamp)
            {
                gridStamp = stamp;
                int bottom = area.Top;
                foreach (Tile t in tiles) bottom = Math.Max(bottom, t.Rect.Bottom);
                int height = Math.Max(1, Math.Min(16000, bottom - area.Top + 24));
                if (gridCache != null) gridCache.Dispose();
                gridCache = new Bitmap(Width, height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(gridCache)) g.Clear(GridBackground); // baked in: a scroll frame is a plain copy
                gridHasIcon.Clear();
                PaintIntoCache(tiles, area, false);
                return;
            }

            List<Tile> arrived = null;
            foreach (Tile t in tiles)
            {
                if (t.Folder != null) continue;
                if (t.Icon == null) lock (tileIconCache) tileIconCache.TryGetValue(t.Target, out t.Icon);
                if (t.Icon == null || gridHasIcon.Contains(t)) continue;
                if (t.PinRow) { gridStamp = unchecked(gridStamp + 1); dirty = true; continue; } // sits on the row's backdrop: rebuild
                if (arrived == null) arrived = new List<Tile>();
                arrived.Add(t);
                if (arrived.Count >= 16) { dirty = true; break; } // the rest next frame
            }
            if (arrived != null) PaintIntoCache(arrived, area, true);
        }

        void PaintIntoCache(IEnumerable<Tile> list, Rectangle area, bool clearCell)
        {
            using (Graphics g = Graphics.FromImage(gridCache))
            using (StringFormat nameFormat = Typography.Format(StringAlignment.Center))
            using (var white = new SolidBrush(Color.White))
            using (var placeholder = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
            using (var bg = new SolidBrush(GridBackground))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                Typography.Prepare(g);
                g.TranslateTransform(0, -area.Top);
                nameFormat.LineAlignment = StringAlignment.Near;

                if (!clearCell && !pinBand.IsEmpty)
                {
                    // A thin divider under the featured row.
                    using (var line = new Pen(Color.FromArgb(52, 255, 255, 255)))
                        g.DrawLine(line, pinBand.Left + 6, pinBand.Bottom + 15, pinBand.Right - 6, pinBand.Bottom + 15);
                }

                foreach (Tile t in list)
                {
                    if (clearCell)
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.SmoothingMode = SmoothingMode.None;
                        g.FillRectangle(bg, t.Rect);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.CompositingMode = CompositingMode.SourceOver;
                    }
                    PaintTile(g, t, t.Rect, t.IconRect, nameFormat, white, placeholder);
                    if (t.Folder == null && t.Icon != null) gridHasIcon.Add(t);
                }
            }
        }
        static readonly Color GridBackground = Color.FromArgb(0, 0, 0, 0); // transparent: the glass is repainted under the tiles every frame
        // // the opaque All Apps page colour

        void PaintTile(Graphics g, Tile t, Rectangle cell, Rectangle icon, StringFormat nameFormat, Brush white, Brush placeholder)
        {
            if (t.Folder != null) DrawFolderIcon(g, t, icon);
            else if (t.Icon != null) g.DrawImage(t.Icon, icon);
            else using (GraphicsPath p = Layered.RoundedRect(icon, 20)) g.FillPath(placeholder, p);

            nameFormat.FormatFlags |= StringFormatFlags.NoWrap;
            nameFormat.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(t.Shown, titleFont, white, new Rectangle(cell.Left + 6, icon.Bottom + 10, cell.Width - 12, 22), nameFormat);
        }

        // A rounded glass tile with the first nine apps of the folder in a 3x3 grid, like macOS.
        void DrawFolderIcon(Graphics g, Tile t, Rectangle icon)
        {
            using (GraphicsPath p = Layered.RoundedRect(icon, 20))
            using (var fill = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            using (var rim = new Pen(Color.FromArgb(60, 255, 255, 255)))
            {
                g.FillPath(fill, p);
                g.DrawPath(rim, p);
            }
            int pad = 9, cell = (icon.Width - pad * 2) / 3;
            for (int k = 0; k < Math.Min(9, t.Members.Count); k++)
            {
                Tile m = t.Members[k];
                if (m.Icon == null) lock (tileIconCache) tileIconCache.TryGetValue(m.Target, out m.Icon);
                if (m.Icon != null) g.DrawImage(m.Icon, icon.Left + pad + (k % 3) * cell + 1, icon.Top + pad + (k / 3) * cell + 1, cell - 2, cell - 2);
            }
        }

        // The title of an open folder (editable by clicking it) and the chip that goes back to the main grid.
        void DrawFolderPanel(Graphics g)
        {
            Rectangle back = FolderBackRect;
            using (GraphicsPath p = Layered.RoundedRect(back, 19))
            using (var fill = new SolidBrush(Color.FromArgb(dropKind == DropKind.Remove ? 90 : 40, 255, 255, 255))) // lit while an app is dragged out
            using (var rim = new Pen(Color.FromArgb(46, 255, 255, 255)))
            using (var text = new SolidBrush(Color.White))
            {
                g.FillPath(fill, p);
                g.DrawPath(rim, p);
                g.DrawString("â€¹  All Apps", titleFont, text, back, Typography.Format(StringAlignment.Center));
            }

            Rectangle name = FolderNameRect;
            if (renaming)
            {
                using (GraphicsPath p = Layered.RoundedRect(name, 10))
                using (var fill = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                    g.FillPath(fill, p);
            }
            using (var b = new SolidBrush(Color.White))
                g.DrawString(renaming ? renameBuffer + "|" : openFolder, searchFont, b, name, Typography.Format(StringAlignment.Center));
        }

        static bool IsPinned(Tile t)
        {
            if (t.Target == null) return false;
            return DockForm.Instance != null && DockForm.Instance.IsPinned(t.Target);
        }

        static void DrawRoundButton(Graphics g, Rectangle r, Color fill, string glyph, bool hot)
        {
            Rectangle body = hot ? Rectangle.Inflate(r, 2, 2) : r;
            using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillEllipse(shadow, body.Left, body.Top + 2, body.Width, body.Height);
            using (var b = new SolidBrush(fill))
                g.FillEllipse(b, body);
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cx = body.Left + body.Width / 2f, cy = body.Top + body.Height / 2f, k = body.Width * 0.2f;
                switch (glyph)
                {
                    case "+":
                        g.DrawLine(pen, cx - k, cy, cx + k, cy);
                        g.DrawLine(pen, cx, cy - k, cx, cy + k);
                        break;
                    case "x":
                        g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                        g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
                        break;
                    default:
                        g.DrawLines(pen, new[] { new PointF(cx - k * 1.1f, cy), new PointF(cx - k * 0.2f, cy + k * 0.9f), new PointF(cx + k * 1.2f, cy - k * 0.8f) });
                        break;
                }
            }
        }

        // ---------- input ----------

        // What is under the cursor: an item index plus whether the cursor is on its small button.
        void HitTest(Point p, out int index, out bool onButton)
        {
            index = -1;
            onButton = false;
            if (page == Page.Windows)
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    Card c = cards[i];
                    bool hot = i == hover || i == selected;
                    if (hot && c.CloseRect.Contains(p)) { index = i; onButton = true; return; }
                    Rectangle area = Rectangle.Inflate(c.Rect, 6, 6);
                    area.Height += 36; // include the label
                    if (area.Contains(p)) { index = i; return; }
                }
            }
            else
            {
                int offset = (int)Math.Round(scroll.Value);
                Rectangle clip = PanelClip;
                if (!clip.Contains(p)) return;
                for (int i = 0; i < tiles.Count; i++)
                {
                    Rectangle add = tiles[i].AddRect; add.Offset(0, -offset);
                    Rectangle cell = tiles[i].Rect; cell.Offset(0, -offset);
                    if (tiles[i].Folder == null && (i == hover || i == selected) && add.Contains(p)) { index = i; onButton = true; return; }
                    if (cell.Contains(p)) { index = i; return; }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (dragTile != null && (e.Button & MouseButtons.Left) != 0)
            {
                if (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 10)
                {
                    Tile t = dragTile;
                    dragTile = null;
                    if (t.IsFile) return;
                    var data = new DataObject();
                    if (t.Folder == null) data.SetData(DockForm.AppDragFormat, t.Name + "\n" + t.Target); // the dock accepts apps
                    else data.SetData("MacDockLaunchpadFolder", t.Folder);
                    dragSource = t;
                    dragPoint = e.Location;
                    dirty = true;
                    Kick();
                    DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
                    dragSource = null; dropTile = null; dropKind = DropKind.None;
                    dirty = true; // the dock or the grid may have changed
                    Kick();
                }
                return;
            }

            int index; bool onButton;
            HitTest(e.Location, out index, out onButton);
            int seg = -1;
            for (int i = 0; i < 2; i++) if (segments[i].Contains(e.Location)) seg = i;
            int ch = ChipAt(e.Location);

            if (index != hover || onButton != hoverAction || seg != segmentHover || ch != chipHover)
            {
                chipHover = ch;
                hover = index;
                hoverAction = onButton;
                segmentHover = seg;
                dirty = true;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || page != Page.Apps) return;

            int index; bool onButton;
            HitTest(e.Location, out index, out onButton);
            if (index >= 0 && !onButton)
            {
                dragTile = tiles[index];
                dragStart = e.Location;
            }
        }

        // ---------- dragging tiles inside the overview ----------

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = dragSource != null ? DragDropEffects.Move : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (dragSource == null) { e.Effect = DragDropEffects.None; return; }

            dragPoint = PointToClient(new Point(e.X, e.Y));
            ComputeDrop(dragPoint);
            e.Effect = dropKind != DropKind.None ? DragDropEffects.Move : DragDropEffects.None;

            // Near the top or bottom of the grid the page scrolls along.
            Rectangle area = ContentArea;
            if (dragPoint.Y < area.Top + 36) scrollGoal = Math.Max(0, scrollGoal - 14);
            else if (dragPoint.Y > area.Bottom - 36) scrollGoal = Math.Min(maxScroll, scrollGoal + 14);
            dirty = true;
            Kick();
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            dropKind = DropKind.None;
            dropTile = null;
            dirty = true;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (dragSource == null) return;
            Point p = PointToClient(new Point(e.X, e.Y));
            ComputeDrop(p);
            Tile src = dragSource, target = dropTile;
            DropKind kind = dropKind;
            dropKind = DropKind.None;
            dropTile = null;

            switch (kind)
            {
                case DropKind.IntoFolder:
                    LaunchpadPins.Remove(src.Target);
                    LaunchpadFolders.Set(src.Target, target.Folder);
                    break;
                case DropKind.MakeFolder:
                    string name = LaunchpadFolders.NewName();
                    LaunchpadPins.Remove(src.Target);
                    LaunchpadFolders.Set(target.Target, name);
                    LaunchpadFolders.Set(src.Target, name);
                    break;
                case DropKind.Pin:
                    if (src.Folder == null) LaunchpadFolders.Set(src.Target, null);
                    LaunchpadPins.Add(PinKey(src));
                    break;
                case DropKind.PinBefore:
                case DropKind.PinAfter:
                    if (!src.PinRow && src.Folder == null) LaunchpadFolders.Set(src.Target, null);
                    LaunchpadPins.Insert(PinKey(src), PinKey(target), kind == DropKind.PinAfter);
                    break;
                case DropKind.Unpin:
                    LaunchpadPins.Remove(PinKey(src));
                    break;
                case DropKind.Remove:
                    LaunchpadFolders.Set(src.Target, null);
                    break;
                case DropKind.Before:
                case DropKind.After:
                    ReorderTiles(src, target, kind == DropKind.After);
                    break;
                default:
                    return;
            }
            selected = -1;
            hover = -1;
            SortAllTiles();
            ApplyFilter();
            LayoutAll();
            dirty = true;
            Kick();
        }

        // Works out what dropping the dragged tile at p would do. Searching shows a flat list, so nothing is droppable then.
        void ComputeDrop(Point p)
        {
            dropKind = DropKind.None;
            dropTile = null;
            Tile src = dragSource;
            if (src == null || page != Page.Apps || search.Length > 0) return;

            int index; bool onButton;
            HitTest(p, out index, out onButton);

            if (openFolder != null && src.Folder == null && string.Equals(LaunchpadFolders.Of(src.Target), openFolder, StringComparison.OrdinalIgnoreCase)
                && (FolderBackRect.Contains(p) || (index < 0 && !FolderNameRect.Contains(p))))
            {
                dropKind = DropKind.Remove; // out of the folder: onto the back chip or empty space
                return;
            }
            int offset = (int)Math.Round(scroll.Value);

            // The Pinned row: dropping into it pins (at the nearest tile edge when over a tile), dragging out of it unpins.
            if (openFolder == null)
            {
                Rectangle band = pinBand;
                band.Offset(0, -offset);
                bool overPinTile = index >= 0 && tiles[index].PinRow && tiles[index] != src;
                if (overPinTile)
                {
                    Tile pt = tiles[index];
                    Rectangle pc = pt.Rect;
                    pc.Offset(0, -offset);
                    dropKind = (p.X - pc.Left) / (double)pc.Width < 0.5 ? DropKind.PinBefore : DropKind.PinAfter;
                    dropTile = pt;
                    return;
                }
                if (!src.PinRow && !pinBand.IsEmpty && band.Contains(p)) { dropKind = DropKind.Pin; return; }
                if (src.PinRow)
                {
                    if (index >= 0 && tiles[index] == src) return;
                    if (band.Contains(p)) return; // still inside its own row
                    if (index < 0 || tiles[index].PinRow || tiles[index].Folder == null && src.Folder != null) { dropKind = DropKind.Unpin; return; }
                }
            }
            if (index < 0 || tiles[index] == src) return;

            Tile t = tiles[index];
            Rectangle cell = t.Rect;
            cell.Offset(0, -offset);
            double fx = (p.X - cell.Left) / (double)cell.Width;
            bool centre = fx >= 0.28 && fx <= 0.72;
            if (src.PinRow && (!centre || src.Folder != null)) { dropKind = DropKind.Unpin; return; } // onto the grid: back to it

            if (centre && openFolder == null && src.Folder == null)
            {
                if (t.Folder != null) { dropKind = DropKind.IntoFolder; dropTile = t; return; }
                dropKind = DropKind.MakeFolder; // an app dropped on another app
                dropTile = t;
                return;
            }
            dropKind = fx < 0.5 ? DropKind.Before : DropKind.After;
            dropTile = t;
        }

        // Moves src (an app, or a whole folder) before or after dst in the user's own order, and switches to Custom.
        void ReorderTiles(Tile src, Tile dst, bool after)
        {
            List<string> order = allTiles.Select(t => t.Target).Where(x => x != null).ToList();
            List<string> moving = src.Folder != null ? src.Members.Select(m => m.Target).ToList() : new List<string> { src.Target };
            order.RemoveAll(x => moving.Contains(x));

            string anchor = dst.Folder != null
                ? (after ? dst.Members.Last(m => order.Contains(m.Target)).Target : dst.Members.First(m => order.Contains(m.Target)).Target)
                : dst.Target;
            int at = order.IndexOf(anchor);
            if (at < 0) at = order.Count; else if (after) at++;
            order.InsertRange(at, moving);
            LaunchpadOrder.SetOrder(order);
            LaunchpadOrder.Mode = "custom";
        }

        // The ghost under the cursor, the drop-target highlight and the insertion bar.
        void DrawDragOverlay(Graphics g, int offset)
        {
            if (dragSource == null) return;

            if (dropTile != null)
            {
                Rectangle cell = dropTile.Rect, icon = dropTile.IconRect;
                cell.Offset(0, -offset);
                icon.Offset(0, -offset);
                if (dropKind == DropKind.IntoFolder || dropKind == DropKind.MakeFolder)
                {
                    using (GraphicsPath ring = Layered.RoundedRect(Rectangle.Inflate(icon, 8, 8), 26))
                    using (var fill = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                    using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 3f))
                    {
                        g.FillPath(fill, ring);
                        g.DrawPath(pen, ring);
                    }
                }
                else if (dropKind == DropKind.Before || dropKind == DropKind.After || dropKind == DropKind.PinBefore || dropKind == DropKind.PinAfter)
                {
                    int x = dropKind == DropKind.Before || dropKind == DropKind.PinBefore ? cell.Left + 3 : cell.Right - 7;
                    using (GraphicsPath bar = Layered.RoundedRect(new Rectangle(x, icon.Top - 4, 4, icon.Height + 8), 2))
                    using (var b = new SolidBrush(Color.White))
                        g.FillPath(b, bar);
                }
            }

            if (dropKind == DropKind.Pin && !pinBand.IsEmpty)
            {
                Rectangle band = pinBand;
                band.Offset(0, -offset);
                using (GraphicsPath ring = Layered.RoundedRect(band, 24))
                using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 3f))
                    g.DrawPath(pen, ring);
            }

            Rectangle ghost = new Rectangle(dragPoint.X - TileIcon / 2, dragPoint.Y - TileIcon / 2, TileIcon, TileIcon);
            if (dragSource.Folder != null) DrawFolderIcon(g, dragSource, ghost);
            else if (dragSource.Icon != null) g.DrawImage(dragSource.Icon, ghost, 0, 0, dragSource.Icon.Width, dragSource.Icon.Height, GraphicsUnit.Pixel, ghostAttr);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragTile = null;

            for (int i = 0; i < 2; i++)
            {
                if (e.Button == MouseButtons.Left && segments[i].Contains(e.Location))
                {
                    Page wanted = i == 0 ? Page.Windows : Page.Apps;
                    if (page != wanted) SwitchPage(wanted);
                    return;
                }
            }

            int chip = e.Button == MouseButtons.Left ? ChipAt(e.Location) : -1;
            if (chip >= 0)
            {
                category = Categories[chip];
                search = "";
                selected = -1; hover = -1;
                scroll = new Spring(0); scrollGoal = 0;
                ApplyFilter(); LayoutAll(); dirty = true; Kick();
                return;
            }

            if (e.Button == MouseButtons.Left && page == Page.Apps && openFolder == null && SortRect.Contains(e.Location))
            {
                ShowSortMenu();
                return;
            }

            int index; bool onButton;
            HitTest(e.Location, out index, out onButton);

            if (e.Button == MouseButtons.Right)
            {
                if (page == Page.Apps && index >= 0 && !tiles[index].IsFile) ShowTileMenu(tiles[index], e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (renaming && !FolderNameRect.Contains(e.Location)) CommitRename();

            if (index < 0)
            {
                if (page == Page.Windows) { BeginClose(); return; } // a click on empty space dismisses Mission Control

                // All Apps closes from a click outside the panel and search pill, or from a bottom corner.
                if (openFolder == null && !PanelRect.Contains(e.Location) && !PillRect.Contains(e.Location)) { BeginClose(); return; }
                if (e.Y >= Height - CornerSize && (e.X < CornerSize || e.X >= Width - CornerSize)) { BeginClose(); return; }
                if (openFolder != null)
                {
                    if (FolderNameRect.Contains(e.Location)) { renaming = true; renameBuffer = openFolder; dirty = true; }
                    else ShowFolder(null); // the back chip or any empty space leaves the folder
                }
                return;
            }

            if (page == Page.Windows)
            {
                Card c = cards[index];
                if (onButton)
                {
                    Win32.PostMessage(c.Handle, WinUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    cards.RemoveAt(index);
                    hover = -1;
                    LayoutAll();
                    dirty = true;
                }
                else ActivateCard(c);
            }
            else
            {
                Tile t = tiles[index];
                if (onButton) TogglePin(t);
                else ActivateTile(t);
            }
        }

        const int CornerSize = 96;

        void ActivateTile(Tile t)
        {
            if (t.Folder != null) ShowFolder(t.Folder);
            else LaunchTile(t);
        }

        // Opens a folder in the panel, or returns to the main grid with null.
        void ShowFolder(string name)
        {
            openFolder = name;
            renaming = false;
            selected = -1;
            hover = -1;
            scroll = new Spring(0);
            scrollGoal = 0;
            ApplyFilter();
            LayoutAll();
            dirty = true;
        }

        void CommitRename()
        {
            renaming = false;
            string name = renameBuffer.Trim().Replace('\t', ' ');
            if (name.Length > 0 && openFolder != null && name != openFolder)
            {
                LaunchpadFolders.Rename(openFolder, name);
                LaunchpadPins.Rename("folder:" + openFolder, "folder:" + name);
                openFolder = name;
            }
            ApplyFilter();
            LayoutAll();
            dirty = true;
        }

        // Pins or unpins an app or a folder. An app that is pinned leaves its folder (it lives in the top row).
        void TogglePinRow(Tile t)
        {
            string key = PinKey(t);
            if (LaunchpadPins.Contains(key)) LaunchpadPins.Remove(key);
            else
            {
                if (t.Folder == null) LaunchpadFolders.Set(t.Target, null);
                LaunchpadPins.Add(key);
            }
            selected = -1;
            hover = -1;
            ApplyFilter();
            LayoutAll();
            dirty = true;
            Kick();
        }

        void MoveToFolder(Tile t, string folder)
        {
            LaunchpadPins.Remove(t.Target); // an app in a folder is no longer pinned
            LaunchpadFolders.Set(t.Target, folder);
            selected = -1;
            hover = -1;
            ApplyFilter();
            LayoutAll();
            dirty = true;
        }

        void DissolveFolder(Tile folderTile)
        {
            foreach (Tile m in folderTile.Members) LaunchpadFolders.Set(m.Target, null);
            LaunchpadPins.Remove("folder:" + folderTile.Folder);
            ShowFolder(null);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (page != Page.Apps) return;
            if (openFolder == null && ChipRow.Contains(e.Location))
            {
                chipScroll = Math.Max(0, Math.Min(Math.Max(0, chipTotal - ChipRow.Width), chipScroll - e.Delta / 2));
                dirty = true; Kick();
                return;
            }
            scrollGoal = Math.Max(0, Math.Min(maxScroll, scrollGoal - e.Delta * 0.9));
            Kick();
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (renaming)
            {
                if (e.KeyChar == '\b') { if (renameBuffer.Length > 0) renameBuffer = renameBuffer.Substring(0, renameBuffer.Length - 1); }
                else if (!char.IsControl(e.KeyChar) && renameBuffer.Length < 40) renameBuffer += e.KeyChar;
                dirty = true;
                return;
            }
            if (e.KeyChar == '\b')
            {
                if (search.Length > 0) { search = search.Substring(0, search.Length - 1); RefreshSearch(); }
                return;
            }
            if (char.IsControl(e.KeyChar)) return;

            // Typing anywhere starts an app search.
            if (page != Page.Apps) SwitchPage(Page.Apps);
            openFolder = null; // search covers every app, in or out of folders
            search += e.KeyChar;
            RefreshSearch();
        }

        void RefreshSearch()
        {
            fileTiles = new List<Tile>();
            int seq = ++fileSeq;
            string q = search;
            if (q.Length > 0)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    List<Spotlight.Result> found = Spotlight.SearchFiles(q);
                    var made = new List<Tile>();
                    foreach (Spotlight.Result r in found.Take(24))
                    {
                        Bitmap raw = DockForm.LoadShellIcon(r.Path, 0, 0);
                        made.Add(new Tile { Name = r.Name, Target = r.Path, IsFile = true, Icon = raw == null ? null : DockForm.Tile(raw) });
                    }
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (disposedFlag || seq != fileSeq) return;
                            fileTiles = made;
                            ApplyFilter();
                            if (selected >= tiles.Count) selected = tiles.Count > 0 ? 0 : -1;
                            LayoutAll();
                            dirty = true;
                            Kick();
                        }));
                    }
                    catch { }
                });
            ApplyFilter();
            scroll = new Spring(0);
            scrollGoal = 0;
            selected = tiles.Count > 0 ? 0 : -1;
            LayoutAll();
            dirty = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    if (tileMenu != null) return true; // the menu closes itself on Esc; keep the overview open
                    if (renaming) { renaming = false; dirty = true; }
                    else if (search.Length > 0) { search = ""; RefreshSearch(); }
                    else if (openFolder != null) ShowFolder(null);
                    else BeginClose();
                    return true;
                case Keys.Tab:
                    return true;
                case Keys.Left: MoveSelection(-1, 0); return true;
                case Keys.Right: MoveSelection(1, 0); return true;
                case Keys.Up: MoveSelection(0, -1); return true;
                case Keys.Down: MoveSelection(0, 1); return true;
                case Keys.Enter:
                    if (renaming) CommitRename();
                    else if (page == Page.Windows && selected >= 0 && selected < cards.Count) ActivateCard(cards[selected]);
                    else if (page == Page.Apps && selected >= 0 && selected < tiles.Count) ActivateTile(tiles[selected]);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MoveSelection(int dx, int dy)
        {
            int count = page == Page.Windows ? cards.Count : tiles.Count;
            if (count == 0) return;
            if (selected < 0) selected = 0;
            else selected = Math.Max(0, Math.Min(count - 1, selected + dx + dy * columns));

            if (page == Page.Apps)
            {
                // Keep the selected row in view.
                Rectangle area = ContentArea;
                int rowTop = tiles[selected].Rect.Top - area.Top, rowBottom = rowTop + TileH;
                if (rowTop < scrollGoal) scrollGoal = rowTop;
                else if (rowBottom > scrollGoal + area.Height) scrollGoal = rowBottom - area.Height;
                scrollGoal = Math.Max(0, Math.Min(maxScroll, scrollGoal));
            }
            hover = -1;
            dirty = true;
            Kick();
        }

        // ---------- actions ----------

        void ActivateCard(Card c)
        {
            suppressRestore = true;
            BeginClose();
            WinUtil.Focus(c.Handle);
        }

        void LaunchTile(Tile t)
        {
            suppressRestore = true;
            BeginClose();
            if (t.IsFile)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t.Target) { UseShellExecute = true }); } catch { }
                return;
            }
            Pwa.Launch(t.Target); // a web app that is already open is brought forward instead of opening again
        }

        void TogglePin(Tile t)
        {
            DockForm dock = DockForm.Instance;
            if (dock == null) return;
            if (dock.IsPinned(t.Target)) dock.RemoveApp(t.Target);
            else dock.AddApp(t.Name, t.Target);
            dirty = true;
        }

        void ShowTileMenu(Tile t, Point at)
        {
            if (tileMenu != null) tileMenu.Close();
            var list = new List<MenuEntry>();
            if (t.Folder != null)
            {
                list.Add(MenuEntry.Item("Open Folder", null, () => ShowFolder(t.Folder)));
                list.Add(MenuEntry.Item(LaunchpadPins.Contains(PinKey(t)) ? "Unpin from Top Row" : "Pin to Top Row", null, () => TogglePinRow(t)));
                list.Add(MenuEntry.Item("Rename", null, () => { string n = t.Folder; ShowFolder(n); renaming = true; renameBuffer = n; }));
                list.Add(MenuEntry.Item("Dissolve Folder", null, () => DissolveFolder(t)));
            }
            else
            {
                list.Add(MenuEntry.Item("Open", null, () => LaunchTile(t)));
                IconChoice.AddMenu(list, t.Target, t.Name, this);
                list.Add(MenuEntry.Sep());
                list.Add(MenuEntry.Item(LaunchpadPins.Contains(PinKey(t)) ? "Unpin from Top Row" : "Pin to Top Row", null, () => TogglePinRow(t)));
                bool pinned = IsPinned(t);
                list.Add(MenuEntry.Item(pinned ? "Remove from Dock" : "Add to Dock", null, () => TogglePin(t)));

                // MenuPopup has no submenus, so "Move to Folder" is a header with its choices listed under it.
                list.Add(MenuEntry.Sep());
                list.Add(MenuEntry.Item("Move to Folder", null, null));
                string current = LaunchpadFolders.Of(t.Target);
                foreach (string name in LaunchpadFolders.Names())
                {
                    string folder = name;
                    if (!string.Equals(folder, current, StringComparison.OrdinalIgnoreCase))
                        list.Add(MenuEntry.Item("    " + folder, null, () => MoveToFolder(t, folder)));
                }
                list.Add(MenuEntry.Item("    New Folder", null, () => MoveToFolder(t, LaunchpadFolders.NewName())));
                if (current != null) list.Add(MenuEntry.Item("Remove from Folder", null, () => MoveToFolder(t, null)));

                string family = StoreFamily(t.Target);
                list.Add(MenuEntry.Sep());
                list.Add(MenuEntry.Item("Uninstall", null, () => UninstallTile(t, family)));
            }

            // The same frosted popup as the dock's menu; it closes on Esc or a click outside it.
            MenuPopup menu = null;
            int dockTop = int.MaxValue;
            try { var df = DockForm.Instance; if (df != null && df.Visible && df.Bounds.Top > Screen.FromPoint(PointToScreen(at)).Bounds.Top) dockTop = df.Bounds.Top - 6; } catch { }
            menu = new MenuPopup(list, PointToScreen(at), 0, () =>
            {
                if (tileMenu == menu) tileMenu = null;
                // Closing the popup hands focus to the app underneath, which would bury this ordinary window behind it
                // (it looks like the overview closed). Take focus back once the popup is gone.
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!closing && !IsDisposed && tileMenu == null) { WinUtil.Focus(Handle); Activate(); }
                    }));
                }
                catch { }
            }, 20, dockTop);
            tileMenu = menu;
            menu.Show();
        }

        MenuPopup tileMenu;

        // Name A-Z, Name Z-A or Custom (the dragged order); the choice is remembered and applies to the grid and to folders.
        void ShowSortMenu()
        {
            if (tileMenu != null) tileMenu.Close();
            string mode = LaunchpadOrder.Mode;
            var list = new List<MenuEntry>
            {
                new MenuEntry { Text = "Name Aâ€“Z", Action = () => SetSort("name-asc"), Checked = mode == "name-asc" },
                new MenuEntry { Text = "Name Zâ€“A", Action = () => SetSort("name-desc"), Checked = mode == "name-desc" },
                new MenuEntry { Text = "Newest installed first", Action = () => SetSort("date-new"), Checked = mode == "date-new" },
                new MenuEntry { Text = "Oldest installed first", Action = () => SetSort("date-old"), Checked = mode == "date-old" },
                new MenuEntry { Text = "Custom order", Action = () => SetSort("custom"), Checked = mode == "custom" }
            };
            Rectangle r = SortRect;
            MenuPopup menu = null;
            menu = new MenuPopup(list, PointToScreen(new Point(r.Left, r.Bottom + 6)), 0, () =>
            {
                if (tileMenu == menu) tileMenu = null;
                try { BeginInvoke(new Action(() => { if (!closing && !IsDisposed && tileMenu == null) { WinUtil.Focus(Handle); Activate(); } })); }
                catch { }
            });
            tileMenu = menu;
            menu.Show();
        }

        void SetSort(string mode)
        {
            if (mode == "custom" && LaunchpadOrder.IndexMap().Count == 0)
                LaunchpadOrder.SetOrder(allTiles.Select(t => t.Target).Where(x => x != null).ToList()); // start Custom from what is shown
            LaunchpadOrder.Mode = mode;
            selected = -1;
            hover = -1;
            SortAllTiles();
            ApplyFilter();
            LayoutAll();
            dirty = true;
            Kick();
        }

        // ---------- uninstall ----------

        // The package family of a Store app (its AppsFolder id is Family!AppId), or null for a desktop app.
        static string StoreFamily(string target)
        {
            string id = target.Substring(ShellIcons.AppsFolderPrefix.Length);
            int bang = id.IndexOf('!');
            if (bang <= 0) return null;
            string family = id.Substring(0, bang);
            return System.Text.RegularExpressions.Regex.IsMatch(family, @"^[A-Za-z0-9._-]+$") ? family : null;
        }

        // The uninstall command of a desktop app, found by its display name in the Uninstall registry keys.
        static string FindUninstallCommand(string name)
        {
            List<string> targets = AppDirectory.TargetsFor(name); // the app's Start Menu shortcut target(s), to match by install location
            Func<Microsoft.Win32.RegistryKey, bool> byLocation = app =>
            {
                foreach (string v in new[] { "InstallLocation", "DisplayIcon" })
                {
                    string loc = (app.GetValue(v) as string ?? "").Trim('"');
                    int comma = loc.LastIndexOf(',');
                    if (v == "DisplayIcon" && comma > 0) loc = loc.Substring(0, comma).Trim('"');
                    if (loc.Length < 6) continue;
                    foreach (string tg in targets)
                        if (string.Equals(tg, loc, StringComparison.OrdinalIgnoreCase) || tg.StartsWith(loc.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            };
            string byPath = null;
            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var keys = new[] {
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.LocalMachine, sub),
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                new KeyValuePair<Microsoft.Win32.RegistryKey, string>(Microsoft.Win32.Registry.CurrentUser, sub) };
            string loose = null;
            foreach (var k in keys)
            {
                try
                {
                    using (var root = k.Key.OpenSubKey(k.Value))
                    {
                        if (root == null) continue;
                        foreach (string n in root.GetSubKeyNames())
                            using (var app = root.OpenSubKey(n))
                            {
                                string display = app == null ? null : app.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(display)) continue;
                                if (app.GetValue("SystemComponent") is int && (int)app.GetValue("SystemComponent") == 1) continue;
                                string cmd = (app.GetValue("QuietUninstallString") ?? app.GetValue("UninstallString")) as string;
                                if (string.IsNullOrWhiteSpace(cmd) || !UninstallerExists(cmd)) continue; // a leftover entry whose uninstaller is gone
                                if (string.Equals(display, name, StringComparison.OrdinalIgnoreCase)) return FixMsi(cmd);
                                if (byPath == null && byLocation(app)) byPath = FixMsi(cmd);
                                if (loose == null && Math.Min(display.Length, name.Length) >= 4 &&
                                    (display.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(display, StringComparison.OrdinalIgnoreCase)))
                                    loose = FixMsi(cmd);
                            }
                    }
                }
                catch { }
            }
            return byPath ?? loose ?? FolderUninstaller(targets);
        }

        // Apps like Sideloadly ship an Uninstall.exe / unins000.exe next to the app but register nothing: look in the shortcut target's folder.
        static string FolderUninstaller(List<string> targets)
        {
            string bad = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string tg in targets)
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(tg);
                    if (string.IsNullOrEmpty(dir) || dir.Length < 8 || dir.StartsWith(bad, StringComparison.OrdinalIgnoreCase) || !System.IO.Directory.Exists(dir)) continue;
                    foreach (string pat in new[] { "unins???.exe", "Uninstall*.exe", "uninst*.exe" })
                        foreach (string f in System.IO.Directory.GetFiles(dir, pat))
                            return "\"" + f + "\"";
                }
                catch { }
            }
            return null;
        }

        // False when the command starts with a full path to an executable that is not there.
        static bool UninstallerExists(string cmd)
        {
            string c = cmd.Trim(), exe = null;
            if (c.StartsWith("\"")) { int q = c.IndexOf('"', 1); if (q > 0) exe = c.Substring(1, q - 1); }
            else { int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (e > 0) exe = c.Substring(0, e + 4); }
            if (exe == null || exe.Length < 3 || exe[1] != ':') return true; // msiexec, rundll32 etc.: resolved by Windows
            return System.IO.File.Exists(Environment.ExpandEnvironmentVariables(exe));
        }

        // MSI entries often say "msiexec /I{guid}" (modify); uninstalling needs /X.
        static string FixMsi(string cmd)
        {
            if (cmd.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) < 0) return cmd;
            cmd = System.Text.RegularExpressions.Regex.Replace(cmd, @"/I(?=\s*\{)", "/X", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return cmd + " /passive /norestart";
        }

        void UninstallTile(Tile t, string family)
        {
            string win32Cmd = family == null ? FindUninstallCommand(t.Name) : null;
            if (family == null && win32Cmd == null)
            {
                // Portable app or a bare shortcut: nothing registered to run, and files are never deleted blindly.
                MessageBox.Show(this, "No working uninstaller found for " + t.Name + " (its uninstall entry is missing or points at a deleted file). Nothing was changed.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Uninstall " + t.Name + "?", "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (Environment.GetEnvironmentVariable("MACDOCK_UNINSTALL_DRYRUN") == "1")
            {
                MessageBox.Show(this, "Dry run. Would run:\n" + (family != null ? "Remove-AppxPackage (family " + family + ")" : win32Cmd), "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                string error = null;
                try
                {
                    ProcessStartInfo psi;
                    if (family != null)
                        psi = new ProcessStartInfo("powershell.exe",
                            "-NoProfile -Command \"$p = Get-AppxPackage | ? { $_.PackageFamilyName -eq '" + family + "' -and -not $_.NonRemovable }; if (-not $p) { exit 2 }; $p | Remove-AppxPackage -ErrorAction Stop\"")
                        { UseShellExecute = false, CreateNoWindow = true };
                    else
                    {
                        // Launch the uninstaller itself (not through cmd) so an elevation request surfaces as a UAC prompt.
                        string exe = null, args = "";
                        string c = win32Cmd.Trim();
                        if (c.StartsWith("\""))
                        {
                            int q = c.IndexOf('"', 1);
                            if (q > 0) { exe = c.Substring(1, q - 1); args = c.Substring(q + 1).Trim(); }
                        }
                        else
                        {
                            int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (e > 0) { exe = c.Substring(0, e + 4); args = c.Substring(e + 4).Trim(); }
                        }
                        psi = exe != null
                            ? new ProcessStartInfo(exe, args) { UseShellExecute = true }
                            : new ProcessStartInfo("cmd.exe", "/c \"" + win32Cmd + "\"") { UseShellExecute = true };
                    }

                    Process started;
                    try { started = Process.Start(psi); }
                    catch (System.ComponentModel.Win32Exception wx)
                    {
                        if (wx.NativeErrorCode != 740 || !psi.UseShellExecute) throw;
                        psi.Verb = "runas"; // the uninstaller needs administrator rights: ask through UAC
                        started = Process.Start(psi);
                    }
                    using (Process p = started)
                    {
                        p.WaitForExit();
                        ok = p.ExitCode == 0 || p.ExitCode == 3010;
                        if (!ok) error = p.ExitCode == 2 && family != null ? "Windows does not allow removing this app." : "The uninstaller exited with code " + p.ExitCode + ".";
                    }
                }
                catch (Exception ex) { error = ex.Message; } // includes a declined UAC prompt

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!ok) { MessageBox.Show(this, "Could not uninstall " + t.Name + ". " + error, "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        allTiles.Remove(t);
                        LaunchpadFolders.Set(t.Target, null);
                        ApplyFilter();
                        lock (AppDirectory.Names)
                        {
                            AppDirectory.Apps.RemoveAll(a => ShellIcons.AppsFolderPrefix + a.Value == t.Target);
                            AppDirectory.Names.Remove(t.Target.Substring(ShellIcons.AppsFolderPrefix.Length));
                        }
                        lock (tileIconCache) tileIconCache.Remove(t.Target);
                        LayoutAll();
                        dirty = true;
                    }));
                }
                catch { }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); caretTimer.Dispose(); if (caretPatch != null) { caretPatch.Dispose(); caretPatch = null; }
                if (deskBlur != null) deskBlur.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Which All Apps folder each app is in, kept in launchpad-folders.txt next to the exe ("folder<TAB>app id" per line).
    static class LaunchpadFolders
    {
        static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool loaded;

        static string FilePath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launchpad-folders.txt"); } }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!System.IO.File.Exists(FilePath)) return;
                foreach (string line in System.IO.File.ReadAllLines(FilePath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && tab < line.Length - 1) map[line.Substring(tab + 1)] = line.Substring(0, tab);
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static void Save()
        {
            try { System.IO.File.WriteAllLines(FilePath, map.Select(kv => kv.Value + "\t" + kv.Key).ToArray()); }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static string Of(string target)
        {
            Load();
            string folder;
            return target != null && map.TryGetValue(target, out folder) ? folder : null;
        }

        // Puts an app in a folder (creating it), or takes it out with null. A folder with no apps simply stops existing.
        public static void Set(string target, string folder)
        {
            Load();
            if (target == null) return;
            if (folder == null) { if (!map.Remove(target)) return; }
            else map[target] = folder;
            Save();
        }

        public static List<string> Names()
        {
            Load();
            return map.Values.Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string NewName()
        {
            List<string> names = Names();
            string name = "New Folder";
            for (int n = 2; names.Contains(name, StringComparer.CurrentCultureIgnoreCase); n++) name = "New Folder " + n;
            return name;
        }

        public static void Rename(string from, string to)
        {
            Load();
            foreach (string key in map.Where(kv => string.Equals(kv.Value, from, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                map[key] = to;
            Save();
        }
    }

    // What is pinned to Launchpad's top row, in order: an app's id, or "folder:<name>" for a folder (launchpad-pins.txt).
    static class LaunchpadPins
    {
        static string FilePath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launchpad-pins.txt"); } }

        static bool loaded;
        static List<string> keys = new List<string>();

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try { if (System.IO.File.Exists(FilePath)) keys = System.IO.File.ReadAllLines(FilePath).Where(l => l.Length > 0).ToList(); }
            catch (Exception ex) { Program.Log(ex); }
        }

        static void Save()
        {
            try { System.IO.File.WriteAllLines(FilePath, keys.ToArray()); }
            catch (Exception ex) { Program.Log(ex); }
        }

        static int IndexOf(string key)
        {
            return keys.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> Keys() { Load(); return keys.ToList(); }

        public static bool Contains(string key) { Load(); return key != null && IndexOf(key) >= 0; }

        public static void Add(string key)
        {
            Load();
            if (key == null || IndexOf(key) >= 0) return;
            keys.Add(key);
            Save();
        }

        public static void Remove(string key)
        {
            Load();
            int i = key == null ? -1 : IndexOf(key);
            if (i < 0) return;
            keys.RemoveAt(i);
            Save();
        }

        // Puts key just before or after anchor (moving it if it is already pinned).
        public static void Insert(string key, string anchor, bool after)
        {
            Load();
            if (key == null) return;
            int old = IndexOf(key);
            if (old >= 0) keys.RemoveAt(old);
            int at = anchor == null ? -1 : IndexOf(anchor);
            if (at < 0) keys.Add(key);
            else keys.Insert(after ? at + 1 : at, key);
            Save();
        }

        public static void Rename(string from, string to)
        {
            Load();
            int i = IndexOf(from);
            if (i < 0) return;
            keys[i] = to;
            Save();
        }
    }
    // When each app was installed: creation time of its Start Menu shortcut (earliest if several), else of its Store package
    // folder or program file. Cached for 60 s. Unknown = DateTime.MinValue.
    static class InstallDates
    {
        static Dictionary<string, DateTime> byName = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static DateTime built = DateTime.MinValue;
        static readonly Dictionary<string, DateTime> byTarget = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        static void Build()
        {
            if ((DateTime.Now - built).TotalSeconds < 60 && byName.Count > 0) return;
            built = DateTime.Now;
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string[] dirs = {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs") };
            foreach (string d in dirs)
            {
                try
                {
                    if (!System.IO.Directory.Exists(d)) continue;
                    foreach (string lnk in System.IO.Directory.GetFiles(d, "*.lnk", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            DateTime t = System.IO.File.GetCreationTime(lnk);
                            string n = System.IO.Path.GetFileNameWithoutExtension(lnk);
                            DateTime old;
                            if (!map.TryGetValue(n, out old) || t < old) map[n] = t;
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Program.Log(ex); }
            }
            byName = map;
        }

        public static DateTime For(string name, string target)
        {
            lock (byTarget)
            {
                Build();
                DateTime d;
                if (name != null && byName.TryGetValue(name, out d)) return d;
                if (target != null && byTarget.TryGetValue(target, out d)) return d;
                d = Fallback(target);
                if (target != null) byTarget[target] = d;
                return d;
            }
        }

        // Store app: the AppsFolder id is Family!App, and its package folder under WindowsApps starts with the family's name part.
        static DateTime Fallback(string target)
        {
            try
            {
                if (target == null) return DateTime.MinValue;
                string id = target.Substring(target.IndexOf((char)92) + 1);
                int bang = id.IndexOf('!');
                if (bang > 0)
                {
                    string fam = id.Substring(0, bang);
                    string pre = fam.Contains("_") ? fam.Substring(0, fam.LastIndexOf('_')) : fam;
                    string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                    foreach (string dir in System.IO.Directory.GetDirectories(root, pre + "_*"))
                        return System.IO.Directory.GetCreationTime(dir);
                }
                else if (id.Length > 3 && id[1] == ':' && System.IO.File.Exists(id)) return System.IO.File.GetCreationTime(id);
            }
            catch { }
            return DateTime.MinValue;
        }
    }

    // How Launchpad orders its apps and folders: the sort mode ("name-asc", "name-desc" or "custom") and, for Custom, the
    // dragged order of every app. Kept next to the exe in launchpad-sort.txt and launchpad-order.txt.
    static class LaunchpadOrder
    {
        static string OrderPath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launchpad-order.txt"); } }
        static string SortPath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launchpad-sort.txt"); } }

        static bool loaded;
        static string mode = "name-asc";
        static List<string> order = new List<string>();

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (System.IO.File.Exists(SortPath))
                {
                    string m = System.IO.File.ReadAllText(SortPath).Trim();
                    if (m == "name-asc" || m == "name-desc" || m == "date-new" || m == "date-old" || m == "custom") mode = m;
                }
                if (System.IO.File.Exists(OrderPath)) order = System.IO.File.ReadAllLines(OrderPath).Where(l => l.Length > 0).ToList();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static string Mode
        {
            get { Load(); return mode; }
            set
            {
                Load();
                mode = value;
                try { System.IO.File.WriteAllText(SortPath, mode); } catch (Exception ex) { Program.Log(ex); }
            }
        }

        public static Dictionary<string, int> IndexMap()
        {
            Load();
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) map[order[i]] = i;
            return map;
        }

        public static void SetOrder(List<string> newOrder)
        {
            Load();
            order = newOrder;
            try { System.IO.File.WriteAllLines(OrderPath, order.ToArray()); } catch (Exception ex) { Program.Log(ex); }
        }
    }}
