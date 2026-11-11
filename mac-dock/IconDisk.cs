// On-disk cache of finished dock icons (%APPDATA%\MacDock\iconcache\*.png), so a cold start (e.g. right after login,
// when the machine is busy) does not rebuild every icon from the shell. The file name carries a stamp of everything the
// icon depends on (this exe, light/dark mode, icon-pack folders, the target's own file time), so stale entries are just ignored.

using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MacDock
{
    static class IconDisk
    {
        static readonly string Dir = Path.Combine(AppPaths.DataDir, "iconcache");
        static string baseStamp;

        static string BaseStamp()
        {
            if (baseStamp != null) return baseStamp;
            var sb = new StringBuilder("v1|");
            try { sb.Append(File.GetLastWriteTimeUtc(System.Windows.Forms.Application.ExecutablePath).Ticks).Append('|'); } catch { }
            sb.Append(Theme.Light).Append('|');
            try { sb.Append(IconPack.Enabled).Append('|'); } catch { }
            try
            {
                foreach (string d in new[] { AppPaths.IconsDir, Path.Combine(AppPaths.IconsDir, "dark"), Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "icons")), Path.GetFullPath(Path.Combine(AppPaths.ExeDir, "..", "dark-icons")), Path.Combine(AppPaths.ExeDir, "icons-theme") })
                    if (Directory.Exists(d)) sb.Append(d).Append(Directory.GetLastWriteTimeUtc(d).Ticks).Append('|');
                if (File.Exists(AppPaths.IconMap)) sb.Append(File.GetLastWriteTimeUtc(AppPaths.IconMap).Ticks);
            }
            catch { }
            return baseStamp = sb.ToString();
        }

        static string PathFor(string key, string target)
        {
            string stamp = BaseStamp() + "|" + key;
            try { if (!string.IsNullOrEmpty(target) && File.Exists(target)) stamp += "|" + File.GetLastWriteTimeUtc(target).Ticks; } catch { }
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(stamp));
                return Path.Combine(Dir, BitConverter.ToString(h).Replace("-", "").ToLowerInvariant() + ".png");
            }
        }

        public static Bitmap TryLoad(string key, string target)
        {
            try
            {
                string p = PathFor(key, target);
                if (!File.Exists(p) || File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-7)) return null; // weekly refresh, so a one-off fallback icon doesn't stick
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs, false, false))
                    return new Bitmap(img); // detached copy: no file lock, no stream dependency
            }
            catch { return null; }
        }

        public static void Save(string key, string target, Bitmap bmp)
        {
            if (bmp == null) return;
            try
            {
                Directory.CreateDirectory(Dir);
                string p = PathFor(key, target);
                string tmp = p + "." + Environment.TickCount + ".tmp";
                using (var copy = new Bitmap(bmp)) copy.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                if (File.Exists(p)) File.Delete(tmp); else File.Move(tmp, p);
                // keep the folder from growing forever: drop files not touched for 60 days
                if ((Environment.TickCount & 7) == 0)
                    foreach (string f in Directory.GetFiles(Dir, "*.png"))
                        if (File.GetLastAccessTimeUtc(f) < DateTime.UtcNow.AddDays(-60) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-60)) File.Delete(f);
            }
            catch { }
        }
    }
}
