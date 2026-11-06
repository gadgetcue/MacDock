// tahoe-theme.exe apply | revert : per-user Tahoe wallpaper + cursor scheme. No system files touched (HKCU + SystemParametersInfo only).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

static class TahoeTheme
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint a, uint b, string c, uint d);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
    const string Src = @"D:\#AI workflows\Claude work\Tahoe-download\ready";
    static readonly string Dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"MacDock\tahoe");
    static readonly string Backup = Path.Combine(Dst, "backup.txt");
    static readonly string[,] Map = {
        {"Arrow","Normal.cur"},{"Help","Help.cur"},{"AppStarting","Working.ani"},{"Wait","Busy.ani"},{"Crosshair","Precision.cur"},
        {"Hand","Link.cur"},{"IBeam","Text.cur"},{"No","Unavailable.cur"},{"NWPen","Handwriting.cur"},{"SizeAll","Move.cur"},
        {"SizeNESW","Diagonal Resize 2.cur"},{"SizeNWSE","Diagonal Resize 1.cur"},{"SizeNS","Vertical Resize.cur"},{"SizeWE","Horizontal Resize.cur"},{"UpArrow","Alternate.cur"}};

    static int Main(string[] a)
    {
        string cmd = a.Length > 0 ? a[0].ToLowerInvariant() : "";
        try { if (cmd == "apply") return Apply(); if (cmd == "revert") return Revert(); }
        catch (Exception e) { Console.WriteLine("Failed: " + e.Message); return 1; }
        Console.WriteLine("usage: tahoe-theme apply | revert"); return 2;
    }

    static int Apply()
    {
        string cur = Path.Combine(Src, @"cursors\macOS_cu_6572\macOS\1. Sierra and newer\2. With Shadow\1. Normal");
        string wp = Path.Combine(Src, @"wallpaper\Mac OS Tahoe.png");
        if (!Directory.Exists(cur) || !File.Exists(wp)) { Console.WriteLine("Reference files not found under " + Src); return 1; }
        Directory.CreateDirectory(Dst);
        var bk = new StringBuilder();
        if (!File.Exists(Backup)) // keep the ORIGINAL settings, even if apply runs twice
        {
            var sb = new StringBuilder(256); SystemParametersInfo(0x73, 256, sb.ToString(), 0);
            IntPtr p = Marshal.AllocHGlobal(1024); SystemParametersInfo(0x73, 512, p, 0); bk.AppendLine("wallpaper=" + Marshal.PtrToStringUni(p)); Marshal.FreeHGlobal(p);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
            {
                bk.AppendLine("scheme=" + (k.GetValue("") ?? ""));
                for (int i = 0; i < Map.GetLength(0); i++) bk.AppendLine("c:" + Map[i, 0] + "=" + (k.GetValue(Map[i, 0]) ?? ""));
            }
            File.WriteAllText(Backup, bk.ToString());
        }
        string cdst = Path.Combine(Dst, "cursors"); Directory.CreateDirectory(cdst);
        string wdst = Path.Combine(Dst, "wallpaper.png"); File.Copy(wp, wdst, true);
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true))
        {
            for (int i = 0; i < Map.GetLength(0); i++)
            {
                string f = Path.Combine(cur, Map[i, 1]); if (!File.Exists(f)) continue;
                string t = Path.Combine(cdst, Map[i, 1]); File.Copy(f, t, true);
                k.SetValue(Map[i, 0], t, RegistryValueKind.ExpandString);
            }
            k.SetValue("", "macOS Tahoe (MacDock)");
        }
        SystemParametersInfo(0x57, 0, IntPtr.Zero, 0);            // reload cursors
        SystemParametersInfo(0x14, 0, wdst, 3);                    // set wallpaper
        Console.WriteLine("Applied Tahoe wallpaper and cursors. Undo: tahoe-theme revert"); return 0;
    }

    static int Revert()
    {
        if (!File.Exists(Backup)) { Console.WriteLine("Nothing to revert."); return 0; }
        string wp = "";
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true))
            foreach (string l in File.ReadAllLines(Backup))
            {
                int e = l.IndexOf('='); if (e < 0) continue;
                string n = l.Substring(0, e), v = l.Substring(e + 1);
                if (n == "wallpaper") wp = v;
                else if (n == "scheme") k.SetValue("", v);
                else if (n.StartsWith("c:")) k.SetValue(n.Substring(2), v, RegistryValueKind.ExpandString);
            }
        SystemParametersInfo(0x57, 0, IntPtr.Zero, 0);
        if (wp.Length > 0) SystemParametersInfo(0x14, 0, wp, 3);
        File.Delete(Backup);
        Console.WriteLine("Restored previous wallpaper and cursors."); return 0;
    }
}
