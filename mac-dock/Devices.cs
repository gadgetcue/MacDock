// Windows radios (Wi-Fi, Bluetooth), connected Bluetooth devices, audio outputs and the master volume.
// Everything here is a real Windows API; nothing is simulated. Calls can block, so use a worker thread
// for DeviceFeed and Wifi. Audio is quick and can run on the UI thread.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MacDock
{
    sealed class WifiNetwork
    {
        public string Name;
        public int Signal;
        public string Security;
        public bool Connected;
        public string Auth;
    }

    static class DeviceFeed
    {
        // Radio kind is "WiFi" or "Bluetooth". Returns null if Windows exposes no such radio.
        public static bool? RadioOn(string kind)
        {
            try
            {
                object radio = FindRadio(kind);
                return radio == null ? (bool?)null : Rt.Get(radio, "State").ToString() == "On";
            }
            catch { return null; }
        }

        public static bool SetRadio(string kind, bool on)
        {
            try
            {
                Type radioType = Rt.Winrt("Windows.Devices.Radios.Radio");
                if (Rt.CallStaticAsync(radioType, "RequestAccessAsync").ToString() != "Allowed") return false;

                object radio = FindRadio(kind);
                if (radio == null) return false;

                Type stateType = Rt.Winrt("Windows.Devices.Radios.RadioState");
                object state = Enum.ToObject(stateType, on ? 1 : 2);
                return Rt.CallAsync(radio, "SetStateAsync", state).ToString() == "Allowed";
            }
            catch { return false; }
        }

        static object FindRadio(string kind)
        {
            Type radioType = Rt.Winrt("Windows.Devices.Radios.Radio");
            object list = Rt.CallStaticAsync(radioType, "GetRadiosAsync");
            foreach (object radio in (IEnumerable)list)
                if (Rt.Get(radio, "Kind").ToString() == kind) return radio;
            return null;
        }

        public static List<string> ConnectedBluetooth()
        {
            var names = new List<string>();
            try
            {
                Type status = Rt.Winrt("Windows.Devices.Bluetooth.BluetoothConnectionStatus");
                object connected = Enum.ToObject(status, 1);
                foreach (string typeName in new[] { "Windows.Devices.Bluetooth.BluetoothDevice", "Windows.Devices.Bluetooth.BluetoothLEDevice" })
                {
                    string selector = (string)Rt.CallStatic(Rt.Winrt(typeName), "GetDeviceSelectorFromConnectionStatus", connected);
                    foreach (string n in DeviceNames(selector))
                        if (!names.Contains(n)) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        // Output devices; "defaultName" is the one Windows is using now.
        public static List<string> AudioOutputs(out string defaultName)
        {
            defaultName = null;
            var names = new List<string>();
            try
            {
                Type media = Rt.Winrt("Windows.Media.Devices.MediaDevice");
                string selector = (string)Rt.CallStatic(media, "GetAudioRenderSelector");
                Type role = Rt.Winrt("Windows.Media.Devices.AudioDeviceRole");
                string defaultId = (string)Rt.CallStatic(media, "GetDefaultAudioRenderId", Enum.ToObject(role, 0));

                Type info = Rt.Winrt("Windows.Devices.Enumeration.DeviceInformation");
                foreach (object d in (IEnumerable)Rt.CallStaticAsync(info, "FindAllAsync", selector))
                {
                    string name = (string)Rt.Get(d, "Name");
                    names.Add(name);
                    if ((string)Rt.Get(d, "Id") == defaultId) defaultName = name;
                }
            }
            catch { }
            return names;
        }

        static List<string> DeviceNames(string selector)
        {
            var names = new List<string>();
            Type info = Rt.Winrt("Windows.Devices.Enumeration.DeviceInformation");
            foreach (object d in (IEnumerable)Rt.CallStaticAsync(info, "FindAllAsync", selector))
            {
                string name = (string)Rt.Get(d, "Name");
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }
    }

    static class Wifi
    {
        // Reads the connected network and nearby networks from Windows' own WLAN tool.
        public static List<WifiNetwork> Scan()
        {
            var result = new List<WifiNetwork>();
            try
            {
                string current = null;
                string interfaces = Run("netsh", "wlan show interfaces");
                Match connected = Regex.Match(interfaces, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                if (connected.Success) current = connected.Groups[1].Value;

                string networks = Run("netsh", "wlan show networks mode=bssid");
                WifiNetwork last = null;
                foreach (string raw in networks.Split('\n'))
                {
                    string line = raw.Trim();
                    Match ssid = Regex.Match(line, @"^SSID\s+\d+\s*:\s*(.*)$");
                    if (ssid.Success)
                    {
                        string name = ssid.Groups[1].Value.Trim();
                        last = null;
                        if (name.Length == 0 || result.Any(n => n.Name == name)) continue;
                        last = new WifiNetwork { Name = name, Connected = name == current };
                        result.Add(last);
                        continue;
                    }
                    if (last == null) continue;

                    Match auth = Regex.Match(line, @"^Authentication\s*:\s*(.+)$");
                    if (auth.Success) { last.Auth = auth.Groups[1].Value.Trim(); last.Security = last.Auth == "Open" ? "Open" : "Secured"; continue; }

                    Match sig = Regex.Match(line, @"^Signal\s*:\s*(\d+)%");
                    if (sig.Success && last.Signal == 0) last.Signal = int.Parse(sig.Groups[1].Value);
                }

                if (current != null && !result.Any(n => n.Connected))
                    result.Insert(0, new WifiNetwork { Name = current, Connected = true, Security = "Secured" });
            }
            catch { }
            return result.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToList();
        }

        // Disconnects from the current network. Returns an error text, or null on success.
        public static string Disconnect()
        {
            try { Run("netsh", "wlan disconnect"); return null; }
            catch (Exception e) { return e.Message; }
        }

        // Connects to a network: a saved profile is used as is; otherwise an open network gets a profile, and a secured one
        // asks for the password first. Returns an error text, or null when the connect request went through.
        public static string Connect(WifiNetwork n)
        {
            try
            {
                string profiles = Run("netsh", "wlan show profiles");
                bool known = Regex.IsMatch(profiles, @":\s*" + Regex.Escape(n.Name) + @"\s*$", RegexOptions.Multiline);
                if (!known)
                {
                    string key = null;
                    if (n.Security != "Open")
                    {
                        key = Microsoft.VisualBasic.Interaction.InputBox("Password for \"" + n.Name + "\":", "Wi-Fi", "");
                        if (string.IsNullOrEmpty(key)) return null;
                    }
                    string err = AddProfile(n, key);
                    if (err != null) return err;
                }
                string outp = Run("netsh", "wlan connect name=\"" + n.Name + "\" ssid=\"" + n.Name + "\"");
                return outp.Contains("successfully") ? null : "Could not connect";
            }
            catch (Exception e) { return e.Message; }
        }

        static string AddProfile(WifiNetwork n, string key)
        {
            string name = System.Security.SecurityElement.Escape(n.Name);
            string sec;
            if (key == null) sec = "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>";
            else
            {
                string a = n.Auth != null && n.Auth.Contains("WPA3") ? "WPA3SAE" : "WPA2PSK";
                sec = "<authEncryption><authentication>" + a + "</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
                      "<sharedKey><keyType>passphrase</keyType><protected>false</protected><keyMaterial>" + System.Security.SecurityElement.Escape(key) + "</keyMaterial></sharedKey>";
            }
            string xml = "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" + name + "</name><SSIDConfig><SSID><name>" + name +
                         "</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security>" + sec + "</security></MSM></WLANProfile>";
            string file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wifi-profile.tmp.xml");
            try
            {
                System.IO.File.WriteAllText(file, xml);
                string outp = Run("netsh", "wlan add profile filename=\"" + file + "\" user=current");
                return outp.Contains("added") ? null : "Could not save the network";
            }
            finally { try { System.IO.File.Delete(file); } catch { } }
        }

        static string Run(string file, string args)        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                return output;
            }
        }
    }

    sealed class BtDevice
    {
        public string Name;
        public ulong Address;
        public bool Connected;
    }

    // Paired Bluetooth devices with connect / disconnect, through the Win32 Bluetooth APIs (bthprops.cpl):
    // a device is connected by enabling its audio / headset / HID services and disconnected by disabling them.
    static class BtClassic
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public SYSTEMTIME stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SEARCH_PARAMS
        {
            public uint dwSize;
            public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)] static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO d);
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)] static extern bool BluetoothFindNextDevice(IntPtr h, ref DEVICE_INFO d);
        [DllImport("bthprops.cpl")] static extern bool BluetoothFindDeviceClose(IntPtr h);
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)] static extern uint BluetoothSetServiceState(IntPtr radio, ref DEVICE_INFO d, ref Guid service, uint flags);

        static readonly string[] Services = { "0000110b", "0000110e", "0000111e", "00001108", "00001124" }; // A2DP sink, AVRCP, Handsfree, Headset, HID

        static List<DEVICE_INFO> Enumerate()
        {
            var list = new List<DEVICE_INFO>();
            var sp = new SEARCH_PARAMS { fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnConnected = 1 };
            sp.dwSize = (uint)Marshal.SizeOf(typeof(SEARCH_PARAMS));
            var di = new DEVICE_INFO();
            di.dwSize = (uint)Marshal.SizeOf(typeof(DEVICE_INFO));
            IntPtr h = BluetoothFindFirstDevice(ref sp, ref di);
            if (h == IntPtr.Zero) return list;
            try
            {
                do { list.Add(di); di.dwSize = (uint)Marshal.SizeOf(typeof(DEVICE_INFO)); }
                while (BluetoothFindNextDevice(h, ref di));
            }
            finally { BluetoothFindDeviceClose(h); }
            return list;
        }

        public static List<BtDevice> Paired()
        {
            var r = new List<BtDevice>();
            try
            {
                foreach (DEVICE_INFO d in Enumerate())
                    if (!string.IsNullOrWhiteSpace(d.szName) && (d.fAuthenticated != 0 || d.fConnected != 0))
                        r.Add(new BtDevice { Name = d.szName, Address = d.Address, Connected = d.fConnected != 0 });
            }
            catch { }
            return r.OrderByDescending(x => x.Connected).ThenBy(x => x.Name).ToList();
        }

        public static bool SetConnected(ulong address, bool connect)
        {
            try
            {
                foreach (DEVICE_INFO dev in Enumerate())
                {
                    if (dev.Address != address) continue;
                    DEVICE_INFO d = dev;
                    foreach (string sv in Services)
                    {
                        Guid g = new Guid(sv + "-0000-1000-8000-00805f9b34fb");
                        BluetoothSetServiceState(IntPtr.Zero, ref d, ref g, 0);
                        if (connect) BluetoothSetServiceState(IntPtr.Zero, ref d, ref g, 1);
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
    // Master volume of the default output device (Core Audio).
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IAudioEndpointVolume endpoint;

        static IAudioEndpointVolume Endpoint()
        {
            if (endpoint != null) return endpoint;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;
                endpoint = (IAudioEndpointVolume)o;
            }
            catch { endpoint = null; }
            return endpoint;
        }

        // 0..1, or -1 if there is no output device.
        public static float Volume
        {
            get
            {
                try
                {
                    IAudioEndpointVolume e = Endpoint();
                    float level;
                    if (e != null && e.GetMasterVolumeLevelScalar(out level) == 0) return level;
                }
                catch { endpoint = null; }
                return -1;
            }
            set
            {
                try
                {
                    IAudioEndpointVolume e = Endpoint();
                    Guid none = Guid.Empty;
                    if (e != null) e.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, value)), ref none);
                }
                catch { endpoint = null; }
            }
        }

        public static bool Muted
        {
            get
            {
                try
                {
                    IAudioEndpointVolume e = Endpoint();
                    bool m;
                    if (e != null && e.GetMute(out m) == 0) return m;
                }
                catch { endpoint = null; }
                return false;
            }
            set
            {
                try
                {
                    IAudioEndpointVolume e = Endpoint();
                    Guid none = Guid.Empty;
                    if (e != null) e.SetMute(value, ref none);
                }
                catch { endpoint = null; }
            }
        }
    }
}
