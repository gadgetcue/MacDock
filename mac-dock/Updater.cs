// "Check for Updates..." : compares the running version with the latest GitHub release and swaps in the new MacDock.exe.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    static class Updater
    {
        const string Api = "https://api.github.com/repos/gadgetcue/MacDock/releases/latest";
        static int busy;

        internal static void Run()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            var t = new Thread(() =>
            {
                try { Do(); }
                catch (Exception e) { MacDockProgram_Log(e); ScreenCapture.Toast("Update failed: " + e.Message); }
                finally { busy = 0; }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void MacDockProgram_Log(Exception e) { try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "update.log"), DateTime.Now + " " + e + "\r\n"); } catch { } }

        static Version Parse(string s)
        {
            Match m = Regex.Match(s ?? "", @"\d+(\.\d+){1,3}");
            Version v;
            return m.Success && Version.TryParse(m.Value, out v) ? v : null;
        }

        static void Do()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            ScreenCapture.Toast("Checking for updates...");
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MacDock-Updater";
                json = wc.DownloadString(Api);
            }
            string tag = Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""").Groups[1].Value;
            Version latest = Parse(tag), cur = Parse(Application.ProductVersion);
            if (latest == null) throw new Exception("no release found");
            if (cur != null && latest <= cur) { Thread.Sleep(2000); ScreenCapture.Toast("MacDock is up to date (" + cur + ")"); return; }

            // Prefer a .zip asset; fall back to the source zipball is NOT used (needs bin\MacDock.exe).
            string url = null;
            foreach (Match m in Regex.Matches(json, @"""browser_download_url""\s*:\s*""([^""]+\.zip)"""))
            { url = m.Groups[1].Value; break; }
            if (url == null) throw new Exception("release has no zip");

            Thread.Sleep(2000);
            ScreenCapture.Toast("Updating to " + tag + "...");
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            string tmpZip = Path.Combine(dir, "update.zip.tmp");
            string staged = exe + ".new";
            string old = exe + ".old";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MacDock-Updater";
                wc.DownloadFile(url, tmpZip);
            }
            try
            {
                using (var z = ZipFile.OpenRead(tmpZip))
                {
                    ZipArchiveEntry e = null;
                    foreach (var en in z.Entries)
                        if (en.Name.Equals("MacDock.exe", StringComparison.OrdinalIgnoreCase)) { e = en; break; }
                    if (e == null) throw new Exception("MacDock.exe not in zip");
                    e.ExtractToFile(staged, true);
                }
            }
            finally { try { File.Delete(tmpZip); } catch { } }

            if (new FileInfo(staged).Length < 100000) { File.Delete(staged); throw new Exception("downloaded exe looks invalid"); }
            // Staged exe must carry a newer version, else refuse (never downgrade / swap in junk).
            Version nv = Parse(FileVersionInfo.GetVersionInfo(staged).FileVersion);
            if (nv == null || (cur != null && nv <= cur)) { File.Delete(staged); throw new Exception("release exe is not newer"); }

            // Rename the running exe (allowed on Windows), put the new one in its place. dock.txt etc. are untouched.
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(staged, exe); }
            catch { File.Move(old, exe); throw; }

            ScreenCapture.Toast("Updated to " + tag + " - restarting...");
            Thread.Sleep(1500);
            // Restart through the MacDockGuard task once this instance has exited.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & schtasks /Run /TN MacDockGuard") { CreateNoWindow = true, UseShellExecute = false });
            Program.QuitRequested = true;
            Environment.Exit(0);
        }
    }
}
