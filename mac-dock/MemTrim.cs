// Keeps the working set small: after startup, popups and Launchpad, compact the heap and hand unused pages back to Windows.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    static class MemTrim
    {
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        static Timer timer;

        public static void Now(bool collect)
        {
            try
            {
                if (collect) GC.Collect(2, GCCollectionMode.Forced, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        // Call on the UI thread. Restarts the countdown, so a burst of events trims once, after it.
        public static void Soon(int ms = 1000)
        {
            try
            {
                if (timer == null) { timer = new Timer(); timer.Tick += (s, e) => { timer.Stop(); Now(true); }; }
                timer.Stop(); timer.Interval = Math.Max(50, ms); timer.Start();
            }
            catch { }
        }
    }
}
