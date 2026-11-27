// Island extras: Pomodoro, water and stand-up reminders, volume HUD, Caps/Num Lock toast, and the Focus tab.
// Settings keys (dock.txt): island-pomo-focus, island-pomo-break, island-water, island-water-goal, island-water-every,
// island-stand, island-stand-every, island-locks, island-volume. Water count: island-water-count "yyyyMMdd:n".

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MacDock
{
    static class IslandCfg
    {
        public static int Int(string key, int def, int min, int max)
        {
            int n;
            if (!int.TryParse(SetCfg.Get(key), out n)) n = def;
            return Math.Max(min, Math.Min(max, n));
        }
        public static int FocusMin { get { return Int("island-pomo-focus", 25, 1, 180); } }
        public static int BreakMin { get { return Int("island-pomo-break", 5, 1, 60); } }
        public static int WaterGoal { get { return Int("island-water-goal", 8, 1, 30); } }
        public static int WaterEvery { get { return Int("island-water-every", 60, 5, 240); } }
        public static int StandEvery { get { return Int("island-stand-every", 45, 5, 240); } }
        public static bool WaterOn { get { return SetCfg.GetBool("island-water", true); } }
        public static bool StandOn { get { return SetCfg.GetBool("island-stand", true); } }
        public static bool LocksOn { get { return SetCfg.GetBool("island-locks", true); } }
        public static bool PrivacyOn { get { return SetCfg.GetBool("island-privacy", true); } }
        public static bool BrightOn { get { return SetCfg.GetBool("island-brightness", true); } }
        public static bool VolumeOn { get { return SetCfg.GetBool("island-volume", true); } }

        public static int WaterCount()
        {
            string v = SetCfg.Get("island-water-count");
            string today = DateTime.Now.ToString("yyyyMMdd");
            if (v == null) return 0;
            string[] p = v.Split(':');
            int n;
            return p.Length == 2 && p[0] == today && int.TryParse(p[1], out n) ? n : 0;
        }
        public static void SetWaterCount(int n)
        {
            SetCfg.Set("island-water-count", DateTime.Now.ToString("yyyyMMdd") + ":" + Math.Max(0, n), false);
        }
    }

    // Default output device's master volume and mute (what the volume keys change).
    static class MasterVol
    {

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnum { [PreserveSig] int EnumAudioEndpoints(int f, int m, out IntPtr d); [PreserveSig] int GetDefaultAudioEndpoint(int f, int r, out IDev d); }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDev { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEndpointVol
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
            [PreserveSig] int GetChannelCount(out uint n);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IEndpointVol ep;
        static DateTime fetched = DateTime.MinValue;

        public static bool Read(out float level, out bool muted)
        {
            level = 0; muted = false;
            try
            {
                if (ep == null || (DateTime.Now - fetched).TotalSeconds > 10)
                {
                    ep = null;
                    var en = (IEnum)CoreAudio.NewEnumerator();
                    IDev dev;
                    if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return false;
                    Guid iid = typeof(IEndpointVol).GUID;
                    object o;
                    if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                    ep = (IEndpointVol)o;
                    fetched = DateTime.Now;
                }
                if (ep.GetMasterVolumeLevelScalar(out level) != 0) { ep = null; return false; }
                ep.GetMute(out muted);
                return true;
            }
            catch { ep = null; return false; }
        }
    }

    sealed partial class IslandForm
    {
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

        // ---------- background poller: mic/camera use (registry) and panel brightness (WMI, slow so off the UI thread) ----------
        volatile bool micOn, camOn;
        volatile int bright = -2;
        bool pollStarted;

        static bool CapInUse(string cap)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + cap))
                {
                    if (k == null) return false;
                    foreach (string n in k.GetSubKeyNames())
                        using (RegistryKey s = k.OpenSubKey(n))
                        {
                            if (s == null) continue;
                            if (KeyBusy(s)) return true;
                            if (n == "NonPackaged")
                                foreach (string m in s.GetSubKeyNames())
                                    using (RegistryKey t = s.OpenSubKey(m))
                                        if (t != null && KeyBusy(t)) return true;
                        }
                }
            }
            catch { }
            return false;
        }

        static bool KeyBusy(RegistryKey k)
        {
            object a = k.GetValue("LastUsedTimeStart"), b = k.GetValue("LastUsedTimeStop");
            return a is long && b is long && (long)a > 0 && (long)b == 0;
        }

        void StartPoller()
        {
            pollStarted = true;
            // Brightness is event driven (no polling, so no hitches while the island animates).
            try
            {
                bright = Brightness.Get();
                var watcher = new System.Management.ManagementEventWatcher(new System.Management.ManagementScope(@"root\WMI"), new System.Management.WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                watcher.EventArrived += delegate (object so, System.Management.EventArrivedEventArgs ea)
                {
                    try { bright = Convert.ToInt32(ea.NewEvent["Brightness"]); } catch { }
                };
                watcher.Start();
            }
            catch { }
            var t = new System.Threading.Thread(delegate ()
            {
                int tick = 0;
                while (!closing)
                {
                    try
                    {
                        if (tick % 4 == 0 && !animating)
                        {
                            if (IslandCfg.PrivacyOn) { micOn = CapInUse("microphone"); camOn = CapInUse("webcam"); }
                            else { micOn = false; camOn = false; }
                        }
                    }
                    catch { }
                    tick++;
                    System.Threading.Thread.Sleep(400);
                }
            });
            t.IsBackground = true;
            t.Priority = System.Threading.ThreadPriority.BelowNormal;
            t.Start();
        }

        bool PrivacyActive { get { return micOn || camOn; } }
        int lastBright = -2;

        // ---------- generic toast ----------
        string toastText, toastSub, toastGlyph;
        Color toastColor = Color.White;
        int toastKind;          // 0 none, 1 volume, 2 key, 3 water, 4 stand, 5 timer
        double toastBar = -1;   // 0..1, or -1 for no bar
        Action toastClick;

        void ShowToast(string text, string sub, Color color, int kind, double bar, double seconds, string glyph, Action click)
        {
            if (!Enabled || mode == Mode.Panel || mode == Mode.Notif) return;
            toastText = text; toastSub = sub; toastColor = color; toastKind = kind; toastBar = bar; toastGlyph = glyph; toastClick = click;
            mode = Mode.Toast;
            until = DateTime.Now.AddSeconds(seconds);
            Wake();
            Redraw(); // show the new value now, not at the next slow repaint
        }

        // ---------- Pomodoro ----------
        enum PomoState { Off, Focus, Break }
        PomoState pomo = PomoState.Off;
        bool pomoPaused;
        DateTime pomoEnd;
        double pomoLeft;   // seconds, while paused
        double pomoTotal;

        bool PomoActive { get { return pomo != PomoState.Off; } }
        double PomoRemain { get { return pomoPaused ? pomoLeft : Math.Max(0, (pomoEnd - DateTime.Now).TotalSeconds); } }
        static string Clock(double secs) { int s = (int)Math.Ceiling(secs); return (s / 60) + ":" + (s % 60).ToString("00"); }

        void PomoBegin(PomoState st)
        {
            pomo = st;
            pomoPaused = false;
            pomoTotal = (st == PomoState.Focus ? IslandCfg.FocusMin : IslandCfg.BreakMin) * 60.0;
            pomoEnd = DateTime.Now.AddSeconds(pomoTotal);
            if (st == PomoState.Focus) nextStand = pomoEnd; // stand-up waits for the break
        }

        void PomoStart()
        {
            PomoBegin(PomoState.Focus);
            ShowToast("Focus", IslandCfg.FocusMin + " min", Color.FromArgb(255, 69, 58), 5, -1, 2.5, null, null);
        }

        void PomoTogglePause()
        {
            if (!PomoActive) return;
            if (pomoPaused) { pomoEnd = DateTime.Now.AddSeconds(pomoLeft); pomoPaused = false; }
            else { pomoLeft = PomoRemain; pomoPaused = true; }
        }

        void PomoSkip()
        {
            if (pomo == PomoState.Focus) { PomoBegin(PomoState.Break); ShowToast("Break", IslandCfg.BreakMin + " min", Color.FromArgb(52, 199, 89), 5, -1, 2.5, null, null); }
            else pomo = PomoState.Off;
        }

        void PomoStop() { pomo = PomoState.Off; pomoPaused = false; }

        // ---------- reminders + system watchers ----------
        DateTime nextWater = DateTime.MinValue, nextStand = DateTime.MinValue, lastExtras = DateTime.MinValue, lastVolPoll = DateTime.MinValue;
        bool capsInit, lastCaps, lastNum, volInit, lastMuted;
        float lastVol;

        void CheckExtras()
        {
            DateTime now = DateTime.Now;
            if (!pollStarted) StartPoller();
            int b = bright;
            if (b >= 0)
            {
                if (lastBright == -2) lastBright = b;
                else if (Math.Abs(b - lastBright) >= 2)
                {
                    lastBright = b;
                    if (IslandCfg.BrightOn) ShowToast("Brightness", b + "%", Color.White, 6, b / 100.0, 1.6, null, null);
                }
            }

            // Volume HUD (polled ~7x/s) and lock keys.
            if ((now - lastVolPoll).TotalMilliseconds >= 50 && !(mode == Mode.Panel && wasBusy))
            {
                lastVolPoll = now;
                bool caps = (GetKeyState(0x14) & 1) != 0, num = (GetKeyState(0x90) & 1) != 0;
                if (!capsInit) { capsInit = true; lastCaps = caps; lastNum = num; }
                else if (IslandCfg.LocksOn)
                {
                    if (caps != lastCaps) ShowToast("Caps Lock", caps ? "On" : "Off", caps ? Color.FromArgb(255, 214, 10) : Color.White, 2, -1, 1.8, "A", null);
                    else if (num != lastNum) ShowToast("Num Lock", num ? "On" : "Off", num ? Color.FromArgb(255, 214, 10) : Color.White, 2, -1, 1.8, "1", null);
                }
                lastCaps = caps; lastNum = num;

                float vol; bool muted;
                if (MasterVol.Read(out vol, out muted))
                {
                    if (!volInit) { volInit = true; lastVol = vol; lastMuted = muted; }
                    else if (Math.Abs(vol - lastVol) > 0.004f || muted != lastMuted)
                    {
                        lastVol = vol; lastMuted = muted;
                        if (IslandCfg.VolumeOn)
                            ShowToast(muted ? "Muted" : "Volume", muted ? "" : Math.Round(vol * 100) + "%", Color.White, 1, muted ? 0 : vol, 1.6, null, null);
                    }
                }
            }

            // Pomodoro, water and stand-up are time based: checked once a second.
            if ((now - lastExtras).TotalMilliseconds < 1000) return;
            lastExtras = now;

            if (PomoActive && !pomoPaused && now >= pomoEnd)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                if (pomo == PomoState.Focus)
                {
                    PomoBegin(PomoState.Break);
                    ShowToast("Focus done", "Break " + IslandCfg.BreakMin + " min", Color.FromArgb(52, 199, 89), 5, -1, 4, null, null);
                }
                else
                {
                    pomo = PomoState.Off;
                    ShowToast("Break over", "Ready to focus?", Color.FromArgb(255, 69, 58), 5, -1, 4, null, PomoStart);
                }
            }

            if (nextWater == DateTime.MinValue) nextWater = now.AddMinutes(IslandCfg.WaterEvery);
            if (nextStand == DateTime.MinValue) nextStand = now.AddMinutes(IslandCfg.StandEvery);

            if (IslandCfg.WaterOn && now >= nextWater)
            {
                nextWater = now.AddMinutes(IslandCfg.WaterEvery);
                int n = IslandCfg.WaterCount(), goal = IslandCfg.WaterGoal;
                if (n < goal && mode != Mode.Panel) ShowToast("Time for water", n + " / " + goal, Color.FromArgb(10, 132, 255), 3, -1, 6, null, AddGlass);
            }
            if (IslandCfg.StandOn && now >= nextStand && !(pomo == PomoState.Focus))
            {
                nextStand = now.AddMinutes(IslandCfg.StandEvery);
                ShowToast("Stand up", "Stretch for a minute", Color.FromArgb(255, 159, 10), 4, -1, 6, null, null);
            }
        }

        void AddGlass()
        {
            int n = IslandCfg.WaterCount() + 1, goal = IslandCfg.WaterGoal;
            IslandCfg.SetWaterCount(n);
            nextWater = DateTime.Now.AddMinutes(IslandCfg.WaterEvery);
            ShowToast(n >= goal ? "Goal reached" : "Glass logged", n + " / " + goal, Color.FromArgb(10, 132, 255), 3, Math.Min(1.0, n / (double)goal), 2.2, null, null);
        }

        // ---------- drawing ----------

        void DrawPrivacy(Graphics g, RectangleF pill)
        {
            float pulse = 0.65f + 0.35f * (float)Math.Sin(Environment.TickCount / 250.0);
            float cx = pill.Left + pill.Width / 2f, cy = pill.Top + pill.Height / 2f, r = 3.2f;
            bool both = micOn && camOn;
            if (micOn)
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * pulse), 255, 159, 10)))
                    g.FillEllipse(br, cx - r - (both ? 4.5f : 0), cy - r, r * 2, r * 2);
            if (camOn)
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * pulse), 52, 199, 89)))
                    g.FillEllipse(br, cx - r + (both ? 4.5f : 0), cy - r, r * 2, r * 2);
        }

        void DrawPomoRing(Graphics g, RectangleF pill)
        {
            double frac = pomoTotal <= 0 ? 0 : Math.Max(0, Math.Min(1, PomoRemain / pomoTotal));
            Color c = pomo == PomoState.Focus ? Color.FromArgb(255, 69, 58) : Color.FromArgb(52, 199, 89);
            var r = new RectangleF(pill.Left + 3, pill.Top + 3, pill.Width - 6, pill.Height - 6);
            using (var track = new Pen(Color.FromArgb(50, 255, 255, 255), 2f))
            using (var pen = new Pen(pomoPaused ? Color.FromArgb(150, 255, 255, 255) : c, 2f))
            {
                g.DrawEllipse(track, r);
                if (frac > 0.002) g.DrawArc(pen, r, -90, (float)(360 * frac));
            }
        }

        void DrawToast(Graphics g, RectangleF pill, Brush white, Brush gray, int alpha)
        {
            var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var fmtR = new StringFormat(fmt) { Alignment = StringAlignment.Far };
            float ix = pill.Left + 18, cy = pill.Top + pill.Height / 2f;
            Color c = A(toastColor, alpha);
            Bitmap appIcon = (toastKind == 1 || toastKind == 6) ? null : AppIconBmp(); // volume/brightness: glyph only, no MacDock icon
            bool onlyIcon = appIcon != null && !(toastKind == 1 || toastKind == 2 || toastKind == 6);
            if (appIcon != null)
            {
                var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                g.DrawImage(appIcon, new Rectangle((int)ix, (int)(cy - 14), 28, 28), 0, 0, appIcon.Width, appIcon.Height, GraphicsUnit.Pixel, ia);
                ix += 36;
            }
            if (!onlyIcon)
            using (var pen = new Pen(c, 1.6f))
            using (var br = new SolidBrush(c))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                switch (toastKind)
                {
                    case 1: // speaker
                        g.FillPolygon(br, new[] { new PointF(ix, cy - 3), new PointF(ix + 5, cy - 3), new PointF(ix + 10, cy - 8), new PointF(ix + 10, cy + 8), new PointF(ix + 5, cy + 3), new PointF(ix, cy + 3) });
                        if (toastBar > 0.01) g.DrawArc(pen, ix + 8, cy - 5, 8, 10, -60, 120);
                        if (toastBar > 0.5) g.DrawArc(pen, ix + 8, cy - 9, 14, 18, -60, 120);
                        break;
                    case 2: // key cap with glyph
                        using (var path = Layered.RoundedRect(new RectangleF(ix, cy - 9, 20, 18), 5))
                            g.DrawPath(pen, path);
                        g.DrawString(toastGlyph ?? "", nameFont, br, new RectangleF(ix, cy - 9, 20, 19), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                        break;
                    case 3: // drop
                        g.FillPolygon(br, new[] { new PointF(ix + 8, cy - 10), new PointF(ix + 15, cy + 1), new PointF(ix + 1, cy + 1) });
                        g.FillEllipse(br, ix + 1, cy - 4, 14, 14);
                        break;
                    case 4: // person stretching
                        g.FillEllipse(br, ix + 6, cy - 10, 7, 7);
                        g.DrawLine(pen, ix + 9.5f, cy - 3, ix + 9.5f, cy + 4);
                        g.DrawLine(pen, ix + 9.5f, cy + 4, ix + 4, cy + 10);
                        g.DrawLine(pen, ix + 9.5f, cy + 4, ix + 15, cy + 10);
                        g.DrawLine(pen, ix + 2, cy - 7, ix + 9.5f, cy - 1);
                        g.DrawLine(pen, ix + 17, cy - 7, ix + 9.5f, cy - 1);
                        break;
                    case 6: // sun
                        g.FillEllipse(br, ix + 5, cy - 5, 10, 10);
                        for (int i = 0; i < 8; i++)
                        {
                            double an = i * Math.PI / 4;
                            g.DrawLine(pen, ix + 10 + (float)Math.Cos(an) * 7, cy + (float)Math.Sin(an) * 7, ix + 10 + (float)Math.Cos(an) * 10, cy + (float)Math.Sin(an) * 10);
                        }
                        break;
                    default: // timer
                        g.DrawEllipse(pen, ix, cy - 9, 18, 18);
                        g.DrawLine(pen, ix + 9, cy, ix + 9, cy - 5);
                        g.DrawLine(pen, ix + 9, cy, ix + 13, cy + 2);
                        break;
                }
            }
            float tx = onlyIcon ? ix + 2 : ix + 32;
            if (toastBar >= 0)
            {
                g.DrawString(toastText ?? "", boldFont, white, new RectangleF(tx, pill.Top, 84, pill.Height), fmt);
                var track = new RectangleF(tx + 84, cy - 3, pill.Right - 22 - (tx + 84), 6);
                using (var tb = new SolidBrush(A(Color.FromArgb(60, 255, 255, 255), alpha)))
                using (var path = Layered.RoundedRect(track, 3))
                    g.FillPath(tb, path);
                if (toastBar > 0.01)
                {
                    var fillR = new RectangleF(track.Left, track.Top, (float)(track.Width * Math.Min(1.0, toastBar)), track.Height);
                    using (var fb = new SolidBrush(A(Color.White, alpha)))
                    using (var path = Layered.RoundedRect(fillR, 3))
                        g.FillPath(fb, path);
                }
            }
            else
            {
                g.DrawString(toastText ?? "", boldFont, white, new RectangleF(tx, pill.Top, pill.Width - (tx - pill.Left) - 20, pill.Height), fmt);
                g.DrawString(toastSub ?? "", bodyFont, gray, new RectangleF(tx, pill.Top, pill.Width - (tx - pill.Left) - 20, pill.Height), fmtR);
            }
        }

        // ---------- Focus tab ----------
        int FocusRowH { get { return (int)(40 * Ds); } }

        void DrawBtn(Graphics g, RectangleF pill, float right, float cy, float w, string text, int alpha, Action click)
        {
            float h = 24 * Ds;
            var r = new Rectangle((int)(right - w), (int)(cy - h / 2), (int)w, (int)h);
            hits.Add(new Hit { Rect = r, Click = click });
            bool hot = r.Contains(PointToClient(Cursor.Position));
            using (var path = Layered.RoundedRect(r, (int)(h / 2)))
            using (var fb = new SolidBrush(A(Color.FromArgb(hot ? 90 : 56, 255, 255, 255), alpha)))
                g.FillPath(fb, path);
            using (var wb = new SolidBrush(A(Color.White, alpha)))
                g.DrawString(text, nameFont, wb, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }

        void DrawFocus(Graphics g, RectangleF pill, float y, Brush white, Brush gray, int alpha)
        {
            int rh = FocusRowH;
            var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            float rt = pill.Right - 18, tw = 190 * Ds;
            float bw = 54 * Ds, gap = 6;

            // Pomodoro
            float top = pill.Top + y, cy = top + rh / 2f;
            string t1 = !PomoActive ? "Pomodoro" : (pomo == PomoState.Focus ? "Focus  " : "Break  ") + Clock(PomoRemain);
            string t2 = !PomoActive ? IslandCfg.FocusMin + " min focus, " + IslandCfg.BreakMin + " min break" : (pomoPaused ? "Paused" : "Running");
            g.DrawString(t1, boldFont, white, new RectangleF(pill.Left + 22, top, tw, rh / 2f + 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Far });
            g.DrawString(t2, nameFont, gray, new RectangleF(pill.Left + 22, top + rh / 2f + 4, tw, rh / 2f - 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Near });
            if (!PomoActive) DrawBtn(g, pill, rt, cy, bw, "Start", alpha, PomoStart);
            else
            {
                DrawBtn(g, pill, rt, cy, bw, "Stop", alpha, PomoStop);
                DrawBtn(g, pill, rt - bw - gap, cy, bw, "Skip", alpha, PomoSkip);
                DrawBtn(g, pill, rt - 2 * (bw + gap), cy, bw, pomoPaused ? "Resume" : "Pause", alpha, PomoTogglePause);
            }

            // Water
            top += rh; cy = top + rh / 2f;
            int n = IslandCfg.WaterCount(), goal = IslandCfg.WaterGoal;
            g.DrawString("Water  " + n + " / " + goal, boldFont, white, new RectangleF(pill.Left + 22, top, tw, rh / 2f + 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Far });
            g.DrawString(IslandCfg.WaterOn ? "Reminds every " + IslandCfg.WaterEvery + " min" : "Reminders off", nameFont, gray, new RectangleF(pill.Left + 22, top + rh / 2f + 4, tw, rh / 2f - 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Near });
            DrawBtn(g, pill, rt, cy, bw, "+ Glass", alpha, delegate { IslandCfg.SetWaterCount(IslandCfg.WaterCount() + 1); nextWater = DateTime.Now.AddMinutes(IslandCfg.WaterEvery); });
            DrawBtn(g, pill, rt - bw - gap, cy, 30 * Ds, "-", alpha, delegate { IslandCfg.SetWaterCount(IslandCfg.WaterCount() - 1); });

            // Stand-up
            top += rh; cy = top + rh / 2f;
            double mins = Math.Max(0, (nextStand - DateTime.Now).TotalMinutes);
            g.DrawString("Stand up", boldFont, white, new RectangleF(pill.Left + 22, top, tw, rh / 2f + 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Far });
            string s2 = !IslandCfg.StandOn ? "Reminders off" : (pomo == PomoState.Focus ? "After this focus session" : "Next in " + (int)Math.Ceiling(mins) + " min");
            g.DrawString(s2, nameFont, gray, new RectangleF(pill.Left + 22, top + rh / 2f + 4, tw, rh / 2f - 4), new StringFormat(fmt) { LineAlignment = StringAlignment.Near });
            DrawBtn(g, pill, rt, cy, bw, "Done", alpha, delegate { nextStand = DateTime.Now.AddMinutes(IslandCfg.StandEvery); });
        }
    }
}
