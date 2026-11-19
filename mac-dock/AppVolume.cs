// Per-app volume: the audio sessions Windows keeps on the default output (what Volume Mixer shows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock
{
    sealed class AppSession
    {
        public string Name;
        public ISimpleAudioVolumeCom Vol;
        public float Level { get { float f; return Vol.GetMasterVolume(out f) == 0 ? f : 0; } }
        public void Set(float v) { Guid g = Guid.Empty; Vol.SetMasterVolume(Math.Max(0, Math.Min(1, v)), ref g); }
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolumeCom
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static class AppVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class Enumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnum { [PreserveSig] int EnumAudioEndpoints(int f, int m, out IntPtr d); [PreserveSig] int GetDefaultAudioEndpoint(int f, int r, out IDev d); }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDev { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMgr2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr g, int f, out IntPtr c);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
            [PreserveSig] int GetSessionEnumerator(out ISessEnum e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISessEnum { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetSession(int i, out ISessCtl2 s); }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISessCtl2
        {
            [PreserveSig] int GetState(out int s);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string n, ref Guid c);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string p);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string p, ref Guid c);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid c);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string i);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string i);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool opt);
        }

        public static List<AppSession> List()
        {
            var res = new List<AppSession>();
            try
            {
                var en = (IEnum)new Enumerator();
                IDev dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0) return res;
                Guid iid = typeof(IMgr2).GUID; object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return res;
                ISessEnum se; ((IMgr2)o).GetSessionEnumerator(out se);
                int n; se.GetCount(out n);
                var seen = new HashSet<string>();
                for (int i = 0; i < n; i++)
                {
                    ISessCtl2 c; if (se.GetSession(i, out c) != 0) continue;
                    uint pid; c.GetProcessId(out pid);
                    if (pid == 0 || c.IsSystemSoundsSession() == 0) continue;
                    string name;
                    try { name = Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                    if (!seen.Add(name)) continue;
                    var v = c as ISimpleAudioVolumeCom;
                    if (v != null) res.Add(new AppSession { Name = name, Vol = v });
                }
            }
            catch { }
            return res;
        }
    }
}
