// Finds macOS-style icons on macosicons.com for apps the built-in pack doesn't cover (Brave, Discord, VS Code...).
//
// The site's search API needs a free API key (https://macosicons.com/developers; the free plan allows 50
// requests a month). Put the key in dock.txt as   @macosicons-key = YOUR_KEY   or set MACOSICONS_API_KEY.
//
// It only runs when asked (never in the background), asks for at most 20 apps per run, remembers apps it
// already looked up, and records each icon's author in icons\credits.txt.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MacDock
{
    static class IconFetcher
    {
        const string SearchUrl = "https://api.macosicons.com/api/v1/search";
        const int MaxPerRun = 20;
        const int MaxIconBytes = 6 * 1024 * 1024;

        static readonly string IconsDir = AppPaths.IconsDir;
        static readonly string CheckedFile = Path.Combine(IconsDir, "_checked.txt");
        static readonly string CreditsFile = Path.Combine(IconsDir, "credits.txt");
        static readonly string ConfigFile = AppPaths.Config;

        static bool running;

        static string ApiKey()
        {
            string key = Environment.GetEnvironmentVariable("MACOSICONS_API_KEY");
            if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
            try
            {
                foreach (string raw in File.ReadAllLines(ConfigFile))
                {
                    string line = raw.Trim();
                    if (!line.ToLowerInvariant().StartsWith("@macosicons-key")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        // Looks up each name and saves the best match as icons\<name>.png. Reports the outcome in a message box.
        public static void FindIcons(List<string> names)
        {
            if (running) return;

            string key = ApiKey();
            if (string.IsNullOrEmpty(key))
            {
                MessageBox.Show(
                    "macosicons.com needs a free API key before MacDock can look up icons.\n\n" +
                    "1. Sign in at macosicons.com/developers and copy your API key.\n" +
                    "2. Add this line to dock.txt:   @macosicons-key = YOUR_KEY\n" +
                    "3. Run this command again.\n\n" +
                    "dock.txt will open now.",
                    "macOS icons", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start("notepad.exe", ConfigFile); } catch { }
                return;
            }

            Directory.CreateDirectory(IconsDir);
            var alreadyChecked = new HashSet<string>(ReadLines(CheckedFile), StringComparer.OrdinalIgnoreCase);
            List<string> todo = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !File.Exists(Path.Combine(IconsDir, IconPack.FileName(n) + ".png")) && !alreadyChecked.Contains(n))
                .Take(MaxPerRun)
                .ToList();

            if (todo.Count == 0)
            {
                MessageBox.Show("Every app in the Dock already has an icon, or was looked up before.\n(Delete icons\\_checked.txt to search again.)",
                    "macOS icons", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            running = true;
            SynchronizationContext ui = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var found = new List<string>();
                var missing = new List<string>();
                string error = null;

                foreach (string name in todo)
                {
                    try
                    {
                        if (Download(key, name)) found.Add(name);
                        else missing.Add(name);
                    }
                    catch (WebException ex)
                    {
                        HttpWebResponse response = ex.Response as HttpWebResponse;
                        error = response != null && (int)response.StatusCode == 401 ? "The API key was rejected."
                            : response != null && (int)response.StatusCode == 429 ? "The monthly request limit has been reached."
                            : ex.Message;
                        break;
                    }
                    catch (Exception ex) { error = ex.Message; break; }
                }

                // Remember the misses so they aren't looked up (and counted against the limit) again.
                try { File.AppendAllLines(CheckedFile, missing); } catch { }

                Action finish = () =>
                {
                    running = false;
                    if (found.Count > 0)
                    {
                        IconPack.Reset(); MissionControl.ResetIcons();
                        if (DockForm.Instance != null) DockForm.Instance.RefreshIcons();
                    }
                    string text = (found.Count > 0 ? "Added icons for: " + string.Join(", ", found) + ".\n" : "No new icons were added.\n")
                        + (missing.Count > 0 ? "No match on macosicons.com for: " + string.Join(", ", missing) + ".\n" : "")
                        + (error != null ? "\nStopped early: " + error : "");
                    MessageBox.Show(text.Trim(), "macOS icons", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                if (ui != null) ui.Post(__ => finish(), null); else finish();
            });
        }

        // Returns true if an icon was found and saved.
        static bool Download(string key, string name)
        {
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "query", name } });
            var request = (HttpWebRequest)WebRequest.Create(SearchUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Headers["x-api-key"] = key;
            request.UserAgent = "MacDock";
            request.Timeout = 20000;
            byte[] body = Encoding.UTF8.GetBytes(json);
            using (Stream s = request.GetRequestStream()) s.Write(body, 0, body.Length);

            string reply;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                reply = reader.ReadToEnd();

            var root = new JavaScriptSerializer().DeserializeObject(reply) as Dictionary<string, object>;
            object hitsObject;
            if (root == null || !root.TryGetValue("hits", out hitsObject)) return false;
            var hits = (hitsObject as IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>().ToList();

            // Best match: an app with exactly this name, otherwise one whose name contains (or is contained in) it.
            // Among those, prefer the style that fits macOS 27: Liquid Glass, light mode, official, most downloaded.
            Func<Dictionary<string, object>, string> nameOf = h => h.ContainsKey("appName") ? Convert.ToString(h["appName"]) : "";
            Func<Dictionary<string, object>, string, bool> flag = (h, k) => h.ContainsKey(k) && h[k] is bool && (bool)h[k];
            Func<Dictionary<string, object>, long> downloads = h => h.ContainsKey("downloads") ? Convert.ToInt64(h["downloads"]) : 0;

            List<Dictionary<string, object>> candidates = hits.Where(h => string.Equals(nameOf(h), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
                candidates = hits.Where(h => nameOf(h).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                                          || (nameOf(h).Length > 2 && name.IndexOf(nameOf(h), StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            Dictionary<string, object> best = candidates
                .Where(h => h.ContainsKey("lowResPngUrl"))
                .OrderByDescending(h => flag(h, "isLiquidGlass"))
                .ThenBy(h => flag(h, "isDarkMode"))
                .ThenByDescending(h => flag(h, "isOfficial"))
                .ThenByDescending(downloads)
                .FirstOrDefault();
            if (best == null) return false;

            string url = Convert.ToString(best["lowResPngUrl"]);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

            byte[] png;
            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = "MacDock";
                png = client.DownloadData(url);
            }

            // Only keep real PNG images of a sensible size.
            bool isPng = png.Length > 8 && png.Length <= MaxIconBytes
                && png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71 && png[4] == 13 && png[5] == 10 && png[6] == 26 && png[7] == 10;
            if (!isPng) return false;

            File.WriteAllBytes(Path.Combine(IconsDir, IconPack.FileName(name) + ".png"), png);

            Func<string, string> text = k => best.ContainsKey(k) && best[k] != null ? Convert.ToString(best[k]).Trim() : "";
            string credit = new[] { text("credit"), text("designedBy"), text("usersName") }.FirstOrDefault(s => s.Length > 0) ?? "unknown";
            string creditUrl = new[] { text("creditUrl"), text("designedByUrl") }.FirstOrDefault(s => s.Length > 0) ?? "";
            try { File.AppendAllText(CreditsFile, name + " | icon by " + credit + " | " + creditUrl + " | macosicons.com" + Environment.NewLine); } catch { }
            return true;
        }

        static IEnumerable<string> ReadLines(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path) : new string[0]; }
            catch { return new string[0]; }
        }
    }
}
