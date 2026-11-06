// Cheap system readings for the top bar and island, plus the background feed for notifications and media.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacDock
{
    enum NetKind { None, Wifi, Ethernet }

    static class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static bool HasBattery;
        public static int BatteryPercent;
        public static bool OnAc;
        public static bool Charging;
        public static int BatterySecondsLeft = -1; // -1 when Windows can't estimate it
        public static int Cpu;
        public static int Ram;
        public static NetKind Net;

        static long lastIdle, lastKernel, lastUser;

        // Battery, CPU and memory: a few cheap kernel calls.
        public static void UpdateFast()
        {
            SYSTEM_POWER_STATUS p;
            if (GetSystemPowerStatus(out p))
            {
                HasBattery = (p.BatteryFlag & 128) == 0 && p.BatteryLifePercent <= 100;
                BatteryPercent = p.BatteryLifePercent <= 100 ? p.BatteryLifePercent : 0;
                OnAc = p.ACLineStatus == 1;
                Charging = (p.BatteryFlag & 8) != 0;
                BatterySecondsLeft = p.BatteryLifeTime;
            }

            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long dIdle = idle - lastIdle, dKernel = kernel - lastKernel, dUser = user - lastUser;
                long total = dKernel + dUser; // kernel time already includes idle time
                if (lastKernel != 0 && total > 0) Cpu = (int)Math.Max(0, Math.Min(100, 100 - dIdle * 100 / total));
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref mem)) Ram = (int)mem.dwMemoryLoad;
        }

        // Which kind of connection is up. Slower, so called every few seconds.
        public static void UpdateNet()
        {
            NetKind kind = NetKind.None;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) { kind = NetKind.Wifi; break; }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet && ni.GetIPProperties().GatewayAddresses.Count > 0)
                        kind = NetKind.Ethernet;
                }
            }
            catch { }
            Net = kind;
        }
    }

    // Polls notifications and media on a worker thread and raises events on the UI thread.
    static class Feed
    {
        public static List<NotificationInfo> History = new List<NotificationInfo>();
        public static MediaInfo Media;
        public static int Unread;
        public static event Action<NotificationInfo> Arrived;
        public static event Action Changed;

        static SynchronizationContext ui;

        public static void Start()
        {
            ui = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(_ => { AppDirectory.Build(); MissionControl.Preload();
                DockForm d = DockForm.Instance; // icons built before the app list existed (Store apps) are rebuilt now
                try { if (d != null && d.IsHandleCreated) d.BeginInvoke((Action)d.RefreshIcons); } catch { } }); // friendly app names for notifications and search
            var thread = new Thread(Loop) { IsBackground = true, Name = "MacDock feed" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public static void ClearAll()
        {
            NotificationFeed.DismissAll(); // before the poll can re-read Windows' database and bring them back
            History = new List<NotificationInfo>();
            Unread = 0;
            ThreadPool.QueueUserWorkItem(_ => NotificationFeed.ClearAll());
            if (Changed != null) Changed();
        }

        // Dismisses one notification (swipe). Windows' own copy is left alone: its ids differ from the listener's.
        public static void Remove(NotificationInfo n)
        {
            NotificationFeed.Dismiss(n.Id);
            var next = new List<NotificationInfo>(History);
            next.RemoveAll(x => x.Id == n.Id);
            History = next;
            if (Changed != null) Changed();
        }

        static void Loop()
        {
            while (true)
            {
                List<NotificationInfo> added;
                List<NotificationInfo> all = NotificationFeed.Poll(out added);
                MediaInfo media = MediaFeed.Poll();

                // Only wake the UI thread when something actually changed.
                bool changed = added.Count > 0
                    || all.Count != History.Count
                    || (all.Count > 0 && History.Count > 0 && all[0].Id != History[0].Id)
                    || (media == null) != (Media == null)
                    || (media != null && Media != null && (media.Title != Media.Title || media.Playing != Media.Playing));

                if (ui != null && changed)
                {
                    ui.Post(_ =>
                    {
                        History = all.Where(x => !NotificationFeed.IsDismissed(x)).ToList(); // a poll in flight may predate a dismissal
                        Media = media;
                        foreach (NotificationInfo n in added)
                        {
                            Unread++;
                            if (Arrived != null) Arrived(n);
                        }
                        if (Changed != null) Changed();
                    }, null);
                }
                Thread.Sleep(700); // each poll only compares file timestamps unless something changed
            }
        }
    }
}
