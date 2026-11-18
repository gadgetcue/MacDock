// Keeps app windows above the dock's reserved strip. The appbar reservation already moves the work area, but some
// apps ignore it (custom frames, restored-to-full-screen sizes, windows opened before the reservation). Every ~350 ms
// a maximized (or monitor-sized, framed) window on the dock's display that reaches below the strip is shortened to end at it.
// Elevated windows can't be resized from here (UIPI); Windows already honours the work area for those.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    static class DockClamp
    {
        // Screen y of the top of the reserved strip; 0 = nothing reserved.
        internal static int Top;
        internal static Rectangle Monitor;
        // Screen y of the bottom of the top status bar; windows whose title bar sits above it are slid down (0 = off).
        internal static int BarBottom;
        static readonly List<IntPtr> lower = new List<IntPtr>();

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Win32.RECT r, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        static readonly uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        static readonly Dictionary<IntPtr, int> tries = new Dictionary<IntPtr, int>();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k);
        static Timer timer;

        internal static void Start()
        {
            HookStart();
            if (timer != null) return;
            timer = new Timer { Interval = 350 };
            timer.Tick += delegate { try { Tick(); } catch { } };
            timer.Start();
        }

        // Mid-drag clamp: a location-change hook fires for every move step, so a window can't be dragged over the top bar at all.
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
        static WinEventProc hookProc;
        static bool clamping;

        static void HookStart()
        {
            if (hookProc != null) return;
            hookProc = OnMove;
            SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, hookProc, 0, 0, 0x0000 | 0x0002); // EVENT_OBJECT_LOCATIONCHANGE, OUTOFCONTEXT | SKIPOWNPROCESS
        }

        static void OnMove(IntPtr hook, uint ev, IntPtr h, int idObj, int idChild, uint thread, uint time)
        {
            if (idObj != 0 || BarBottom == 0 || clamping || h == IntPtr.Zero) return;
            try
            {
                clamping = true;
                if (GetAncestor(h, 2) != h || !IsWindowVisible(h) || IsIconic(h) || IsZoomed(h)) return;
                int ex = GetWindowLong(h, -20), st = GetWindowLong(h, -16);
                if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return;
                if ((st & 0x00C00000) == 0 && (st & 0x00040000) == 0) return;
                Win32.RECT w, vis;
                if (!GetWindowRect(h, out w)) return;
                if (DwmGetWindowAttribute(h, 9, out vis, 16) != 0) vis = w;
                if (!Monitor.Contains((vis.Left + vis.Right) / 2, vis.Top + 10) && vis.Top >= BarBottom) return;
                if (vis.Top >= BarBottom || vis.Bottom <= BarBottom + 40) return;
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return;
                SetWindowPos(h, IntPtr.Zero, w.Left, w.Top + (BarBottom - vis.Top), 0, 0, 0x0001 | 0x0004 | 0x0010);
            }
            catch { }
            finally { clamping = false; }
        }

        static void Tick()
        {
            int top = Top;
            if (top == 0 && BarBottom == 0) return; // not gated on Fullscreen.Active: a framed monitor-sized window counts as "fullscreen" there, but is exactly what we clamp; borderless ones are skipped per window below
            Rectangle mon = Monitor;
            var fix = new List<IntPtr>();
            lower.Clear();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                int ex = GetWindowLong(h, -20), st = GetWindowLong(h, -16);
                if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return true;       // tool window (not app window)
                if ((st & 0x00C00000) == 0 && (st & 0x00040000) == 0) return true; // no caption and no sizing frame: fullscreen/borderless, leave it
                int cloaked; DwmGetWindowAttribute(h, 14, out cloaked, 4);
                if (cloaked != 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == self) return true;
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
                Win32.RECT r;
                if (DwmGetWindowAttribute(h, 9, out r, 16) != 0 && !GetWindowRect(h, out r)) return true; // visible frame
                int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                if (!mon.Contains(cx, cy)) return true;
                Win32.RECT wr; GetWindowRect(h, out wr); // outer rect includes the invisible resize border
                bool tall = IsZoomed(h) || (wr.Top <= mon.Top + 2 && wr.Bottom >= mon.Bottom - 2);
                if (BarBottom > 0 && !IsZoomed(h) && r.Top < BarBottom && r.Bottom > BarBottom + 40) lower.Add(h);
                if (top > 0 && r.Bottom > top + 1 && (tall || r.Top < top - 60)) fix.Add(h); // any normal window reaching into the strip (not only maximized ones)
                else tries.Remove(h);
                return true;
            }, IntPtr.Zero);

            if (lower.Count > 0 && (GetAsyncKeyState(0x01) & 0x8000) == 0) // not mid-drag
                foreach (IntPtr h in lower)
                {
                    Win32.RECT w, vis;
                    if (!GetWindowRect(h, out w)) continue;
                    if (DwmGetWindowAttribute(h, 9, out vis, 16) != 0) vis = w;
                    int down = BarBottom - vis.Top;
                    if (down > 0) SetWindowPos(h, IntPtr.Zero, w.Left, w.Top + down, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
                }

            foreach (IntPtr h in fix)
            {
                int n; tries.TryGetValue(h, out n);
                if (n >= 4) continue; // the app keeps resizing itself back (or it is elevated): stop fighting
                tries[h] = n + 1;
                Win32.RECT w, vis;
                if (!GetWindowRect(h, out w)) continue;
                if (DwmGetWindowAttribute(h, 9, out vis, 16) != 0) vis = w;
                int over = vis.Bottom - Top;
                if (over <= 0) continue;
                if (!IsZoomed(h) && !(w.Top <= mon.Top + 2 && w.Bottom >= mon.Bottom - 2))
                {
                    // ordinary resized/moved window: don't fight a drag; first slide it up, shrink only if it still doesn't fit
                    if ((GetAsyncKeyState(0x01) & 0x8000) != 0) { tries[h] = n; continue; }
                    int workTop = Screen.FromRectangle(mon).WorkingArea.Top;
                    int up = Math.Min(over, Math.Max(0, vis.Top - workTop));
                    int rest = over - up;
                    SetWindowPos(h, IntPtr.Zero, w.Left, w.Top - up, w.Right - w.Left, (w.Bottom - w.Top) - rest, 0x0004 | 0x0010);
                    tries.Remove(h);
                    continue;
                }
                // NOMOVE | NOZORDER | NOACTIVATE
                SetWindowPos(h, IntPtr.Zero, 0, 0, w.Right - w.Left, (w.Bottom - w.Top) - over, 0x0002 | 0x0004 | 0x0010);
            }
        }
    }
}
