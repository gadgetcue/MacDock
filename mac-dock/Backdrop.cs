// Native Windows background blur (DWM) for the dock, the top bar, the menus, the panels and Mission Control.
//
// A layered window with per-pixel alpha can't be blurred by DWM, so each glass element gets a companion
// window that sits just below it: a plain window with no bitmap of its own, with the blur accent applied.
// DWM and the GPU do the blurring; the glass window above keeps drawing the tint, rim highlight and icons.
//
// The blur layer does not reliably follow a window region, so the blur rectangle is simply kept inside the
// glass shape: inset far enough that its square corners never poke out past the glass's rounded corners.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    static class Blur
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor; // ABGR
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        const int WCA_ACCENT_POLICY = 19;

        // Acrylic (4), not plain blur-behind (3): on this Windows 11 build plain blur-behind renders solid black over the
        // desktop wallpaper. Acrylic stays translucent, falling back to a flat tint when the window isn't active.
        const int ACCENT_ENABLE_BLURBEHIND = 4;

        // Packs a color for the accent policy, which expects 0xAABBGGRR.
        public static uint Tint(int alpha, int r, int g, int b)
        {
            return (uint)(alpha << 24 | b << 16 | g << 8 | r);
        }

        const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2; // a flat translucent tint with no blur

        // Turns the accent effect off again so the window paints as an ordinary opaque one (used by light themes).
        public static bool Disable(IntPtr hwnd) { return Apply(hwnd, 0, false, true); }

        public static bool Apply(IntPtr hwnd, uint tint, bool flat = false, bool off = false)
        {
            var policy = new AccentPolicy { AccentState = off ? 0 : (flat ? ACCENT_ENABLE_TRANSPARENTGRADIENT : ACCENT_ENABLE_BLURBEHIND), AccentFlags = 2, GradientColor = tint };
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = buffer, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    sealed class Backdrop : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4;
        const int WM_NCHITTEST = 0x84;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        readonly uint tint;
        Rectangle placed;
        float placedRadius = -1;
        bool shown, madeTopmost;

        // True (the default) for the dock, bar, menus and panels, which float above every app. Mission Control is an
        // ordinary window, so its blur must stay in the normal layer, just below it.
        public bool AlwaysOnTop = true;

        readonly bool flat;

        public Backdrop(uint tint, bool flat = false)
        {
            this.tint = tint;
            this.flat = flat;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // tool window, no-activate, transparent to input
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Blur.Apply(Handle, tint, flat)) Program.Log("Window blur is not available on this system; the glass stays translucent without it.");
        }

        // Mouse input falls through to whatever is underneath (the glass window above takes the clicks itself).
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)(-1); return; }
            if (m.Msg == 0x86 && !flat) m.WParam = (IntPtr)1; // WM_NCACTIVATE: always look active, or acrylic drops to a solid dark fill
            base.WndProc(ref m);
        }

        // The blur is a stack of square acrylic windows: one body plus thin bands whose widths follow the corner arcs.
        // Acrylic ignores window regions and Windows' own corner rounding is a fixed small radius, so this is the way
        // to get the pill's exact rounded shape.
        const int CornerBands = 6;
        readonly System.Collections.Generic.List<Backdrop> strips = new System.Collections.Generic.List<Backdrop>();
        bool cornersSet;

        // Puts the blur at a screen rectangle (the glass's bounds), directly below the window that draws the glass.
        public void Place(Rectangle screenRect, float cornerRadius, IntPtr windowAbove)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
            if (shown && screenRect == placed && Math.Abs(cornerRadius - placedRadius) < 0.01f) return;

            int r = (int)Math.Min(Math.Round(cornerRadius), Math.Min(screenRect.Width, screenRect.Height) / 2);
            if (r < 2)
            {
                PlaceSquare(screenRect, windowAbove);
            }
            else
            {
                PlaceSquare(new Rectangle(screenRect.Left, screenRect.Top + r, screenRect.Width, screenRect.Height - 2 * r), windowAbove);
                while (strips.Count < CornerBands * 2)
                    strips.Add(new Backdrop(tint, flat) { AlwaysOnTop = AlwaysOnTop });
                for (int k = 0; k < CornerBands; k++)
                {
                    int y0 = k * r / CornerBands, y1 = (k + 1) * r / CornerBands;
                    if (y1 <= y0) y1 = y0 + 1;
                    double dy = r - (y0 + 0.5);
                    int inset = (int)Math.Ceiling(r - Math.Sqrt(Math.Max(0, (double)r * r - dy * dy)));
                    int left = screenRect.Left + inset, width = screenRect.Width - 2 * inset;
                    strips[k * 2].PlaceSquare(new Rectangle(left, screenRect.Top + y0, width, y1 - y0), windowAbove);
                    strips[k * 2 + 1].PlaceSquare(new Rectangle(left, screenRect.Bottom - y1, width, y1 - y0), windowAbove);
                }
            }

            placed = screenRect;
            placedRadius = cornerRadius;
            shown = true;
        }

        void PlaceSquare(Rectangle area, IntPtr windowAbove)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            IntPtr handle = Handle; // creates the window on first use
            if (!cornersSet)
            {
                int none = 1; // DWMWCP_DONOTROUND: Windows 11 would otherwise round each piece's corners
                DwmSetWindowAttribute(handle, 33, ref none, sizeof(int));
                cornersSet = true;
            }

            // WinForms never applies "topmost" to a window that is only shown through SetWindowPos, which left these
            // windows in the normal layer, hidden behind every app. Put them in the topmost layer explicitly.
            if (AlwaysOnTop && !madeTopmost)
            {
                SetWindowPos(handle, HWND_TOPMOST, area.Left, area.Top, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                madeTopmost = true;
            }
            SetWindowPos(handle, windowAbove, area.Left, area.Top, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
        }

        public void HideBackdrop()
        {
            foreach (Backdrop strip in strips) strip.HideBackdrop();
            if (!shown || !IsHandleCreated) return;
            shown = false;
            madeTopmost = false;
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (Backdrop strip in strips) strip.Dispose();
            base.Dispose(disposing);
        }    }
}
