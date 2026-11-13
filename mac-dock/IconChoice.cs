// Per-app icon choices: custom icon file, original app icon, or MacDock's pack icon; plus the global "use original icons".
// Order: custom icon, then per-app choice, then the global setting. Stored in %APPDATA%\MacDock\iconchoice.txt as key|mode|path.
// An app is keyed by its target (exe path / shell:AppsFolder id) and by its name ("n:<name>"), so the dock and the Launchpad agree.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MacDock
{
    static class IconChoice
    {
        static readonly string FilePath = Path.Combine(AppPaths.DataDir, "iconchoice.txt");
        static Dictionary<string, string[]> map; // key -> [mode, path]  (mode: orig | pack | custom)
        static readonly object gate = new object();
        public static int Version;

        static Dictionary<string, string[]> Load()
        {
            lock (gate)
            {
                if (map != null) return map;
                map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string l in File.ReadAllLines(FilePath))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 2 && p[0].Length > 0) map[p[0]] = new[] { p[1], p.Length > 2 ? p[2] : "" };
                    }
                }
                catch { }
                return map;
            }
        }

        static void Save()
        {
            try { lock (gate) File.WriteAllLines(FilePath, Load().Select(kv => kv.Key + "|" + kv.Value[0] + "|" + kv.Value[1]).ToArray()); } catch { }
        }

        public static bool GlobalOriginal { get { return SetCfg.GetBool("original-icons", false); } }

        static string[] Find(string target, string label)
        {
            var m = Load();
            string[] v;
            lock (gate)
            {
                if (!string.IsNullOrEmpty(target) && m.TryGetValue(target, out v)) return v;
                if (!string.IsNullOrEmpty(label) && m.TryGetValue("n:" + label.Trim(), out v)) return v;
            }
            return null;
        }

        // "custom", "orig", "pack" or null (default = follow the global setting).
        public static string Mode(string target, string label) { var v = Find(target, label); return v == null ? null : v[0]; }

        public static string CustomPath(string target, string label)
        {
            var v = Find(target, label);
            return v != null && v[0] == "custom" && File.Exists(v[1]) ? v[1] : null;
        }

        // True when the app should show its own icon instead of the MacDock pack icon.
        public static bool UseOriginal(string target, string label)
        {
            var v = Find(target, label);
            if (v != null) { if (v[0] == "custom") return false; if (v[0] == "orig") return true; if (v[0] == "pack") return false; }
            return GlobalOriginal;
        }

        // The custom icon (not tiled), or null.
        public static Bitmap Custom(string target, string label)
        {
            string p = CustomPath(target, label);
            if (p == null) return null;
            try
            {
                if (p.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    using (var ic = new Icon(p, 256, 256)) return new Bitmap(ic.ToBitmap());
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs, false, false)) return new Bitmap(img);
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        static void Put(string target, string label, string mode, string path)
        {
            var m = Load();
            lock (gate)
            {
                foreach (string k in new[] { target, string.IsNullOrWhiteSpace(label) ? null : "n:" + label.Trim() })
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    if (mode == null) m.Remove(k); else m[k] = new[] { mode, path ?? "" };
                }
            }
            Save();
            Changed();
        }

        // Drops every cached icon and redraws the dock and the Launchpad.
        public static void Changed()
        {
            Version++;
            try
            {
                DockForm d = DockForm.Instance;
                Action a = () => { try { IconPack.Reset(); MissionControl.IconsChanged(); d.RefreshIcons(); } catch (Exception ex) { Program.Log(ex); } };
                if (d != null && d.IsHandleCreated) d.BeginInvoke(a);
            }
            catch { }
        }

        public static void AddMenu(List<MenuEntry> list, string target, string label, Control host)
        {
            string mode = Mode(target, label);
            string tgt = target, lbl = label;
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Item("Icon", null, null));
            list.Add(new MenuEntry { Text = "    Default", Action = () => Put(tgt, lbl, null, null), Checked = mode == null });
            list.Add(new MenuEntry { Text = "    MacDock Icon", Action = () => Put(tgt, lbl, "pack", null), Checked = mode == "pack" });
            list.Add(new MenuEntry { Text = "    Original Icon", Action = () => Put(tgt, lbl, "orig", null), Checked = mode == "orig" });
            list.Add(new MenuEntry { Text = "    Choose Custom Icon...", Action = () => host.BeginInvoke((Action)(() => Pick(tgt, lbl))), Checked = mode == "custom" });
            if (mode != null) list.Add(MenuEntry.Item("    Reset Icon", null, () => Put(tgt, lbl, null, null)));
        }

        static void Pick(string target, string label)
        {
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1), FormBorderStyle = FormBorderStyle.None })
            using (var dlg = new OpenFileDialog { Title = "Choose icon", Filter = "Icons and images|*.ico;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" })
            {
                owner.Show();
                if (dlg.ShowDialog(owner) == DialogResult.OK) Put(target, label, "custom", dlg.FileName);
            }
        }
    }
}
