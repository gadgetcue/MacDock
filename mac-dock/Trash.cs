using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MacDock
{
    // Windows Recycle Bin helpers: state query, empty, open, and its (empty/full) shell icon.
    static class RecycleBin
    {
        public const string Target = "[trash]";

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHSTOCKICONINFO
        {
            public int cbSize; public IntPtr hIcon; public int iSysImageIndex; public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);
        [DllImport("shell32.dll")] static extern int SHGetStockIconInfo(int id, uint flags, ref SHSTOCKICONINFO info);

        public static bool IsFull()
        {
            try
            {
                var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                return SHQueryRecycleBinW(null, ref i) == 0 && i.i64NumItems > 0;
            }
            catch { return false; }
        }

        public static bool IsTarget(string t)
        {
            return t != null && (t.Equals(Target, StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase)
                || t.IndexOf("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static void Open()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); } catch { }
        }

        // Sends files/folders to the Recycle Bin (undoable), showing only error dialogs.
        public static void Delete(string[] paths)
        {
            foreach (string p in paths)
            {
                try
                {
                    if (System.IO.Directory.Exists(p))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                    else if (System.IO.File.Exists(p))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                }
                catch { }
            }
        }

        public static void Empty()
        {
            // 0 = show the standard confirmation, progress UI and sound.
            try { SHEmptyRecycleBinW(IntPtr.Zero, null, 0); } catch { }
        }

        // SIID_RECYCLER = 31, SIID_RECYCLERFULL = 32; SHGSI_SYSICONINDEX = 0x4000.
        public static int IconIndex(bool full)
        {
            var s = new SHSTOCKICONINFO { cbSize = Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            return SHGetStockIconInfo(full ? 32 : 31, 0x4000, ref s) == 0 ? s.iSysImageIndex : -1;
        }
    }
}
