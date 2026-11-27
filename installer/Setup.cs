// MacDock single-file installer: one exe, per-user (no admin), payload embedded as a zip resource.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("MacDock Setup")]
[assembly: AssemblyDescription("Installer for MacDock (open source, per-user, no admin)")]
[assembly: AssemblyCompany("MacDock Project")]
[assembly: AssemblyProduct("MacDock")]
[assembly: AssemblyCopyright("Copyright (c) MacDock contributors")]
[assembly: AssemblyVersion("3.1.0.0")]
[assembly: AssemblyFileVersion("3.1.0.0")]

static class Setup
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool MoveFileEx(string from, string to, int flags);
    static string logFile;
    // Every change is written to <install>\install.log (same idea as WacOS instalacao.log), so a user or a scanner can see exactly what was done.
    static void Log(string line) { try { if (logFile != null) File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine); } catch { } }
    static void Task(string args) { try { Process.Start(new ProcessStartInfo("schtasks", args) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(15000); } catch { } }
    [STAThread]
    static int Main(string[] args)
    {
        string dest = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "MacDock");
        bool silent = Array.IndexOf(args, "/S") >= 0;
        if (Array.IndexOf(args, "/uninstall") >= 0)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            if (Array.IndexOf(args, "/final") < 0)
            {
                if (!silent && MessageBox.Show("Remove MacDock? Your pins and settings in %APPDATA%\\MacDock are kept.", "MacDock Setup", MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
                // Run the removal from a temp copy so the install folder (and this exe) can be deleted normally: no cmd/timeout self-delete trick.
                string tmp = Path.Combine(Path.GetTempPath(), "MacDock-Uninstall.exe");
                File.Copy(self, tmp, true);
                Process.Start(new ProcessStartInfo(tmp, "/uninstall /final" + (silent ? " /S" : "")) { UseShellExecute = false });
                return 0;
            }
            string ex = Path.Combine(dest, "bin", "MacDock.exe");
            try { if (File.Exists(ex)) Process.Start(ex, "/restore").WaitForExit(5000); } catch { } // brings the Windows taskbar back
            foreach (var p in Process.GetProcessesByName("MacDock")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            Task("/Delete /TN MacDockGuard /F");
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) k.DeleteValue("MacDock", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacDock", false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\MacDock", true)) k.DeleteValue("AutostartChoice", false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true)) k.DeleteValue(ex, false); } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacDock.lnk")); } catch { }
            for (int i = 0; i < 10 && Directory.Exists(dest); i++) { try { Directory.Delete(dest, true); } catch { Thread.Sleep(500); } }
            MoveFileEx(self, null, 4); // delete this temp copy at next restart
            return 0;
        }
        if (!silent && MessageBox.Show("Install MacDock for this user?\n\nNo admin rights needed. Installs to:\n" + dest, "MacDock Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;
        // A developer "live" install (flat layout, deployed by mac-dock\bi.ps1) must stay the only copy.
        if (File.Exists(Path.Combine(dest, "MacDock.exe")))
        {
            if (!silent) MessageBox.Show("A developer install already exists in " + dest + ".\nUpdate it with mac-dock\\bi.ps1 instead.", "MacDock Setup");
            return 1;
        }
        try
        {
            foreach (var p in Process.GetProcessesByName("MacDock")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            Directory.CreateDirectory(dest);
            logFile = Path.Combine(dest, "install.log"); Log("install " + Assembly.GetExecutingAssembly().GetName().Version + " to " + dest + " (per-user, no admin)");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var z = new ZipArchive(s))
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string t = Path.GetFullPath(Path.Combine(dest, e.FullName));
                    if (!t.StartsWith(dest + Path.DirectorySeparatorChar)) continue; // no path escape
                    Directory.CreateDirectory(Path.GetDirectoryName(t));
                    e.ExtractToFile(t, true);
                }
            Log("extracted payload to " + dest);
            string exe = Path.Combine(dest, "bin", "MacDock.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(dest, "Setup.exe"), true);
            Log("copied Setup.exe (the uninstaller)");
            // Start Menu shortcut
            Type sh = Type.GetTypeFromProgID("WScript.Shell"); object w = Activator.CreateInstance(sh);
            object lnk = sh.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, w, new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacDock.lnk") });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Log("shortcut: Start Menu\\MacDock.lnk");
            // Settings > Apps > Installed apps entry (per-user)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacDock"))
            {
                k.SetValue("DisplayName", "MacDock"); k.SetValue("DisplayVersion", "3.1.0"); k.SetValue("Publisher", "MacDock Project");
                k.SetValue("DisplayIcon", exe); k.SetValue("InstallLocation", dest);
                k.SetValue("UninstallString", "\"" + Path.Combine(dest, "Setup.exe") + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord); k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            // Integrated GPU (power saving) for the dock, as Settings > System > Display > Graphics does.
            try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences")) k.SetValue(exe, "GpuPreference=1;"); } catch { }
            // Start at login is opt-in. Silent installs keep an earlier choice and never enable it on their own.
            // The answer is stored for the app, which creates its one at-logon task (MacDockGuard) only when it is 1.
            bool auto;
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\MacDock"))
            {
                object prev = k.GetValue("AutostartChoice");
                auto = silent ? (prev is int && (int)prev == 1) : MessageBox.Show("Start MacDock automatically when you sign in?\n(You can change this later in MacDock settings.)", "MacDock Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                k.SetValue("AutostartChoice", auto ? 1 : 0, RegistryValueKind.DWord);
            }
            Log("start at sign-in: " + (auto ? "on" : "off"));
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            Log("install complete");
            if (!silent) MessageBox.Show("MacDock is installed and running." + (auto ? " It starts when you sign in (toggle in Settings)." : ""), "MacDock Setup");
            return 0;
        }
        catch (Exception ex) { MessageBox.Show("Install failed: " + ex.Message, "MacDock Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
    }
}

