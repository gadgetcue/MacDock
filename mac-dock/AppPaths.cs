// Where MacDock keeps the files it reads and writes (pins, icon map, icon pack).
//
// These live in %APPDATA%\MacDock rather than next to the exe: Windows' Controlled Folder Access can block an
// unsigned exe from writing into protected folders, which would make "add to Dock" and icon downloads fail
// silently. Files found next to the exe from earlier versions are copied over once.

using System;
using System.IO;

namespace MacDock
{
    static class AppPaths
    {
        public static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock");
        public static readonly string Config = Path.Combine(DataDir, "dock.txt");
        public static readonly string IconMap = Path.Combine(DataDir, "iconmap.txt");
        public static readonly string IconsDir = Path.Combine(DataDir, "icons");

        static AppPaths()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                CopyIfMissing(Path.Combine(ExeDir, "dock.txt"), Config);
                CopyIfMissing(Path.Combine(ExeDir, "iconmap.txt"), IconMap);

                // Icons that were placed next to the exe.
                string oldIcons = Path.Combine(ExeDir, "icons");
                if (Directory.Exists(oldIcons) && !Directory.Exists(IconsDir))
                {
                    Directory.CreateDirectory(IconsDir);
                    foreach (string file in Directory.GetFiles(oldIcons))
                        CopyIfMissing(file, Path.Combine(IconsDir, Path.GetFileName(file)));
                }
            }
            catch { }
        }

        static void CopyIfMissing(string from, string to)
        {
            try { if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to); }
            catch { }
        }
    }
}
