// Windows radios (Wi-Fi, Bluetooth), connected Bluetooth devices, audio outputs and the master volume.
// Everything here is a real Windows API; nothing is simulated. Calls can block, so use a worker thread
// for DeviceFeed (Wi-Fi is in Wifi.cs). Audio is quick and can run on the UI thread.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MacDock
{
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
    static class CoreAudio
    {
        // One untyped wrapper for the device enumerator everywhere: Windows hands every caller the same COM object, so
        // a second [ComImport] class for it makes the cast throw.
        public static object NewEnumerator() { return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))); }
    }

    // Master volume of the default output device (Core Audio).
    static class Audio
    {

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p1, p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);

        // Undocumented, but what the Sound control panel itself uses to switch the default device.
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        class PolicyConfigCom { }

        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat(); void GetProcessingPeriod();
            void SetProcessingPeriod(); void GetShareMode(); void SetShareMode(); void GetPropertyValue(); void SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
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

        // Cached per thread (the COM object can't cross from the UI thread to a worker), and re-checked so it
        // follows the default device when that changes (headphones plugged in, output switched).
        [ThreadStatic] static IAudioEndpointVolume endpoint;
        [ThreadStatic] static string endpointId;
        [ThreadStatic] static int checkedAt;

        static IAudioEndpointVolume Endpoint()
        {
            if (endpoint != null && unchecked(Environment.TickCount - checkedAt) < 250) return endpoint;
            try
            {
                var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
                IMMDevice device;
                string id;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device.GetId(out id) != 0) return endpoint = null;
                checkedAt = Environment.TickCount;
                if (endpoint != null && id == endpointId) return endpoint;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return endpoint = null;
                endpoint = (IAudioEndpointVolume)o;
                endpointId = id;
            }
            catch (Exception ex) { endpoint = null; Program.Log(ex); }
            return endpoint;
        }

        // Active output devices as (id, name); "defaultId" is the one Windows is using now.
        public static List<KeyValuePair<string, string>> Outputs(out string defaultId)
        {
            defaultId = null;
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) == 0) device.GetId(out defaultId);
                IMMDeviceCollection all;
                int count;
                if (enumerator.EnumAudioEndpoints(0, 1, out all) != 0 || all.GetCount(out count) != 0) return list;
                for (int i = 0; i < count; i++)
                {
                    string id, name = null;
                    IPropertyStore store;
                    if (all.Item(i, out device) != 0 || device.GetId(out id) != 0) continue;
                    var key = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 }; // friendly name
                    PROPVARIANT v;
                    if (device.OpenPropertyStore(0, out store) == 0 && store.GetValue(ref key, out v) == 0)
                    {
                        if (v.vt == 31) name = Marshal.PtrToStringUni(v.p1);
                        PropVariantClear(ref v);
                    }
                    list.Add(new KeyValuePair<string, string>(id, name ?? "Output device"));
                }
            }
            catch { }
            return list;
        }

        public static void SetDefault(string deviceId)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigCom();
                for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(deviceId, role);
                checkedAt = unchecked(Environment.TickCount - 1000);
            }
            catch { }
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
                catch (Exception ex) { endpoint = null; Program.Log(ex); }
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
                catch (Exception ex) { endpoint = null; Program.Log(ex); }
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
                catch (Exception ex) { endpoint = null; Program.Log(ex); }
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
                catch (Exception ex) { endpoint = null; Program.Log(ex); }
            }
        }
    }
}
