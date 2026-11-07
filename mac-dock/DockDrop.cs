// Drag-and-drop helpers for the dock: open files with an app, move/copy/link files into a folder (Explorer's rules).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    static class DockDrop
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

        const int KeyShift = 4, KeyCtrl = 8, KeyAlt = 32;

        // The exe (and its fixed arguments) behind a dock app, or null when files can't be handed to it.
        public static bool ResolveApp(string target, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrEmpty(target)) return false;
            string t = Environment.ExpandEnvironmentVariables(target.Trim('"'));
            if (!File.Exists(t)) return false;
            string ext = Path.GetExtension(t).ToLowerInvariant();
            if (ext == ".lnk")
            {
                try
                {
                    dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    dynamic lnk = sh.CreateShortcut(t);
                    string tp = lnk.TargetPath; string a = lnk.Arguments;
                    if (string.IsNullOrEmpty(tp) || !File.Exists(tp)) return false;
                    exe = tp; args = string.IsNullOrEmpty(a) ? "" : a + " ";
                    return true;
                }
                catch { return false; }
            }
            if (ext == ".exe" || ext == ".bat" || ext == ".cmd") { exe = t; return true; }
            return false;
        }

        public static bool CanOpenWith(string target) { string e, a; return ResolveApp(target, out e, out a); }

        public static bool OpenWith(string target, string[] files)
        {
            string exe, args;
            if (!ResolveApp(target, out exe, out args)) return false;
            try
            {
                string list = string.Join(" ", files.Select(f => "\"" + f + "\""));
                Process.Start(new ProcessStartInfo(exe, args + list) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(files[0]) ?? "" });
                return true;
            }
            catch { return false; }
        }

        static string Root(string p) { try { return Path.GetPathRoot(Path.GetFullPath(p)).ToUpperInvariant(); } catch { return ""; } }

        // Explorer's rule: Shift = move, Ctrl = copy, Ctrl+Shift or Alt = shortcut, otherwise move on the same drive and copy across drives.
        public static DragDropEffects Choose(string[] files, string folder, int keyState, DragDropEffects allowed)
        {
            if (files == null || files.Length == 0 || !Directory.Exists(folder)) return DragDropEffects.None;
            string f = folder.TrimEnd((char)92);
            foreach (string s in files) // not into itself, a child of itself, or the folder it already lives in
            {
                string sp = s.TrimEnd((char)92);
                if (string.Equals(sp, f, StringComparison.OrdinalIgnoreCase)) return DragDropEffects.None;
                if (f.StartsWith(sp + "\\", StringComparison.OrdinalIgnoreCase)) return DragDropEffects.None;
            }
            bool shift = (keyState & KeyShift) != 0, ctrl = (keyState & KeyCtrl) != 0, alt = (keyState & KeyAlt) != 0;
            DragDropEffects want;
            if ((shift && ctrl) || alt) want = DragDropEffects.Link;
            else if (shift) want = DragDropEffects.Move;
            else if (ctrl) want = DragDropEffects.Copy;
            else want = Root(files[0]) == Root(folder) ? DragDropEffects.Move : DragDropEffects.Copy;
            if ((allowed & want) != 0) return want;
            foreach (DragDropEffects e in new[] { DragDropEffects.Copy, DragDropEffects.Move, DragDropEffects.Link })
                if ((allowed & e) != 0) return e;
            return DragDropEffects.None;
        }

        // Runs on its own STA thread so shell progress / conflict dialogs never freeze the dock.
        public static void Transfer(string[] files, string folder, DragDropEffects effect, Action done)
        {
            var t = new Thread(() =>
            {
                try
                {
                    if (effect == DragDropEffects.Link) MakeLinks(files, folder);
                    else
                    {
                        var op = new SHFILEOPSTRUCT
                        {
                            wFunc = effect == DragDropEffects.Move ? 1u : 2u, // FO_MOVE / FO_COPY
                            pFrom = string.Join("\0", files) + "\0\0",
                            pTo = folder + "\0\0",
                            fFlags = 0x40 | 0x200 // FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR
                        };
                        SHFileOperationW(ref op);
                    }
                }
                catch { }
                if (done != null) done();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        static void MakeLinks(string[] files, string folder)
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach (string f in files)
            {
                string name = Path.GetFileName(f.TrimEnd((char)92)) + " - Shortcut", lnk = Path.Combine(folder, name + ".lnk");
                for (int i = 2; File.Exists(lnk); i++) lnk = Path.Combine(folder, name + " (" + i + ").lnk");
                dynamic s = sh.CreateShortcut(lnk);
                s.TargetPath = f; s.Save();
            }
        }
    }
}
