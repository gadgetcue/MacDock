// Windows' own Settings handler for the Night light "Strength" slider, loaded in-process from SettingsHandlers_Display.dll.
// Settings > Night light calls this same object, so a change applies as it does there, with no window.
// The interface is undocumented (ISettingItem), so it is called by vtable slot and every failure throws.

using System;
using System.Runtime.InteropServices;

namespace MacDock
{
    static class SettingHandler
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void WindowsCreateString(string s, int length, out IntPtr h);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr h);
        [DllImport("combase.dll", PreserveSig = false)] static extern void RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSettingFn(IntPtr id, out IntPtr item, IntPtr n);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetIntFn(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetByteFn(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetValueFn(IntPtr self, IntPtr name, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetValueFn(IntPtr self, IntPtr name, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateUInt32Fn(IntPtr self, int n, out IntPtr value);

        // vtable slots (IInspectable ends at 5).
        const int ItemIsEnabled = 9, ItemGetValue = 13, ItemSetValue = 14, FactoryCreateUInt32 = 11, ValueGetUInt32 = 12;

        static IntPtr item;

        // Callers serialise access.
        public static void Reset()
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            item = IntPtr.Zero;
        }

        public static int Get()
        {
            Open();
            IntPtr name = Str("Value"), boxed = IntPtr.Zero, number = IntPtr.Zero;
            try
            {
                Check(Fn<GetValueFn>(item, ItemGetValue)(item, name, out boxed));
                Guid propertyValue = new Guid("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
                Check(Marshal.QueryInterface(boxed, ref propertyValue, out number));
                int n;
                Check(Fn<GetIntFn>(number, ValueGetUInt32)(number, out n));
                if (n < 0 || n > 100) throw new InvalidOperationException("strength out of range: " + n);
                return n;
            }
            finally { Free(number); Free(boxed); WindowsDeleteString(name); }
        }

        public static void Set(int percent)
        {
            Open();
            IntPtr name = Str("Value"), cls = Str("Windows.Foundation.PropertyValue"), factory = IntPtr.Zero, boxed = IntPtr.Zero;
            try
            {
                Guid statics = new Guid("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");
                RoGetActivationFactory(cls, ref statics, out factory);
                Check(Fn<CreateUInt32Fn>(factory, FactoryCreateUInt32)(factory, Math.Max(0, Math.Min(100, percent)), out boxed));
                Check(Fn<SetValueFn>(item, ItemSetValue)(item, name, boxed));
            }
            finally { Free(boxed); Free(factory); WindowsDeleteString(cls); WindowsDeleteString(name); }
        }

        static void Open()
        {
            if (item != IntPtr.Zero) return;
            IntPtr module = LoadLibrary(Environment.SystemDirectory + @"\SettingsHandlers_Display.dll");
            IntPtr proc = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "GetSetting");
            if (proc == IntPtr.Zero) throw new InvalidOperationException("Night light handler not found");
            var get = (GetSettingFn)Marshal.GetDelegateForFunctionPointer(proc, typeof(GetSettingFn));
            IntPtr id = Str("SystemSettings_Display_BlueLight_ColorTemperature");
            try { Check(get(id, out item, IntPtr.Zero)); }
            finally { WindowsDeleteString(id); }
            if (item == IntPtr.Zero) throw new InvalidOperationException("Night light setting not available");

            // A new handler loads in the background and reports a placeholder value until it is enabled.
            byte enabled = 0;
            for (int i = 0; i < 100 && enabled == 0; i++)
            {
                System.Threading.Thread.Sleep(10);
                Check(Fn<GetByteFn>(item, ItemIsEnabled)(item, out enabled));
            }
            System.Threading.Thread.Sleep(50);
        }

        static IntPtr Str(string s) { IntPtr h; WindowsCreateString(s, s.Length, out h); return h; }
        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        static void Free(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr p = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }
    }
}
