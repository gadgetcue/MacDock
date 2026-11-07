using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace MacDock
{
    // Per-folder icon tint, stored in foldercolors.txt next to the exe ("path=Name").
    static class FolderColor
    {
        public static readonly KeyValuePair<string, Color>[] Choices =
        {
            new KeyValuePair<string, Color>("Blue", Color.FromArgb(10, 132, 255)),
            new KeyValuePair<string, Color>("Red", Color.FromArgb(255, 69, 58)),
            new KeyValuePair<string, Color>("Orange", Color.FromArgb(255, 149, 0)),
            new KeyValuePair<string, Color>("Yellow", Color.FromArgb(255, 204, 0)),
            new KeyValuePair<string, Color>("Green", Color.FromArgb(48, 209, 88)),
            new KeyValuePair<string, Color>("Purple", Color.FromArgb(175, 82, 222)),
            new KeyValuePair<string, Color>("Pink", Color.FromArgb(255, 105, 180)),
            new KeyValuePair<string, Color>("Graphite", Color.FromArgb(142, 142, 147)),
        };

        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "foldercolors.txt"); } }

        static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string l in File.ReadAllLines(FilePath))
                {
                    int i = l.LastIndexOf('=');
                    if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        public static string Get(string path)
        {
            string v; return Load().TryGetValue(path, out v) ? v : null;
        }

        public static void Set(string path, string name)
        {
            var d = Load();
            if (name == null) d.Remove(path); else d[path] = name;
            try { File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value).ToArray()); } catch { }
        }

        // Returns a tinted copy (disposing the source) keeping the icon's shading.
        public static Bitmap Apply(string path, Bitmap src)
        {
            string name = Get(path);
            if (name == null || src == null) return src;
            Color c = Color.Empty;
            foreach (var ch in Choices) if (ch.Key == name) c = ch.Value;
            if (c.IsEmpty) return src;
            float th = c.GetHue(), ts = c.GetSaturation();
            bool gray = name == "Graphite";
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Bitmap s = new Bitmap(src))
            {
                var r = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData sd = s.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData dd = bmp.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                byte[] buf = new byte[sd.Stride * sd.Height];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, buf, 0, buf.Length);
                for (int i = 0; i < buf.Length; i += 4)
                {
                    if (buf[i + 3] == 0) continue;
                    Color p = Color.FromArgb(buf[i + 2], buf[i + 1], buf[i]); // unpremultiplied BGRA from GDI+
                    float sat = p.GetSaturation(), l = p.GetBrightness();
                    if (sat < 0.12f && !gray) continue; // keep neutral shading/outlines
                    float ns = gray ? sat * 0.12f : Math.Min(1f, Math.Max(sat, 0.2f) * (0.5f + ts * 0.5f));
                    Color o = FromHsl(gray ? 220 : th, ns, l);
                    buf[i] = o.B; buf[i + 1] = o.G; buf[i + 2] = o.R;
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, dd.Scan0, buf.Length);
                s.UnlockBits(sd); bmp.UnlockBits(dd);
            }
            src.Dispose();
            return bmp;
        }

        static Color FromHsl(float h, float s, float l)
        {
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q, hk = h / 360f;
            Func<float, int> f = t =>
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                float v = t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
                return (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            };
            return Color.FromArgb(f(hk + 1f / 3), f(hk), f(hk - 1f / 3));
        }
    }

    // Custom display names for dock items, stored in itemnames.txt ("target=Name"). Files on disk are never renamed.
    static class ItemNames
    {
        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "itemnames.txt"); } }
        static Dictionary<string, string> cache;

        static Dictionary<string, string> Load()
        {
            if (cache != null) return cache;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string l in File.ReadAllLines(FilePath))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
            return cache = d;
        }

        public static string Get(string target)
        {
            string v; return target != null && Load().TryGetValue(target, out v) ? v : null;
        }

        public static string Display(string target, string label) { return Get(target) ?? label; }

        public static void Set(string target, string name)
        {
            var d = Load();
            if (string.IsNullOrWhiteSpace(name)) d.Remove(target); else d[target] = name.Trim();
            try { File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value).ToArray()); } catch { }
        }
    }
}

namespace MacDock
{
    // Small topmost rename box that takes focus; Enter confirms, Esc cancels.
    static class RenameBox
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        public static string Show(string current)
        {
            string result = null;
            using (var f = new System.Windows.Forms.Form())
            using (var tb = new System.Windows.Forms.TextBox())
            using (var ok = new System.Windows.Forms.Button())
            using (var cancel = new System.Windows.Forms.Button())
            {
                f.Text = "Rename"; f.TopMost = true; f.ShowInTaskbar = false;
                f.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                f.ClientSize = new Size(340, 84);
                tb.SetBounds(12, 14, 316, 24); tb.Text = current;
                ok.Text = "OK"; ok.SetBounds(172, 48, 74, 26); ok.DialogResult = System.Windows.Forms.DialogResult.OK;
                cancel.Text = "Cancel"; cancel.SetBounds(254, 48, 74, 26); cancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                f.Controls.AddRange(new System.Windows.Forms.Control[] { tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { f.TopMost = true; f.Activate(); SetForegroundWindow(f.Handle); tb.Focus(); tb.SelectAll(); };
                if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = tb.Text;
            }
            return result;
        }
    }
}
