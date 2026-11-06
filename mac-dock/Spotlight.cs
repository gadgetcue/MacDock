// Spotlight-style search overlay (Win+Space). Searches apps from the Start Menu and files from the
// Windows Search index. Arrow keys move the selection, Enter opens, Escape closes.

using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    // Win+Space is reserved by Windows for switching input language, so RegisterHotKey can't have it.
    // A low-level keyboard hook sees the key first and swallows it.
    static class HotkeyHook
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public const int ModWin = 1, ModCtrl = 2, ModAlt = 4, ModShift = 8;

        public const string DefaultSpotlight = "Win+Space";
        public const string DefaultMission = "Win+Tab";
        public const string DefaultLaunchpad = "Win+Shift+A";

        static Win32.HookProc proc; // kept in a field so the delegate isn't garbage collected
        static IntPtr hook;
        static Action onWinSpace, onMission, onLaunchpad;
        static int spotMods, spotVk, missionMods, missionVk, launchMods, launchVk;
        static DateTime last = DateTime.MinValue;
        static bool spaceSwallowed;

        // While set, the next non-modifier key press (with its modifiers) goes here instead of triggering anything.
        // The settings window uses it to record a shortcut, including Win combos that Windows would otherwise eat.
        public static Action<int, int> Capture;

        // Win+Space opens search. Launchpad uses the shortcut from dock.txt (Win+Tab by default,
        // replacing Windows' Task View).
        public static void Install(Action winSpace, Action mission)
        {
            onWinSpace = winSpace;
            onMission = mission;
            onLaunchpad = () => MissionControl.Toggle(true);
            Reload();
            proc = Callback;
            hook = Win32.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Win32.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Program.Log("Keyboard hook failed, error " + Marshal.GetLastWin32Error());
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        // Re-reads "@hotkey-mission" and "@hotkey-launchpad" from dock.txt; takes effect immediately.
        public static void Reload()
        {
            if (!TryParse(SetCfg.Get("hotkey-spotlight") ?? DefaultSpotlight, out spotMods, out spotVk)) TryParse(DefaultSpotlight, out spotMods, out spotVk);
            if (!TryParse(SetCfg.Get("hotkey-mission") ?? DefaultMission, out missionMods, out missionVk)) TryParse(DefaultMission, out missionMods, out missionVk);
            if (!TryParse(SetCfg.Get("hotkey-launchpad") ?? DefaultLaunchpad, out launchMods, out launchVk)) TryParse(DefaultLaunchpad, out launchMods, out launchVk);
        }

        public static bool TryParse(string text, out int mods, out int vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string raw in text.Split('+'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string l = t.ToLowerInvariant();
                if (l == "win" || l == "windows") mods |= ModWin;
                else if (l == "ctrl" || l == "control") mods |= ModCtrl;
                else if (l == "alt") mods |= ModAlt;
                else if (l == "shift") mods |= ModShift;
                else
                {
                    if (vk != 0) return false;
                    if (t.Length == 1 && t[0] >= '0' && t[0] <= '9') t = "D" + t;
                    try { vk = (int)(Keys)Enum.Parse(typeof(Keys), t, true) & 0xFF; } catch { return false; }
                }
            }
            // A bare letter would hijack typing; only function keys may go without a modifier.
            return vk != 0 && (mods != 0 || (vk >= 0x70 && vk <= 0x87));
        }

        public static string Format(int mods, int vk)
        {
            var parts = new List<string>();
            if ((mods & ModWin) != 0) parts.Add("Win");
            if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
            if ((mods & ModAlt) != 0) parts.Add("Alt");
            if ((mods & ModShift) != 0) parts.Add("Shift");
            string key = ((Keys)vk).ToString();
            if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key.Substring(1);
            parts.Add(key);
            return string.Join("+", parts.ToArray());
        }

        static bool Down(int vk) { return (Win32.GetAsyncKeyState(vk) & 0x8000) != 0; }

        static int CurrentMods()
        {
            int m = 0;
            if (Down(VK_LWIN) || Down(VK_RWIN)) m |= ModWin;
            if (Down(0x11)) m |= ModCtrl;
            if (Down(0x12)) m |= ModAlt;
            if (Down(0x10)) m |= ModShift;
            return m;
        }

        static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // Win+Space must never reach Windows' input-language switcher: swallow Space (down, repeat and up)
                // whenever a Win key is held, with any other modifiers, unless a shortcut is being recorded.
                if (code >= 0 && Capture == null && Marshal.ReadInt32(lParam) == VK_SPACE)
                {
                    bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                    bool isUp = wParam == (IntPtr)0x101 || wParam == (IntPtr)0x105;
                    if (isDown && (Down(VK_LWIN) || Down(VK_RWIN)))
                    {
                        if (!spaceSwallowed)
                        {
                            spaceSwallowed = true;
                            // Stops the Win key release from opening Start.
                            NativeMethods.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            NativeMethods.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                            if ((CurrentMods() == spotMods && spotVk == VK_SPACE) && (DateTime.Now - last).TotalMilliseconds > 250)
                            {
                                last = DateTime.Now;
                                onWinSpace();
                            }
                        }
                        return (IntPtr)1;
                    }
                    if (isUp && spaceSwallowed) { spaceSwallowed = false; return (IntPtr)1; }
                }
                if (code >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    int vk = Marshal.ReadInt32(lParam);
                    // Our own 0xE8 "unassigned key" (sent below to stop the Win key opening Start) can come back through the
                    // hook; it must never be recorded as a shortcut.
                    if (vk == 0xE8) return Win32.CallNextHookEx(hook, code, wParam, lParam);
                    bool isModifier = vk == VK_LWIN || vk == VK_RWIN || (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5);
                    int mods = isModifier ? 0 : CurrentMods();

                    if (!isModifier && Capture != null)
                    {
                        Action<int, int> cap = Capture;
                        if ((mods & ModWin) != 0)
                        {
                            NativeMethods.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            NativeMethods.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        }
                        cap(mods, vk);
                        return (IntPtr)1;
                    }

                    Action action = null;
                    if (!isModifier)
                    {
                        if (mods == spotMods && vk == spotVk) action = onWinSpace;
                        else if (mods == missionMods && vk == missionVk) action = onMission;
                        else if (mods == launchMods && vk == launchVk) action = onLaunchpad;
                    }
                    if (action != null)
                    {
                        // An unassigned key press stops Windows from treating the Win key release as "open Start".
                        if ((mods & ModWin) != 0)
                        {
                            NativeMethods.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            NativeMethods.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        }
                        if ((DateTime.Now - last).TotalMilliseconds > 250)
                        {
                            last = DateTime.Now;
                            action();
                        }
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            return Win32.CallNextHookEx(hook, code, wParam, lParam);
        }
    }

    // Spotlight shares the Launchpad's UI: Win+Space opens it with search ready, and typing filters apps and files into the grid.
    sealed class Spotlight
    {
        public sealed class Result
        {
            public string Name;
            public string Path;
            public string Kind;
        }

        public void Toggle() { MissionControl.Toggle(true); }
        public void Dispose() { }

        // 0 = starts with the query, 1 = a word starts with it, 2 = contains it, 3 = no match.
        static int Rank(string name, string query)
        {
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.IndexOf(" " + query, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 3;
        }

        // Files and folders from the Windows Search index, limited to the user's profile.
        public static List<Result> SearchFiles(string query)
        {
            var found = new List<Result>();
            try
            {
                string safe = new string(query.Where(c => c != '%' && c != '_' && c != '[' && c != ']').ToArray()).Replace("'", "''");
                string profile = Environment.GetEnvironmentVariable("USERPROFILE").Replace("'", "''");
                string sql = "SELECT TOP 40 System.ItemNameDisplay, System.ItemPathDisplay FROM SystemIndex " +
                             "WHERE SCOPE='file:" + profile + "' AND System.ItemNameDisplay LIKE '%" + safe + "%' " +
                             "AND System.ItemPathDisplay NOT LIKE '%\\AppData\\%' ORDER BY System.DateModified DESC";

                using (var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                {
                    connection.Open();
                    using (var command = new OleDbCommand(sql, connection))
                    using (OleDbDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string name = reader[0] as string, path = reader[1] as string;
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;
                            found.Add(new Result { Name = name, Path = path, Kind = Directory.Exists(path) ? "Folder" : "File" });
                        }
                    }
                }
            }
            catch { }

            return found.OrderBy(r => Rank(r.Name, query)).ToList();
        }
    }
}