// MacDock - a lightweight macOS-style dock for Windows.
//
// dock.txt (in %APPDATA%\MacDock) controls what is pinned:
//   - apps (.exe / .lnk), folders, files, websites (https://...), shell:/ms-settings: links, and [start]
//   - "Label | target" gives an item a custom name
//   - a line with --- starts the right-hand section (folders and other things, like the Downloads stack)
//   - "@hide-taskbar off" keeps the Windows taskbar visible
// You can also drag files/folders onto the dock or use the right-click menu to add things.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    static class Program
    {
        static readonly System.Collections.Generic.List<Timer> startupTrims = new System.Collections.Generic.List<Timer>();
        internal static bool QuitRequested; // set only by the deliberate "Quit MacDock" menu item

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        static void Relaunch()
        {
            // Parent the new process to explorer.exe so it is outside the caller's process tree / job object.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Application.ExecutablePath + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Log(ex); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCENTRY32W { public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PROCENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PROCENTRY32W e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Lower-cased parent process name ("" if unknown). The dock must never stay a child of Claude / a terminal / its job object.
        static string ParentName()
        {
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return "";
            try
            {
                var e = new PROCENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCENTRY32W)) };
                uint me = (uint)Process.GetCurrentProcess().Id, ppid = 0;
                var names = new Dictionary<uint, string>();
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    names[e.th32ProcessID] = e.szExeFile;
                    if (e.th32ProcessID == me) ppid = e.th32ParentProcessID;
                }
                string n; return names.TryGetValue(ppid, out n) ? n.ToLowerInvariant() : "";
            }
            finally { CloseHandle(snap); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO { public int cb; public string r, d, t; public int x, y, xs, ys, xc, yc, fa, fl; public short sw, cr2; public IntPtr r2, i, o, e; }
        [StructLayout(LayoutKind.Sequential)]
        struct PROCINFO { public IntPtr hP, hT; public int pid, tid; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCINFO pi);

        // CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS | CREATE_NO_WINDOW-free; fails harmlessly if the job forbids breakaway.
        static bool SpawnDetached(string exe)
        {
            try
            {
                var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCINFO pi;
                if (!CreateProcessW(null, "\"" + exe + "\" --detached", IntPtr.Zero, IntPtr.Zero, false, 0x01000000 | 0x200 | 0x8, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi)) return false;
                CloseHandle(pi.hP); CloseHandle(pi.hT);
                return true;
            }
            catch { return false; }
        }

        // True once another instance holds the single-instance mutex (waits up to 5 s).
        static bool DetachedUp()
        {
            for (int i = 0; i < 25; i++)
            {
                try { System.Threading.Mutex.OpenExisting("MacDockSingleInstance").Dispose(); return true; } catch { }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Safety net: "MacDock.exe /restore" brings the Windows taskbar back if the dock was killed while it was hidden.
            if (args.Any(a => a.Equals("/restore", StringComparison.OrdinalIgnoreCase)))
            {
                TaskbarControl.ForceRestore();
                return;
            }

            int test = Array.FindIndex(args, a => a.Equals("/wt-selftest", StringComparison.OrdinalIgnoreCase));
            if (test >= 0 && test + 1 < args.Length) { Environment.ExitCode = WindowThemeTest.Run(args[test + 1]); return; }

            { int sb = Array.FindIndex(args, a => a.Equals("--scroll-bench", StringComparison.OrdinalIgnoreCase)); if (sb >= 0 && sb + 1 < args.Length) { Environment.ExitCode = SettingsForm.ScrollBench(args[sb + 1]); return; } }

            // "MacDock.exe --send <file>" (Explorer right-click verb): queue the file for the LocalSend panel in the running dock.
            if (args.Length >= 2 && args[0].Equals("--send", StringComparison.OrdinalIgnoreCase)) { LocalSend.QueueSend(args.Skip(1).ToArray()); return; }

            // "MacDock.exe --quit" asks the running instance to exit for good (used for rebuilds).
            if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase) || a.Equals("/quit", StringComparison.OrdinalIgnoreCase)))
            {
                try { System.Threading.EventWaitHandle.OpenExisting("MacDockQuitEvent").Set(); } catch { }
                return;
            }

            if (args.Any(a => a.Equals("--ls-selftest", StringComparison.OrdinalIgnoreCase))) { Environment.ExitCode = LocalSendTest.Run(args); return; }

            // "--wait" (used by Restart): give the previous instance a moment to exit before taking the single-instance lock.
            if (args.Any(a => a.Equals("--wait", StringComparison.OrdinalIgnoreCase))) System.Threading.Thread.Sleep(2500);

            // Self-detach: if launched by anything but explorer/services/Task Scheduler (e.g. a Claude session, whose job object is
            // killed when Claude closes), re-launch through explorer.exe and exit, so the dock is never in Claude's process tree.
            if (!args.Any(a => a.Equals("--detached", StringComparison.OrdinalIgnoreCase)))
            {
                string par = ParentName();
                if (par != "" && par != "explorer.exe" && par != "svchost.exe" && par != "taskeng.exe" && par != "taskhostw.exe" && par != "services.exe" && par != "macdock.exe" && par != "winlogon.exe" && par != "userinit.exe")
                {
                    // Only exit once a detached copy is really up (it takes the single-instance mutex); otherwise keep running here.
                    string self = Application.ExecutablePath;
                    if (File.Exists(self))
                    {
                        if (SpawnDetached(self) && DetachedUp()) { Log("DETACH ok via CreateProcess breakaway"); return; }
                        try
                        {
                            // No explorer.exe launch here: from a packaged (Claude) context the per-user AppData path is virtualised, and explorer shows "path does not exist" popups. Ask the existing MacDockGuard task (real path, outside any job) instead.
                            Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN MacDockGuard") { UseShellExecute = false, CreateNoWindow = true });
                            if (DetachedUp()) { Log("DETACH ok via MacDockGuard task"); return; }
                        }
                        catch (Exception ex) { Log(ex); }
                        Log("DETACH failed; running in place");
                    }
                }
            }

            bool created;
            using (new System.Threading.Mutex(true, "MacDockSingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log(e.Exception);
                Log("START pid=" + Process.GetCurrentProcess().Id + " parent=" + ParentName() + " args=" + string.Join(" ", args));
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { Log("EXIT (ProcessExit) QuitRequested=" + QuitRequested + " " + Environment.StackTrace); TaskbarControl.Restore(); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UNHANDLED (terminating=" + e.IsTerminating + ") " + e.ExceptionObject);
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => Log("UNOBSERVED " + e.Exception);
                Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => Log("SessionEnding reason=" + e.Reason);
 Theme.Refresh();
                Motion.Refresh();
                SetUi.EnsureAutostart(); // rewritten every launch unless the user turned it off in Settings
                Typography.Configure(AppPaths.Config);

 var dock = new DockForm();
                dock.VisibleChanged += (s0, e0) => { if (dock.Visible) Log("READY dock visible " + (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds + " ms after process start"); };
                var quitEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "MacDockQuitEvent");
                new System.Threading.Thread(() =>
                {
                    quitEvt.WaitOne();
                    QuitRequested = true;
                    try { dock.BeginInvoke((Action)(() => Application.Exit())); } catch { }
                }) { IsBackground = true }.Start();
                var bar = new TopBar();
                var island = new IslandForm();
                var spotlight = new Spotlight();
                try { Icon logo = DockForm.LogoIcon(); dock.Icon = logo; bar.Icon = logo; island.Icon = logo; } catch { }

                IslandForm.Enabled = SetCfg.GetBool("island", true);
                IslandForm.EnabledChanged += () => { try { island.BeginInvoke((Action)(() => island.ApplyEnabled())); } catch { } };
                bar.BellClicked += island.TogglePanel;
                bar.SearchClicked += spotlight.Toggle;
                HotkeyHook.Install(spotlight.Toggle, null);

                // Four- or five-finger swipes on a precision touchpad ("@gestures off" in dock.txt disables this).
                TouchpadGestures touchpad = null;
                try
                {
                    string config = AppPaths.Config;
                    bool gesturesOff = File.Exists(config) && File.ReadAllLines(config).Any(l => l.Trim().ToLowerInvariant().Replace(" ", "").StartsWith("@gestures") && l.ToLowerInvariant().Contains("off"));
                    if (!gesturesOff)
                    {
                        touchpad = new TouchpadGestures();
                        touchpad.SwipeDown += MissionControl.Dismiss;
                    }
                }
                catch (Exception ex) { Log(ex); }

                // Windows can drop topmost state and move windows around across sleep, unlock and display changes.
                Action reassert = () =>
                {
                    try
                    {
                        if (!dock.IsHandleCreated || dock.IsDisposed) return;
                        dock.BeginInvoke((Action)(() =>
                        {
                            try { dock.Reassert(); bar.Reposition(); island.Reposition(); if (!Fullscreen.Active) { DockForm.KeepOnTop(bar); if (IslandForm.Enabled) DockForm.KeepOnTop(island); } } catch (Exception ex) { Log(ex); }
                        }));
                    }
                    catch (Exception ex) { Log(ex); }
                };
                Microsoft.Win32.PowerModeChangedEventHandler onPower = (s, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) reassert(); };
                Microsoft.Win32.SessionSwitchEventHandler onSession = (s, e) =>
                {
                    if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock || e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect || e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect) reassert();
                };
                EventHandler onDisplay = (s, e) => reassert();
                Microsoft.Win32.SystemEvents.PowerModeChanged += onPower;
                Microsoft.Win32.SystemEvents.SessionSwitch += onSession;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onDisplay;

                try
                {
                    LocalSend.EnsureStarted();
                    { var lsT = new Timer { Interval = 4000 }; lsT.Tick += (s3, e3) => { lsT.Stop(); lsT.Dispose(); try { LocalSend.EnsureVerb(); LocalSend.EnsureSendTo(); } catch (Exception ex3) { Log(ex3); } }; lsT.Start(); } // shell integration: after the dock is up
                    LocalSend.WatchSends(id => bar.OpenPanel(id), bar);
                    bar.Show();
                    if (IslandForm.Enabled) island.Show(); else { island.Show(); island.Hide(); } // handle must exist so it can be re-shown live
                    Feed.Start();
                    var watchdog = new Timer { Interval = 2000 };
                    watchdog.Tick += (s, e) =>
                    {
                        try
                        {
                            if (dock.IsDisposed) return;
                            Fullscreen.Update();
                            if (Fullscreen.Active) return; // a fullscreen app owns the display: leave dock, bar and island hidden
                            if (!dock.Visible) { dock.Show(); dock.Reassert(); }
                            else DockForm.KeepOnTop(dock);
                            if (!bar.Visible) bar.Show();
                            if (IslandForm.Enabled) { if (!island.Visible) island.Show(); DockForm.KeepOnTop(island); }
                            DockForm.KeepOnTop(bar);
                        }
                        catch (Exception ex) { Log(ex); }
                    };
                    watchdog.Start();
                    // Memory diet: every 45 s compact the heap and hand unused pages back to Windows (they fault back in on demand).
                    var trim = new Timer { Interval = 45000 };
                    trim.Tick += (s, e) =>
                    {
                        try
                        {
                            GC.Collect(2, GCCollectionMode.Forced, true);
                            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
                        }
                        catch { }
                    };
                    trim.Start();
                    MissionControl.PrewarmSoon(4000);
                    foreach (int ms in new[] { 1500, 3000, 4500, 6000, 8000, 10000, 13000, 16000, 20000, 25000, 30000 }) { var st = new Timer { Interval = ms }; st.Tick += (s2, e2) => { ((Timer)s2).Stop(); MemTrim.Now(true); }; st.Start(); startupTrims.Add(st); }
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => { if (!QuitRequested) Relaunch(); };
 Application.Run(dock);
                }
                catch (Exception ex) { Log(ex); }
                finally
                {
                    Microsoft.Win32.SystemEvents.PowerModeChanged -= onPower;
                    Microsoft.Win32.SystemEvents.SessionSwitch -= onSession;
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onDisplay;
                    if (touchpad != null) touchpad.Dispose();
                    HotkeyHook.Uninstall();
                    bar.Dispose();
                    island.Dispose();
                    spotlight.Dispose();
                    TaskbarControl.Restore();
                    if (!QuitRequested) Relaunch(); // closed or crashed without a deliberate Quit
                }
            }
        }

        // Errors go to %LOCALAPPDATA%\MacDock\dock.log instead of a modal dialog.
        internal static void Log(string message)
        {
            Log(new Exception(message));
        }

        internal static void Log(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "dock.log"), DateTime.Now + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    // Hides the Windows taskbar while the dock runs: turns on auto-hide (so the work area is released)
    // and hides the taskbar windows so hovering the screen edge can't bring it back. Restore() undoes both.
    static class TaskbarControl
    {
        const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        const uint ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
        const int SW_HIDE = 0, SW_SHOWNA = 8;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        static bool hidden;
        static uint previousState = ABS_ALWAYSONTOP;

        public static bool IsHidden { get { return hidden; } }

        public static void Hide()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            if (!hidden)
            {
                APPBARDATA get = NewData(tray);
                previousState = (uint)(ulong)SHAppBarMessage(ABM_GETSTATE, ref get);
                try { File.WriteAllText(StatePath(), previousState.ToString()); } catch { } // survives a kill, so /restore puts the user's real state back
                APPBARDATA set = NewData(tray);
                set.lParam = (IntPtr)(previousState | ABS_AUTOHIDE);
                SHAppBarMessage(ABM_SETSTATE, ref set);
                hidden = true;
            }
            HideWindows();
        }

        // Explorer can bring the taskbar back (e.g. after a restart), so this is called on every refresh.
        public static void EnsureHidden()
        {
            if (hidden) HideWindows();
        }

        public static void Restore()
        {
            if (!hidden) return;
            hidden = false;

            foreach (IntPtr h in TrayWindows()) NativeMethods.ShowWindow(h, SW_SHOWNA);

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                APPBARDATA set = NewData(tray);
                set.lParam = (IntPtr)previousState; // exactly what the user had, including their own auto-hide choice
                SHAppBarMessage(ABM_SETSTATE, ref set);
                // Nudge Explorer to re-read taskbar settings and relayout (pins can stay blank after SW_HIDE otherwise)
                IntPtr res;
                SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "TraySettings", 2, 500, out res);
                try { File.Delete(StatePath()); } catch { }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, string l, uint f, uint t, out IntPtr r);
        static string StatePath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "taskbar-state.txt"); }

        public static void ForceRestore()
        {
            previousState = ABS_ALWAYSONTOP;
            try { uint v; if (uint.TryParse(File.ReadAllText(StatePath()).Trim(), out v)) previousState = v; } catch { }
            hidden = true;
            Restore();
        }

        static APPBARDATA NewData(IntPtr hwnd)
        {
            var d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        static void HideWindows()
        {
            foreach (IntPtr h in TrayWindows())
                if (NativeMethods.IsWindowVisible(h)) NativeMethods.ShowWindow(h, SW_HIDE);
        }

        static List<IntPtr> TrayWindows()
        {
            var list = new List<IntPtr>();
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
            return list;
        }
    }

    static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        // Only GetIcon is used. The void members fill the vtable slots before it.
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            void Add();
            void ReplaceIcon();
            void SetOverlayImage();
            void Replace();
            void AddMasked();
            void Draw();
            void Remove();
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll")] public static extern int SHGetImageList(int listType, ref Guid iid, out IImageList list);
    }

    enum Kind { App, Folder, File, Url, Start, Mission, Launchpad, Trash }

    // One line of dock.txt.
    sealed class Entry
    {
        public int Section;     // 0 = apps, 2 = right-hand section (after ---)
        public string Label;    // optional custom name
        public string Target;
        public string Raw;      // the original line, used to remove it again
    }

    sealed class Item
    {
        public Kind Kind;
        public string Target;
        public string Label;
        public Bitmap WinIcon;  // a running web app window's own icon (fallback when no Start-menu icon is found)
        public string Aumid;    // AppUserModelID of the app's window, when running (Store apps, web apps...)
        public Entry Entry;     // null for running apps that are not pinned
        public int Section;     // 0 pinned apps, 1 running-only apps, 2 right-hand section
        public bool SepBefore;
        public Bitmap Icon;
        // Icon pre-scaled to whole pixel sizes, so a frame blits 1:1 instead of re-filtering the 96px source.
        public readonly Dictionary<int, Bitmap> Scaled = new Dictionary<int, Bitmap>();
        public Bitmap ScaledFrom;
        public List<IntPtr> Handles = new List<IntPtr>();
        public Spring ScaleSpring = new Spring(1);
        public double Goal = 1;
        public double BounceHeight, BounceVelocity;      // launch bounce: a ball bouncing on the dock
        public DateTime FocusUntil = DateTime.MinValue, RefocusAt = DateTime.MinValue; // pending focus after a launch
        public DateTime LaunchStart = DateTime.MinValue; // set while an app is launching
        public DateTime RestUntil = DateTime.MinValue;   // short pause between rounds of bounces
        public Spring Shift = new Spring(0);             // horizontal glide when icons trade places
        public RectangleF Rect;

        public double Scale { get { return ScaleSpring.Value; } }
        public bool Bouncing { get { return BounceHeight > 0.01 || BounceVelocity > 0.01; } }

        public string Key { get { return Kind + "|" + Target; } }
    }

    sealed class DockForm : Form
    {
        static int BaseSize = 48;       // icon size at rest (px); see docksize.txt
        static double MaxScale = 1.75;  // magnification under the cursor; 1 = off
        // Layout constants in 96-dpi units; Sc is the dock monitor scale (Dpi.Of), so these properties are device pixels.
        internal static float Sc = 1f;
        static int Px(int v) { return (int)Math.Round(v * Sc); }
        static int Gap { get { return Px(Tokens.Space2); } }              // space between icons
        static int BasePx { get { return Px(BaseSize); } }    // icon size at rest in device pixels
        static int GroupGap { get { return Px(Tokens.Space4); } }        // extra space between sections
        static int PadX { get { return Px(Tokens.Space3); } }            // inner padding of the dock pill
        static int PadY { get { return Px(Tokens.Space2); } }
        static int BottomMargin { get { return Px(Tokens.Space2); } }     // space between dock and the bottom of the screen
        static int FormHeight { get { return (int)Math.Ceiling(BasePx * Math.Max(1.0, MaxScale)) + Px(66); } }
        static int IconSource = 96;     // stored icon resolution; FitIconSource raises it so max zoom is never upscaled (blurry at 125%+ DPI)
        // Stored size = largest drawn size (rest size x zoom x DPI), rounded up to 16 and kept within 96..192. True when it changed.
        static bool FitIconSource()
        {
            int need = (int)Math.Ceiling(BaseSize * Sc * Math.Max(1.0, MaxScale));
            need = Math.Max(96, Math.Min(192, (need + 15) / 16 * 16));
            if (need == IconSource) return false;
            IconSource = need;
            return true;
        }

        // Motion. Magnification is a nearly critically damped spring: quick, no wobble. The launch bounce is
        // a ball under gravity that keeps ~55% of its speed on each landing (about three bounces per round).
        const double MagnifyOmega = 26, MagnifyZeta = 0.9;
        const double Gravity = 2600, Restitution = 0.55, MinBounceSpeed = 50;
        const double BounceLaunchSpeed = 322;   // = sqrt(2 * Gravity * 20 px)
        static double LaunchSpeed { get { return Motion.Reduced ? 0 : BounceLaunchSpeed; } } // Reduce Motion: no bounce
        const double LaunchTimeout = 8;         // seconds to keep bouncing if the app never shows a window
        static int PillRadius { get { return Math.Max(Px(10), PillHeightConst / 4); } }  // ~1/4 of bar height (reference)      // rounded-rectangle corners, like the macOS dock
        static int CanvasSlack { get { return Px(260); } }    // extra width so magnified icons never clip

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_APPWINDOW = 0x40000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_LAYERED = 0x80000;
        const int DWMWA_CLOAKED = 14;
        const int SW_RESTORE = 9, SW_MINIMIZE = 6;
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        const uint SHGFI_SYSICONINDEX = 0x4000;
        const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        const int SHIL_JUMBO = 4;
        const int ILD_TRANSPARENT = 1;
        const byte VK_MENU = 0x12;
        const byte VK_LWIN = 0x5B;
        const uint KEYEVENTF_KEYUP = 0x2;

        static readonly Color LabelFill = Color.FromArgb(190, 28, 28, 32);

        static readonly string[] DefaultConfig =
        {
            "# MacDock pins. One item per line. Lines starting with # are ignored.",
            "# Items: apps (.exe/.lnk), folders, files, websites (https://...), ms-settings:, shell: links, [start]",
            "# Use \"Label | target\" to give an item a custom name. A line with --- starts the right-hand section.",
            "# @hide-taskbar on   hides the Windows taskbar while the dock runs (use off to keep it).",
            "@hide-taskbar on",
            "# @font = Inter   sets the typeface (default: Inter, then Segoe UI Variable). SF Pro isn't licensed for Windows.",
            "",
            "[start]",
            "%windir%\\explorer.exe",
            "%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe",
            "%windir%\\system32\\notepad.exe",
            "%windir%\\system32\\calc.exe",
            "---",
            "Downloads | %USERPROFILE%\\Downloads",
            "Documents | %USERPROFILE%\\Documents",
            "Settings | ms-settings:"
        };

        readonly Timer animTimer = new Timer();
        readonly Timer refreshTimer = new Timer();
        MenuPopup popup; // the open right-click menu, if any
        Item previewItem;
        DateTime lastPreviewClose = DateTime.MinValue;
        int dropIndex = -1;                      // dock item under a file drag in progress (-1 = none)
        DateTime dropSince = DateTime.MaxValue;  // when the drag started resting on dropIndex
        Item dropHot;                            // item highlighted as a valid drop target
        Form preview; // window thumbnails shown while hovering an app with several windows
        DateTime hoverSince = DateTime.UtcNow;
        Point speedPt; DateTime speedAt = DateTime.UtcNow; bool cursorSlow = true;
        Font labelFont = Typography.Text(9.5f, Weight.Medium);
        readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly string configPath = AppPaths.Config;
        static int pillHeight { get { return BasePx + 2 * PadY; } }

        List<Entry> entries = new List<Entry>();
        bool hideTaskbar = true;
        DateTime configStamp = DateTime.MinValue;
        List<Item> items = new List<Item>();
        RectangleF pillRect;
        int hoverIndex = -1;
        Surface surface;
        readonly FrameClock clock = new FrameClock();
        VsyncDriver vsync;
        bool wasBusy;
        double smoothX = double.NaN; long smoothTk; const double SmoothTau = 0.014;

        // Long press to rearrange: hold still on a pinned icon to pick it up, drag it sideways, release to drop it.
        const int HoldMs = 380, HoldTolerance = 4;
        const double DragScale = 1.15, ShiftOmega = 30, ShiftZeta = 0.85;
        Item pressItem, dragItem;
        Point pressPoint;
        DateTime pressTime;
        double dragX;

        // Slides down out of sight while a fullscreen app is in front (0 = shown, 1 = hidden).
        Spring hideSpring = new Spring(0);
        int fullscreenTick;
        const double HideOmega = 22, HideZeta = 1.0;
        static float HideDistance { get { return PillHeightConst + BottomMargin + Px(14); } }
        static int PillHeightConst { get { return BasePx + 2 * PadY; } }

        public static DockForm Instance { get; private set; }

        // "Show Dock" and "Auto-hide dock" are kept in dock.txt (@show-dock, @auto-hide-dock).
        internal static bool ShowDockOn = true, AutoHideOn;
        bool dockReserved;
        int dockReservedHeight;

        static void ReadDockSwitches()
        {
            ShowDockOn = true;  // never fully hidden by the "show dock" switch
            string ah = SetCfg.Get("auto-hide-dock");
            for (int i = 0; ah == null && i < 5; i++) { System.Threading.Thread.Sleep(100); ah = SetCfg.Get("auto-hide-dock"); } // file may be briefly locked at startup
            AutoHideOn = ah != null && SetCfg.GetBool("auto-hide-dock", false);
        }

        // The centre of the dock icon that belongs to this window's app (screen coordinates), for the genie effect.
        internal bool TryIconCenter(IntPtr hwnd, out Point center)
        {
            center = Point.Empty;
            if (!ShowDockOn || !Visible) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            string exe = ProcessPath(pid);
            foreach (Item it in items)
            {
                if (it.Handles.Contains(hwnd) || (exe != null && string.Equals(it.Target, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    RectangleF r = it.Rect;
                    center = new Point(Left + (int)(r.Left + r.Width / 2), Top + (int)(r.Top + r.Height / 2));
                    return true;
                }
            }
            return false;
        }

        // A switch changed (Settings, the top bar menu or an edit of dock.txt): apply it now.
        internal void ApplyDockMode()
        {
            ReadDockSwitches();
            animTimer.Interval = 50;
            if (!animTimer.Enabled) animTimer.Start();
            UpdateReservation();
        }

        // With the dock shown and not auto-hiding, its strip is reserved as a bottom appbar so maximized windows stop at
        // its top edge (the top bar does the same at the top). Otherwise the reservation is dropped.
        void UpdateReservation()
        {
            if (ShowDockOn && !AutoHideOn && IsHandleCreated) ReserveBottom();
            else ReleaseBottom();
        }

        void ReserveBottom()
        {
            try
            {
                Rectangle screen = DockScreen.Bounds;
                int h = PillHeightConst + BottomMargin + 2;
                var data = new Win32.APPBARDATA();
                data.cbSize = Marshal.SizeOf(typeof(Win32.APPBARDATA));
                data.hWnd = Handle;
                if (!dockReserved)
                {
                    data.uCallbackMessage = Win32.RegisterWindowMessage("MacDockDockBar");
                    Win32.SHAppBarMessage(0, ref data); // ABM_NEW
                }
                data.uEdge = 3; // bottom
                data.rc = new Win32.RECT { Left = screen.Left, Top = screen.Bottom - h, Right = screen.Right, Bottom = screen.Bottom };
                Win32.SHAppBarMessage(2, ref data); // ABM_QUERYPOS
                data.rc.Top = data.rc.Bottom - h;
                Win32.SHAppBarMessage(3, ref data); // ABM_SETPOS
                dockReserved = true;
                dockReservedHeight = h;
                DockClamp.Top = screen.Bottom - h; DockClamp.Monitor = screen; DockClamp.Start(); // backstop for apps that ignore the work area
            }
            catch { }
        }

        void ReleaseBottom()
        {
            if (!dockReserved) return;
            dockReserved = false;
            DockClamp.Top = 0;
            var data = new Win32.APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(Win32.APPBARDATA));
            data.hWnd = Handle;
            Win32.SHAppBarMessage(1, ref data); // ABM_REMOVE
        }

        // Dock size / magnification, kept next to the exe in docksize.txt ("size=48" / "magnify=1.75").
        static string SizeFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docksize.txt"); } }
        static void LoadSize()
        {
            try
            {
                foreach (string line in File.ReadAllLines(SizeFile))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length == 2 && kv[0].Trim() == "monitor") { DockMonitor = kv[1].Trim(); continue; }
                    double v;
                    if (kv.Length != 2 || !double.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                    if (kv[0].Trim() == "size") BaseSize = (int)Math.Max(28, Math.Min(88, v));
                    else if (kv[0].Trim() == "magnify") MaxScale = Math.Max(1.0, Math.Min(2.2, v));
                }
            }
            catch { }
        }

        // The display the dock lives on: docksize.txt "monitor=<Screen.DeviceName>" (e.g. \.\DISPLAY2), else the primary one.
        // Dock, its bottom reservation, Mission Control and genie targets all use it.
        internal static string DockMonitor;
        internal static Screen DockScreen
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(DockMonitor))
                        foreach (Screen s in Screen.AllScreens) if (string.Equals(s.DeviceName, DockMonitor, StringComparison.OrdinalIgnoreCase)) return s;
                }
                catch { }
                return Screen.PrimaryScreen;
            }
        }

        public static int CurrentSize { get { return BaseSize; } }
        public static double CurrentMagnify { get { return MaxScale; } }
        public const int MinSize = 28, MaxSize = 88;
        public const double MinMagnify = 1.0, MaxMagnify = 2.2;
        public void ApplyDockSize(int size, double magnify) { SetDockSize(size, magnify); }

        void SetDockSize(int size, double magnify)
        {
            BaseSize = Math.Max(28, Math.Min(88, size));
            MaxScale = Math.Max(1.0, Math.Min(2.2, magnify));
            try { File.WriteAllText(SizeFile, "size=" + BaseSize + "\nmagnify=" + MaxScale.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"); } catch { }
            if (preview != null) preview.Close();
            bool bigger = FitIconSource();
            FitCanvas();
            if (ShowDockOn && !AutoHideOn && IsHandleCreated) ReserveBottom();
            if (bigger && IsHandleCreated) BeginInvoke(new Action(RefreshIcons));
            Invalidate();
        }

        public DockForm()
        {
            Instance = this;
            LoadSize();
            ReadDockSwitches();
            Genie.Install(); // the minimize / restore effect watches for windows being minimized and restored
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AllowDrop = true;
            StartPosition = FormStartPosition.Manual;

            // Polls the cursor; idles at 50 ms and speeds up to ~60 fps only while animating.
            animTimer.Interval = 50;
            animTimer.Tick += (s, e) => Step();
            vsync = new VsyncDriver(this, Step);

            refreshTimer.Interval = 2000;
            refreshTimer.Tick += (s, e) => RebuildItems();

            LoadConfig();
            ApplyTaskbarSetting();
            RebuildItems();
            animTimer.Start();
            refreshTimer.Start();
        }

        // ---------- keyboard, accessibility, live settings ----------

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HK_TOGGLE = 0xDC01, HK_LEFT = 0xDC02, HK_RIGHT = 0xDC03, HK_ENTER = 0xDC04, HK_ESC = 0xDC05, HK_HOME = 0xDC06, HK_END = 0xDC07, HK_MENU = 0xDC08;
        int kbIndex = -1;
        Point kbCursor;

        // Ctrl+Alt+Shift+D puts the keyboard on the dock: Left/Right/Home/End move, Enter opens, Menu key opens the context menu, Esc leaves.
        void RegisterToggleKey() { try { RegisterHotKey(Handle, HK_TOGGLE, 0x2 | 0x1 | 0x4 | 0x4000, 0x44); } catch { } } // MOD_CONTROL|MOD_ALT|MOD_NOREPEAT, D

        void KbEnter()
        {
            if (items.Count == 0 || !Visible) return;
            NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
            kbCursor = new Point(p.X, p.Y);
            kbIndex = Math.Max(0, items.FindIndex(i => i.Handles.Count > 0 && false));
            uint[] keys = { 0x25, 0x27, 0x0D, 0x1B, 0x24, 0x23, 0x5D };
            int[] ids = { HK_LEFT, HK_RIGHT, HK_ENTER, HK_ESC, HK_HOME, HK_END, HK_MENU };
            for (int i = 0; i < keys.Length; i++) try { RegisterHotKey(Handle, ids[i], 0x4000, keys[i]); } catch { }
            if (!animTimer.Enabled) animTimer.Start();
            animTimer.Interval = 16;
        }

        void KbExit()
        {
            if (kbIndex < 0) return;
            kbIndex = -1;
            foreach (int id in new[] { HK_LEFT, HK_RIGHT, HK_ENTER, HK_ESC, HK_HOME, HK_END, HK_MENU }) try { UnregisterHotKey(Handle, id); } catch { }
        }

        void KbKey(int id)
        {
            int n = items.Count;
            if (kbIndex < 0 || n == 0) return;
            NativeMethods.POINT p; NativeMethods.GetCursorPos(out p); kbCursor = new Point(p.X, p.Y);
            if (id == HK_LEFT) kbIndex = (kbIndex + n - 1) % n;
            else if (id == HK_RIGHT) kbIndex = (kbIndex + 1) % n;
            else if (id == HK_HOME) kbIndex = 0;
            else if (id == HK_END) kbIndex = n - 1;
            else if (id == HK_ESC) KbExit();
            else if (id == HK_ENTER) { Item it = items[Math.Min(kbIndex, n - 1)]; KbExit(); Activate(it); }
            else if (id == HK_MENU)
            {
                Item it = items[Math.Min(kbIndex, n - 1)];
                KbExit();
                ShowMenu(it, new Point((int)(it.Rect.Left + it.Rect.Width / 2), (int)it.Rect.Top));
            }
            if (!animTimer.Enabled) animTimer.Start();
            animTimer.Interval = 16;
        }

        // Windows colour mode or the animation-effects switch changed: re-read both and repaint everything that is open.
        void OnSystemSettingChanged()
        {
            bool wasLight = Theme.Light;
            Theme.Refresh();
            Motion.Refresh();
            if (wasLight != Theme.Light)
                foreach (Form f in Application.OpenForms) try { f.Invalidate(true); } catch { }
            Redraw();
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new DockAccessible(this); }

        sealed class DockAccessible : Control.ControlAccessibleObject
        {
            readonly DockForm dock;
            public DockAccessible(DockForm d) : base(d) { dock = d; }
            public override string Name { get { return "Dock"; } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.ToolBar; } }
            public override int GetChildCount() { return dock.items.Count; }
            public override AccessibleObject GetChild(int i) { return i >= 0 && i < dock.items.Count ? new ItemAccessible(this, dock, i) : null; }
        }

        sealed class ItemAccessible : AccessibleObject
        {
            readonly DockAccessible parent; readonly DockForm dock; readonly int index;
            public ItemAccessible(DockAccessible p, DockForm d, int i) { parent = p; dock = d; index = i; }
            Item It { get { return index < dock.items.Count ? dock.items[index] : null; } }
            public override string Name { get { Item it = It; return it == null ? "" : ItemNames.Display(it.Target, it.Label) + (it.Handles.Count > 0 ? ", running" : ""); } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return "Open"; } }
            public override void DoDefaultAction() { Item it = It; if (it != null) dock.BeginInvoke(new Action(() => dock.Activate(it))); }
            public override Rectangle Bounds
            {
                get
                {
                    Item it = It; if (it == null) return Rectangle.Empty;
                    RectangleF r = it.Rect;
                    Point tl = dock.PointToScreen(new Point((int)r.Left, (int)r.Top));
                    return new Rectangle(tl.X, tl.Y, (int)r.Width, (int)r.Height);
                }
            }
        }

        // Follows the dock monitor's DPI: layout constants and the label font are rebuilt when it changes.
        void UpdateScale(Rectangle area)
        {
            float s = Dpi.Of(area);
            if (Math.Abs(s - Sc) < 0.001f) return;
            Sc = s;
            if (FitIconSource()) { if (IsHandleCreated) BeginInvoke(new Action(RefreshIcons)); else RefreshIcons(); }
            Font old = labelFont;
            labelFont = Typography.Text(9.5f * Sc, Weight.Medium);
            if (old != null) old.Dispose();
            if (dockReserved) UpdateReservation();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) // WM_DPICHANGED: the window is laid out by FitCanvas, not by the suggested rectangle
            {
                try { FitCanvas(); Redraw(); } catch { }
                return;
            }
            if (m.Msg == 0x0312) { int id = m.WParam.ToInt32(); if (id == HK_TOGGLE) { if (kbIndex >= 0) KbExit(); else KbEnter(); } else KbKey(id); return; }
            if (dockReserved && m.Msg == (int)Win32.RegisterWindowMessage("MacDockDockBar") && IsHandleCreated) { BeginInvoke(new Action(UpdateReservation)); }
            if (m.Msg == 0x001A) { BeginInvoke(new Action(OnSystemSettingChanged)); }
            base.WndProc(ref m);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Layered = per-pixel alpha (real glass); tool window = hidden from Alt+Tab; no-activate = never steals focus.
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterToggleKey();
            UpdateReservation();
            Redraw();
            BeginInvoke(new Action(Genie.Warm)); // build the genie overlay now, so the first minimize isn't slow
            // WindowTheme (traffic-light overlay) disabled: it blocked moving/maximizing windows.
        }

        // ---------- drawing ----------

        // Draws the dock into an ARGB bitmap and hands it to Windows as a layered window.
        // Fully transparent pixels let clicks fall through to whatever is underneath.
        void Redraw()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            if (surface == null || surface.Width != Width || surface.Height != Height)
            {
                if (surface != null) surface.Dispose();
                surface = new Surface(Width, Height);
            }

            using (Graphics g = surface.Begin())
            {
                DrawDock(g);
            }
            surface.Present(Handle, Left, Top);

            // The blur follows the pill (including its slide out of sight) just below this window.
            float lift = (float)(hideSpring.Value * HideDistance);
            // (blur companion window removed: the glass pill is drawn alone)
        }

        void DrawDock(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Icons are stored at 96px and drawn at 48-84px every animation frame. High-quality bilinear
            // pre-filters when shrinking and costs about half of bicubic, which keeps hover animation light.
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Typography.Prepare(g);
            g.TranslateTransform(0, (float)(hideSpring.Value * HideDistance));

            // Glass pill, same material as the bar and panels: a faint cool tint, a soft sheen that fades
            // downward, and a thin rim that is brightest along the top edge (the specular highlight).
            using (GraphicsPath pill = RoundedRect(pillRect, PillRadius))
            using (var baseFill = new SolidBrush(Color.FromArgb(70, 40, 48, 62))) // translucent tint; nothing sits underneath
            using (var sheen = new LinearGradientBrush(pillRect, Color.FromArgb(38, 255, 255, 255), Color.FromArgb(8, 255, 255, 255), LinearGradientMode.Vertical))
            using (var rimBrush = new LinearGradientBrush(pillRect, Color.FromArgb(96, 255, 255, 255), Color.FromArgb(28, 255, 255, 255), LinearGradientMode.Vertical))
            using (var rim = new Pen(rimBrush, 1f))
            {
                g.FillPath(baseFill, pill);
                g.FillPath(sheen, pill);
                g.DrawPath(rim, pill);
            }

            using (var dot = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            using (var sepPen = new Pen(Color.FromArgb(90, 255, 255, 255)))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i];

                    if (it.SepBefore && i > 0)
                    {
                        float x = it.Rect.Left - (Gap + GroupGap) / 2f;
                        g.DrawLine(sepPen, x, pillRect.Top + Px(14), x, pillRect.Bottom - Px(14));
                    }

                    if (it == dragItem) continue; // drawn last, lifted above the others
                    if (it == dropHot) // file drag resting here: it will take the drop
                    {
                        RectangleF hr = RectangleF.Inflate(it.Rect, Px(5), Px(5));
                        using (GraphicsPath hp = RoundedRect(hr, Px(Tokens.RadiusMd)))
                        using (var hf = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                        using (var hb = new Pen(Color.FromArgb(190, 255, 255, 255), 2f))
                        { g.FillPath(hf, hp); g.DrawPath(hb, hp); }
                    }
                    if (it.Kind == Kind.Folder && preview is DownloadsFan && previewItem != null && previewItem.Key == it.Key) DrawFanTile(g, it, ((DownloadsFan)preview).Presence);
                    else DrawIcon(g, it, it.Rect);
                    if (it.Handles.Count > 0) // running indicator, like the macOS dot
                        g.FillEllipse(dot, it.Rect.Left + it.Rect.Width / 2 - Px(2), pillRect.Bottom - Px(6), Px(4), Px(4));
                }

                if (dragItem != null)
                {
                    RectangleF r = dragItem.Rect;
                    using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                        g.FillEllipse(shadow, r.Left + Px(6), pillRect.Bottom - PadY - Px(2), r.Width - Px(12), Px(8));
                    DrawIcon(g, dragItem, r);
                }
            }

            if (hoverIndex >= 0 && hoverIndex < items.Count)
            {
                Item it = items[hoverIndex];
                string shown = ItemNames.Display(it.Target, it.Label);
                SizeF size = g.MeasureString(shown, labelFont, 400, StringFormat.GenericTypographic);
                float cx = it.Rect.Left + it.Rect.Width / 2f;
                float w = size.Width + Px(20), h = Px(24);
                float x = Math.Max(4, Math.Min(Width - w - 4, cx - w / 2));
                float y = it.Rect.Top - h - Px(12);
                using (GraphicsPath lp = RoundedRect(new RectangleF(x, y, w, h), Px(Tokens.RadiusSm)))
                using (var b = new SolidBrush(LabelFill))
                {
                    g.FillPath(b, lp);
                    // Small arrow pointing at the icon, like the macOS dock tooltip.
                    g.FillPolygon(b, new[] { new PointF(cx - Px(6), y + h - 0.5f), new PointF(cx + Px(6), y + h - 0.5f), new PointF(cx, y + h + Px(6)) });
                }
                using (var tb = new SolidBrush(Color.White))
                    g.DrawString(shown, labelFont, tb, new RectangleF(x, y, w, h), Typography.Format(StringAlignment.Center));
            }
        }

        // While a folder's fan is open its dock slot turns into a dark tile with a chevron (click it to fold the fan back);
        // the chevron nudges down and up as a hint.
        void DrawFanTile(Graphics g, Item it, double k)
        {
            if (k < 0.98) DrawIcon(g, it, it.Rect); // the folder shows through while the tile fades and scales in or out
            RectangleF r = it.Rect;
            if (k <= 0.01) return;
            float sc = (float)(0.55 + 0.45 * k); r = new RectangleF(r.Left + r.Width * (1 - sc) / 2, r.Top + r.Height * (1 - sc) / 2, r.Width * sc, r.Height * sc);
            int a = (int)(255 * k);
            float bob = (float)Math.Sin(Environment.TickCount / 260.0) * 1.6f * (r.Width / 64f);
            using (GraphicsPath tp = RoundedRect(RectangleF.Inflate(r, -Px(3), -Px(3)), Px(Tokens.RadiusMd)))
            using (var fill = new SolidBrush(Color.FromArgb(235 * a / 255, 40, 40, 44)))
            using (var edge = new Pen(Color.FromArgb(60 * a / 255, 255, 255, 255), 1f))
            { g.FillPath(fill, tp); g.DrawPath(edge, tp); }
            float cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2 + bob, w = r.Width * 0.14f;
            using (var pen = new Pen(Color.FromArgb(245 * a / 255, 255, 255, 255), Math.Max(2f, r.Width * 0.055f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - w, cy - w * 0.5f), new PointF(cx, cy + w * 0.5f), new PointF(cx + w, cy - w * 0.5f) });
        }

        void DrawIcon(Graphics g, Item it, RectangleF r)
        {
            int sz = (int)Math.Round(Math.Min(r.Width, r.Height));
            if (it.Icon == null || sz < 4 || sz > 200) { if (it.Icon != null) g.DrawImage(it.Icon, r); return; }
            if (it.ScaledFrom != it.Icon) { it.Scaled.Clear(); it.ScaledFrom = it.Icon; }
            Bitmap b;
            if (!it.Scaled.TryGetValue(sz, out b))
            {
                if (it.Scaled.Count > 64) it.Scaled.Clear();
                b = new Bitmap(sz, sz, PixelFormat.Format32bppPArgb);
                using (Graphics bg = Graphics.FromImage(b))
                {
                    bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.DrawImage(it.Icon, new Rectangle(0, 0, sz, sz));
                }
                it.Scaled[sz] = b;
            }
            g.DrawImage(b, new Rectangle((int)Math.Round(r.Left + (r.Width - sz) / 2f), (int)Math.Round(r.Top + (r.Height - sz) / 2f), sz, sz));
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---------- animation and layout ----------

        // Auto-hide: the dock slides away while a normal app window is in front, and comes back when the cursor
        // rests on the bottom 3 px of the screen (about 150 ms) or while it stays over the dock. The cursor is
        // polled here, so the reveal works even while the dock window is hidden.
        bool revealed;
        DateTime edgeSince = DateTime.MaxValue;

        bool AutoHideAway()
        {
            if (!Fullscreen.AppInFront) { revealed = false; edgeSince = DateTime.MaxValue; return false; }

            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            Rectangle screen = Screen.FromPoint(new Point(p.X, p.Y)).Bounds;
            Rectangle dockScreen = Screen.FromHandle(Handle).Bounds;
            bool onDockScreen = screen == dockScreen;

            bool held = (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0 && pressItem == null && dragItem == null && popup == null; // resizing a window must not reveal the dock
            bool atEdge = onDockScreen && !held && p.Y >= dockScreen.Bottom - 3; // thin strip at the very bottom pixel rows, so app controls just above it never trigger
            if (atEdge)
            {
                if (edgeSince == DateTime.MaxValue) edgeSince = DateTime.Now;
                else if ((DateTime.Now - edgeSince).TotalMilliseconds >= 150) revealed = true;
            }
            else edgeSince = DateTime.MaxValue;

            if (revealed)
            {
                // Stay while the cursor is over the pill (or the dock is being used), release when it leaves.
                bool over = onDockScreen && p.X >= Left && p.X < Left + Width && p.Y >= Top + pillRect.Top - 8;
                if (!over && !atEdge && dragItem == null && pressItem == null && popup == null) revealed = false;
            }
            return !revealed;
        }

        // Runs each tick: reads the cursor, eases each icon toward its target size, and redraws only when something moved.
        void Step()
        {
            if (hideTaskbar) TaskbarControl.EnsureHidden(); // Explorer can re-show it at any time

            // Dock switched off and already out of sight: nothing to poll, draw or react to until it is switched on again.
            if (!ShowDockOn && !Visible)
            {
                if (vsync.Active) vsync.Sleep();
                animTimer.Interval = 250;
                return;
            }

            if (++fullscreenTick % 5 == 0) Fullscreen.Update(); // ~4 times a second is plenty
            bool autoAway = !ShowDockOn || (AutoHideOn && AutoHideAway()); // auto-hide polls the cursor; off, it costs nothing
            bool away = Fullscreen.Active || autoAway;

            if (!Visible)
            {
                if (vsync.Active) { vsync.Sleep(); animTimer.Interval = 50; if (!animTimer.Enabled) animTimer.Start(); }
                if (!away)
                {
                    Show();       // fullscreen ended: reappear, sliding up from below
                    UpdateReservation(); // a hidden window can't hold the reserved strip, so take it again now
                    wasBusy = false;
                    Redraw();
                }
                return;
            }

            NativeMethods.POINT p;
            NativeMethods.GetCursorPos(out p);
            Point local = PointToClient(new Point(p.X, p.Y));

            // Long press: holding still on a pinned icon picks it up; moving before the hold finishes cancels it.
            if (pressItem != null)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0) pressItem = null;
                else if (dragItem == null)
                {
                    bool still = Math.Abs(local.X - pressPoint.X) + Math.Abs(local.Y - pressPoint.Y) <= HoldTolerance;
                    if (!still) BeginDrag(pressItem); // drag immediately, like macOS; no long press needed
                    else if ((DateTime.Now - pressTime).TotalMilliseconds >= HoldMs) BeginDrag(pressItem);
                }
            }
            if (dragItem != null)
            {
                dragX = local.X;
                ContinueDrag();
            }

            int n = items.Count;
            double[] ones = new double[n];
            for (int i = 0; i < n; i++) ones[i] = 1;
            float baseTotal;
            float[] baseX = Row(ones, out baseTotal);
            float baseLeft = (Width - (baseTotal + 2 * PadX)) / 2f + PadX;
            if (kbIndex >= 0)
            {
                if (kbIndex >= n || p.X != kbCursor.X || p.Y != kbCursor.Y) KbExit(); // moving the mouse hands control back
                else local = new Point((int)(baseLeft + baseX[kbIndex] + BasePx / 2.0), (int)pillRect.Top + 1);
            }

            // A button held that the dock didn't start (resizing/moving a window, dragging a selection) must not wake the dock:
            // no magnify, no window-preview popup while the cursor passes over it.
            bool foreignPress = (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0 && pressItem == null && dragItem == null && dropIndex < 0 && popup == null;
            // Entering needs the cursor near the pill (app controls just above must not wake the dock); once icons are magnified the full height keeps them hoverable.
            bool magnified = false;
            for (int i = 0; i < n; i++) if (items[i].Scale > 1.02) { magnified = true; break; }
            bool nearDock = !away && !foreignPress && hideSpring.Value < 0.05 && ClientRectangle.Contains(local) && local.Y >= pillRect.Top - (magnified ? (MaxScale - 1) * BasePx + 4 : 6);
            // Magnify follows a time-based low-pass of the cursor X (sub-pixel, frame-rate independent) so integer
            // cursor steps and uneven frame gaps don't show up as stepping in the icon sizes. Hover uses the raw X.
            long nowTk = System.Diagnostics.Stopwatch.GetTimestamp();
            double sdt = smoothTk == 0 ? 1 : Math.Min(0.05, (nowTk - smoothTk) / (double)System.Diagnostics.Stopwatch.Frequency);
            smoothTk = nowTk;
            if (!nearDock || Motion.Reduced || double.IsNaN(smoothX) || Math.Abs(smoothX - local.X) > BasePx * 4) smoothX = local.X;
            else smoothX += (local.X - smoothX) * (1 - Math.Exp(-sdt / SmoothTau));
            bool smoothBusy = nearDock && Math.Abs(smoothX - local.X) > 0.05;
            if (!smoothBusy) smoothX = local.X;
            int hover = -1;
            for (int i = 0; i < n; i++)
            {
                double center = baseLeft + baseX[i] + BasePx / 2.0;
                double dist = Math.Abs(smoothX - center);
                double t = dist / (BasePx * 2.2);
                if (dragItem != null) items[i].Goal = items[i] == dragItem ? DragScale : 1; // no magnifying while rearranging
                else items[i].Goal = nearDock && t < 1 ? 1 + (MaxScale - 1) * Math.Pow(Math.Cos(t * Math.PI / 2), 2) : 1;
                if (dragItem == null && nearDock && Math.Abs(local.X - center) < BasePx / 2.0) hover = i;
            }

            double dt;
            if (wasBusy) dt = vsync.Frame(clock.Next());
            else { clock.Reset(); dt = vsync.Dt; } // first frame after idling

            bool busy = false;
            foreach (Item it in items)
            {
                if (!Motion.Reduced && !it.ScaleSpring.IsSettled(it.Goal))
                {
                    it.ScaleSpring.Step(it.Goal, MagnifyOmega, MagnifyZeta, dt);
                    busy = true;
                }
                else it.ScaleSpring.Snap(it.Goal);

                if (Math.Abs(it.Shift.Value) > 0.3 || Math.Abs(it.Shift.Velocity) > 0.3)
                {
                    it.Shift.Step(0, ShiftOmega, ShiftZeta, dt); // icons glide into the gap left by a rearranged neighbour
                    busy = true;
                }
                else it.Shift.Snap(0);

                if (StepBounce(it, dt)) busy = true;
            }
            if (dragItem != null || smoothBusy) busy = true; // follow the cursor every frame
            if (preview is DownloadsFan) busy = true; // the folder tile fades and bobs with the fan

            double hideTarget = away ? 1 : 0;
            if (!hideSpring.IsSettled(hideTarget, 0.003))
            {
                hideSpring.Step(hideTarget, HideOmega, HideZeta, dt);
                busy = true;
            }
            else hideSpring.Snap(hideTarget);
            wasBusy = busy;

            // Cursor speed gate: under 4 px over the last ~80 ms counts as resting; fast sweeps restart the hover delay.
            if ((DateTime.UtcNow - speedAt).TotalMilliseconds >= 80)
            {
                cursorSlow = Math.Abs(local.X - speedPt.X) + Math.Abs(local.Y - speedPt.Y) < 4;
                speedPt = local; speedAt = DateTime.UtcNow;
                if (!cursorSlow) hoverSince = DateTime.UtcNow;
            }
            bool hoverChanged = hover != hoverIndex;
            hoverIndex = hover;
            if (hoverChanged) { hoverSince = DateTime.UtcNow; if (hover >= 0 && hover < items.Count && items[hover].Kind == Kind.Folder) DownloadsFan.Prefetch(Environment.ExpandEnvironmentVariables(items[hover].Target)); }
            // Moving straight from one icon to the next swaps the popup at once (no dwell, no gap); the old one closes
            // in the same tick the new one opens, so nothing flashes in between.
            if (dropIndex >= 0 && dropIndex < items.Count && popup == null && dragItem == null)
            {
                // A file drag is resting on an icon: after ~600 ms an app's window strip springs open.
                Item di = items[dropIndex];
                if (preview != null && (previewItem == null || previewItem.Key != di.Key)) preview.Close();
                else if (preview == null && di.Kind == Kind.App && di.Handles.Count > 0 && (DateTime.UtcNow - dropSince).TotalMilliseconds >= 600)
                    ShowPreview(di);
            }
            else if (dropIndex >= 0) { }
            else if (hover >= 0 && preview != null && (previewItem == null || previewItem.Key != items[hover].Key) && !(preview is DownloadsFan && ((DownloadsFan)preview).Pinned) && popup == null && dragItem == null)
            {
                preview.Close();
                if (!FanClickMode(items[hover])) ShowPreview(items[hover]);
            }
            else if (hover >= 0 && preview == null && popup == null && dragItem == null &&
                     ((DateTime.UtcNow - hoverSince).TotalMilliseconds > (items[hover].Kind == Kind.Folder ? 120 : 350) || (DateTime.UtcNow - lastPreviewClose).TotalMilliseconds < 120) && cursorSlow)
                { if (!FanClickMode(items[hover])) ShowPreview(items[hover]); else hoverSince = DateTime.MaxValue; }
            LayoutItems();

            if (busy || hoverChanged) Redraw();
            if (away && hideSpring.Value >= 0.999)
            {
                Hide(); // fully out of sight: stop drawing and stop catching clicks
            }
            if (busy)
            {
                if (!vsync.Active) { animTimer.Stop(); vsync.Wake(); } // motion: one step per display refresh, no timer
            }
            else
            {
                if (vsync.Active) vsync.Sleep();
                animTimer.Interval = 50;
                if (!animTimer.Enabled) animTimer.Start();
            }
        }

        // Bouncing ball. While an app is launching, rounds of bounces repeat until its window appears.
        bool StepBounce(Item it, double dt)
        {
            if (it.LaunchStart != DateTime.MinValue)
            {
                bool finished = it.Handles.Count > 0 || (DateTime.Now - it.LaunchStart).TotalSeconds > LaunchTimeout;
                if (finished)
                {
                    it.LaunchStart = DateTime.MinValue;
                }
                else if (!it.Bouncing && DateTime.Now >= it.RestUntil) it.BounceVelocity = LaunchSpeed;
            }

            // Launched app: once its window shows up (possibly long after the bounce gives up) force it to the front, then
            // re-assert shortly after in case focus was stolen back.
            bool focusPending = false;
            if (it.FocusUntil != DateTime.MinValue)
            {
                if (DateTime.Now > it.FocusUntil) it.FocusUntil = DateTime.MinValue;
                else
                {
                    IntPtr lh = IntPtr.Zero;
                    foreach (IntPtr h in it.Handles) if (NativeMethods.IsWindow(h)) { lh = h; break; }
                    if (lh != IntPtr.Zero)
                    {
                        it.FocusUntil = DateTime.MinValue;
                        FocusWindow(lh);
                        it.RefocusAt = DateTime.Now.AddMilliseconds(300);
                    }
                    else focusPending = true;
                }
            }
            if (it.RefocusAt != DateTime.MinValue)
            {
                if (DateTime.Now >= it.RefocusAt)
                {
                    it.RefocusAt = DateTime.MinValue;
                    foreach (IntPtr h in it.Handles)
                        if (NativeMethods.IsWindow(h))
                        {
                            IntPtr fg = Win32.GetForegroundWindow();
                            if (fg != h && GetAncestor(fg, 3) != h) FocusWindow(h);
                            break;
                        }
                }
                else focusPending = true;
            }

            if (!it.Bouncing)
            {
                it.BounceHeight = 0;
                it.BounceVelocity = 0;
                return it.LaunchStart != DateTime.MinValue || focusPending; // keep ticking while waiting for the next round
            }

            it.BounceVelocity -= Gravity * dt;
            it.BounceHeight += it.BounceVelocity * dt;
            if (it.BounceHeight <= 0)
            {
                it.BounceHeight = 0;
                it.BounceVelocity = -it.BounceVelocity * Restitution;
                if (it.BounceVelocity < MinBounceSpeed)
                {
                    it.BounceVelocity = 0;
                    it.RestUntil = DateTime.Now.AddMilliseconds(180);
                }
            }
            return true;
        }

        // Lays items out in a row centered in the pill. Scaled icons grow from the bottom and push neighbors aside.
        float[] Row(double[] scales, out float total)
        {
            var xs = new float[scales.Length];
            float x = 0;
            for (int i = 0; i < scales.Length; i++)
            {
                xs[i] = x;
                x += (float)(BasePx * scales[i]);
                if (i < scales.Length - 1)
                    x += Gap + (items[i + 1].SepBefore ? GroupGap : 0);
            }
            total = x;
            return xs;
        }

        void LayoutItems()
        {
            int n = items.Count;
            var scales = new double[n];
            for (int i = 0; i < n; i++) scales[i] = items[i].Scale;

            float total;
            float[] xs = Row(scales, out total);
            float pillWidth = Math.Max(BasePx, total) + 2 * PadX;
            float left = (Width - pillWidth) / 2f;
            pillRect = new RectangleF(left, Height - BottomMargin - pillHeight, pillWidth, pillHeight);

            float baseline = pillRect.Bottom - PadY;
            for (int i = 0; i < n; i++)
            {
                Item it = items[i];
                float size = (float)(BasePx * it.Scale);
                it.Rect = new RectangleF(left + PadX + xs[i] + (float)it.Shift.Value, baseline - size - (float)it.BounceHeight, size, size);
            }

            // The picked-up icon follows the cursor, lifted a little, and stays inside the pill.
            if (dragItem != null && items.Contains(dragItem))
            {
                float size = (float)(BasePx * dragItem.Scale);
                float cx = Math.Max(pillRect.Left + size / 2, Math.Min(pillRect.Right - size / 2, (float)dragX));
                dragItem.Rect = new RectangleF(cx - size / 2, baseline - size - 12, size, size);
            }
        }

        // ---------- rearranging ----------

        void BeginDrag(Item item)
        {
            dragItem = item;
            pressItem = null;
            hoverIndex = -1;
            dragX = item.Rect.Left + item.Rect.Width / 2;
            animTimer.Interval = 16;
        }

        // Swaps the dragged icon with a neighbour in its own section once the cursor passes that neighbour's centre.
        void ContinueDrag()
        {
            int k = items.IndexOf(dragItem);
            if (k < 0) return;

            while (k > 0 && items[k - 1].Section == dragItem.Section && dragX < items[k - 1].Rect.Left + items[k - 1].Rect.Width / 2)
            {
                MoveItem(k, k - 1);
                k--;
            }
            while (k < items.Count - 1 && items[k + 1].Section == dragItem.Section && dragX > items[k + 1].Rect.Left + items[k + 1].Rect.Width / 2)
            {
                MoveItem(k, k + 1);
                k++;
            }
        }

        void MoveItem(int from, int to)
        {
            var before = new Dictionary<Item, float>();
            foreach (Item it in items) before[it] = it.Rect.Left;

            Item moved = items[from];
            items.RemoveAt(from);
            items.Insert(to, moved);
            for (int i = 0; i < items.Count; i++) items[i].SepBefore = i > 0 && items[i - 1].Section != items[i].Section;
            LayoutItems();

            // Neighbours keep their on-screen position for a moment and then glide into their new slot.
            foreach (Item it in items)
                if (it != dragItem) it.Shift.Value += before[it] - it.Rect.Left;
        }

        void EndDrag()
        {
            Item moved = dragItem;
            dragItem = null;
            pressItem = null;
            if (moved == null) return;

            float releasedAt = moved.Rect.Left;

            // Dropping a pinned icon on the Recycle Bin removes it from the dock.
            Item bin = items.FirstOrDefault(i => i.Kind == Kind.Trash);
            if (bin != null && moved != bin && moved.Entry != null && moved.Kind != Kind.Trash
                && dragX >= bin.Rect.Left && dragX <= bin.Rect.Right)
            {
                RemovePin(moved.Entry.Raw);
                LoadConfig();
                RebuildItems();
                animTimer.Interval = 16;
                return;
            }

            // The icon that now follows it in the same section decides where its line goes in dock.txt.
            Entry next = null;
            int k = items.IndexOf(moved);
            for (int j = k + 1; j >= 0 && j < items.Count && items[j].Section == moved.Section; j++)
            {
                if (items[j].Entry != null) { next = items[j].Entry; break; }
            }
            if (moved.Entry != null) ReorderPin(moved.Entry, next, moved.Section);

            LoadConfig();
            RebuildItems();

            // Let the dropped icon settle into its slot instead of snapping there.
            Item dropped = items.FirstOrDefault(i => i.Key == moved.Key);
            if (dropped != null)
            {
                dropped.Shift.Value = releasedAt - dropped.Rect.Left;
                LayoutItems();
                Redraw();
            }
            animTimer.Interval = 16;
        }

        // Moves one pin's line within dock.txt: just before the pin that follows it, or to the end of its section.
        void ReorderPin(Entry moved, Entry next, int section)
        {
            try
            {
                List<string> lines = File.ReadAllLines(configPath).ToList();
                int from = lines.FindIndex(l => l == moved.Raw);
                if (from < 0) return;
                lines.RemoveAt(from);

                int to;
                if (next != null)
                {
                    to = lines.FindIndex(l => l == next.Raw);
                    if (to < 0) to = lines.Count;
                }
                else if (section == 0)
                {
                    int sep = lines.FindIndex(l => l.Trim() == "---");
                    to = sep >= 0 ? sep : lines.Count;
                }
                else to = lines.Count;

                lines.Insert(to, moved.Raw);
                File.WriteAllLines(configPath, lines);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        // Puts a window back above every other window without moving or activating it.
        internal static void KeepOnTop(Form f)
        {
            if (f != null && f.IsHandleCreated)
                SetWindowPos(f.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0200); // NOMOVE|NOSIZE|NOACTIVATE|NOOWNERZORDER
        }

        // After sleep, unlock or a display change: re-place the dock, hide the taskbar again and redraw.
        internal void Reassert()
        {
            if (hideTaskbar) TaskbarControl.EnsureHidden();
            Genie.Abort(); // an effect in flight was laid out for the old display
            UpdateReservation(); // moves the reserved strip to the display that is main now
            if (!ShowDockOn || Fullscreen.Active) return; // a hidden dock stays hidden through sleep, unlock and display changes
            FitCanvas();
            KeepOnTop(this);
            Redraw();
        }

        // Sizes the window to the current icon count (plus slack for magnification) and centers it at the screen bottom.
        void FitCanvas()
        {
            Rectangle area = DockScreen.Bounds;
            UpdateScale(area);
            var ones = new double[items.Count];
            for (int i = 0; i < ones.Length; i++) ones[i] = 1;
            float total;
            Row(ones, out total);
            if (!hideTaskbar)
            {
                area = DockScreen.WorkingArea;
                if (dockReserved) area.Height += dockReservedHeight; // the dock's own reservation must not push it up
            }
            int width = Math.Min(area.Width, (int)Math.Ceiling(total + 2 * PadX + CanvasSlack));
            Rectangle bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Bottom - FormHeight, width, FormHeight);
            if (Bounds != bounds) Bounds = bounds;
        }

        // ---------- items ----------

        void RebuildItems()
        {
            if (dragItem != null) return; // the order on screen is mid-change; it is saved when the icon is dropped

            if (File.Exists(configPath) && File.GetLastWriteTime(configPath) != configStamp)
            {
                LoadConfig();
                ApplyTaskbarSetting();
                ApplyDockMode();
            }
            if (hideTaskbar) TaskbarControl.EnsureHidden();

            Dictionary<string, List<IntPtr>> running = EnumerateRunningWindows();
            var previous = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (Item old in items) previous[old.Key] = old;

            var list = new List<Item>();
            var pinnedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool showStart = SetCfg.GetBool("show-start-icon", true);
            foreach (Entry e in entries.Where(x => x.Section == 0 && (showStart || x.Target != "[start]")))
                AddEntryItem(list, e, running, pinnedApps);

            // Entries in the right-hand section may also be apps; they count as pinned too.
            var rightItems = new List<Item>();
            foreach (Entry e in entries.Where(x => x.Section != 0 && (showStart || x.Target != "[start]")))
                AddEntryItem(rightItems, e, running, pinnedApps);

            // Running-only apps keep their place; newcomers join at the end (sorting by path made icons jump around).
            var oldOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int oi = 0; oi < items.Count; oi++) if (items[oi].Section == 1) oldOrder[items[oi].Key] = oi;
            foreach (string path in running.Keys.OrderBy(k => oldOrder.ContainsKey(k) ? oldOrder[k] : int.MaxValue).ThenBy(k => k))
            {
                if (!pinnedApps.Add(path)) continue;
                list.Add(NewItem(Kind.App, path, null, null, 1, running));
            }
            list.AddRange(rightItems);

            // The Recycle Bin always sits at the far right (like the macOS Trash), unless the user already pinned one.
            if (!list.Any(x => x.Kind == Kind.Trash))
                list.Add(NewItem(Kind.Trash, RecycleBin.Target, null, null, 3, running));

            for (int i = 0; i < list.Count; i++)
            {
                Item it = list[i];
                it.SepBefore = i > 0 && list[i - 1].Section != it.Section;

                Item old;
                if (previous.TryGetValue(it.Key, out old))
                {
                    it.ScaleSpring = old.ScaleSpring;
                    it.Goal = old.Goal;
                    it.BounceHeight = old.BounceHeight;
                    it.BounceVelocity = old.BounceVelocity;
                    it.LaunchStart = old.LaunchStart;
                    it.FocusUntil = old.FocusUntil;
                    it.RefocusAt = old.RefocusAt;
                    it.RestUntil = old.RestUntil;
                    it.Shift = old.Shift;
                }
            }

            var oldX = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (Item old in items) oldX[old.Key] = Left + old.Rect.Left; // on-screen position before the change

 items = list;
            foreach (Item fi in list) if (fi.Kind == Kind.Folder) DownloadsFan.Prefetch(Environment.ExpandEnvironmentVariables(fi.Target)); // warm the fan caches
            if (hoverIndex >= items.Count) hoverIndex = -1;
            refreshTimer.Interval = items.Any(i => i.LaunchStart != DateTime.MinValue || i.FocusUntil != DateTime.MinValue) ? 500 : 2000;
            FitCanvas();
            LayoutItems();

            // Icons that stay glide from where they were to their new slot when an app opens or closes.
            bool slid = false;
            if (!Motion.Reduced)
                foreach (Item it in items)
                {
                    float px;
                    if (!oldX.TryGetValue(it.Key, out px)) continue;
                    float d = px - (Left + it.Rect.Left);
                    if (Math.Abs(d) > 0.5f) { it.Shift.Value += d; slid = true; }
                }
            if (slid) { LayoutItems(); animTimer.Interval = 16; if (!animTimer.Enabled) animTimer.Start(); }
            Redraw();
        }

        void AddEntryItem(List<Item> list, Entry e, Dictionary<string, List<IntPtr>> running, HashSet<string> pinnedApps)
        {
            Kind kind;
            if (!TryClassify(e.Target, out kind)) return;
            if (kind == Kind.App && !pinnedApps.Add(e.Target)) return;
            if (kind == Kind.App)
            {
                string exe = MatchRunning(running, e.Target);
                if (exe != null) pinnedApps.Add(exe); // keeps the running-only duplicate out of the dock
            }
            list.Add(NewItem(kind, e.Target, e.Label, e, e.Section, running));
        }

        Item NewItem(Kind kind, string target, string label, Entry entry, int section, Dictionary<string, List<IntPtr>> running)
        {
            var it = new Item
            {
                Kind = kind,
                Target = target,
                Entry = entry,
                Section = section,
                Label = !string.IsNullOrEmpty(label) ? label : DefaultLabel(kind, target)
            };
            // Universal lookup: a running app's window carries its own AppUserModelID, which names the Start-menu entry
            // (proper name + icon) no matter how odd the exe is called.
            List<IntPtr> win;
            string runKey = kind == Kind.App ? MatchRunning(running, target) : null;
            if (runKey != null && !ShellIcons.IsAppsFolderItem(target) && running.TryGetValue(runKey, out win))
            {
                foreach (IntPtr h in win.Take(3))
                {
                    string id = Pwa.AumidOfWindow(h);
                    if (string.IsNullOrEmpty(id) || Pwa.IsWebAppId(id)) continue; // web apps share the browser's exe
                    it.Aumid = id;
                    lock (AppDirectory.Names)
                    {
                        string listed;
                        if (string.IsNullOrEmpty(label) && AppDirectory.Names.TryGetValue(id, out listed) && !string.IsNullOrWhiteSpace(listed)) it.Label = listed;
                    }
                    break;
                }
            }
            if (kind == Kind.App && Pwa.IsWebAppId(Pwa.IdOfTarget(target)) && running.TryGetValue(target, out win))
                foreach (IntPtr h in win.Take(3)) { it.WinIcon = Pwa.WindowIcon(h); if (it.WinIcon != null) break; }
            it.Icon = GetIcon(it);

            List<IntPtr> handles;
            if (runKey != null && running.TryGetValue(runKey, out handles)) it.Handles = handles;
            return it;
        }

        static readonly System.Text.RegularExpressions.Regex PkgVersion = new System.Text.RegularExpressions.Regex(@"(WindowsApps\\[^_\\]+)_[^_\\]+_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly Dictionary<string, string> LnkCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Store packages carry their version in the folder name, which changes on update; ignore it when comparing.
        static string NormPath(string p) { return PkgVersion.Replace(p, "$1_*_"); }

        static string ResolveLnk(string path)
        {
            string r;
            if (LnkCache.TryGetValue(path, out r)) return r;
            r = null;
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                r = (string)sh.CreateShortcut(path).TargetPath;
                r = string.IsNullOrEmpty(r) ? null : Environment.ExpandEnvironmentVariables(r);
            }
            catch { }
            LnkCache[path] = r;
            return r;
        }

        // Finds the key in `running` (an exe path) that belongs to a pinned target: the exe itself, the exe a .lnk points
        // at, or, for Start-menu / Store items, the exe whose window carries that AppUserModelID (or whose path ends
        // the item's "{KnownFolder}\Sub\App.exe" id).
        static string MatchRunning(Dictionary<string, List<IntPtr>> running, string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (running.ContainsKey(target)) return target;

            string id = Pwa.IdOfTarget(target);
            if (id == null)
            {
                string exe = target;
                if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) exe = ResolveLnk(target);
                if (exe == null) return null;
                if (running.ContainsKey(exe)) return exe;
                string n = NormPath(exe);
                foreach (string k in running.Keys)
                    if (string.Equals(NormPath(k), n, StringComparison.OrdinalIgnoreCase)) return k;
                return null;
            }

            foreach (KeyValuePair<string, List<IntPtr>> kv in running)
            {
                if (kv.Key.StartsWith(Pwa.AppsFolder, StringComparison.OrdinalIgnoreCase)) continue; // web apps match by exact key above
                foreach (IntPtr h in kv.Value.Take(4))
                {
                    string a = Pwa.AumidOfWindow(h);
                    if (!string.IsNullOrEmpty(a) && string.Equals(a, id, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                }
            }
            // Store apps: "<Name>_<publisherId>!<App>" matches the exe under WindowsApps\<Name>_<version>_<arch>__<publisherId>.
            int bang = id.IndexOf('!'), us = id.LastIndexOf('_', bang > 0 ? bang : id.Length - 1);
            if (bang > 0 && us > 0)
            {
                string pkg = id.Substring(0, us), pub = id.Substring(us + 1, bang - us - 1);
                foreach (string k in running.Keys)
                    if (k.IndexOf("\\WindowsApps\\" + pkg + "_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        k.IndexOf("__" + pub + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return k;
            }
            int brace = id.IndexOf("}\\", StringComparison.Ordinal);
            if (id.StartsWith("{") && brace > 0)
            {
                string tail = "\\" + id.Substring(brace + 2);
                foreach (string k in running.Keys)
                    if (k.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return k;
            }
            return null;
        }

        static string DefaultLabel(Kind kind, string target)
        {
            switch (kind)
            {
                case Kind.Start: return "Start";
                case Kind.Mission: return "Mission Control";
                case Kind.Launchpad: return "All Apps";
                case Kind.Trash: return "Recycle Bin";
                case Kind.Url:
                    Uri uri;
                    return Uri.TryCreate(target, UriKind.Absolute, out uri) && uri.Host.Length > 0 ? uri.Host : target;
                case Kind.Folder:
                    string name = Path.GetFileName(target.TrimEnd('\\', '/'));
                    return name.Length > 0 ? name : target;
                default:
                    if (ShellIcons.IsAppsFolderItem(target)) return AppDirectory.NameFor(target.Substring(ShellIcons.AppsFolderPrefix.Length));
                    if (kind == Kind.App)
                    {
                        // The app's own display name ("Microsoft Edge"), not its file name ("msedge").
                        try
                        {
                            string description = FileVersionInfo.GetVersionInfo(target).FileDescription;
                            // A Store app's exe can carry a raw name ("WhatsApp.Root"): prefer the Start-menu name it contains.
                            var listed = AppDirectory.Match(!string.IsNullOrWhiteSpace(description) ? description : Path.GetFileNameWithoutExtension(target));
                            if (listed != null && target.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) return listed.Value.Key;
                            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
                        }
                        catch { }
                    }
                    return Path.GetFileNameWithoutExtension(target);
            }
        }

        static bool IsUri(string t)
        {
            return t.IndexOf("://", StringComparison.Ordinal) > 0
                || t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAppFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return (ext == ".exe" || ext == ".lnk") && File.Exists(path);
        }

        static bool TryClassify(string target, out Kind kind)
        {
            kind = Kind.File;
            if (target == "[start]") { kind = Kind.Start; return true; }
            if (target == "[apps]") { kind = Kind.Launchpad; return true; }
            if (RecycleBin.IsTarget(target)) { kind = Kind.Trash; return true; }
            if (ShellIcons.IsAppsFolderItem(target)) { kind = Kind.App; return true; } // Store apps and other Start menu apps
            if (IsUri(target)) { kind = Kind.Url; return true; }
            if (Directory.Exists(target)) { kind = Kind.Folder; return true; }
            if (File.Exists(target)) { kind = IsAppFile(target) ? Kind.App : Kind.File; return true; }
            return false;
        }

        static bool FanClickMode(Item it) { return it.Kind == Kind.Folder && SetCfg.GetBool("fan-click", false); }

        void ShowPreview(Item it)
        {
            hoverSince = DateTime.MaxValue; // once per hover
            if (it.Kind == Kind.Folder)
            {
                Item dt = it;
                DownloadsFan fan = null;
                fan = new DownloadsFan(Environment.ExpandEnvironmentVariables(it.Target), PointToScreen(new Point((int)(it.Rect.Left + it.Rect.Width / 2), CardAnchorY(it))), sp =>
                {
                    // The whole column from the icon down to the screen edge counts, so resting on the icon never closes the fan.
                    Item cur = items.FirstOrDefault(x => x.Key == dt.Key) ?? dt; Rectangle r = RectangleToScreen(Rectangle.Round(cur.Rect));
                    r.Inflate(8, 0);
                    r.Y -= 60; r.Height = Screen.FromPoint(sp).Bounds.Bottom - r.Y + 40;
                    return r.Contains(sp);
                }, () => { if (preview == fan) { preview = null; lastPreviewClose = DateTime.UtcNow; } });
                preview = fan;
                previewItem = it;
                fan.Show();
                return;
            }
            var live = it.Handles.Where(h => NativeMethods.IsWindow(h)).ToList();
            if (live.Count < 1) return;
            Point c = PointToScreen(new Point((int)(it.Rect.Left + it.Rect.Width / 2), CardAnchorY(it)));
            Item target = it;
            WindowPreview pv = null;
            pv = new WindowPreview(live, it.Icon, c, FocusWindow, sp =>
            {
                Rectangle r = RectangleToScreen(Rectangle.Round(target.Rect));
                r.Inflate(4, 20);
                return r.Contains(sp);
            }, () => { if (preview == pv) { preview = null; lastPreviewClose = DateTime.UtcNow; } });
            preview = pv;
            previewItem = it;
            pv.Show();
        }

        static void FocusWindow(IntPtr target) { WinUtil.Focus(target); }

        void Activate(Item it)
        {
            if (it.Kind == Kind.Folder)
            {
                // Clicking a folder stack opens its fan (or folds it back when it is already open); "Open in Explorer" sits at the fan's top.
                bool wasOpen = preview is DownloadsFan && previewItem != null && previewItem.Key == it.Key;
                if (preview != null) preview.Close();
                if (!wasOpen) { ShowPreview(it); if (preview is DownloadsFan) ((DownloadsFan)preview).Pinned = true; }
                return;
            }
            if (preview != null) preview.Close();
            // Clicking a dock item while Launchpad or Mission Control is open closes it, then the item opens as usual.
            if (it.Kind != Kind.Mission && it.Kind != Kind.Launchpad) MissionControl.DismissForLaunch();

            if (it.Kind == Kind.Start)
            {
                NativeMethods.keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                return;
            }
            if (it.Kind == Kind.Launchpad) { MissionControl.Toggle(true); return; }
            if (it.Kind == Kind.Trash) { RecycleBin.Open(); it.BounceVelocity = LaunchSpeed * 0.7; return; }

            // Clicking the icon of the app that is in front minimizes its front window (the genie effect plays); otherwise the
            // app's window is restored if needed and brought forward. The dock never takes focus, so the first click while
            // another app is in front always lands on the "bring forward" branch.
            IntPtr front = Win32.GetForegroundWindow();
            IntPtr frontOwner = front == IntPtr.Zero ? IntPtr.Zero : GetAncestor(front, 3); // GA_ROOTOWNER
            foreach (IntPtr h in it.Handles)
            {
                if (!NativeMethods.IsWindow(h) || (h != front && h != frontOwner)) continue;
                if (!NativeMethods.IsIconic(h)) { NativeMethods.ShowWindow(h, SW_MINIMIZE); return; }
                break;
            }

            IntPtr target = IntPtr.Zero;
            foreach (IntPtr h in it.Handles)
            {
                if (NativeMethods.IsWindow(h)) { target = h; break; }
            }

            if (target != IntPtr.Zero)
            {
                WinUtil.Focus(target);
            }
            else
            {
                if (!Pwa.Launch(it.Target)) return; // a web app that is already open is raised, not started again

                // Only apps get the launch bounce; folders and links just open.
                if (it.Kind == Kind.App)
                {
                    it.LaunchStart = DateTime.Now;
                    it.FocusUntil = DateTime.Now.AddSeconds(20);
                    it.BounceVelocity = LaunchSpeed;
                    refreshTimer.Interval = 500; // notice the new window quickly so the bounce stops on time
                }
                else
                {
                    it.BounceVelocity = LaunchSpeed * 0.7; // one short acknowledgement bounce
                }
            }
        }

        // ---------- config ----------

        void LoadConfig()
        {
            if (!File.Exists(configPath)) File.WriteAllLines(configPath, DefaultConfig);
            configStamp = File.GetLastWriteTime(configPath);

            entries = new List<Entry>();
            hideTaskbar = true;
            int section = 0;

            foreach (string raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line == "---") { section = 2; continue; }

                if (line.StartsWith("@"))
                {
                    string opt = line.Substring(1).Trim().ToLowerInvariant();
                    if (opt.StartsWith("hide-taskbar"))
                        hideTaskbar = !(opt.EndsWith("off") || opt.EndsWith("false") || opt.EndsWith("no"));
                    continue;
                }

                string label = null, target = line;
                int bar = line.IndexOf('|');
                if (bar > 0)
                {
                    label = line.Substring(0, bar).Trim();
                    target = line.Substring(bar + 1).Trim();
                }
                target = Environment.ExpandEnvironmentVariables(target.Trim('"'));
                entries.Add(new Entry { Section = section, Label = label, Target = target, Raw = raw });
            }
        }

        void ApplyTaskbarSetting()
        {
            if (hideTaskbar && !TaskbarControl.IsHidden) TaskbarControl.Hide();
            else if (!hideTaskbar && TaskbarControl.IsHidden) TaskbarControl.Restore();
        }

        // Adds a line to dock.txt: apps go before the --- line, everything else after it.
        void AddPin(string line, string target, int section)
        {
            try
            {
                List<string> lines = File.ReadAllLines(configPath).ToList();
                foreach (string l in lines)
                {
                    string t = l.Trim();
                    int bar = t.IndexOf('|');
                    string existing = bar > 0 ? t.Substring(bar + 1).Trim() : t;
                    if (string.Equals(Environment.ExpandEnvironmentVariables(existing), target, StringComparison.OrdinalIgnoreCase)) return;
                }

                int sep = lines.FindIndex(l => l.Trim() == "---");
                if (section == 0)
                {
                    if (sep >= 0) lines.Insert(sep, line); else lines.Add(line);
                }
                else
                {
                    if (sep < 0) lines.Add("---");
                    lines.Add(line);
                }
                File.WriteAllLines(configPath, lines);
            }
            catch { }
            LoadConfig();
            RebuildItems();
        }

        void RemovePin(string raw)
        {
            try
            {
                List<string> lines = File.ReadAllLines(configPath).ToList();
                int index = lines.FindIndex(l => l == raw);
                if (index >= 0)
                {
                    lines.RemoveAt(index);
                    File.WriteAllLines(configPath, lines);
                }
            }
            catch { }
            LoadConfig();
            RebuildItems();
        }

        // Apps in the dock that have no macOS icon yet (their names, for the macosicons.com lookup).
        public List<string> AppsWithoutMacIcon()
        {
            var names = new List<string>();
            foreach (Item it in items)
            {
                if (it.Kind != Kind.App) continue;
                using (Bitmap pack = IconPack.ForItem(it.Kind, it.Target, it.Label))
                    if (pack == null) names.Add(it.Label);
            }
            return names;
        }

        // Re-reads every icon, e.g. after the icon pack changed.
        public void RefreshIcons()
        {
            foreach (Bitmap b in iconCache.Values) b.Dispose();
            iconCache.Clear();
            items = new List<Item>(); // drop references to the disposed bitmaps before rebuilding
            RebuildItems();
        }

        // The three "add to Dock" prompts. Also reachable from the top bar's logo menu.
        public void PromptAddFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose a folder to add to the Dock" })
                if (d.ShowDialog() == DialogResult.OK) AddPin(d.SelectedPath, d.SelectedPath, 2);
        }

        public void PromptAddApp()
        {
            using (var d = new OpenFileDialog { Title = "Add an app or file to the Dock", DereferenceLinks = false })
                if (d.ShowDialog() == DialogResult.OK) AddPath(d.FileName);
        }

        public void PromptAddWebsite()
        {
            AddWebsite(Microsoft.VisualBasic.Interaction.InputBox("Website address:", "Add website to the Dock", "https://"));
        }

        // Used by Mission Control's All Apps view (drag an app onto the dock, or press its + button).
        public const string AppDragFormat = "MacDockApp";

        public void AddApp(string name, string target)
        {
            AddPin(name + " | " + target, target, 0);
        }

        public bool IsPinned(string target)
        {
            return entries.Any(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));
        }

        public void RemoveApp(string target)
        {
            Entry entry = entries.FirstOrDefault(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));
            if (entry != null) RemovePin(entry.Raw);
        }

        void AddPath(string path)
        {
            AddPin(path, path, IsAppFile(path) ? 0 : 2);
        }

        void AddWebsite(string url)
        {
            url = url.Trim();
            if (!IsUri(url)) return;
            AddPin(DefaultLabel(Kind.Url, url) + " | " + url, url, 2);
        }

        // ---------- input ----------

        int IndexAt(int x, int y)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Rect.Contains(x, y)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            // Only pinned items can be rearranged (running apps that aren't pinned have no place in dock.txt).
            int index = IndexAt(e.X, e.Y);
            if (index >= 0 && items[index].Entry != null)
            {
                pressItem = items[index];
                pressPoint = e.Location;
                pressTime = DateTime.Now;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragItem != null) { EndDrag(); return; } // dropping after a long press is not a click
            pressItem = null;

            int index = IndexAt(e.X, e.Y);

            if (e.Button == MouseButtons.Right) ShowMenu(index >= 0 ? items[index] : null, e.Location);
            else if (e.Button == MouseButtons.Left && index >= 0) Activate(items[index]);
        }

        // Shows or hides the Windows Start icon. Stored as show-start-icon in the settings; the "[start]" pin stays in dock.txt.
        internal void SetStartIcon(bool on)
        {
            SetCfg.SetBool("show-start-icon", on);
            RebuildItems();
        }

        void ShowMenu(Item it, Point at)
        {
            if (popup != null) popup.Close();
            if (preview != null) preview.Close();
            var list = new List<MenuEntry>();

            if (it != null && it.Kind == Kind.Trash)
            {
                list.Add(MenuEntry.Item("Open", null, () => RecycleBin.Open()));
                list.Add(MenuEntry.Item("Empty Recycle Bin", null, RecycleBin.IsFull() ? (Action)(() => RecycleBin.Empty()) : null));
                list.Add(MenuEntry.Sep());
            }
            else if (it == null && !SetCfg.GetBool("show-start-icon", true))
            {
                list.Add(MenuEntry.Item("Show Start Icon", null, () => SetStartIcon(true)));
                list.Add(MenuEntry.Sep());
            }
            else if (it != null)
            {
                list.Add(MenuEntry.Item(ItemNames.Display(it.Target, it.Label), null, null)); // header, greyed out

                if (it.Handles != null && it.Handles.Count > 0)
                {
                    Item targetItem = it;
                    list.Add(MenuEntry.Item("Close", null, () => CloseItem(targetItem)));
                    if (it.Handles.Count >= 2) list.Add(MenuEntry.Item("Close all windows", null, () => CloseItem(targetItem))); // WM_CLOSE to each window only; never touches MacDock
                    list.Add(MenuEntry.Item("Force Close", null, () => ForceCloseItem(targetItem)));
                    list.Add(MenuEntry.Sep());
                }

                if (it.Kind == Kind.Start)
                    list.Add(MenuEntry.Item("Hide Start Icon", null, () => SetStartIcon(false)));
                if (it.Entry != null && it.Kind != Kind.Start)
                {
                    Entry entry = it.Entry;
                    list.Add(MenuEntry.Item("Remove from Dock", null, () => RemovePin(entry.Raw)));
                }
                else if (it.Entry == null && it.Kind == Kind.App)
                {
                    string path = it.Target;
                    list.Add(MenuEntry.Item("Keep in Dock", null, () => AddPin(path, path, 0)));
                }
                list.Add(MenuEntry.Sep());
            }

            if (it != null && (it.Kind == Kind.App || it.Kind == Kind.Folder || it.Kind == Kind.Url || it.Kind == Kind.File))
            {
                Item rn = it;
                list.Add(MenuEntry.Item("Rename...", null, () => BeginInvoke((Action)(() =>
                {
                    string curName = ItemNames.Display(rn.Target, rn.Label);
                    string r = RenameBox.Show(curName);
                    if (r == null || r == curName) return;
                    ItemNames.Set(rn.Target, r);
                    Invalidate();
                }))));
                if (ItemNames.Get(rn.Target) != null)
                    list.Add(MenuEntry.Item("Reset Name", null, () => { ItemNames.Set(rn.Target, null); Invalidate(); }));
                list.Add(MenuEntry.Sep());
            }

            if (it != null && it.Kind == Kind.Folder)
            {
                string cur = DownloadsFan.GetStyle();
                list.Add(MenuEntry.Item((cur == "fan" ? "✓ " : "    ") + "View as Fan", null, () => DownloadsFan.SetStyle("fan")));
                list.Add(MenuEntry.Item((cur == "grid" ? "✓ " : "    ") + "View as Grid", null, () => DownloadsFan.SetStyle("grid")));
                list.Add(MenuEntry.Item((cur == "list" ? "✓ " : "    ") + "View as List", null, () => DownloadsFan.SetStyle("list")));
                list.Add(MenuEntry.Item((SetCfg.GetBool("fan-click", false) ? "✓ " : "    ") + "Open on Click (not Hover)", null, () => SetCfg.SetBool("fan-click", !SetCfg.GetBool("fan-click", false))));
                list.Add(MenuEntry.Sep());
                string folderPath = it.Target;
                string curColor = FolderColor.Get(folderPath);
                foreach (var fc in FolderColor.Choices)
                {
                    string cname = fc.Key;
                    var me = MenuEntry.Item((curColor == cname ? "✓ " : "    ") + "Folder Color: " + cname, null, () => { FolderColor.Set(folderPath, cname); RefreshIcons(); });
                    me.Dot = fc.Value;
                    list.Add(me);
                }
                list.Add(MenuEntry.Item("    Folder Color: Reset", null, () => { FolderColor.Set(folderPath, null); RefreshIcons(); }));
                list.Add(MenuEntry.Sep());
            }
            list.Add(MenuEntry.Item("Add folder to Dock...", null, () => PromptAddFolder()));
            list.Add(MenuEntry.Item("Add app or file to Dock...", null, () => PromptAddApp()));
            list.Add(MenuEntry.Item("Add website to Dock...", null, () => PromptAddWebsite()));
            list.Add(MenuEntry.Sep());

            // The same frosted popup as the top bar's menus; it opens upward from the click, above the dock.
            Point screenAt = PointToScreen(at);
            MenuPopup menu = null;
            menu = new MenuPopup(list, screenAt, 0, () => { if (popup == menu) popup = null; });
            menu.launchpadGlass = true;
            Rectangle area = Screen.FromPoint(screenAt).Bounds;
            int pillTop = Top + (int)Math.Round(pillRect.Top);
            int clearTop = pillTop; // clear the hover title label (drawn 36px above the icon) with a gap
            if (it != null) clearTop = Math.Min(clearTop, Top + (int)Math.Round(it.Rect.Top) - 36);
            menu.Top = Math.Max(area.Top + 4, clearTop - menu.Height - 8);
            popup = menu;
            menu.Show();
        }

        // Screen-space-relative Y above the hover title label (top = Rect.Top - 36px) plus an 8px gap; shared by the folder fan and preview card.
        int CardAnchorY(Item it)
        {
            // Use the icon's fully magnified (and bounced) top, not its current one, so the card stays above the label
            // even if the icon is still growing when the card opens. Label top = icon top - 36px.
            double full = it.Rect.Bottom + it.BounceHeight - BasePx * Math.Max(1.0, MaxScale);
            double top = Math.Min(it.Rect.Top, full);
            return (int)Math.Round(top - Px(36)) - Px(8);
        }

        static void CloseItem(Item it)
        {
            if (it == null || it.Handles == null) return;
            foreach (IntPtr h in it.Handles.ToList())
            {
                try { Win32.PostMessage(h, WinUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                catch { }
            }
        }

        static void ForceCloseItem(Item it)
        {
            if (it == null) return;
            var pids = new HashSet<uint>();

            if (it.Handles != null)
            {
                foreach (IntPtr h in it.Handles)
                {
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(h, out pid);
                    if (pid != 0) pids.Add(pid);
                }
            }

            if (pids.Count == 0 && it.Kind == Kind.App && !string.IsNullOrEmpty(it.Target))
            {
                try
                {
                    string exeName = Path.GetFileNameWithoutExtension(it.Target);
                    foreach (var p in Process.GetProcessesByName(exeName))
                    {
                        try { if (!p.ProcessName.Equals("MacDock", StringComparison.OrdinalIgnoreCase)) pids.Add((uint)p.Id); } catch { }
                    }
                }
                catch { }
            }

            pids.Remove((uint)Process.GetCurrentProcess().Id); // force quit must never take the dock down with it
            Program.Log("FORCECLOSE " + it.Label + " pids=" + string.Join(",", pids));
            foreach (uint pid in pids)
            {
                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                    {
                        p.Kill();
                    }
                }
                catch { }
            }
        }
        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            bool ok = e.Data.GetDataPresent(AppDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            UpdateDropTarget(e);
        }

        int ItemAtDrag(DragEventArgs e)
        {
            Point c = PointToClient(new Point(e.X, e.Y));
            return IndexAt(c.X, c.Y);
        }

        bool OverTrash(DragEventArgs e)
        {
            int i = ItemAtDrag(e);
            return i >= 0 && items[i].Kind == Kind.Trash;
        }

        static DragDropEffects Pick(DragDropEffects allowed, params DragDropEffects[] prefer)
        {
            foreach (DragDropEffects p in prefer) if ((allowed & p) != 0) return p;
            return DragDropEffects.None;
        }

        // Which effect (cursor) the icon under a file drag offers; also tracks the highlight and the hover dwell.
        void UpdateDropTarget(DragEventArgs e)
        {
            int i = ItemAtDrag(e);
            string[] files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            if (i != dropIndex) { dropIndex = i; dropSince = DateTime.UtcNow; }
            Item hot = null;
            if (files != null && i >= 0)
            {
                Item it = items[i];
                DragDropEffects eff = DragDropEffects.None;
                if (it.Kind == Kind.Trash) eff = Pick(e.AllowedEffect, DragDropEffects.Move, DragDropEffects.Copy);
                else if (it.Kind == Kind.Folder) eff = DockDrop.Choose(files, Environment.ExpandEnvironmentVariables(it.Target), e.KeyState, e.AllowedEffect);
                else if (it.Kind == Kind.App && DockDrop.CanOpenWith(it.Target)) eff = Pick(e.AllowedEffect, DragDropEffects.Copy, DragDropEffects.Link, DragDropEffects.Move);
                else eff = Pick(e.AllowedEffect, DragDropEffects.Copy, DragDropEffects.Link, DragDropEffects.Move); // anything else just pins the file
                if (it.Kind == Kind.Trash || it.Kind == Kind.Folder || it.Kind == Kind.App && DockDrop.CanOpenWith(it.Target))
                {
                    e.Effect = eff;
                    if (eff != DragDropEffects.None) hot = it;
                }
                else e.Effect = eff;
            }
            else if (files != null) e.Effect = Pick(e.AllowedEffect, DragDropEffects.Copy, DragDropEffects.Link, DragDropEffects.Move);
            if (hot != dropHot) { dropHot = hot; Redraw(); }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            UpdateDropTarget(e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            // The cursor may be moving onto a window preview: that closes itself, so leave it alone.
            dropIndex = -1; dropSince = DateTime.MaxValue;
            if (dropHot != null) { dropHot = null; Redraw(); }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            int di = ItemAtDrag(e);
            Item target = di >= 0 ? items[di] : null;
            dropIndex = -1; dropSince = DateTime.MaxValue;
            if (dropHot != null) { dropHot = null; Redraw(); }

            string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped != null && target != null)
            {
                if (target.Kind == Kind.Trash)
                {
                    // Deferred so the drag source (Explorer) isn't blocked while confirmation/progress UI shows.
                    BeginInvoke((Action)(() => { RecycleBin.Delete(dropped); RebuildItems(); }));
                    return;
                }
                if (target.Kind == Kind.Folder)
                {
                    string dest = Environment.ExpandEnvironmentVariables(target.Target);
                    DragDropEffects eff = DockDrop.Choose(dropped, dest, e.KeyState, e.AllowedEffect);
                    if (eff != DragDropEffects.None) DockDrop.Transfer((string[])dropped.Clone(), dest, eff, null);
                    return;
                }
                if (target.Kind == Kind.App && DockDrop.CanOpenWith(target.Target))
                {
                    string[] files = (string[])dropped.Clone();
                    if (preview != null) preview.Close();
                    if (DockDrop.OpenWith(target.Target, files) && target.Handles.Count == 0)
                    {
                        target.LaunchStart = DateTime.Now;
                        target.FocusUntil = DateTime.Now.AddSeconds(20);
                        target.BounceVelocity = LaunchSpeed;
                        refreshTimer.Interval = 500;
                    }
                    return;
                }
            }

            // From Mission Control: "name\ntarget".
            string app = e.Data.GetData(AppDragFormat) as string;
            if (app != null)
            {
                string[] parts = app.Split('\n');
                if (parts.Length == 2) AddApp(parts[0], parts[1]);
                return;
            }

            if (dropped != null)
            {
                foreach (string f in dropped) AddPath(f);
                return;
            }
            string text = e.Data.GetData(DataFormats.UnicodeText) as string;
            if (text != null) AddWebsite(text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Genie.Uninstall();
                WindowTheme.Uninstall();
                ReleaseBottom();
                TaskbarControl.Restore();
                foreach (Bitmap b in iconCache.Values) b.Dispose();
                if (surface != null) surface.Dispose();
                if (popup != null) popup.Close();
                vsync.Dispose();
                animTimer.Dispose();
                refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- icons ----------

        Bitmap GetIcon(Item it)
        {
            bool shared = (it.Kind == Kind.Url && !it.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) || it.Kind == Kind.Start || it.Kind == Kind.Mission || it.Kind == Kind.Launchpad;
            string key = shared ? "@" + it.Kind : it.Kind == Kind.Trash ? "@Trash" + RecycleBin.IsFull() : it.Target;
            Bitmap bmp;
            if (!iconCache.TryGetValue(key, out bmp))
            {
                // Disk cache for stable icons (not http site icons, which start as a globe, nor the changing Trash / coloured folders).
                bool disk = it.Kind != Kind.Trash && it.Kind != Kind.Folder && !(it.Kind == Kind.Url && it.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                bmp = disk ? IconDisk.TryLoad(key, it.Target) : null;
                if (bmp == null)
                {
                    bmp = BuildIcon(it);
                    if (disk) IconDisk.Save(key, it.Target, bmp);
                }
                iconCache[key] = bmp;
            }
            return bmp;
        }

        static Bitmap BuildIcon(Item it)
        {
            // An installed icon pack wins when it has a macOS counterpart for this app.
            Bitmap packed = IconPack.ForItem(it.Kind, it.Target, it.Label);
            if (packed != null) return packed;

            switch (it.Kind)
            {
                case Kind.Start:
                    return StartIcon();
                case Kind.Mission:
                    return MissionIcon();
                case Kind.Launchpad:
                    return LaunchpadIcon();
                case Kind.Trash:
                    return TrashIcon(RecycleBin.IsFull());
                case Kind.Folder:
                    // Folders keep their own artwork, like the folder stacks in the macOS dock.
                    return FolderColor.Apply(it.Target, LoadShellIcon(it.Target, 0, 0) ?? Logo());
                case Kind.Url:
                    // Drawn here instead of asking Windows for the ".url" icon: that lookup runs the browser's icon
                    // handler, which tries to write to the Favorites folder and trips Controlled Folder Access.
                    if (it.Target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return GearIcon();
                    Bitmap site = SiteIcons.Get(it.Target); // the site's own icon once fetched; globe until then
                    return site != null ? Tile(ScaleIcon(site)) : WebIcon();
                default:
                    if (ShellIcons.IsAppsFolderItem(it.Target))
                    {
                        bool isWeb = Pwa.IsWebAppId(Pwa.IdOfTarget(it.Target));
                        Bitmap webIcon = isWeb ? Pwa.IconFor(it.Label) : null; // web apps keep their own icon
                        if (webIcon == null && it.WinIcon != null) webIcon = it.WinIcon; // else the icon the app's own window carries
                        if (isWeb && webIcon == null) return Tile(Logo()); // never fall back to the browser's icon
                        return Tile(webIcon ?? ShellIcons.Load(it.Target, IconSource) ?? Logo());
                    }

                    Bitmap icon = null;
                    // 1) the Start-menu entry of the window's own app id; 2) one whose name matches (Store exes)
                    string aumid = it.Aumid;
                    if (aumid == null && it.Target.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var listed = AppDirectory.Match(it.Label);
                        if (listed != null) aumid = listed.Value.Value;
                    }
                    if (aumid != null)
                    {
                        icon = ShellIcons.Load(ShellIcons.AppsFolderPrefix + aumid, IconSource);
                        if (icon != null && IsBlank(icon)) { icon.Dispose(); icon = null; }
                        if (icon != null) return Tile(icon);
                    }
                    // 3) the exe's shell icon, unless it came back empty; 4) the associated icon; 5) the generic app icon
                    icon = LoadShellIcon(it.Target, 0, 0);
                    if (icon != null && IsBlank(icon)) { icon.Dispose(); icon = null; }
                    if (icon == null)
                    {
                        try { using (Icon i = Icon.ExtractAssociatedIcon(it.Target)) icon = ScaleIcon(i.ToBitmap()); if (IsBlank(icon)) { icon.Dispose(); icon = Logo(); } }
                        catch { icon = Logo(); }
                    }
                    return Tile(icon);
            }
        }

        // Pulls the 256px shell icon so it stays sharp when zoomed.
        internal static Bitmap LoadShellIcon(string path, uint extraFlags, uint attrs)
        {
            try
            {
                var info = new NativeMethods.SHFILEINFO();
                IntPtr ok = NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX | extraFlags);
                Guid iid = typeof(NativeMethods.IImageList).GUID;
                NativeMethods.IImageList list;
                if (ok != IntPtr.Zero && NativeMethods.SHGetImageList(SHIL_JUMBO, ref iid, out list) == 0)
                {
                    IntPtr hIcon = IntPtr.Zero;
                    list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref hIcon);
                    Marshal.ReleaseComObject(list);
                    if (hIcon != IntPtr.Zero)
                    {
                        Bitmap big;
                        big = ShellIcons.FromHIcon(hIcon);
                        if (big == null) using (Icon icon = Icon.FromHandle(hIcon)) big = icon.ToBitmap();
                        NativeMethods.DestroyIcon(hIcon);
                        return ScaleIcon(big);
                    }
                }
            }
            catch { }
            return null;
        }

        // macOS-style trash: a tapered wire-mesh can with a rim; crumpled paper peeks out when it is full.
        static Bitmap TrashIcon(bool full)
        {
            try
            {
                int S = IconSource;
                var bmp = new Bitmap(S, S);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float cx = S / 2f, top = S * 0.20f, bot = S * 0.90f, tw = S * 0.31f, bw = S * 0.235f;
                    float rimH = S * 0.075f;
                    if (full)
                    {
                        using (var paper = new SolidBrush(Color.FromArgb(235, 244, 246, 250)))
                        using (var pen = new Pen(Color.FromArgb(120, 150, 156, 170), 1.5f))
                        {
                            g.FillPolygon(paper, new[] { new PointF(cx - tw * 0.8f, top + rimH), new PointF(cx - tw * 0.45f, top - S * 0.07f), new PointF(cx - tw * 0.05f, top - S * 0.02f), new PointF(cx + tw * 0.35f, top - S * 0.09f), new PointF(cx + tw * 0.8f, top + rimH) });
                            g.DrawLines(pen, new[] { new PointF(cx - tw * 0.45f, top - S * 0.07f), new PointF(cx - tw * 0.05f, top - S * 0.02f), new PointF(cx + tw * 0.35f, top - S * 0.09f) });
                        }
                    }
                    var body = new[] { new PointF(cx - tw, top), new PointF(cx + tw, top), new PointF(cx + bw, bot), new PointF(cx - bw, bot) };
                    using (var bodyBrush = new LinearGradientBrush(new RectangleF(cx - tw, top, tw * 2, bot - top), Color.FromArgb(215, 214, 220, 230), Color.FromArgb(215, 150, 158, 172), 90f))
                    using (var edge = new Pen(Color.FromArgb(235, 255, 255, 255), 2.2f))
                    using (var mesh = new Pen(Color.FromArgb(130, 110, 118, 134), 1.6f))
                    {
                        g.FillPolygon(bodyBrush, body);
                        for (int i = -2; i <= 2; i++)
                        {
                            float k = i / 3f;
                            g.DrawLine(mesh, cx + tw * k * 0.95f, top + rimH, cx + bw * k * 0.95f, bot - S * 0.03f);
                        }
                        for (int j = 1; j <= 3; j++)
                        {
                            float y = top + rimH + (bot - top - rimH) * j / 4.2f;
                            float w = tw + (bw - tw) * (y - top) / (bot - top);
                            g.DrawLine(mesh, cx - w * 0.97f, y, cx + w * 0.97f, y);
                        }
                        g.DrawPolygon(edge, body);
                    }
                    using (var rimBrush = new SolidBrush(Color.FromArgb(240, 232, 236, 244)))
                    using (var rimPen = new Pen(Color.FromArgb(200, 120, 128, 144), 1.2f))
                    using (GraphicsPath rim = RoundedRect(new RectangleF(cx - tw - S * 0.03f, top - rimH * 0.5f, (tw + S * 0.03f) * 2, rimH), rimH / 2f))
                    {
                        g.FillPath(rimBrush, rim);
                        g.DrawPath(rimPen, rim);
                    }
                }
                return bmp;
            }
            catch { }
            try
            {
                int idx = RecycleBin.IconIndex(full);
                Guid iid = typeof(NativeMethods.IImageList).GUID;
                NativeMethods.IImageList list;
                if (idx >= 0 && NativeMethods.SHGetImageList(SHIL_JUMBO, ref iid, out list) == 0)
                {
                    IntPtr hIcon = IntPtr.Zero;
                    list.GetIcon(idx, ILD_TRANSPARENT, ref hIcon);
                    Marshal.ReleaseComObject(list);
                    if (hIcon != IntPtr.Zero)
                    {
                        Bitmap big = ShellIcons.FromHIcon(hIcon);
                        if (big == null) using (Icon icon = Icon.FromHandle(hIcon)) big = icon.ToBitmap();
                        NativeMethods.DestroyIcon(hIcon);
                        return ScaleIcon(big);
                    }
                }
            }
            catch { }
            return Logo();
        }

        static Bitmap ScaleIcon(Bitmap src)
        {
            var dst = new Bitmap(IconSource, IconSource);
            using (src)
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                g.DrawImage(src, 0, 0, IconSource, IconSource);
            }
            return dst;
        }

        // Puts the icon on a macOS-style rounded-square tile. Light icons get a dark tile so they stay visible.
        internal static Bitmap Tile(Bitmap icon)
        {
            int S = IconSource;
            // Icons that already are a full rounded-square tile (macOS-style, transparent corners) keep their own shape:
            // no extra plate behind them, scaled so the tile fills the slot.
            Rectangle full;
            if (IsFullTile(icon, out full))
            {
                var bare = new Bitmap(S, S);
                using (Graphics g = Graphics.FromImage(bare))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(icon, new Rectangle(1, 1, S - 2, S - 2), full, GraphicsUnit.Pixel);
                }
                icon.Dispose();
                return bare;
            }

            Color top, bottom;
            if (AverageLuminance(icon) > 0.72)
            {
                top = Color.FromArgb(92, 98, 120);
                bottom = Color.FromArgb(40, 43, 58);
            }
            else
            {
                top = Color.FromArgb(255, 255, 255);
                bottom = Color.FromArgb(205, 211, 224);
            }

            var tile = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(tile))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath path = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, top, bottom, 90f))
                using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(rim, path);
                }

                float inner = S * 0.66f;
                g.DrawImage(icon, (S - inner) / 2f, (S - inner) / 2f, inner, inner);
            }
            icon.Dispose();
            return tile;
        }

        // A "full tile" icon: transparent corners, and a large, almost solid, roughly square body (bounds filled > 85%).
        static bool IsFullTile(Bitmap icon, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            try
            {
                int w = icon.Width, h = icon.Height, minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                        if (icon.GetPixel(x, y).A > 100)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                if (maxX < 0) return false;
                int bw = maxX - minX + 2, bh = maxY - minY + 2;
                if (bw < w * 0.6 || bh < h * 0.6 || Math.Abs(bw - bh) > Math.Max(bw, bh) * 0.06) return false;
                if (icon.GetPixel(minX, minY).A > 100) return false; // top-left corner of the body must be rounded away
                int solid = 0, total = 0;
                for (int y = minY; y <= maxY; y += 2)
                    for (int x = minX; x <= maxX; x += 2) { total++; if (icon.GetPixel(x, y).A > 200) solid++; }
                if (total == 0 || solid < total * 0.85) return false;
                bounds = new Rectangle(minX, minY, bw, bh);
                return true;
            }
            catch { return false; }
        }

        // The MacDock logo (embedded exe icon): used for every window and as the fallback so no icon is ever empty.
        internal static Icon LogoIcon()
        {
            try { var i = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (i != null) return i; } catch { }
            return SystemIcons.Application;
        }
        internal static Bitmap Logo() { using (Icon i = LogoIcon()) return ScaleIcon(i.ToBitmap()); }

        // True when (nearly) every pixel is transparent: the shell returns that for some Store exes.
        static bool IsBlank(Bitmap icon)
        {
            try
            {
                for (int y = 0; y < icon.Height; y += 4)
                    for (int x = 0; x < icon.Width; x += 4)
                        if (icon.GetPixel(x, y).A > 16) return false;
            }
            catch { return false; }
            return true;
        }

        static double AverageLuminance(Bitmap icon)
        {
            using (var small = new Bitmap(icon, 16, 16))
            {
                double sum = 0;
                int count = 0;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        if (c.A < 40) continue;
                        sum += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                        count++;
                    }
                }
                return count == 0 ? 0.5 : sum / count;
            }
        }

        // A gear on a graphite plate: the Settings icon when no icon pack is installed.
        static Bitmap GearIcon()
        {
            int S = IconSource;
            var bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath plate = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, Color.FromArgb(172, 178, 190), Color.FromArgb(98, 103, 116), 90f))
                using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                {
                    g.FillPath(fill, plate);
                    g.DrawPath(rim, plate);
                }

                // Eight teeth around a ring, with a hole in the middle.
                float c = S / 2f, outer = S * 0.33f, inner = S * 0.255f;
                const int teeth = 8;
                var points = new List<PointF>();
                for (int i = 0; i < teeth; i++)
                {
                    double a0 = Math.PI * 2 * i / teeth;
                    double step = Math.PI * 2 / teeth;
                    foreach (double[] p in new[] { new[] { 0.0, inner }, new[] { 0.16, outer }, new[] { 0.34, outer }, new[] { 0.5, inner } })
                    {
                        double a = a0 + step * p[0];
                        points.Add(new PointF(c + (float)(Math.Cos(a) * p[1]), c + (float)(Math.Sin(a) * p[1])));
                    }
                }
                using (var gear = new GraphicsPath())
                {
                    gear.AddPolygon(points.ToArray());
                    gear.AddEllipse(c - S * 0.1f, c - S * 0.1f, S * 0.2f, S * 0.2f); // hole (even-odd fill)
                    using (var body = new LinearGradientBrush(rect, Color.FromArgb(250, 250, 252), Color.FromArgb(214, 218, 226), 90f))
                        g.FillPath(body, gear);
                }
            }
            return bmp;
        }

        // A globe on a blue plate for websites and other links.
        static Bitmap WebIcon()
        {
            int S = IconSource;
            var bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath plate = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, Color.FromArgb(90, 190, 255), Color.FromArgb(10, 110, 230), 90f))
                using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                {
                    g.FillPath(fill, plate);
                    g.DrawPath(rim, plate);
                }

                float c = S / 2f, r = S * 0.30f;
                using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 2.6f))
                {
                    g.DrawEllipse(pen, c - r, c - r, r * 2, r * 2);
                    g.DrawEllipse(pen, c - r * 0.45f, c - r, r * 0.9f, r * 2);   // meridian
                    g.DrawLine(pen, c - r, c, c + r, c);                         // equator
                    g.DrawArc(pen, c - r, c - r * 0.55f, r * 2, r * 1.1f, 0, 180); // parallels
                    g.DrawArc(pen, c - r, c - r * 0.55f, r * 2, r * 1.1f, 180, 180);
                }
            }
            return bmp;
        }

        // Three overlapping windows on a dark plate, like the Mission Control icon.
        static Bitmap MissionIcon()
        {
            int S = IconSource;
            var bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath plate = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, Color.FromArgb(70, 74, 96), Color.FromArgb(28, 30, 42), 90f))
                using (var rim = new Pen(Color.FromArgb(110, 255, 255, 255), 1.5f))
                {
                    g.FillPath(fill, plate);
                    g.DrawPath(rim, plate);
                }

                float w = S * 0.30f, h = S * 0.22f;
                DrawWindowGlyph(g, S * 0.14f, S * 0.22f, w, h);
                DrawWindowGlyph(g, S * 0.56f, S * 0.22f, w, h);
                DrawWindowGlyph(g, S * 0.35f, S * 0.54f, w, h);
            }
            return bmp;
        }

        static void DrawWindowGlyph(Graphics g, float x, float y, float w, float h)
        {
            var r = new RectangleF(x, y, w, h);
            using (GraphicsPath p = RoundedRect(r, 4))
            using (var fill = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
            using (var bar = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
            {
                g.FillPath(fill, p);
                g.DrawPath(pen, p);
                g.FillRectangle(bar, x + 3, y + 3, w - 6, 3);
            }
        }

        // A grid of colored squares, like Launchpad.
        static Bitmap LaunchpadIcon()
        {
            int S = IconSource;
            var bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath plate = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, Color.FromArgb(250, 250, 252), Color.FromArgb(206, 210, 222), 90f))
                using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                {
                    g.FillPath(fill, plate);
                    g.DrawPath(rim, plate);
                }

                Color[] colors =
                {
                    Color.FromArgb(255, 69, 58), Color.FromArgb(255, 159, 10), Color.FromArgb(255, 214, 10),
                    Color.FromArgb(52, 199, 89), Color.FromArgb(10, 132, 255), Color.FromArgb(94, 92, 230),
                    Color.FromArgb(191, 90, 242), Color.FromArgb(255, 55, 95), Color.FromArgb(100, 210, 255)
                };
                float cell = S * 0.19f, gap = S * 0.06f, start = (S - (3 * cell + 2 * gap)) / 2f;
                for (int i = 0; i < 9; i++)
                {
                    using (GraphicsPath p = RoundedRect(new RectangleF(start + (i % 3) * (cell + gap), start + (i / 3) * (cell + gap), cell, cell), 5))
                    using (var b = new SolidBrush(colors[i]))
                        g.FillPath(b, p);
                }
            }
            return bmp;
        }

        // The Start item replaces the hidden taskbar's Start button.
        static Bitmap StartIcon()
        {
            int S = IconSource;
            var bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1.5f, 1.5f, S - 3, S - 3);
                using (GraphicsPath path = RoundedRect(rect, S * 0.225f))
                using (var fill = new LinearGradientBrush(rect, Color.FromArgb(70, 160, 255), Color.FromArgb(0, 84, 190), 90f))
                using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(rim, path);

                    float pane = S * 0.2f, gap = S * 0.04f, start = (S - (2 * pane + gap)) / 2f;
                    for (int row = 0; row < 2; row++)
                        for (int col = 0; col < 2; col++)
                            g.FillRectangle(white, start + col * (pane + gap), start + row * (pane + gap), pane, pane);
                }
            }
            return bmp;
        }

        // ---------- running windows ----------

        static readonly Dictionary<uint, string> PathCache = new Dictionary<uint, string>();

        // Returns visible, titled app windows grouped by their exe path.
        internal static Dictionary<string, List<IntPtr>> EnumerateRunningWindows()
        {
            var result = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
            var pathByPid = PathCache; // persists across refreshes; MainModule lookups are the expensive part
            uint self;
            using (Process me = Process.GetCurrentProcess()) self = (uint)me.Id;
            var className = new StringBuilder(256);

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.GetWindowTextLength(hWnd) == 0) return true;

                int ex = NativeMethods.GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;

                int cloaked;
                if (NativeMethods.DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;

                className.Clear();
                NativeMethods.GetClassName(hWnd, className, className.Capacity);
                string cls = className.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                if (pid == self)
                {
                    // MacDock's own windows are hidden from the dock, except the Settings window: minimizing it keeps it here.
                    var title = new StringBuilder(64);
                    Win32.GetWindowText(hWnd, title, title.Capacity);
                    if (title.ToString() != "MacDock Settings") return true;
                }

                string exe;
                if (!pathByPid.TryGetValue(pid, out exe))
                {
                    exe = ProcessPath(pid);
                    pathByPid[pid] = exe;
                }
                if (exe == null) return true;

                // A Brave / Edge / Chrome web app is its own app: group its windows under its AppsFolder id, not under
                // the browser's exe, so it neither merges into the browser nor into other web apps.
                if (cls == "Chrome_WidgetWin_1")
                {
                    string aumid = Pwa.AumidOfWindow(hWnd);
                    if (Pwa.IsWebAppId(aumid)) exe = Pwa.TargetFor(aumid);
                }

                List<IntPtr> handles;
                if (!result.TryGetValue(exe, out handles))
                {
                    handles = new List<IntPtr>();
                    result[exe] = handles;
                }
                handles.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            return result;
        }

        internal static string ProcessPath(uint pid)
        {
            // PROCESS_QUERY_LIMITED_INFORMATION works on elevated (admin) processes; Process.MainModule does not.
            IntPtr h = NativeMethods.OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int n = sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref n)) return sb.ToString();
                }
                catch { }
                finally { NativeMethods.CloseHandle(h); }
            }
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                    return p.MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }
    }
}
