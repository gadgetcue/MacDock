// Control Center helpers: brightness (WMI, laptop panels) and dark mode (registry + broadcast).
// Wi-Fi and Bluetooth radios live in DeviceFeed (Devices.cs).

using System;
using System.Management;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
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

        // Strength lives in the settings blob as a varint-like colour temperature after the 0xCF 0x28 marker:
        // kelvin = (b0 - 0x80) / 2 + b1 * 64. Returns 0..100 (6500 K = 0, 1200 K = 100) or -1 when the blob isn't recognised.
        const string CurrentKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\";
        // Newer Windows 11 builds keep the live strength in the per-device copy; older ones in the default copy. Both are kept in step.
        static readonly string[] SettingsPaths =
        {
            CurrentKey + @"{31b4860e-ad92-48c8-929d-2808e44c7525}$windows.data.bluelightreduction.settingsperdevice\windows.data.bluelightreduction.settingsperdevice",
            CurrentKey + @"default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings"
        };

        static int TempIndex(byte[] d)
        {
            if (d == null) return -1;
            for (int i = 0; i + 3 < d.Length; i++)
                if (d[i] == 0xCF && d[i + 1] == 0x28 && (d[i + 2] & 0x80) != 0 && d[i + 3] < 0x80) return i + 2;
            return -1;
        }

        public static int Strength()
        {
            return lastSet >= 0 ? lastSet : StrengthFromRegistry();
        }

        // The stored registry copy goes stale on current Windows 11 builds, so the real slider value is read from Settings
        // (hidden) once, in the background, the first time the panel opens. Returns the value or -1.
        public static bool NeedsRead { get { return lastSet < 0; } }
        public static void ReadActual()
        {
            StrengthSession ses = null;
            try
            {
                ses = OpenStrength();
                if (ses != null && lastSet < 0) lastSet = (int)Math.Round(ses.Slider.Current.Value);
            }
            catch (Exception e) { Log("read: " + e.Message); }
            finally { CloseStrength(ses); }
        }

        static int StrengthFromRegistry()
        {
            try
            {
                byte[] d;
                d = null;
                int i = -1;
                foreach (string path in SettingsPaths)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path)) d = k == null ? null : k.GetValue("Data") as byte[];
                    i = TempIndex(d);
                    if (i >= 0) break;
                }
                if (i < 0) return -1;
                int kelvin = (d[i] - 0x80) / 2 + d[i + 1] * 64;
                kelvin = Math.Max(1200, Math.Min(6500, kelvin));
                return (int)Math.Round((6500 - kelvin) * 100.0 / 5300);
            }
            catch { return -1; }
        }

        static readonly object sGate = new object();
        static int sWanted = -1;
        static bool sRunning;
        static int lastSet = -1;

        // Drags are collapsed: the newest value wins. The hidden Settings window stays open while values keep arriving.
        public static void SetStrengthLatest(int pct)
        {
            lastSet = pct;
            lock (sGate) { sWanted = pct; if (sRunning) return; sRunning = true; }
            Task.Run(() =>
            {
                StrengthSession ses = null;
                try
                {
                    int idle = 0;
                    while (true)
                    {
                        System.Threading.Thread.Sleep(ses == null ? 450 : 60);
                        int v;
                        lock (sGate)
                        {
                            v = sWanted;
                            if (v >= 0) sWanted = -1;
                            else if (ses == null || idle >= 2000) { sRunning = false; return; }
                        }
                        if (v < 0) { idle += 60; continue; }
                        idle = 0;
                        try
                        {
                            if (ses == null) ses = OpenStrength();
                            if (ses == null) { Log("gave up setting " + v); continue; }
                            ses.Slider.SetValue(v);
                            Log("set " + v + " -> " + ses.Slider.Current.Value);
                        }
                        catch (Exception e) { Log("retry: " + e.Message); CloseStrength(ses); ses = null; }
                    }
                }
                finally { CloseStrength(ses); }
            });
        }

        sealed class StrengthSession
        {
            public System.Windows.Automation.AutomationElement Win;
            public System.Windows.Automation.RangeValuePattern Slider;
        }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static void Log(string m)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nightlight.log"), DateTime.Now.ToString("HH:mm:ss ") + m + "\r\n"); } catch { }
        }

        static void CloseStrength(StrengthSession s)
        {
            if (s == null) return;
            try { ((System.Windows.Automation.WindowPattern)s.Win.GetCurrentPattern(System.Windows.Automation.WindowPattern.Pattern)).Close(); } catch { }
        }

        // Windows ignores edits to the stored strength, so the value is set the way the user would: on the Strength slider of
        // Settings > Night light, through UI Automation, with that window parked off screen.
        static StrengthSession OpenStrength()
        {
            var AE = System.Windows.Automation.AutomationElement.RootElement;
            var byName = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Settings");
            var strength = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Strength");
            var isSlider = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Slider);
            var both = new System.Windows.Automation.AndCondition(strength, isSlider);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true });
            for (int i = 0; i < 100; i++)
            {
                System.Threading.Thread.Sleep(100);
                try
                {
                    System.Windows.Automation.AutomationElement w = AE.FindFirst(System.Windows.Automation.TreeScope.Children, byName);
                    if (w == null) continue;
                    try { SetWindowPos((IntPtr)w.Current.NativeWindowHandle, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010); } catch { }
                    System.Windows.Automation.AutomationElement slider = w.FindFirst(System.Windows.Automation.TreeScope.Descendants, both);
                    if (slider == null) continue;
                    return new StrengthSession { Win = w, Slider = (System.Windows.Automation.RangeValuePattern)slider.GetCurrentPattern(System.Windows.Automation.RangeValuePattern.Pattern) };
                }
                catch (Exception e) { Log("retry: " + e.Message); }
            }
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

    // Do Not Disturb: Windows 11 stores it as NOC_GLOBAL_SETTING_TOASTS_ENABLED (0 = notification banners off).
    static class DoNotDisturb
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        const string Name = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object v = k == null ? null : k.GetValue(Name);
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                    k.SetValue(Name, on ? 0 : 1, RegistryValueKind.DWord);
            }
            catch { }
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
