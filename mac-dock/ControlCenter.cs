// Control Center helpers: brightness (WMI, laptop panels) and dark mode (registry + broadcast).
// Wi-Fi and Bluetooth radios live in DeviceFeed (Devices.cs).

using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacDock
{
    static class Brightness
    {
        static readonly object gate = new object();
        static int wanted = -1;
        static bool running;

        // Current panel brightness 0..100, or -1 when Windows exposes no adjustable display (most desktop monitors).
        public static int Get()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (ManagementObject o in s.Get())
                    return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return -1;
        }

        static void Set(int percent)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                foreach (ManagementObject o in s.Get())
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
            }
            catch { }
        }

        // Drags arrive faster than WMI answers, so only the newest value is kept and sent.
        public static void SetLatest(int percent)
        {
            lock (gate)
            {
                wanted = percent;
                if (running) return;
                running = true;
            }
            Task.Run(() =>
            {
                while (true)
                {
                    int v;
                    lock (gate)
                    {
                        v = wanted;
                        wanted = -1;
                        if (v < 0) { running = false; return; }
                    }
                    Set(v);
                }
            });
        }
    }

    // Night Light has no public API. Windows keeps its switch in an undocumented registry value: a 43-byte blob with
    // byte 18 = 0x15 when on, a 41-byte blob with byte 18 = 0x13 when off. Writing the blob flips the switch, but a
    // Windows update could change that format; IsOn() then returns null and the tile falls back to opening Settings.
    static class NightLight
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

        static byte[] Read()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return k == null ? null : k.GetValue("Data") as byte[];
            }
            catch { return null; }
        }

        public static bool? IsOn()
        {
            byte[] d = Read();
            if (d == null || d.Length < 41) return null;
            if (d[18] == 0x15 && d.Length >= 43) return true;
            if (d[18] == 0x13) return false;
            return null;
        }

        public static bool Set(bool on)
        {
            try
            {
                byte[] d = Read();
                bool? now = IsOn();
                if (d == null || now == null) return false;
                if (now.Value == on) return true;

                byte[] next;
                if (on)
                {
                    next = new byte[d.Length + 2];
                    Array.Copy(d, 0, next, 0, 23);
                    next[23] = 0x10;
                    next[24] = 0x00;
                    Array.Copy(d, 23, next, 25, d.Length - 23);
                    next[18] = 0x15;
                }
                else
                {
                    next = new byte[d.Length - 2];
                    Array.Copy(d, 0, next, 0, 23);
                    Array.Copy(d, 25, next, 23, d.Length - 25);
                    next[18] = 0x13;
                }
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return false;
                    k.SetValue("Data", next, RegistryValueKind.Binary);
                }
                return true;
            }
            catch { return false; }
        }
    }

    static class DarkMode
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // Writes the app and system theme values, then tells running apps the colour set changed.
        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("AppsUseLightTheme", on ? 0 : 1, RegistryValueKind.DWord);
                    k.SetValue("SystemUsesLightTheme", on ? 0 : 1, RegistryValueKind.DWord);
                }
                Theme.Light = !on;
                UIntPtr ignored;
                SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out ignored);

                // Reload the dock and Mission Control icons so any icons\dark\ variants swap in (or back out).
                Form dock = DockForm.Instance;
                if (dock != null && dock.IsHandleCreated)
                    dock.BeginInvoke(new Action(() => { IconPack.Reset(); MissionControl.ResetIcons(); DockForm.Instance.RefreshIcons(); }));
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    }
}
