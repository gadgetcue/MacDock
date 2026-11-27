// Windows' own Settings handlers for Night light (switch and "Strength" slider), loaded in-process from
// SettingsHandlers_Display.dll. Settings calls these same objects, so a change applies as it does there, with no window.
// The interface is undocumented (ISettingItem), so it is called by vtable slot and every failure throws.

using System;
using System.Runtime.InteropServices;

namespace MacDock
{
    sealed class SettingHandler
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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBooleanFn(IntPtr self, byte b, out IntPtr value);

        // vtable slots (IInspectable ends at 5).
        const int ItemIsEnabled = 9, ItemGetValue = 13, ItemSetValue = 14, FactoryCreateUInt32 = 11, FactoryCreateBoolean = 17, ValueGetUInt32 = 12;

        public static readonly SettingHandler Strength = new SettingHandler("SystemSettings_Display_BlueLight_ColorTemperature");
        public static readonly SettingHandler Switch = new SettingHandler("SystemSettings_Display_BlueLight_ManualToggleQuickAction");

        readonly string id;
        IntPtr item;

        SettingHandler(string id) { this.id = id; }

        // Callers serialise access.
        public void Reset()
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            item = IntPtr.Zero;
        }

        public int GetNumber()
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
                if (n < 0 || n > 100) throw new InvalidOperationException("value out of range: " + n);
                return n;
            }
            finally { Free(number); Free(boxed); WindowsDeleteString(name); }
        }

        public void SetNumber(int percent)
        {
            Put(f =>
            {
                IntPtr boxed;
                Check(Fn<CreateUInt32Fn>(f, FactoryCreateUInt32)(f, Math.Max(0, Math.Min(100, percent)), out boxed));
                return boxed;
            });
        }

        public void SetBoolean(bool on)
        {
            Put(f =>
            {
                IntPtr boxed;
                Check(Fn<CreateBooleanFn>(f, FactoryCreateBoolean)(f, (byte)(on ? 1 : 0), out boxed));
                return boxed;
            });
        }

        // What Settings sets while the slider thumb is held: the handler saves the value as a live preview, and the
        // screen follows. Without it a new strength is only stored, and shows after Night light is switched off and on.
        public void SetDragging(bool dragging)
        {
            Put(f =>
            {
                IntPtr boxed;
                Check(Fn<CreateBooleanFn>(f, FactoryCreateBoolean)(f, (byte)(dragging ? 1 : 0), out boxed));
                return boxed;
            }, "IsDragging");
        }

        // Boxes a value with Windows.Foundation.PropertyValue and hands it to the handler.
        void Put(Func<IntPtr, IntPtr> box, string key = "Value")
        {
            Open();
            IntPtr name = Str(key), cls = Str("Windows.Foundation.PropertyValue"), factory = IntPtr.Zero, boxed = IntPtr.Zero;
            try
            {
                Guid statics = new Guid("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");
                RoGetActivationFactory(cls, ref statics, out factory);
                boxed = box(factory);
                Check(Fn<SetValueFn>(item, ItemSetValue)(item, name, boxed));
            }
            finally { Free(boxed); Free(factory); WindowsDeleteString(cls); WindowsDeleteString(name); }
        }

        void Open()
        {
            if (item != IntPtr.Zero) return;
            IntPtr module = LoadLibrary(Environment.SystemDirectory + @"\SettingsHandlers_Display.dll");
            IntPtr proc = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "GetSetting");
            if (proc == IntPtr.Zero) throw new InvalidOperationException("Night light handler not found");
            var get = (GetSettingFn)Marshal.GetDelegateForFunctionPointer(proc, typeof(GetSettingFn));
            IntPtr name = Str(id);
            try { Check(get(name, out item, IntPtr.Zero)); }
            finally { WindowsDeleteString(name); }
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
