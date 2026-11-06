// "MacDock.exe /wt-selftest <output folder>": starts the classic Character Map window, lets WindowTheme skin it, saves screenshots
// of its title bar (dark, light, hover symbols, maximised) and reports what happened. No mouse or keyboard input is injected:
// state changes go through the same code paths the overlay uses (Perform, SetHoverForTest).

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    static class WindowThemeTest
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowTheme.WRect r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

        static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
        }

        static void Shot(IntPtr hwnd, string path, bool whole)
        {
            WindowTheme.WRect r;
            GetWindowRect(hwnd, out r);
            int x = r.Left, y = r.Top, w = r.Right - r.Left, h = whole ? Math.Min(r.Bottom - r.Top, 420) : 70;
            using (var bmp = new Bitmap(w + 40, h + 40))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x - 20, y - 20, 0, 0, bmp.Size);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public static int Run(string outDir)
        {
            var log = new StringBuilder();
            Directory.CreateDirectory(outDir);
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Theme.Refresh();
            Typography.Configure(AppPaths.Config);

            Process p = Process.Start("charmap.exe");
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++)
            {
                Pump(100);
                p.Refresh();
                if (p.HasExited) break;
                hwnd = p.MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero) { File.WriteAllText(Path.Combine(outDir, "result.txt"), "no window"); return 2; }
            Pump(600);

            WindowTheme.Install();
            Pump(900);
            CaptionOverlay ov = WindowTheme.Find(hwnd);
            log.AppendLine("installed=" + WindowTheme.IsInstalled + " overlays=" + WindowTheme.OverlayCount + " overlayForWindow=" + (ov != null));
            if (ov == null) { File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString()); return 3; }
            CaptionInfo ci = ov.Info;
            log.AppendLine("caption " + ci.X + "," + ci.Y + " " + ci.W + "x" + ci.H + " dpi=" + ci.Dpi + " deadFrom=" + ci.DeadFrom + " title='" + ci.Title + "' canMin=" + ci.CanMin + " canMax=" + ci.CanMax);
            Shot(hwnd, Path.Combine(outDir, "1-" + (Theme.Light ? "light" : "dark") + ".png"), true);

            ov.SetHoverForTest(true);
            Pump(200);
            Shot(hwnd, Path.Combine(outDir, "2-hover.png"), false);
            ov.SetHoverForTest(false);

            Theme.Light = !Theme.Light;
            WindowTheme.ThemeChanged();
            Pump(700);
            Shot(hwnd, Path.Combine(outDir, "3-" + (Theme.Light ? "light" : "dark") + ".png"), false);
            ov = WindowTheme.Find(hwnd);
            if (ov != null) { ov.SetHoverForTest(true); Pump(200); Shot(hwnd, Path.Combine(outDir, "4-hover-other.png"), false); ov.SetHoverForTest(false); }

            WindowTheme.Perform(hwnd, 2); // green button: maximise
            Pump(1200);
            log.AppendLine("after green: zoomed=" + IsZoomed(hwnd));
            Shot(hwnd, Path.Combine(outDir, "5-maximised.png"), false);
            WindowTheme.Perform(hwnd, 2); // green again: restore
            Pump(1200);
            log.AppendLine("after green again: zoomed=" + IsZoomed(hwnd));

            WindowTheme.Perform(hwnd, 1); // yellow: minimise
            Pump(1500);
            log.AppendLine("after yellow: iconic=" + IsIconic(hwnd) + " overlayRemoved=" + (WindowTheme.Find(hwnd) == null));
            ShowWindowRestore(hwnd);
            Pump(1200);
            log.AppendLine("restored: iconic=" + IsIconic(hwnd) + " overlayBack=" + (WindowTheme.Find(hwnd) != null));

            WindowTheme.Perform(hwnd, 0); // red: close
            Pump(1500);
            log.AppendLine("after red: windowExists=" + IsWindow(hwnd) + " processExited=" + p.HasExited + " overlays=" + WindowTheme.OverlayCount);
            if (!p.HasExited) { try { p.Kill(); } catch { } }

            WindowTheme.Uninstall();
            File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString());
            return 0;
        }

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        static void ShowWindowRestore(IntPtr hwnd) { ShowWindow(hwnd, 9); } // SW_RESTORE
    }
}
