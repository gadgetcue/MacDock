// Shared motion + rendering plumbing: a frame clock, damped springs, and a persistent layered-window surface.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MacDock
{
    // Time since the previous frame, in seconds. Physics uses this instead of a fixed step, so motion keeps
    // the same speed whether the timer fires at 60 Hz, 64 Hz or stutters under load.
    sealed class FrameClock
    {
        readonly Stopwatch watch = Stopwatch.StartNew();
        long last;

        public double Next()
        {
            long now = watch.ElapsedTicks;
            double dt = (now - last) / (double)Stopwatch.Frequency;
            last = now;
            return Math.Max(0.001, Math.Min(0.05, dt)); // a stall must not make things jump
        }

        // Call when an animation starts after idling, so the first frame doesn't see a huge gap.
        public void Reset() { last = watch.ElapsedTicks; }
    }

    // Drives animation frames at the monitor's refresh rate: a worker waits on the compositor (DwmFlush returns
    // once per refresh) and posts one step to the UI thread per vsync. Idle (no worker wakeups) until Wake().
    // Dt is the smoothed refresh interval; Frame() turns a measured frame gap into a stable physics dt.
    sealed class VsyncDriver : IDisposable
    {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

        readonly System.Windows.Forms.Control owner;
        readonly Action step;
        readonly System.Threading.ManualResetEventSlim gate = new System.Threading.ManualResetEventSlim(false);
        volatile bool active, closing;
        volatile float dt = DisplayDt(null);
        int pending;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

        // Per-monitor vblank wait (D3DKMT): ticks exactly at the refresh rate of the monitor the window is on, even with
        // mixed-rate monitors, where DwmFlush beats between both. Falls back to DwmFlush when unavailable.
        [StructLayout(LayoutKind.Sequential)] struct OpenAdapter { public IntPtr hdc; public uint hAdapter, luidLo; public int luidHi; public uint src; }
        [StructLayout(LayoutKind.Sequential)] struct WaitVblank { public uint hAdapter, hDevice, src; }
        [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint hAdapter; }
        [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenAdapter a);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter a);
        [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitVblank w);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string drv, string dev, string port, IntPtr init);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public int l, t, r, b, wl, wt, wr, wb, flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dev; }

        // Off: waiting on raw vblank events made the user's monitor glitch while scrolling/hovering. Back to the compositor tick.
        const bool UseVblankWait = false; // per-monitor vblank, self-checking: drops to the compositor tick (DwmFlush) for good if its timing is off
        bool vblankBad; int offRate;
        IntPtr curMon; WaitVblank wait; bool haveWait; volatile bool recheck = true; volatile string devName;
        static float DisplayDt(string dev)
        {
            try
            {
                DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(dev, -1, ref dm) && dm.dmDisplayFrequency > 1) return 1f / dm.dmDisplayFrequency;
            }
            catch { }
            return 1f / 60;
        }

        // UI thread: which monitor is the window on now? Cheap; re-run on every wake and step so a moved window re-targets.
        void CheckMonitor()
        {
            try
            {
                if (!owner.IsHandleCreated) return;
                IntPtr m = MonitorFromWindow(owner.Handle, 2);
                if (m == curMon) return;
                MONITORINFOEX mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(m, ref mi)) return;
                curMon = m; devName = mi.dev; dt = DisplayDt(mi.dev); recheck = true; // refine from measured ticks if the vblank wait is unavailable
            }
            catch { }
        }

        // Worker thread: (re)open the output of the current monitor.
        void OpenOutput()
        {
            recheck = false;
            if (haveWait) { try { CloseAdapter c = new CloseAdapter { hAdapter = wait.hAdapter }; D3DKMTCloseAdapter(ref c); } catch { } haveWait = false; }
            string dev = devName; if (dev == null || !UseVblankWait || vblankBad) return;
            try
            {
                IntPtr dc = CreateDC(dev, null, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) return;
                OpenAdapter oa = new OpenAdapter { hdc = dc };
                int r = D3DKMTOpenAdapterFromHdc(ref oa);
                DeleteDC(dc);
                if (r == 0) { wait = new WaitVblank { hAdapter = oa.hAdapter, src = oa.src }; haveWait = true; }
            }
            catch { }
        }

        // Blocks until the next vblank of the window's monitor (or a compositor tick if that isn't available).
        void WaitFrame()
        {
            if (recheck) OpenOutput();
            if (haveWait) { try { if (D3DKMTWaitForVerticalBlankEvent(ref wait) == 0) return; } catch { } haveWait = false; }
            // Compositor clock: steady at the DWM composition rate (the fastest monitor's), unlike DwmFlush which jittered here.
            if (false && !dcompFailed) // off: with no handles this just sleeps the full 100 ms timeout (scroll ran at ~13 fps); DwmFlush ticks per refresh
            {
                try { DCompositionWaitForCompositorClock(0, IntPtr.Zero, 100); return; }
                catch { dcompFailed = true; }
            }
            if (DwmFlush() != 0) System.Threading.Thread.Sleep(4); // DWM off: ~250 Hz polling
        }
        [DllImport("dcomp.dll")] static extern uint DCompositionWaitForCompositorClock(uint count, IntPtr handles, uint timeout);
        bool dcompFailed;

        public VsyncDriver(System.Windows.Forms.Control owner, Action step)
        {
            this.owner = owner;
            this.step = step;
            new System.Threading.Thread(Loop) { IsBackground = true, Name = "vsync", Priority = System.Threading.ThreadPriority.AboveNormal }.Start();
        }

        public bool Active { get { return active; } }
        public double Dt { get { return dt; } }

        public void Wake() { CheckMonitor(); active = true; gate.Set(); }
        public void Sleep() { active = false; gate.Reset(); }

        // The measured gap, unless it's about one refresh (then the steady refresh interval, so springs don't jitter).
        public double Frame(double real) { return real < dt * 1.6 ? dt : real; }

        void Loop()
        {
            timeBeginPeriod(1);
            long prev = 0;
            while (!closing)
            {
                if (!active) { prev = 0; gate.Wait(250); continue; }
                WaitFrame();
                long t =Stopwatch.GetTimestamp();
                if (prev != 0)
                {
                    double d = (t - prev) / (double)Stopwatch.Frequency;
                    if (!haveWait && d > 1.0 / 300 && d < 1.0 / 30) dt = (float)(dt * 0.85 + d * 0.15);
                    else if (haveWait) { if (d < dt * 0.5 || d > dt * 1.7) { if (++offRate > 12) { vblankBad = true; recheck = true; offRate = 0; } } else if (offRate > 0) offRate--; } // wait ticking off the monitor rate: give up on it
                }
                prev = t;
                if (closing || !active || !owner.IsHandleCreated) continue;
                if (System.Threading.Interlocked.Exchange(ref pending, 1) == 0)
                {
                    try { owner.BeginInvoke(new Action(() => { System.Threading.Interlocked.Exchange(ref pending, 0); CheckMonitor(); if (active) step(); })); }
                    catch { }
                }
            }
        }

        public void Dispose() { closing = true; gate.Set(); }
    }

    // Drop-in for a short WinForms Timer used for fades: Tick fires once per display refresh instead of every ~15.6 ms.
    sealed class FrameTimer : IDisposable
    {
        readonly VsyncDriver drv;
        bool disposed;
        public event EventHandler Tick;
        public int Interval { get; set; }
        public FrameTimer(System.Windows.Forms.Control owner) { drv = new VsyncDriver(owner, () => { if (!disposed && Tick != null) Tick(this, EventArgs.Empty); }); }
        public void Start() { if (!disposed) drv.Wake(); }
        public void Stop() { drv.Sleep(); }
        public void Dispose() { if (disposed) return; disposed = true; drv.Dispose(); }
    }

    // Damped spring. omega is the natural frequency (rad/s, higher = snappier); zeta is the damping ratio
    // (1 = no overshoot, below 1 overshoots a little). Sub-stepped so large frame gaps stay stable.
    struct Spring
    {
        public double Value, Velocity;

        public Spring(double value) { Value = value; Velocity = 0; }

        public void Step(double target, double omega, double zeta, double dt)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
            double h = dt / steps;
            double k = omega * omega, c = 2 * zeta * omega;
            for (int i = 0; i < steps; i++)
            {
                Velocity += (k * (target - Value) - c * Velocity) * h;
                Value += Velocity * h;
            }
        }

        public bool IsSettled(double target, double tolerance = 0.002)
        {
            return Math.Abs(target - Value) < tolerance && Math.Abs(Velocity) < tolerance * 4;
        }

        public void Snap(double target) { Value = target; Velocity = 0; }
    }

    // A 32-bit premultiplied bitmap backed by a DIB section that stays selected into a memory DC.
    // Drawing goes straight into the memory Windows composites from, so presenting a frame needs no
    // per-frame HBITMAP allocation or pixel copy.
    sealed class Surface : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        public readonly int Width, Height;
        public readonly Bitmap Bitmap;

        readonly IntPtr memDc, dib, oldObject;

        public Surface(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth = Width,
                biHeight = -Height, // top-down
                biPlanes = 1,
                biBitCount = 32
            };
            IntPtr bits;
            dib = CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
            memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            oldObject = NativeMethods.SelectObject(memDc, dib);
            Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        // A cleared Graphics for one frame. Dispose it before calling Present.
        public Graphics Begin()
        {
            Graphics g = Graphics.FromImage(Bitmap);
            g.Clear(Color.Transparent);
            return g;
        }

        // Like Begin(), but clears only the given region (callers that know what changed).
        public Graphics BeginRegion(Rectangle dirty)
        {
            Graphics g = Graphics.FromImage(Bitmap);
            dirty.Intersect(new Rectangle(0, 0, Width, Height));
            g.SetClip(dirty);
            g.Clear(Color.Transparent);
            g.ResetClip();
            return g;
        }

        // "opacity" fades the whole window (0-255) on top of the per-pixel alpha.
        public void Present(IntPtr hwnd, int x, int y, byte opacity = 255)
        {
            var size = new NativeMethods.SIZE { cx = Width, cy = Height };
            var src = new NativeMethods.POINT();
            var dst = new NativeMethods.POINT { X = x, Y = y };
            var blend = new NativeMethods.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            NativeMethods.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UpdateInfo
        {
            public uint cbSize;
            public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
            public uint crKey;
            public IntPtr pblend;
            public uint dwFlags;
            public IntPtr prcDirty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UpdateInfo info);

        // Like Present, but tells Windows that only "dirty" (surface coordinates) changed since the last present, so it
        // doesn't have to refresh the whole window. Falls back to a full present if the call fails.
        public void Present(IntPtr hwnd, int x, int y, byte opacity, Rectangle dirty)
        {
            dirty.Intersect(new Rectangle(0, 0, Width, Height));
            if (dirty.Width <= 0 || dirty.Height <= 0) { Present(hwnd, x, y, opacity); return; }

            IntPtr pDst = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.POINT)));
            IntPtr pSize = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.SIZE)));
            IntPtr pSrc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.POINT)));
            IntPtr pBlend = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.BLENDFUNCTION)));
            IntPtr pDirty = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.StructureToPtr(new NativeMethods.POINT { X = x, Y = y }, pDst, false);
                Marshal.StructureToPtr(new NativeMethods.SIZE { cx = Width, cy = Height }, pSize, false);
                Marshal.StructureToPtr(new NativeMethods.POINT(), pSrc, false);
                Marshal.StructureToPtr(new NativeMethods.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 }, pBlend, false);
                Marshal.WriteInt32(pDirty, 0, dirty.Left);
                Marshal.WriteInt32(pDirty, 4, dirty.Top);
                Marshal.WriteInt32(pDirty, 8, dirty.Right);
                Marshal.WriteInt32(pDirty, 12, dirty.Bottom);
                var info = new UpdateInfo
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(UpdateInfo)),
                    hdcDst = IntPtr.Zero, pptDst = pDst, psize = pSize, hdcSrc = memDc, pptSrc = pSrc,
                    crKey = 0, pblend = pBlend, dwFlags = 2, prcDirty = pDirty
                };
                if (!UpdateLayeredWindowIndirect(hwnd, ref info)) Present(hwnd, x, y, opacity);
            }
            finally
            {
                Marshal.FreeHGlobal(pDst); Marshal.FreeHGlobal(pSize); Marshal.FreeHGlobal(pSrc);
                Marshal.FreeHGlobal(pBlend); Marshal.FreeHGlobal(pDirty);
            }
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            NativeMethods.SelectObject(memDc, oldObject);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memDc);
        }
    }
}
