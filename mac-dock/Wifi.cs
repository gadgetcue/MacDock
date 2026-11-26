// Wi-Fi through the Native Wifi API (wlanapi.dll). netsh output is UTF-8 and localised, so parsing it garbled names such as
// "Alex’s iPhone" and connecting with the garbled name failed. Calls can block: use a worker thread.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacDock
{
    sealed class WifiNetwork
    {
        public string Name;
        public byte[] Ssid;
        public string Profile;          // saved Windows profile, or null
        public int Signal;
        public int Auth;                // DOT11_AUTH_ALGORITHM
        public bool Secured;
        public bool Connected;
        public Guid Adapter;
        public bool Enterprise { get { return Auth == 3 || Auth == 6 || Auth == 8 || Auth == 11; } }
        public string Security { get { return Secured ? "Secured" : "Open"; } }
    }

    static class Wifi
    {
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int version, IntPtr reserved, out int negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid adapter, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid adapter, int flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr handle, ref Guid adapter, IntPtr reserved);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid adapter, int flags, string xml, string security, bool overwrite, IntPtr reserved, out int reason);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanDeleteProfile(IntPtr handle, ref Guid adapter, string name, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr handle, ref Guid adapter, ref ConnectionParameters p, IntPtr reserved);

        [StructLayout(LayoutKind.Sequential)]
        struct ConnectionParameters
        {
            public int Mode;                                    // 0 = saved profile
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid, Bssids;
            public int BssType;                                 // 1 = infrastructure
            public int Flags;
        }

        const int AccessDenied = 5, InterfaceSize = 532, NetworkSize = 628;

        // True when Windows refused the network list because location access is off.
        public static bool LocationBlocked;

        static T Session<T>(Func<IntPtr, T> work, T fallback)
        {
            IntPtr h; int v;
            if (WlanOpenHandle(2, IntPtr.Zero, out v, out h) != 0) return fallback;
            try { return work(h); }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }

        static List<Guid> Adapters(IntPtr h)
        {
            var ids = new List<Guid>();
            IntPtr list;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return ids;
            try
            {
                int n = Marshal.ReadInt32(list);
                for (int i = 0; i < n; i++) ids.Add((Guid)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * InterfaceSize), typeof(Guid)));
            }
            finally { WlanFreeMemory(list); }
            return ids;
        }

        // The connected network first, then by signal. rescan asks the adapter for a fresh scan first (takes about 3 s).
        public static List<WifiNetwork> Scan(bool rescan = false)
        {
            try
            {
                return Session(h =>
                {
                    List<Guid> ids = Adapters(h);
                    if (rescan)
                    {
                        foreach (Guid id in ids) { Guid g = id; WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
                        System.Threading.Thread.Sleep(3000);
                    }
                    var found = new Dictionary<string, WifiNetwork>();
                    foreach (Guid id in ids) Read(h, id, found);
                    return found.Values.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToList();
                }, new List<WifiNetwork>());
            }
            catch (Exception e) { Program.Log(e); return new List<WifiNetwork>(); }
        }

        static void Read(IntPtr h, Guid adapter, Dictionary<string, WifiNetwork> found)
        {
            IntPtr list; Guid g = adapter;
            int err = WlanGetAvailableNetworkList(h, ref g, 0, IntPtr.Zero, out list);
            LocationBlocked = err == AccessDenied;
            if (err != 0) return;
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = IntPtr.Add(list, 8 + i * NetworkSize);
                    int len = Marshal.ReadInt32(p, 512);
                    if (len <= 0 || len > 32) continue;                 // hidden network
                    var ssid = new byte[len];
                    Marshal.Copy(IntPtr.Add(p, 516), ssid, 0, len);
                    string name = Encoding.UTF8.GetString(ssid);
                    if (name.Trim('\0', ' ').Length == 0) continue;
                    string profile = Marshal.PtrToStringUni(p);
                    int flags = Marshal.ReadInt32(p, 620);
                    WifiNetwork n;
                    if (!found.TryGetValue(name, out n)) found[name] = n = new WifiNetwork { Name = name, Ssid = ssid, Adapter = adapter };
                    // The same network is listed twice when a profile exists: keep the profile and the connected flag from either.
                    n.Signal = Math.Max(n.Signal, Marshal.ReadInt32(p, 604));
                    n.Secured = Marshal.ReadInt32(p, 608) != 0;
                    n.Auth = Marshal.ReadInt32(p, 612);
                    if ((flags & 1) != 0) { n.Connected = true; n.Adapter = adapter; }
                    if (!string.IsNullOrEmpty(profile)) n.Profile = profile;
                }
            }
            finally { WlanFreeMemory(list); }
        }

        // Disconnects from the current network. Returns an error text, or null on success.
        public static string Disconnect(WifiNetwork n)
        {
            return Session(h =>
            {
                Guid g = n.Adapter;
                return WlanDisconnect(h, ref g, IntPtr.Zero) == 0 ? null : "Could not disconnect";
            }, "Wi-Fi service unavailable");
        }

        // Connects with the saved profile, or saves one first (password = null for open networks) and removes it again if the
        // network rejects it. Waits for the result. Returns an error text, or null once connected.
        public static string Connect(WifiNetwork n, string password)
        {
            try
            {
                return Session(h =>
                {
                    Guid g = n.Adapter;
                    string profile = n.Profile;
                    bool created = false;
                    if (profile == null || password != null)
                    {
                        profile = profile ?? n.Name;
                        int reason;
                        int err = WlanSetProfile(h, ref g, n.Profile == null ? 2 /* this user */ : 0, ProfileXml(n, profile, password), null, true, IntPtr.Zero, out reason);
                        if (err != 0) return err == AccessDenied ? "Windows denied saving the network" : "Password not accepted (8-63 characters)";
                        created = n.Profile == null;
                    }
                    var p = new ConnectionParameters { Mode = 0, Profile = profile, BssType = 1 };
                    int code = WlanConnect(h, ref g, ref p, IntPtr.Zero);
                    if (code == 0)
                    {
                        for (int i = 0; i < 24; i++)
                        {
                            System.Threading.Thread.Sleep(500);
                            var now = new Dictionary<string, WifiNetwork>();
                            Read(h, n.Adapter, now);
                            WifiNetwork seen;
                            if (now.TryGetValue(n.Name, out seen) && seen.Connected) return null;
                        }
                    }
                    if (created) WlanDeleteProfile(h, ref g, profile, IntPtr.Zero);
                    if (code == AccessDenied) return "Location access is off";
                    return n.Secured ? "Could not connect. Check the password." : "Could not connect";
                }, "Wi-Fi service unavailable");
            }
            catch (Exception e) { Program.Log(e); return e.Message; }
        }

        static string ProfileXml(WifiNetwork n, string profile, string password)
        {
            string sec;
            if (!n.Secured) sec = "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>";
            else
            {
                string auth = n.Auth == 9 ? "WPA3SAE" : n.Auth == 4 ? "WPAPSK" : "WPA2PSK";
                sec = "<authEncryption><authentication>" + auth + "</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
                      "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + System.Security.SecurityElement.Escape(password ?? "") + "</keyMaterial></sharedKey>";
            }
            string hex = BitConverter.ToString(n.Ssid).Replace("-", "");
            return "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" + System.Security.SecurityElement.Escape(profile) +
                   "</name><SSIDConfig><SSID><hex>" + hex + "</hex><name>" + System.Security.SecurityElement.Escape(n.Name) +
                   "</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security>" + sec + "</security></MSM></WLANProfile>";
        }
    }

    // Password prompt for a secured network; stays open with the error until the connection works or the user cancels.
    sealed class WifiPasswordForm : Form
    {
        static WifiPasswordForm open;

        public static void Ask(WifiNetwork n, string error)
        {
            if (open != null && !open.IsDisposed) open.Close();
            open = new WifiPasswordForm(n, error);
            open.Show();
            open.Activate();
        }

        WifiPasswordForm(WifiNetwork n, string error)
        {
            float s = Math.Max(1f, Dpi.Of(Screen.FromPoint(Cursor.Position).WorkingArea));
            Func<int, int> px = v => (int)Math.Round(v * s);
            Font = Typography.SystemFont("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.None;
            Text = "Wi-Fi";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(px(380), px(176));
            try { Icon = DockForm.LogoIcon(); } catch { }

            var title = new Label { Text = "Password for \"" + n.Name + "\"", UseMnemonic = false, AutoEllipsis = true, Bounds = new Rectangle(px(16), px(14), px(348), px(24)) };
            var box = new TextBox { UseSystemPasswordChar = true, MaxLength = 64, Bounds = new Rectangle(px(16), px(42), px(348), px(28)) };
            var show = new CheckBox { Text = "Show password", Bounds = new Rectangle(px(16), px(74), px(200), px(24)) };
            var status = new Label { Text = error ?? "", Bounds = new Rectangle(px(16), px(102), px(348), px(24)), ForeColor = Color.Firebrick };
            var join = new Button { Text = "Join", Enabled = false, Bounds = new Rectangle(px(180), px(132), px(88), px(30)) };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(px(276), px(132), px(88), px(30)) };
            Controls.AddRange(new Control[] { title, box, show, status, join, cancel });
            AcceptButton = join; CancelButton = cancel;

            bool busy = false;
            show.CheckedChanged += (a, e) => box.UseSystemPasswordChar = !show.Checked;
            box.TextChanged += (a, e) => join.Enabled = !busy && box.TextLength >= 8;
            cancel.Click += (a, e) => Close();
            join.Click += (a, e) =>
            {
                string key = box.Text;
                busy = true; join.Enabled = false;
                status.ForeColor = SystemColors.ControlText; status.Text = "Connecting...";
                System.Threading.Tasks.Task.Run(() =>
                {
                    string result = Wifi.Connect(n, key);
                    try
                    {
                        if (IsDisposed) return;
                        BeginInvoke(new Action(() =>
                        {
                            if (result == null) { Close(); return; }
                            busy = false; join.Enabled = box.TextLength >= 8;
                            status.ForeColor = Color.Firebrick; status.Text = result;
                            box.SelectAll(); box.Focus();
                        }));
                    }
                    catch (InvalidOperationException) { }   // closed while connecting
                });
            };
            Shown += (a, e) => box.Focus();
        }
    }
}
