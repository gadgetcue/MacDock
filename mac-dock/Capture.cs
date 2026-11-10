// One-click screenshot and screen recording for the menu bar and Control Center.
// Screenshot: whole screen -> clipboard + Pictures\MacDock Captures. Record: ffmpeg (if installed) -> mp4, otherwise Game Bar (Win+Alt+R).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    static class ScreenCapture
    {
        static Process rec;
        static volatile bool gameBarRec;

        // Four independent toggles. Each defaults to the legacy shared @capture-buttons value so existing users see no change.
        static bool Flag(string key) { return SetCfg.GetBool(key, SetCfg.GetBool("capture-buttons", true)); }
        public static bool BarShot { get { return Flag("capture-bar-shot"); } set { SetCfg.SetBool("capture-bar-shot", value); } }
        public static bool BarRec { get { return Flag("capture-bar-rec"); } set { SetCfg.SetBool("capture-bar-rec", value); } }
        public static bool TileShot { get { return Flag("capture-tile-shot"); } set { SetCfg.SetBool("capture-tile-shot", value); } }
        public static bool TileRec { get { return Flag("capture-tile-rec"); } set { SetCfg.SetBool("capture-tile-rec", value); } }

        public static bool Recording { get { return gameBarRec || (rec != null && !rec.HasExited); } }

        static string Dir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MacDock Captures");
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Screenshot()
        {
            var t = new Thread(() =>
            {
                try
                {
                    Rectangle b = SystemInformation.VirtualScreen;
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        string path = Path.Combine(Dir(), "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
                        bmp.Save(path, ImageFormat.Png);
                        try { Clipboard.SetImage(bmp); } catch { }
                        Toast("Screenshot saved and copied");
                    }
                }
                catch (Exception e) { Toast("Screenshot failed: " + e.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        static string FindFfmpeg()
        {
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (string d in dirs)
            {
                try { string p = Path.Combine(d.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        public static void ToggleRecord()
        {
            try
            {
                if (gameBarRec) { gameBarRec = false; WinUtil.Combo(0x5B, 0x12, (byte)'R'); return; }
                if (rec != null && !rec.HasExited)
                {
                    Process p = rec; rec = null;
                    try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                    ThreadPool.QueueUserWorkItem(_ => { if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } } Toast("Recording saved"); });
                    return;
                }
                string ff = FindFfmpeg();
                if (ff == null) { gameBarRec = true; WinUtil.Combo(0x5B, 0x12, (byte)'R'); Toast("Recording with Game Bar"); return; }
                string path = Path.Combine(Dir(), "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".mp4");
                var psi = new ProcessStartInfo(ff, "-y -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"" + path + "\"")
                { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
                rec = Process.Start(psi);
                Toast("Recording started");
            }
            catch (Exception e) { Toast("Recording failed: " + e.Message); }
        }

        // Small self-closing label at the top centre of the screen.
        static void Toast(string text)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var f = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual,
                        BackColor = Color.FromArgb(32, 32, 36), Opacity = 0.94, Size = new Size(320, 40)
                    };
                    var l = new Label { Text = text, ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10f) };
                    f.Controls.Add(l);
                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                    f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + 40);
                    var tm = new System.Windows.Forms.Timer { Interval = 1800 };
                    tm.Tick += (s, e) => { tm.Stop(); f.Close(); };
                    f.Shown += (s, e) => tm.Start();
                    Application.Run(f);
                }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}
