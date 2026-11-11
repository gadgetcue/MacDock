// Chromium web apps ("Install as app" in Brave, Edge or Chrome). Each one is its own app in Windows: it has an AppUserModelID
// like "Brave._crx_<id>" (its Start-menu / AppsFolder entry) and its windows carry that same id, so a running web app can be
// told apart from the browser and from other web apps even though they all run in brave.exe or msedge.exe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MacDock
{
    static class Pwa
    {
        internal const string AppsFolder = @"shell:AppsFolder\";

        // True for the AppsFolder id of a Chromium web app.
        public static bool IsWebAppId(string id)
        {
            if (id == null) return false;
            if (id.IndexOf("._crx_", StringComparison.OrdinalIgnoreCase) > 0) return true;
            // Chromium --app=URL windows and other web-app flavours: "<Browser>._<app name>" (e.g. "Chrome._https_www.youtube.com")
            int dot = id.IndexOf("._", StringComparison.Ordinal);
            if (dot <= 0 || id.IndexOfAny(new[] { '!', '\\', '/' }) >= 0) return false;
            string b = id.Substring(0, dot);
            return b.StartsWith("Brave", StringComparison.OrdinalIgnoreCase) || b.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase)
                || b.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) || b.StartsWith("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)
                || b.StartsWith("Chromium", StringComparison.OrdinalIgnoreCase) || b.StartsWith("Vivaldi", StringComparison.OrdinalIgnoreCase)
                || b.StartsWith("Opera", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

        // The icon the window itself carries (Chromium sets the web app's icon on its window), as a bitmap, or null.
        public static Bitmap WindowIcon(IntPtr hwnd)
        {
            try
            {
                IntPtr h = IntPtr.Zero;
                foreach (IntPtr which in new[] { (IntPtr)1, (IntPtr)2 }) // ICON_BIG, ICON_SMALL
                {
                    SendMessageTimeout(hwnd, 0x7F, which, IntPtr.Zero, 2, 200, out h); // WM_GETICON
                    if (h != IntPtr.Zero) break;
                }
                if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14); // GCL_HICON
                if (h == IntPtr.Zero) return null;
                using (Icon ico = Icon.FromHandle(h)) return new Bitmap(ico.ToBitmap());
            }
            catch { return null; }
        }

        public static string TargetFor(string aumid) { return AppsFolder + aumid; }

        // The id inside "shell:AppsFolder\<id>", or null for any other target.
        public static string IdOfTarget(string target)
        {
            if (target == null || !target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase)) return null;
            return target.Substring(AppsFolder.Length);
        }

        // ---------- the AppUserModelID a window carries ----------

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p1, p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);
        [DllImport("propsys.dll")]
        static extern int PropVariantToStringAlloc(ref PropVariant pv, out IntPtr str);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);

        static readonly Guid IPropertyStoreId = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        static PropertyKey AppUserModelId = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

        public static string AumidOfWindow(IntPtr hwnd)
        {
            try
            {
                Guid iid = IPropertyStoreId;
                IPropertyStore store;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
                try
                {
                    PropVariant pv;
                    if (store.GetValue(ref AppUserModelId, out pv) != 0) return null;
                    try
                    {
                        IntPtr ptr;
                        if (pv.vt == 0 || PropVariantToStringAlloc(ref pv, out ptr) != 0) return null;
                        string s = Marshal.PtrToStringUni(ptr);
                        Marshal.FreeCoTaskMem(ptr);
                        return string.IsNullOrEmpty(s) ? null : s;
                    }
                    finally { PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return null; }
        }

        // ---------- finding and raising a running web app ----------

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);

        // The visible window of the app with this AppUserModelID, or Zero. By default only Brave / Edge / Chrome windows are
        // looked at (the web apps); anyWindow also checks every other titled top-level window (Store apps and so on).
        public static IntPtr FindWindow(string aumid, bool anyWindow = false)
        {
            IntPtr found = IntPtr.Zero;
            var cls = new StringBuilder(64);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                cls.Clear();
                GetClassName(h, cls, cls.Capacity);
                if (!anyWindow && cls.ToString() != "Chrome_WidgetWin_1") return true; // Brave, Edge and Chrome windows
                if (!string.Equals(AumidOfWindow(h), aumid, StringComparison.OrdinalIgnoreCase)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // Starts an AppsFolder target. A web app that is already open is brought forward (restored if minimized) instead
        // of starting it again, which would open a second window.
        public static bool Launch(string target)
        {
            string id = IdOfTarget(target);
            if (IsWebAppId(id))
            {
                IntPtr w = FindWindow(id);
                if (w != IntPtr.Zero) { WinUtil.Focus(w); return true; }
            }
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
            catch { return false; }
        }

        // ---------- opening what a notification points at ----------

        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        class ApplicationActivationManager { }

        // Starts (or activates) the app with this id and hands it the same argument string Windows would pass when its toast is clicked.
        public static bool ActivateWithArguments(string aumid, string arguments)
        {
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                uint pid;
                int hr = manager.ActivateApplication(aumid, arguments, 0, out pid);
                return hr >= 0;
            }
            catch { return false; }
        }

        static readonly string[] BlockedSchemes = { "file", "javascript", "vbscript", "data", "ms-msdt", "search-ms", "search", "ms-officecmd", "ms-appinstaller", "ms-cxh", "ms-cxh-full", "shell", "cmd" };

        // A link a notification may open: http(s), or a protocol some installed app has registered. Never file or command-style schemes.
        public static bool IsSafeLink(string link, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(link) || link.Length > 2048 || !Uri.TryCreate(link, UriKind.Absolute, out uri)) return false;
            string scheme = uri.Scheme.ToLowerInvariant();
            if (Array.IndexOf(BlockedSchemes, scheme) >= 0 || uri.IsUnc) return false;
            if (scheme == "http" || scheme == "https") return true;
            try
            {
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme))
                    return key != null && key.GetValue("URL Protocol") != null;
            }
            catch { return false; }
        }

        // ---------- the web app's own icon ----------

        // The icon of a web app's Start-menu shortcut (it points at the .ico Chromium wrote for the app), looked up by name.
        public static Bitmap IconFor(string name)
        {
            try
            {
                string[] roots =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
                };
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                string wanted = Normalize(name);
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string lnk in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        if (Normalize(Path.GetFileNameWithoutExtension(lnk)) != wanted) continue;
                        object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                        string args = (string)link.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, link, null);
                        string icon = (string)link.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, link, null);
                        if (args == null || args.IndexOf("--app-id", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrEmpty(icon)) continue;
                        string file = icon;
                        int comma = file.LastIndexOf(',');
                        if (comma > 0) file = file.Substring(0, comma);
                        if (!file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) continue;
                        using (var ico = new Icon(file, new Size(256, 256))) return ico.ToBitmap();
                    }
                }
            }
            catch { }
            return null;
        }

        static string Normalize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
