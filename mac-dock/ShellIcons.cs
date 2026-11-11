// Icons for anything in Windows' "AppsFolder" (Store apps, Start menu apps), addressed as
// shell:AppsFolder\<id>. The classic icon APIs can't resolve these, but the shell's image factory can.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MacDock
{
    static class ShellIcons
    {
        public const string AppsFolderPrefix = "shell:AppsFolder\\";

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeMethods.SIZE size, int flags, out IntPtr hbitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public short bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory factory);
        [DllImport("gdi32.dll")]
        static extern int GetObject(IntPtr handle, int count, out BITMAP bitmap);
        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("user32.dll")]
        static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

        // Converts an HICON to a bitmap with proper transparency. Icon.ToBitmap() can leave an opaque black
        // background behind large shell icons; this reads the colour bits directly and falls back to the AND mask.
        public static Bitmap FromHIcon(IntPtr hIcon)
        {
            ICONINFO ii;
            if (!GetIconInfo(hIcon, out ii)) return null;
            try
            {
                BITMAP bm;
                GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm);
                int w = bm.bmWidth, h = bm.bmHeight;
                if (w <= 0 || h <= 0) return null;

                var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var pixels = new byte[w * h * 4];
                IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(screen, ii.hbmColor, 0, (uint)h, pixels, ref header, 0) == 0) return null;

                    bool hasAlpha = false;
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
                    if (!hasAlpha && ii.hbmMask != IntPtr.Zero)
                    {
                        // No alpha channel: take transparency from the mask (the mask bitmap is w x h, 1 = transparent).
                        var mh = new BITMAPINFOHEADER { biSize = header.biSize, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                        var mask = new byte[w * h * 4];
                        if (GetDIBits(screen, ii.hbmMask, 0, (uint)h, mask, ref mh, 0) != 0)
                            for (int i = 0; i < pixels.Length; i += 4) pixels[i + 3] = (byte)(mask[i] == 0 ? 255 : 0);
                        else
                            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    }
                }
                finally { NativeMethods.ReleaseDC(IntPtr.Zero, screen); }

                // Straight-alpha pixels: load as Argb, then hand back a premultiplied copy.
                using (var raw = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    BitmapData data = raw.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    raw.UnlockBits(data);
                    return new Bitmap(raw);
                }
            }
            catch { return null; }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            }
        }

        public static bool IsWebShortcut(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            string t = IsAppsFolderItem(target) ? target.Substring(AppsFolderPrefix.Length) : target;
            return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        }

        static Bitmap Globe(int size)
        {
            var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float m = size * 0.1f, d = size - 2 * m;
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.RectangleF(m, m, d, d),
                    System.Drawing.Color.FromArgb(90, 190, 255), System.Drawing.Color.FromArgb(10, 100, 220), 90f))
                    g.FillEllipse(fill, m, m, d, d);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 255, 255, 255), Math.Max(1f, size / 40f)))
                {
                    g.DrawEllipse(pen, m, m, d, d);
                    g.DrawEllipse(pen, m + d * 0.25f, m, d * 0.5f, d);
                    g.DrawLine(pen, m, m + d / 2, m + d, m + d / 2);
                    g.DrawLine(pen, m + d / 2, m, m + d / 2, m + d);
                }
            }
            return bmp;
        }

        public static bool IsAppsFolderItem(string target)
        {
            return target != null && target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Returns a size x size icon with transparency, or null if the shell can't produce one.
        // Safe to call from a worker thread that has been set to STA.
        public static Bitmap Load(string parsingName, int size)
        {
            if (IsWebShortcut(parsingName)) return Globe(size);
            // Icon only, then any size, then thumbnails allowed; a fully transparent result counts as a miss.
            return LoadFlags(parsingName, size, 1 | 4) ?? LoadFlags(parsingName, size, 1) ?? LoadFlags(parsingName, size, 0);
        }

        // True when no pixel is visible (an icon the shell "loaded" but that would paint as an empty tile).
        public static bool IsBlank(Bitmap b)
        {
            if (b == null) return true;
            try
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int n = Math.Abs(d.Stride) * b.Height; var buf = new byte[n];
                    Marshal.Copy(d.Scan0, buf, 0, n);
                    for (int y = 0; y < b.Height; y++)
                        for (int x = 0; x < b.Width; x++)
                            if (buf[y * Math.Abs(d.Stride) + x * 4 + 3] > 8) return false;
                    return true;
                }
                finally { b.UnlockBits(d); }
            }
            catch { return false; }
        }

        // Never-blank fallback: rounded gradient tile with the app's initial.
        public static Bitmap Generic(string name, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            string ini = string.IsNullOrEmpty(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
            if (ini.Length == 0) ini = "?";
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float m = size * 0.06f, d = size - 2 * m, r = d * 0.22f;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(m, m, r * 2, r * 2, 180, 90); path.AddArc(m + d - r * 2, m, r * 2, r * 2, 270, 90);
                    path.AddArc(m + d - r * 2, m + d - r * 2, r * 2, r * 2, 0, 90); path.AddArc(m, m + d - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new RectangleF(m, m, d, d), Color.FromArgb(150, 160, 180), Color.FromArgb(90, 100, 125), 90f))
                        g.FillPath(br, path);
                }
                var f = Typography.Text(size * 0.46f * 0.75f, Weight.Semibold);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(ini, f, Brushes.White, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }

        static Bitmap LoadFlags(string parsingName, int size, int flags)
        {
            // Internet shortcuts (.url / web links in the Start menu): the shell's icon handler for them runs the
            // browser, which tries to write to %userprofile%\Favorites and trips Controlled Folder Access
            // ("Unauthorized changes blocked"). Draw a globe instead of asking.
            if (IsWebShortcut(parsingName)) return Globe(size);

            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                IShellItemImageFactory factory;
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;

                // SIIGBF_BIGGERSIZEOK (1) | SIIGBF_ICONONLY (4): the app icon, never a document thumbnail.
                int hr = factory.GetImage(new NativeMethods.SIZE { cx = size, cy = size }, flags, out hbitmap);
                Marshal.ReleaseComObject(factory);
                if (hr != 0 || hbitmap == IntPtr.Zero) return null;

                BITMAP bm;
                GetObject(hbitmap, Marshal.SizeOf(typeof(BITMAP)), out bm);
                if (bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;

                // Read the pixels top-down, with their alpha channel (Image.FromHbitmap would drop it).
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = bm.bmWidth,
                    biHeight = -bm.bmHeight,
                    biPlanes = 1,
                    biBitCount = 32
                };
                var pixels = new byte[bm.bmWidth * bm.bmHeight * 4];
                IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
                int lines = GetDIBits(screen, hbitmap, 0, (uint)bm.bmHeight, pixels, ref header, 0);
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
                if (lines == 0) return null;

                using (var raw = new Bitmap(bm.bmWidth, bm.bmHeight, PixelFormat.Format32bppPArgb))
                {
                    BitmapData data = raw.LockBits(new Rectangle(0, 0, bm.bmWidth, bm.bmHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                    raw.UnlockBits(data);

                    var result = new Bitmap(size, size,PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(result))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(raw, 0, 0, size, size);
                    }
                    if (IsBlank(result)) { result.Dispose(); return null; }
                    return result;
                }
            }
            catch { return null; }
            finally { if (hbitmap != IntPtr.Zero) NativeMethods.DeleteObject(hbitmap); }
        }
    }
}
