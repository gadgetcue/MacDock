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
        public static int DiskPct;              // system drive used %
        public static long NetDownBps, NetUpBps; // bytes/s over all up interfaces
        public static int TempC = -1;            // -1 = not exposed by this PC
        static NetworkInterface[] nics = new NetworkInterface[0];
        static long lastRx, lastTx, lastNetTick;
        static int tempTick;
        public static string Weather = "";       // e.g. "24°C Delhi"; empty until fetched

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

        // Disk usage, network speed and (when ACPI exposes it) temperature. Cheap except the temp query, which runs on a worker.
        public static void UpdateExtra(bool disk, bool net, bool temp)
        {
            try
            {
                if (disk)
                {
                    var d = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory));
                    DiskPct = (int)Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize);
                }
                if (net)
                {
                    if (nics.Length == 0) nics = NetworkInterface.GetAllNetworkInterfaces();
                    long rx = 0, tx = 0;
                    foreach (var ni in nics)
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var st = ni.GetIPv4Statistics(); rx += st.BytesReceived; tx += st.BytesSent;
                    }
                    long now = Environment.TickCount;
                    if (lastNetTick != 0 && now > lastNetTick && rx >= lastRx)
                    {
                        long ms = now - lastNetTick;
                        NetDownBps = (rx - lastRx) * 1000 / ms; NetUpBps = Math.Max(0, (tx - lastTx) * 1000 / ms);
                    }
                    lastRx = rx; lastTx = tx; lastNetTick = now;
                }
                if (temp && ++tempTick % 10 == 1)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using (var q = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                                foreach (System.Management.ManagementObject o in q.Get())
                                { TempC = (int)Math.Round(Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15); break; }
                        }
                        catch { TempC = -1; }
                    });
            }
            catch { }
        }

        public static string Speed(long bps)
        {
            if (bps >= 1048576) return (bps / 1048576.0).ToString("0.0") + "MB/s";
            return (bps / 1024) + "KB/s";
        }

        static bool weatherBusy;
        static long weatherAt;
        // Opt-in: city from the public IP (ipwho.is), conditions from Open-Meteo. Refreshed every 30 minutes on a worker thread.
        public static void UpdateWeather()
        {
            long now = Environment.TickCount;
            if (weatherBusy || (weatherAt != 0 && now - weatherAt < 1800000)) return;
            weatherBusy = true; weatherAt = now;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var wc = new System.Net.WebClient { Encoding = System.Text.Encoding.UTF8 };
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var loc = (Dictionary<string, object>)js.DeserializeObject(wc.DownloadString("https://ipwho.is/?fields=latitude,longitude,city"));
                    string url = "https://api.open-meteo.com/v1/forecast?current=temperature_2m&latitude=" + Convert.ToString(loc["latitude"], System.Globalization.CultureInfo.InvariantCulture) + "&longitude=" + Convert.ToString(loc["longitude"], System.Globalization.CultureInfo.InvariantCulture);
                    var w = (Dictionary<string, object>)js.DeserializeObject(wc.DownloadString(url));
                    var cur = (Dictionary<string, object>)w["current"];
                    Weather = Math.Round(Convert.ToDouble(cur["temperature_2m"], System.Globalization.CultureInfo.InvariantCulture)) + "°C " + loc["city"];
                }
                catch { weatherAt = Environment.TickCount - 1500000; } // retry in ~5 minutes
                finally { weatherBusy = false; }
            });
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
            nics = new NetworkInterface[0]; // re-read the adapter list next sample
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
