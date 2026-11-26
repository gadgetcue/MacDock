// Compacts the heap after startup, popups and Launchpad. The working set is deliberately not trimmed: paging it back in stalled the UI under load.
using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace MacDock
{
    static class MemTrim
    {
        static Timer timer;

        public static void Now(bool collect)
        {
            try
            {
                var sw = Stopwatch.StartNew(); if (collect) GC.Collect(2, GCCollectionMode.Forced, true);
                Program.Slow("MemTrim", sw);
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
