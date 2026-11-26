// MacDock single-file installer: one exe, per-user (no admin), payload embedded as a zip resource.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("MacDock Setup")]
[assembly: AssemblyDescription("Installer for MacDock (open source, per-user, no admin)")]
[assembly: AssemblyCompany("MacDock Project")]
[assembly: AssemblyProduct("MacDock")]
[assembly: AssemblyCopyright("Copyright (c) MacDock contributors")]
[assembly: AssemblyVersion("2.4.0.0")]
[assembly: AssemblyFileVersion("2.4.0.0")]

static class Setup
{
    [STAThread]
    static int Main(string[] args)
    {
        string dest = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "MacDock");
        bool silent = Array.IndexOf(args, "/S") >= 0;
        if (Array.IndexOf(args, "/uninstall") >= 0)
        {
            if (!silent && MessageBox.Show("Remove MacDock? Your pins and settings in %APPDATA%\\MacDock are kept.", "MacDock Setup", MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
            string ex = Path.Combine(dest, "bin", "MacDock.exe");
            try { if (File.Exists(ex)) Process.Start(ex, "/restore").WaitForExit(5000); } catch { } // brings the Windows taskbar back
            foreach (var p in Process.GetProcessesByName("MacDock")) { try { p.Kill(); } catch { } }
            try { Process.Start(new ProcessStartInfo("schtasks", "/Delete /TN MacDockGuard /F") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) k.DeleteValue("MacDock", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacDock", false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true)) k.DeleteValue(ex, false); } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacDock.lnk")); } catch { }
            Process.Start(new ProcessStartInfo("cmd", "/c timeout /t 2 >nul & rmdir /s /q \"" + dest + "\"") { CreateNoWindow = true, UseShellExecute = false });
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
            string exe = Path.Combine(dest, "bin", "MacDock.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(dest, "Setup.exe"), true);
            // Start Menu shortcut
            Type sh = Type.GetTypeFromProgID("WScript.Shell"); object w = Activator.CreateInstance(sh);
            object lnk = sh.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, w, new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacDock.lnk") });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            // Settings > Apps > Installed apps entry (per-user)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacDock"))
            {
                k.SetValue("DisplayName", "MacDock"); k.SetValue("DisplayVersion", "2.4.0"); k.SetValue("Publisher", "MacDock Project");
                k.SetValue("DisplayIcon", exe); k.SetValue("InstallLocation", dest);
                k.SetValue("UninstallString", "\"" + Path.Combine(dest, "Setup.exe") + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord); k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            // Integrated GPU (power saving) for the dock, as Settings > System > Display > Graphics does.
            try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences")) k.SetValue(exe, "GpuPreference=1;"); } catch { }
            try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) k.SetValue("MacDock", "\"" + exe + "\""); } catch { }
            // MacDockGuard: per-user task that restarts the dock within a minute if it is ever not running.
            try { Process.Start(new ProcessStartInfo("schtasks", "/Create /F /SC MINUTE /MO 1 /TN MacDockGuard /TR \"\\\"" + exe + "\\\"\"") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(15000); } catch { }
            bool started = false;
            try { Process.Start(new ProcessStartInfo("schtasks", "/Run /TN MacDockGuard") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(10000); started = true; } catch { }
            if (!started) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) });
            if (!silent) MessageBox.Show("MacDock is installed and running. It starts at login (toggle in Settings).", "MacDock Setup");
            return 0;
        }
        catch (Exception ex) { MessageBox.Show("Install failed: " + ex.Message, "MacDock Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
    }
}

