// Multi-finger touchpad gestures for Mission Control, read straight from a Windows Precision Touchpad.
//
// Windows only exposes 3- and 4-finger gestures through its own settings, so the raw HID reports are read
// instead: the touchpad reports every finger's position, and a swipe of 4 or 5 fingers is recognised here.
// Swipe up opens Mission Control (and from there All Apps); swipe down closes it.
//
// Windows may also react to a 4-finger swipe on its own (Task View). To avoid both reacting, set
// Settings > Bluetooth & devices > Touchpad > Four-finger gestures to "Nothing". Five-finger swipes are
// never used by Windows, so they work either way.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    sealed class TouchpadGestures : NativeWindow, IDisposable
    {
        const int WM_INPUT = 0xFF;
        const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005;
        const uint RIDEV_INPUTSINK = 0x100;
        const uint RIM_TYPEHID = 2;
        const uint HIDP_STATUS_SUCCESS = 0x110000;
        const ushort PageDigitizer = 0x0D, PageDesktop = 0x01;
        const ushort UsageTouchPad = 0x05, UsageContactCount = 0x54, UsageContactId = 0x51, UsageTipSwitch = 0x42;
        const ushort UsageX = 0x30, UsageY = 0x31;

        // How far the fingers must travel, as a fraction of the pad's height, and how vertical the swipe must be.
        const double SwipeDistance = 0.22, VerticalBias = 1.4;
        const int MinFingers = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device; public IntPtr WParam; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID, IsAlias;
            public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
            public ushort BitSize, ReportCount;
            public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
            public uint UnitsExp, Units;
            public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")]
        static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
        [DllImport("hid.dll")]
        static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);
        [DllImport("hid.dll")]
        static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);
        [DllImport("hid.dll")]
        static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages, ref uint length, IntPtr preparsed, byte[] report, uint reportLength);

        sealed class Pad
        {
            public bool IsTouchpad;
            public IntPtr Preparsed;
            public ushort ReportLength;
            public List<ushort> Fingers = new List<ushort>(); // link collection of each finger slot
            public int XMin, XMax = 1, YMin, YMax = 1;
        }

        public event Action SwipeUp, SwipeDown;

        readonly Dictionary<IntPtr, Pad> pads = new Dictionary<IntPtr, Pad>();
        readonly Dictionary<uint, double[]> contacts = new Dictionary<uint, double[]>(); // id -> normalized x, y

        bool active, fired;
        double startX, startY;
        DateTime lastReport = DateTime.MinValue;
        bool registered;

        public bool IsAvailable { get { return registered; } }

        public TouchpadGestures()
        {
            try
            {
                CreateHandle(new CreateParams { ClassName = "Message", Parent = (IntPtr)(-3) }); // message-only window
                var device = new RAWINPUTDEVICE { UsagePage = PageDigitizer, Usage = UsageTouchPad, Flags = RIDEV_INPUTSINK, Target = Handle };
                registered = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void Dispose()
        {
            foreach (Pad p in pads.Values) if (p.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(p.Preparsed);
            pads.Clear();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                try { HandleInput(m.LParam); }
                catch (Exception ex) { Program.Log(ex); }
            }
            base.WndProc(ref m);
        }

        // ---------- raw input ----------

        void HandleInput(IntPtr rawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize) == uint.MaxValue) return;
                var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.Type != RIM_TYPEHID) return;

                int sizeHid = Marshal.ReadInt32(buffer, (int)headerSize);
                int count = Marshal.ReadInt32(buffer, (int)headerSize + 4);
                IntPtr data = IntPtr.Add(buffer, (int)headerSize + 8);

                Pad pad = GetPad(header.Device);
                if (pad == null || !pad.IsTouchpad || sizeHid <= 0) return;

                for (int i = 0; i < count; i++)
                {
                    var report = new byte[sizeHid];
                    Marshal.Copy(IntPtr.Add(data, i * sizeHid), report, 0, sizeHid);
                    ProcessReport(pad, report);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        Pad GetPad(IntPtr device)
        {
            Pad pad;
            if (pads.TryGetValue(device, out pad)) return pad;

            pad = new Pad();
            pads[device] = pad;

            uint size = 0;
            GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return pad;

            pad.Preparsed = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, pad.Preparsed, ref size) == uint.MaxValue) return pad;

            HIDP_CAPS caps;
            if (HidP_GetCaps(pad.Preparsed, out caps) != (int)HIDP_STATUS_SUCCESS) return pad;
            if (caps.UsagePage != PageDigitizer || caps.Usage != UsageTouchPad) return pad;

            // Each finger slot is its own link collection holding X, Y, contact id and tip switch.
            ushort count = caps.NumberInputValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[count];
            if (HidP_GetValueCaps(0, valueCaps, ref count, pad.Preparsed) != (int)HIDP_STATUS_SUCCESS) return pad;

            foreach (HIDP_VALUE_CAPS v in valueCaps.Take(count))
            {
                if (v.UsagePage != PageDesktop || v.IsRange != 0) continue;
                if (v.UsageMin == UsageX)
                {
                    if (!pad.Fingers.Contains(v.LinkCollection)) pad.Fingers.Add(v.LinkCollection);
                    pad.XMin = v.LogicalMin; pad.XMax = Math.Max(v.LogicalMax, v.LogicalMin + 1);
                }
                else if (v.UsageMin == UsageY)
                {
                    pad.YMin = v.LogicalMin; pad.YMax = Math.Max(v.LogicalMax, v.LogicalMin + 1);
                }
            }

            pad.ReportLength = caps.InputReportByteLength;
            pad.IsTouchpad = pad.Fingers.Count > 0;
            if (pad.IsTouchpad)
                Program.Log("Touchpad found: " + pad.Fingers.Count + " finger slots per report. 4- and 5-finger swipes are active.");
            return pad;
        }

        void ProcessReport(Pad pad, byte[] report)
        {
            if (lastReport != DateTime.MinValue && (DateTime.Now - lastReport).TotalMilliseconds > 800) ResetGesture();
            lastReport = DateTime.Now;

            uint reported;
            int declared = HidP_GetUsageValue(0, PageDigitizer, 0, UsageContactCount, out reported, pad.Preparsed, report, (uint)report.Length) == (int)HIDP_STATUS_SUCCESS
                ? (int)reported : -1;
            // Some pads (like this one) send one finger per report; only the first report of a frame carries the
            // contact count, so a count of 0 does NOT mean "no fingers". Lifted fingers are removed through their
            // tip switch below, and a stalled pad is reset by the timeout at the top.

            foreach (ushort link in pad.Fingers)
            {
                uint rawX, rawY, id;
                if (HidP_GetUsageValue(0, PageDesktop, link, UsageX, out rawX, pad.Preparsed, report, (uint)report.Length) != (int)HIDP_STATUS_SUCCESS) continue;
                if (HidP_GetUsageValue(0, PageDesktop, link, UsageY, out rawY, pad.Preparsed, report, (uint)report.Length) != (int)HIDP_STATUS_SUCCESS) continue;
                if (HidP_GetUsageValue(0, PageDigitizer, link, UsageContactId, out id, pad.Preparsed, report, (uint)report.Length) != (int)HIDP_STATUS_SUCCESS) id = link;

                // A finger slot that isn't touching reports its tip switch as released.
                var pressed = new ushort[8];
                uint pressedCount = (uint)pressed.Length;
                bool tip = HidP_GetUsages(0, PageDigitizer, link, pressed, ref pressedCount, pad.Preparsed, report, (uint)report.Length) == (int)HIDP_STATUS_SUCCESS
                    && pressed.Take((int)pressedCount).Contains(UsageTipSwitch);

                if (tip)
                {
                    contacts[id] = new[]
                    {
                        (rawX - pad.XMin) / (double)(pad.XMax - pad.XMin),
                        (rawY - pad.YMin) / (double)(pad.YMax - pad.YMin)
                    };
                }
                else contacts.Remove(id);
            }

            Evaluate(Math.Max(contacts.Count, declared));
        }

        // ---------- gesture ----------

        void ResetGesture()
        {
            contacts.Clear();
            active = false;
            fired = false;
        }

        void Evaluate(int fingers)
        {
            if (fingers < 3)
            {
                active = false;
                fired = false;
                return;
            }
            if (fingers < MinFingers || contacts.Count == 0) return;

            double cx = contacts.Values.Average(c => c[0]), cy = contacts.Values.Average(c => c[1]);
            if (!active)
            {
                active = true;
                fired = false;
                startX = cx;
                startY = cy;
                return;
            }
            if (fired) return;

            double dx = cx - startX, dy = cy - startY;
            if (Math.Abs(dy) >= SwipeDistance && Math.Abs(dy) > VerticalBias * Math.Abs(dx))
            {
                fired = true;
                if (dy < 0) { if (SwipeUp != null) SwipeUp(); }  // the pad's Y axis grows downward
                else if (SwipeDown != null) SwipeDown();
            }
        }
    }
}
