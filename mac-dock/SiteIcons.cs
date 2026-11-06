// Website icons for URL shortcuts in the Dock: the site's own apple-touch-icon / favicon, fetched in the background,
// cached as PNG in icons\sites\<host>.png, and used in place of the generic globe once it arrives.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace MacDock
{
    static class SiteIcons
    {
        static readonly string Dir = Path.Combine(AppPaths.IconsDir, "sites");
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string HostOf(string target)
        {
            Uri u;
            if (!Uri.TryCreate(target, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            return u.Host.ToLowerInvariant();
        }

        // The cached icon for this website (a new bitmap the caller owns), or null. A missing icon is fetched in the
        // background; when it arrives the Dock rebuilds its icons.
        public static Bitmap Get(string target)
        {
            string host = HostOf(target);
            if (host == null) return null;
            string file = Path.Combine(Dir, host + ".png");
            try
            {
                if (File.Exists(file))
                    using (var img = new Bitmap(file)) return new Bitmap(img);
            }
            catch { }

            lock (pending)
            {
                if (failed.Contains(host) || !pending.Add(host)) return null;
            }
            Uri page = new Uri(target);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                try { ok = Fetch(page, file); } catch { }
                lock (pending) { pending.Remove(host); if (!ok) failed.Add(host); }
                if (ok)
                {
                    DockForm d = DockForm.Instance;
                    try { if (d != null && d.IsHandleCreated) d.BeginInvoke((Action)d.RefreshIcons); } catch { }
                }
            });
            return null;
        }

        static bool Fetch(Uri page, string file)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var urls = new List<string>();
            string root = page.GetLeftPart(UriPartial.Authority);

            string html = null;
            try { html = DownloadText(root + "/"); } catch { }
            if (html != null)
            {
                // <link rel="...icon..." href="..." sizes="180x180">: collect, apple-touch-icons and bigger sizes first.
                var found = new List<KeyValuePair<int, string>>();
                foreach (Match m in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string tag = m.Value;
                    string rel = Attr(tag, "rel"), href = Attr(tag, "href"), sizes = Attr(tag, "sizes");
                    if (rel == null || href == null || rel.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (href.IndexOf(".svg", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    int size = 0;
                    if (sizes != null) { var sm = Regex.Match(sizes, "(\\d+)x\\d+"); if (sm.Success) int.TryParse(sm.Groups[1].Value, out size); }
                    if (rel.IndexOf("apple-touch", StringComparison.OrdinalIgnoreCase) >= 0) size += 1000;
                    try { found.Add(new KeyValuePair<int, string>(size, new Uri(new Uri(root + "/"), href).ToString())); } catch { }
                }
                found.Sort((a, b) => b.Key.CompareTo(a.Key));
                foreach (var f in found) urls.Add(f.Value);
            }
            urls.Add(root + "/apple-touch-icon.png");
            urls.Add(root + "/favicon.ico");
            urls.Add("https://www.google.com/s2/favicons?sz=256&domain=" + Uri.EscapeDataString(page.Host));

            Bitmap best = null;
            int tried = 0;
            foreach (string u in urls)
            {
                if (++tried > 6) break;
                Bitmap bmp = null;
                try { bmp = LoadImage(u); } catch { }
                if (bmp == null) continue;
                if (best == null || bmp.Width > best.Width) { if (best != null) best.Dispose(); best = bmp; } else bmp.Dispose();
                if (best.Width >= 120) break; // good enough; stop asking
            }
            if (best == null || best.Width < 16) { if (best != null) best.Dispose(); return false; }

            Directory.CreateDirectory(Dir);
            string tmp = file + ".tmp";
            using (best) best.Save(tmp, ImageFormat.Png);
            File.Copy(tmp, file, true);
            File.Delete(tmp);
            return true;
        }

        static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, "\\b" + name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }

        static byte[] Download(string url, int maxBytes)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MacDock";
            req.Timeout = 8000;
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > maxBytes) return null;
                }
                return ms.ToArray();
            }
        }

        static string DownloadText(string url)
        {
            byte[] b = Download(url, 1024 * 1024);
            return b == null ? null : System.Text.Encoding.UTF8.GetString(b);
        }

        static Bitmap LoadImage(string url)
        {
            byte[] data = Download(url, 6 * 1024 * 1024);
            if (data == null || data.Length < 16) return null;
            bool ico = data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;
            using (var ms = new MemoryStream(data))
            {
                if (ico)
                    using (var icon = new Icon(ms, new Size(256, 256))) return icon.ToBitmap();
                using (var img = Image.FromStream(ms)) return new Bitmap(img);
            }
        }
    }
}
