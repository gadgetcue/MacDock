// The signed-in Windows user's account picture, for the top bar's profile icon.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace MacDock
{
    static class AccountPic
    {
        static Bitmap src;                       // square source, null when none
        static readonly Dictionary<int, Bitmap> sized = new Dictionary<int, Bitmap>();
        static readonly object gate = new object();
        static int started;
        static readonly string CacheFile = Path.Combine(AppPaths.DataDir, "avatar.png");

        // Starts loading once; onReady fires (on a pool thread) when a picture becomes available.
        public static void Start(Action onReady)
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Bitmap cached = Load(CacheFile);
                    if (cached != null) { Set(cached); onReady(); }
                    Bitmap fresh = FindSystemPicture();
                    if (fresh != null)
                    {
                        Set(fresh); onReady();
                        try { Directory.CreateDirectory(AppPaths.DataDir); using (var c = new Bitmap(fresh)) c.Save(CacheFile, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                }
                catch { }
            });
        }

        static void Set(Bitmap b) { lock (gate) { src = b; foreach (var v in sized.Values) v.Dispose(); sized.Clear(); } }

        // Circle-cropped bitmap at px x px, or null (caller falls back to the glyph).
        public static Bitmap Get(int px)
        {
            lock (gate)
            {
                if (src == null) return null;
                Bitmap r;
                if (sized.TryGetValue(px, out r)) return r;
                Bitmap cur = src; int w = cur.Width; bool own = false;
                while (w / 2 >= px) { w /= 2; cur = Scale(cur, w, own); own = true; }
                var o = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(o))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var gp = new GraphicsPath()) { gp.AddEllipse(0, 0, px, px); g.SetClip(gp); }
                    g.DrawImage(cur, new Rectangle(0, 0, px, px), 0, 0, cur.Width, cur.Height, GraphicsUnit.Pixel, Attr());
                }
                if (own) cur.Dispose();
                sized[px] = o; return o;
            }
        }

        static System.Drawing.Imaging.ImageAttributes Attr()
        { var a = new System.Drawing.Imaging.ImageAttributes(); a.SetWrapMode(WrapMode.TileFlipXY); return a; }

        static Bitmap Scale(Bitmap b, int w, bool disposeSrc)
        {
            var o = new Bitmap(w, w, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(o))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(b, new Rectangle(0, 0, w, w), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, Attr());
            }
            if (disposeSrc) b.Dispose();
            return o;
        }

        // Loads a file into a centered-square bitmap.
        static Bitmap Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(data))
                using (var img = new Bitmap(ms))
                {
                    int s = Math.Min(img.Width, img.Height);
                    if (s < 8) return null;
                    var o = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(o))
                        g.DrawImage(img, new Rectangle(0, 0, s, s), (img.Width - s) / 2, (img.Height - s) / 2, s, s, GraphicsUnit.Pixel);
                    return o;
                }
            }
            catch { return null; }
        }

        static Bitmap FindSystemPicture()
        {
            var paths = new List<string>();
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid))
                    if (k != null)
                        foreach (string n in k.GetValueNames().Where(x => x.StartsWith("Image")).OrderByDescending(x => x.Length).ThenByDescending(x => x))
                        { var v = k.GetValue(n) as string; if (!string.IsNullOrEmpty(v)) paths.Add(v); }
                string pub = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AccountPictures", sid);
                if (Directory.Exists(pub))
                    paths.AddRange(Directory.GetFiles(pub).OrderByDescending(f => new FileInfo(f).Length));
            }
            catch { }
            foreach (string p in paths)
            {
                var b = Load(p);
                if (b != null) return b;
            }
            return null;
        }
    }
}
