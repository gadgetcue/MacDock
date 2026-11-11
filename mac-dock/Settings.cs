// MacDock settings window: a dark, macOS "System Settings"-style page (sidebar + grouped rows).
//
// Launcher options are stored as "@key value" lines in dock.txt (the same file the dock reads), so changes made here
// are picked up by the running dock (the taskbar option applies at once) and by the next start (the rest).
// The last sidebar section holds Windows-level settings: links into the Settings app and a few registry toggles.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacDock
{
    // ---------- dock.txt directives ----------

    static class SetCfg
    {
        static readonly object gate = new object();

        static bool IsKey(string line, string key)
        {
            string t = line.Trim();
            if (!t.StartsWith("@")) return false;
            t = t.Substring(1);
            if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Length == key.Length) return true;
            char c = t[key.Length];
            return c == ' ' || c == '=' || c == '\t';
        }

        public static string Get(string key)
        {
            try
            {
                if (!File.Exists(AppPaths.Config)) return null;
                string found = null;
                foreach (string raw in File.ReadAllLines(AppPaths.Config))
                {
                    if (!IsKey(raw, key)) continue;
                    string t = raw.Trim().Substring(1 + key.Length);
                    int eq = t.IndexOf('=');
                    found = (eq >= 0 ? t.Substring(eq + 1) : t).Trim();
                }
                return found;
            }
            catch { return null; }
        }

        public static bool GetBool(string key, bool def)
        {
            string v = Get(key);
            if (v == null) return def;
            v = v.ToLowerInvariant();
            return !(v == "off" || v == "false" || v == "no" || v == "0");
        }

        public static void SetBool(string key, bool on) { Set(key, on ? "on" : "off", false); }

        // value == null removes the line. useEquals writes "@key = value", otherwise "@key value".
        public static void Set(string key, string value, bool useEquals)
        {
            lock (gate)
            {
                try
                {
                    List<string> lines = File.Exists(AppPaths.Config) ? File.ReadAllLines(AppPaths.Config).ToList() : new List<string>();
                    string newLine = value == null ? null : (useEquals ? "@" + key + " = " + value : "@" + key + " " + value);

                    int first = -1;
                    for (int i = lines.Count - 1; i >= 0; i--)
                    {
                        if (!IsKey(lines[i], key)) continue;
                        lines.RemoveAt(i);
                        first = i;
                    }
                    if (newLine != null)
                    {
                        if (first < 0)
                        {
                            // Before the first pin, after the leading comments and options.
                            first = lines.Count;
                            for (int i = 0; i < lines.Count; i++)
                            {
                                string t = lines[i].Trim();
                                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith("@")) continue;
                                first = i;
                                break;
                            }
                        }
                        lines.Insert(first, newLine);
                    }
                    File.WriteAllLines(AppPaths.Config, lines, new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not save settings:\n" + ex.Message, "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    // ---------- colours and small helpers ----------

    // Label that paints its own text with ClearTypeGridFit on an opaque fill (sharp, hinted Inter).
    class CrispLabel : Label
    {
        public CrispLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            SetText.DrawText(g, Text, Font, ClientRectangle, ForeColor, BackColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    static class SetUi
    {
        // Dark theme: pure black is the acrylic window's see-through colour, so anything painted black shows the blurred
        // backdrop. Light theme paints opaque light surfaces instead. Apply() swaps the whole palette.
        public static Color Side, Content, Card, CardLine, Text, Dim;
        public static Color Track, Chevron, RowLine, SepLine, SearchFill, SearchLine, PillFill, PillLine, NavDim, HoverFill, EdgeLine;
        public static readonly Color Accent = Color.FromArgb(10, 132, 255);
        public static bool Dark = true;

        static SetUi() { Apply(true); }

        public static void Apply(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Side = Color.FromArgb(45, 45, 47); Content = Color.FromArgb(37, 37, 39);
                Card = Color.FromArgb(46, 46, 49); CardLine = Color.FromArgb(52, 52, 56);
                Text = Color.FromArgb(236, 236, 240); Dim = Color.FromArgb(150, 150, 160);
                Track = Color.FromArgb(92, 92, 98); Chevron = Color.FromArgb(125, 125, 135); RowLine = Color.FromArgb(62, 62, 66);
                SepLine = Color.FromArgb(60, 255, 255, 255);
                SearchFill = Color.FromArgb(70, 70, 73); SearchLine = Color.FromArgb(78, 78, 82);
                PillFill = Color.FromArgb(48, 48, 51); PillLine = Color.FromArgb(78, 78, 82);
                NavDim = Color.FromArgb(100, 100, 108); HoverFill = Color.FromArgb(22, 255, 255, 255); EdgeLine = Color.FromArgb(18, 18, 20);
            }
            else
            {
                Side = Color.FromArgb(232, 232, 238); Content = Color.FromArgb(246, 246, 249);
                Card = Color.FromArgb(255, 255, 255); CardLine = Color.FromArgb(218, 218, 224);
                Text = Color.FromArgb(28, 28, 32); Dim = Color.FromArgb(112, 112, 122);
                Track = Color.FromArgb(200, 200, 206); Chevron = Color.FromArgb(160, 160, 170); RowLine = Color.FromArgb(228, 228, 233);
                SepLine = Color.FromArgb(40, 0, 0, 0);
                SearchFill = Color.FromArgb(247, 247, 250); SearchLine = Color.FromArgb(204, 204, 212);
                PillFill = Color.FromArgb(255, 255, 255); PillLine = Color.FromArgb(214, 214, 220);
                NavDim = Color.FromArgb(180, 180, 188); HoverFill = Color.FromArgb(18, 0, 0, 0); EdgeLine = Color.FromArgb(206, 206, 212);
            }
        }

        // "dark", "light" or "system" (follow the Windows app theme), from "@theme" in dock.txt.
        public static string ThemeSetting()
        {
            string v = (SetCfg.Get("theme") ?? "dark").Trim().ToLowerInvariant();
            return v == "light" || v == "system" ? v : "dark";
        }

        public static bool WantDark()
        {
            string t = ThemeSetting();
            return t == "system" ? RegInt(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0 : t == "dark";
        }

        // Light-theme variant of a sidebar icon tile: dull greys become a clearer mid grey so white glyphs stay readable.
        public static Color Tile(Color c)
        {
            if (Dark) return c;
            int hi = Math.Max(c.R, Math.Max(c.G, c.B)), lo = Math.Min(c.R, Math.Min(c.G, c.B));
            return hi - lo < 24 ? Color.FromArgb(132, 132, 142) : c;
        }

        // ---- avatars: gradient circle plus a simple symbol ("" draws the user's initial) ----
        public static readonly Color[][] AvatarColors =
        {
            new[] { Color.FromArgb(120, 124, 150), Color.FromArgb(120, 124, 150) },
            new[] { Color.FromArgb(255, 149, 0), Color.FromArgb(255, 94, 58) },
            new[] { Color.FromArgb(52, 199, 89), Color.FromArgb(0, 150, 136) },
            new[] { Color.FromArgb(10, 132, 255), Color.FromArgb(94, 92, 230) },
            new[] { Color.FromArgb(255, 55, 95), Color.FromArgb(191, 90, 242) },
            new[] { Color.FromArgb(255, 204, 0), Color.FromArgb(255, 149, 0) },
            new[] { Color.FromArgb(90, 200, 250), Color.FromArgb(10, 132, 255) },
            new[] { Color.FromArgb(175, 82, 222), Color.FromArgb(88, 86, 214) },
            new[] { Color.FromArgb(142, 142, 147), Color.FromArgb(72, 72, 78) },
            new[] { Color.FromArgb(255, 105, 97), Color.FromArgb(255, 55, 95) },
            new[] { Color.FromArgb(48, 209, 88), Color.FromArgb(163, 214, 40) },
            new[] { Color.FromArgb(100, 210, 255), Color.FromArgb(48, 176, 199) }
        };
        public static readonly string[] AvatarGlyphs = { "", "★", "♥", "☼", "☾", "♪", "⚡", "✿", "☕", "♞", "⚓", "☺" };

        public static int AvatarIndex()
        {
            int n;
            return int.TryParse(SetCfg.Get("avatar"), out n) && n >= 0 && n < AvatarGlyphs.Length ? n : 0;
        }

        public static void DrawAvatar(Graphics g, Rectangle r, int index, string initial)
        {
            if (index < 0 || index >= AvatarGlyphs.Length) index = 0;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color a = AvatarColors[index][0], b = AvatarColors[index][1];
            using (var br = new LinearGradientBrush(r, a, b, 55f)) g.FillEllipse(br, r);
            g.SmoothingMode = old;
            string glyph = index == 0 ? initial : AvatarGlyphs[index];
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            if (index == 0) SetText.DrawText(g, glyph, Typography.Text(r.Width * 0.3f, Weight.Semibold), r, Color.White, mid); // cached font: not disposed
            else using (var f = new Font("Segoe UI Symbol", r.Width * 0.4f, GraphicsUnit.Pixel)) SetText.DrawText(g, glyph, f, r, Color.White, mid);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static HashSet<string> fonts;
        public static bool HasFont(string name)
        {
            if (fonts == null)
            {
                using (var c = new InstalledFontCollection())
                    fonts = new HashSet<string>(c.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            }
            return fonts.Contains(name);
        }

        public static void OpenTarget(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Could not open " + target + "\n" + ex.Message, "MacDock"); }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool GetAutostart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MacDock") != null; }
            catch { return false; }
        }
        const string ChoiceKey = @"Software\MacDock";
        public static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("MacDock", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("MacDock", false);
                }
                RegSet(ChoiceKey, "AutostartChoice", on ? 1 : 0); // remembered, so the default below never overrides the user
            }
            catch (Exception ex) { MessageBox.Show("Could not change startup setting:\n" + ex.Message, "MacDock"); }
        }

        // First run only: start MacDock at login by default. Once the toggle in Settings has been used (on or off), or this
        // has run once, the user's choice stands and it is never switched on again. Only the per-user Run value is written.
        public static void EnsureDefaultAutostart()
        {
            if (RegInt(ChoiceKey, "AutostartChoice", -1) != -1) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    k.SetValue("MacDock", "\"" + Application.ExecutablePath + "\"");
                RegSet(ChoiceKey, "AutostartChoice", 1);
            }
            catch (Exception ex) { Program.Log(ex); } // blocked: stay off, and try again next run
        }

        // Safety net: a per-user scheduled task starts MacDock every minute if it is not running (a second copy exits at the mutex).
        // It lives in Task Scheduler, outside any parent process, so it brings the dock back even after a hard kill.
        static void EnsureGuardTask()
        {
            string exe = Application.ExecutablePath;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var psi = new ProcessStartInfo("schtasks.exe", "/Create /F /SC MINUTE /MO 1 /TN MacDockGuard /TR \"\\\"" + exe + "\\\"\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                    using (var p = Process.Start(psi)) p.WaitForExit(15000);
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        // Every launch: rewrite the Run value unless the user switched autostart off.
        public static void EnsureAutostart()
        {
            if (RegInt(ChoiceKey, "AutostartChoice", -1) == 0) return;
            if (RegInt(ChoiceKey, "GuardTask", 0) == 1) EnsureGuardTask(); // opt-in only (default off)
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    k.SetValue("MacDock", "\"" + Application.ExecutablePath + "\"");
                RegSet(ChoiceKey, "AutostartChoice", 1);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static int RegInt(string path, string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }
        public static void RegSet(string path, string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(path)) k.SetValue(name, value, RegistryValueKind.DWord); }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
        public static void BroadcastSettingChange(string what)
        {
            UIntPtr r;
            SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, UIntPtr.Zero, what, 2, 1000, out r);
        }

        // Shuts MacDock down for good: disables only the MacDockGuard task, blocks self-relaunch, kills other MacDock.exe, exits.
        // The next manual launch re-creates the guard task and the Run value as usual.
        public static void ForceStop()
        {
            Program.QuitRequested = true;
            try
            {
                foreach (string args in new[] { "/Change /TN MacDockGuard /DISABLE", "/End /TN MacDockGuard" })
                {
                    var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                    using (var p = Process.Start(psi)) p.WaitForExit(10000);
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            try
            {
                int me = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName("MacDock"))
                {
                    try { if (p.Id != me) p.Kill(); } catch { }
                    p.Dispose();
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            Application.Exit();
        }

        public static void RestartApp()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--wait") { UseShellExecute = false });
                Application.Exit();
            }
            catch (Exception ex) { MessageBox.Show("Could not restart MacDock:\n" + ex.Message, "MacDock"); }
        }
    }

    // ---------- controls ----------

    sealed class SetToggle : Control
    {
        bool on;
        public event EventHandler Changed;

        public SetToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(38, 22);
            Cursor = Cursors.Hand;
        }

        public bool On { get { return on; } set { if (on == value) return; on = value; Invalidate(); } }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            on = !on;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = SetUi.Round(track, track.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(Enabled ? 255 : 110, on ? SetUi.Accent : SetUi.Track)))
                g.FillPath(b, path);
            float k = Height - 6;
            float x = on ? Width - k - 3 : 3;
            using (var b = new SolidBrush(Color.FromArgb(Enabled ? 255 : 170, 255, 255, 255)))
                g.FillEllipse(b, x, 3, k, k);
        }
    }

    sealed class SetRow : Control
    {
        public string Label, Value;
        public bool Chevron;
        public Control Widget;
        public Action Click;
        int widgetW;

        public SetRow(string label)
        {
            Label = label;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Height = SetCard.RowH;
        }

        public void SetWidget(Control w)
        {
            Widget = w;
            widgetW = w.Width;
            Controls.Add(w);
            PlaceWidget();
        }

        void PlaceWidget()
        {
            if (Widget == null) return;
            // Keep at least ~130px for the label so a wide widget never runs over it.
            int maxW = Math.Max(60, Width - 14 - 130 - 12 - (Chevron ? 14 : 0));
            Widget.Width = Math.Min(widgetW, maxW);
            Widget.Left = Width - 12 - (Chevron ? 14 : 0) - Widget.Width;
            Widget.Top = (Height - Widget.Height) / 2;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); PlaceWidget(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Click != null) Click();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            const TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int right = Width - 12;
            if (Chevron)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(SetUi.Chevron, 1.6f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    float cx = right - 5, cy = Height / 2f;
                    g.DrawLines(pen, new[] { new PointF(cx, cy - 4), new PointF(cx + 4, cy), new PointF(cx, cy + 4) });
                }
                right -= 14;
            }
            int labelRight = Width - 12;
            if (!string.IsNullOrEmpty(Value))
            {
                Font vf = Typography.Body();
                int vw = SetText.MeasureText(g, Value, vf, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 2;
                vw = Math.Min(vw, (int)(Width * 0.45f));
                int left = right - vw;
                SetText.DrawText(g, Value, vf, new Rectangle(left, 0, vw, Height), SetUi.Dim, f | TextFormatFlags.Right);
                labelRight = left - 12;
            }
            else if (Widget != null) labelRight = Widget.Left - 12;
            else labelRight = right;
            SetText.DrawText(g, Label, Typography.Body(), new Rectangle(14, 0, Math.Max(0, labelRight - 14), Height), SetUi.Text, f | TextFormatFlags.Left);
        }
    }

    sealed class SetCard : Control
    {
        public const int RowH = 46;
        readonly List<SetRow> rows = new List<SetRow>();

        public SetCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = SetUi.Content;
        }

        public SetRow Add(SetRow r)
        {
            rows.Add(r);
            Controls.Add(r);
            Height = rows.Count * RowH;
            LayoutRows();
            return r;
        }

        void LayoutRows()
        {
            for (int i = 0; i < rows.Count; i++) rows[i].SetBounds(0, i * RowH, Width, RowH);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutRows(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SetUi.Content);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = SetUi.Round(r, 14))
            {
                using (var b = new SolidBrush(SetUi.Card)) g.FillPath(b, path);
                using (var p = new Pen(SetUi.CardLine)) g.DrawPath(p, path);
            }
            using (var p = new Pen(SetUi.RowLine))
                for (int i = 1; i < rows.Count; i++) g.DrawLine(p, 16, i * RowH, Width - 16, i * RowH);
        }
    }

    // macOS-style slider: thin rounded track, accent fill, white knob. Changed fires for every move while dragging.
    sealed class SetSlider : Control
    {
        const int Pad = 11;
        double val;
        bool drag;
        public event Action<double> Changed;

        public SetSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(230, 28);
            Cursor = Cursors.Hand;
        }

        public double Value { get { return val; } set { val = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        void Pick(int x)
        {
            double v = (x - Pad) / (double)Math.Max(1, Width - 2 * Pad);
            val = Math.Max(0, Math.Min(1, v));
            Invalidate();
            if (Changed != null) Changed(val);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { drag = true; Capture = true; Pick(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Pick(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int a = Enabled ? 255 : 110;
            float cy = Height / 2f, x = Pad + (float)val * (Width - 2 * Pad);
            using (var p = SetUi.Round(new RectangleF(Pad, cy - 2.5f, Width - 2 * Pad, 5), 2.5f))
            using (var b = new SolidBrush(SetUi.Track)) g.FillPath(b, p);
            if (x > Pad)
                using (var p = SetUi.Round(new RectangleF(Pad, cy - 2.5f, x - Pad, 5), 2.5f))
                using (var b = new SolidBrush(Color.FromArgb(a, SetUi.Accent))) g.FillPath(b, p);
            using (var b = new SolidBrush(Color.FromArgb(Enabled ? 50 : 20, 0, 0, 0))) g.FillEllipse(b, x - 9.5f, cy - 8.5f, 19, 19);
            using (var b = new SolidBrush(Color.FromArgb(a, 255, 255, 255))) g.FillEllipse(b, x - 9, cy - 9, 18, 18);
            using (var p = new Pen(Color.FromArgb(Enabled ? 70 : 30, 0, 0, 0))) g.DrawEllipse(p, x - 9, cy - 9, 18, 18);
        }
    }

    // Mini dock illustration for Settings > Dock: icon size and magnification follow the sliders; the pointer drives the zoom.
    sealed class SetDockPreview : Control
    {
        const int Count = 7;
        static readonly Color[] Cols = { Color.FromArgb(70, 140, 235), Color.FromArgb(235, 100, 90), Color.FromArgb(90, 190, 120), Color.FromArgb(240, 180, 70), Color.FromArgb(160, 110, 220), Color.FromArgb(70, 190, 200), Color.FromArgb(150, 150, 160) };
        public int DockSize = 48;
        public double Magnify = 1.75;
        float mx = -1;

        public SetDockPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = SetUi.Content;
            Height = 150;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); mx = e.X; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mx = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SetUi.Content);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = SetUi.Round(r, 14))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(60, 80, 130), Color.FromArgb(30, 36, 60), 90f)) g.FillPath(b, path);
                using (var p = new Pen(SetUi.CardLine)) g.DrawPath(p, path);
            }
            const float k = 0.6f; // preview scale
            float s = DockSize * k, gap = 5, pad = 7;
            float total = Count * s + (Count - 1) * gap;
            float x0 = (Width - total) / 2f;
            float center = mx >= 0 ? mx : x0 + total / 2f;
            // zoom per icon: cosine falloff within ~2.2 icons of the pointer
            var sc = new float[Count]; var xs = new float[Count];
            float cx = x0 + s / 2f;
            float reach = 2.2f * (s + gap), sum = 0;
            for (int i = 0; i < Count; i++)
            {
                float d = Math.Abs(x0 + i * (s + gap) + s / 2f - center);
                float t = d >= reach ? 0 : (float)(0.5 + 0.5 * Math.Cos(Math.PI * d / reach));
                sc[i] = 1f + (float)(Magnify - 1) * t;
                sum += s * sc[i] + (i < Count - 1 ? gap : 0);
            }
            float x = (Width - sum) / 2f;
            float baseY = Height - 22 - pad;
            float dockH = s + pad * 2;
            using (var p = SetUi.Round(new RectangleF((Width - sum) / 2f - pad - 4, baseY - s - pad, sum + 2 * pad + 8, dockH), 16))
            {
                using (var b = new SolidBrush(Color.FromArgb(90, 255, 255, 255))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawPath(pen, p);
            }
            for (int i = 0; i < Count; i++)
            {
                float sz = s * sc[i];
                var rc = new RectangleF(x, baseY - sz, sz, sz);
                using (var p = SetUi.Round(rc, sz * 0.23f))
                using (var b = new LinearGradientBrush(rc, ControlPaint.Light(Cols[i], 0.25f), Cols[i], 90f)) g.FillPath(b, p);
                x += sz + gap;
            }
        }
    }

    // ---------- power and wallpaper plumbing ----------

    static class SetSys
    {
        [DllImport("powrprof.dll")] static extern uint PowerGetActualOverlayScheme(out Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerStatus { public byte Ac, Flag, Life, Saver; public int Left, Full; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);

        static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99"), VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
        static readonly Guid SubEnergy = new Guid("de830923-a562-41af-a086-e3a2c6bad2da"), BattThreshold = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

        // Windows 11 "Power mode" is an overlay on the active plan.
        static readonly Guid[] Overlays =
        {
            new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), // best power efficiency
            Guid.Empty,                                       // balanced
            new Guid("ded574b5-45a0-4f42-8737-46345c09c238")  // best performance
        };
        public static readonly string[] OverlayNames = { "Best power efficiency", "Balanced", "Best performance" };

        public static int PowerMode()
        {
            Guid g;
            if (PowerGetActualOverlayScheme(out g) != 0) return -1;
            for (int i = 0; i < Overlays.Length; i++) if (Overlays[i] == g) return i;
            return g == new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f") ? 2 : 1; // max performance counts as performance
        }

        public static bool SetPowerMode(int i) { return i >= 0 && i < Overlays.Length && PowerSetActiveOverlayScheme(Overlays[i]) == 0; }

        static uint? ReadIndex(bool ac, Guid sub, Guid setting)
        {
            IntPtr sp;
            if (PowerGetActiveScheme(IntPtr.Zero, out sp) != 0 || sp == IntPtr.Zero) return null;
            try
            {
                Guid scheme = (Guid)Marshal.PtrToStructure(sp, typeof(Guid));
                uint v;
                uint r = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v) : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
                return r == 0 ? (uint?)v : null;
            }
            catch { return null; }
            finally { LocalFree(sp); }
        }

        // Minutes (0 = never), or -1 if unreadable.
        public static int ScreenOffMinutes(bool ac) { uint? v = ReadIndex(ac, SubVideo, VideoIdle); return v.HasValue ? (int)(v.Value / 60) : -1; }
        public static int SleepMinutes(bool ac) { uint? v = ReadIndex(ac, SubSleep, StandbyIdle); return v.HasValue ? (int)(v.Value / 60) : -1; }
        public static bool SetScreenOff(bool ac, int min) { return Powercfg("/change monitor-timeout-" + (ac ? "ac " : "dc ") + min); }
        public static bool SetSleep(bool ac, int min) { return Powercfg("/change standby-timeout-" + (ac ? "ac " : "dc ") + min); }

        // Battery Saver's "turn on automatically at" level (percent, 0 = never), or -1 if this PC doesn't expose it.
        public static int BatterySaverLevel() { uint? v = ReadIndex(false, SubEnergy, BattThreshold); return v.HasValue ? (int)v.Value : -1; }

        public static bool SetBatterySaverLevel(int percent)
        {
            return Powercfg("/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD " + percent) && Powercfg("/setactive SCHEME_CURRENT");
        }

        public static bool BatterySaverOn() { PowerStatus s; return GetSystemPowerStatus(out s) && (s.Saver & 1) != 0; }

        public static bool Powercfg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    return p.WaitForExit(10000) && p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // ---- wallpaper ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int param, string value, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int flags);

        public static string WallDir { get { return Path.Combine(AppPaths.DataDir, "wallpapers"); } }

        public static string GetWallpaper()
        {
            var sb = new StringBuilder(520);
            return SystemParametersInfo(0x73, 520, sb, 0) ? sb.ToString() : "";
        }

        public static bool SetWallpaper(string path) { return SystemParametersInfo(0x14, 0, path, 3); } // persisted + broadcast

        public static readonly string[] StyleNames = { "Fill", "Fit", "Stretch", "Tile", "Center", "Span" };
        static readonly string[][] StyleValues = { new[] { "10", "0" }, new[] { "6", "0" }, new[] { "2", "0" }, new[] { "0", "1" }, new[] { "0", "0" }, new[] { "22", "0" } };

        public static int StyleIndex()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    string s = Convert.ToString(k.GetValue("WallpaperStyle", "10")), t = Convert.ToString(k.GetValue("TileWallpaper", "0"));
                    for (int i = 0; i < StyleValues.Length; i++) if (StyleValues[i][0] == s && StyleValues[i][1] == t) return i;
                }
            }
            catch { }
            return 0;
        }

        public static bool SetStyle(int i)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    k.SetValue("WallpaperStyle", StyleValues[i][0], RegistryValueKind.String);
                    k.SetValue("TileWallpaper", StyleValues[i][1], RegistryValueKind.String);
                }
                string cur = GetWallpaper();
                return cur.Length == 0 || SetWallpaper(cur); // re-apply so the new style takes effect
            }
            catch { return false; }
        }

        // ---- generated macOS-style gradient wallpapers ----
        sealed class Preset
        {
            public string Name; public Color A, B; public float Angle; public float[] Blobs; public Color[] BlobColors; // x, y, radius (fractions), alpha
        }

        static Preset P(string name, Color a, Color b, float angle, float[] blobs, params Color[] colors)
        {
            return new Preset { Name = name, A = a, B = b, Angle = angle, Blobs = blobs, BlobColors = colors };
        }

        static readonly Preset[] Presets =
        {
            P("Aurora", Color.FromArgb(10, 16, 44), Color.FromArgb(8, 58, 84), 60f, new[] { 0.2f, 0.75f, 0.6f, 150, 0.8f, 0.25f, 0.55f, 130, 0.55f, 1.05f, 0.5f, 100 }, Color.FromArgb(0, 214, 170), Color.FromArgb(124, 92, 255), Color.FromArgb(30, 200, 255)),
            P("Sunset", Color.FromArgb(255, 190, 110), Color.FromArgb(92, 42, 118), 80f, new[] { 0.3f, 0.85f, 0.6f, 170, 0.8f, 0.15f, 0.55f, 150, 0.1f, 0.2f, 0.4f, 110 }, Color.FromArgb(255, 84, 120), Color.FromArgb(255, 214, 96), Color.FromArgb(255, 140, 70)),
            P("Ocean", Color.FromArgb(8, 34, 84), Color.FromArgb(0, 136, 204), 45f, new[] { 0.15f, 0.9f, 0.6f, 150, 0.85f, 0.2f, 0.5f, 130 }, Color.FromArgb(70, 230, 230), Color.FromArgb(110, 130, 255)),
            P("Meadow", Color.FromArgb(24, 100, 76), Color.FromArgb(176, 224, 120), 35f, new[] { 0.25f, 0.2f, 0.55f, 140, 0.85f, 0.85f, 0.6f, 150 }, Color.FromArgb(210, 255, 150), Color.FromArgb(40, 170, 140)),
            P("Lavender", Color.FromArgb(118, 98, 204), Color.FromArgb(244, 176, 224), 65f, new[] { 0.2f, 0.8f, 0.6f, 150, 0.8f, 0.2f, 0.5f, 140 }, Color.FromArgb(255, 210, 240), Color.FromArgb(150, 130, 255)),
            P("Graphite", Color.FromArgb(22, 22, 28), Color.FromArgb(74, 76, 90), 50f, new[] { 0.75f, 0.2f, 0.6f, 70, 0.15f, 0.85f, 0.5f, 60 }, Color.FromArgb(220, 224, 255), Color.FromArgb(160, 170, 220)),
            P("Peach", Color.FromArgb(255, 208, 172), Color.FromArgb(255, 132, 150), 70f, new[] { 0.25f, 0.2f, 0.5f, 140, 0.85f, 0.85f, 0.6f, 150 }, Color.FromArgb(255, 240, 200), Color.FromArgb(255, 96, 130)),
            P("Midnight", Color.FromArgb(6, 6, 26), Color.FromArgb(44, 22, 96), 55f, new[] { 0.2f, 0.85f, 0.6f, 130, 0.8f, 0.2f, 0.5f, 120 }, Color.FromArgb(70, 100, 255), Color.FromArgb(170, 70, 230))
        };

        public static int PresetCount { get { return Presets.Length; } }
        public static string PresetName(int i) { return Presets[i].Name; }

        public static Bitmap Render(int i, int w, int h)
        {
            Preset d = Presets[i];
            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), d.A, d.B, d.Angle)) g.FillRectangle(br, 0, 0, w, h);
                int big = Math.Max(w, h);
                for (int k = 0; k * 4 < d.Blobs.Length; k++)
                {
                    float cx = d.Blobs[k * 4] * w, cy = d.Blobs[k * 4 + 1] * h, r = d.Blobs[k * 4 + 2] * big;
                    Color c = d.BlobColors[k % d.BlobColors.Length];
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                        using (var pb = new PathGradientBrush(path))
                        {
                            pb.CenterColor = Color.FromArgb((int)d.Blobs[k * 4 + 3], c);
                            pb.SurroundColors = new[] { Color.FromArgb(0, c) };
                            g.FillPath(pb, path);
                        }
                    }
                }
            }
            return bmp;
        }

        // Full-resolution file for a preset, created on first use.
        public static string EnsureFile(int i)
        {
            Rectangle s = Screen.PrimaryScreen.Bounds;
            string path = Path.Combine(WallDir, Presets[i].Name + "-" + s.Width + "x" + s.Height + ".png");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(WallDir);
            using (Bitmap b = Render(i, s.Width, s.Height)) b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        // Which preset a wallpaper path belongs to, or -1.
        public static int PresetOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith(WallDir, StringComparison.OrdinalIgnoreCase)) return -1;
            string name = Path.GetFileName(path);
            for (int i = 0; i < Presets.Length; i++) if (name.StartsWith(Presets[i].Name + "-", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    // Dark/light themed colours for the picker's drop-down menu.
    sealed class SetMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return SetUi.Card; } }
        public override Color ImageMarginGradientBegin { get { return SetUi.Card; } }
        public override Color ImageMarginGradientMiddle { get { return SetUi.Card; } }
        public override Color ImageMarginGradientEnd { get { return SetUi.Card; } }
        public override Color MenuBorder { get { return SetUi.CardLine; } }
        public override Color MenuItemBorder { get { return SetUi.Accent; } }
        public override Color MenuItemSelected { get { return SetUi.Accent; } }
        public override Color MenuItemSelectedGradientBegin { get { return SetUi.Accent; } }
        public override Color MenuItemSelectedGradientEnd { get { return SetUi.Accent; } }
        public override Color CheckBackground { get { return SetUi.Accent; } }
        public override Color CheckSelectedBackground { get { return SetUi.Accent; } }
        public override Color CheckPressedBackground { get { return SetUi.Accent; } }
    }

    // Drop-down picker: shows the current choice, opens a themed menu on click.
    sealed class SetPicker : Control
    {
        string[] options = new string[0];
        int index;
        public event Action<int> Changed;

        public SetPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(210, 30);
            Cursor = Cursors.Hand;
        }

        public string[] Options { get { return options; } set { options = value ?? new string[0]; Invalidate(); } }
        public int Index { get { return index; } set { index = value; Invalidate(); } }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled || options.Length == 0) return;
            var menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new SetMenuColors());
            menu.BackColor = SetUi.Card;
            menu.ForeColor = SetUi.Text;
            menu.Font = Typography.Secondary();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            for (int i = 0; i < options.Length; i++)
            {
                int n = i;
                var item = new ToolStripMenuItem(options[i]);
                item.Checked = i == index;
                item.ForeColor = SetUi.Text;
                item.Click += (s, ev) => { index = n; Invalidate(); if (Changed != null) Changed(n); };
                menu.Items.Add(item);
            }
            menu.Closed += (s, ev) => menu.BeginInvoke((Action)menu.Dispose);
            menu.Show(this, new Point(0, Height + 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            
            string text = index >= 0 && index < options.Length ? options[index] : "";
            Color fg = Enabled ? SetUi.Text : SetUi.Dim;
            SetText.DrawText(g, text, Typography.Secondary(), new Rectangle(0, 0, Width - 30, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var pen = new Pen(SetUi.Chevron, 1.6f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float cx = Width - 16, cy = Height / 2f;
                g.DrawLines(pen, new[] { new PointF(cx - 3.5f, cy - 2.5f), new PointF(cx, cy - 6), new PointF(cx + 3.5f, cy - 2.5f) });
                g.DrawLines(pen, new[] { new PointF(cx - 3.5f, cy + 2.5f), new PointF(cx, cy + 6), new PointF(cx + 3.5f, cy + 2.5f) });
            }
        }
    }

    interface IFlowGrid { int HeightFor(int width); }

    // Wallpaper thumbnails with a name under each; the picked one gets an accent ring.
    sealed class SetWallGrid : Control, IFlowGrid
    {
        public sealed class Cell { public string Title; public Bitmap Thumb; }
        const int TW = 164, TH = 103, Gap = 14, LabelH = 22;
        public readonly List<Cell> Cells = new List<Cell>();
        public int Selected = -1;
        public event Action<int> Picked;
        int hover = -1;

        public SetWallGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        int PerRow(int width) { return Math.Max(1, (width + Gap) / (TW + Gap)); }

        public int HeightFor(int width)
        {
            int per = PerRow(width), rows = Math.Max(1, (Cells.Count + per - 1) / per);
            return rows * (TH + LabelH + Gap) - Gap + 8;
        }

        Rectangle Rect(int i)
        {
            int per = PerRow(Width);
            return new Rectangle(4 + (i % per) * (TW + Gap), 4 + (i / per) * (TH + LabelH + Gap), TW, TH);
        }

        int IndexAt(Point p) { for (int i = 0; i < Cells.Count; i++) if (Rect(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Location); if (i != hover) { hover = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = IndexAt(e.Location); if (i >= 0 && Picked != null) Picked(i); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (Cell c in Cells) if (c.Thumb != null) c.Thumb.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < Cells.Count; i++)
            {
                Rectangle r = Rect(i);
                using (var path = SetUi.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 9))
                {
                    var old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    if (Cells[i].Thumb != null) g.DrawImage(Cells[i].Thumb, r);
                    g.Clip = old;
                    if (i == Selected)
                        using (var p = new Pen(SetUi.Accent, 3f)) g.DrawPath(p, SetUi.Round(new RectangleF(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), 11));
                    else
                        using (var p = new Pen(i == hover ? SetUi.Dim : SetUi.CardLine, 1f)) g.DrawPath(p, path);
                }
                SetText.DrawText(g, Cells[i].Title, Typography.Caption(), new Rectangle(r.X, r.Bottom + 3, r.Width, LabelH - 3), i == Selected ? SetUi.Text : SetUi.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Grid of preset avatars; clicking one saves "@avatar N" to dock.txt.
    sealed class SetAvatarGrid : Control, IFlowGrid
    {
        const int Size_ = 46, Gap = 16;
        public event Action Changed;
        int hover = -1;

        public SetAvatarGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; // shows the page behind it (Clear() would paint an opaque black box on the acrylic)
            Cursor = Cursors.Hand;
        }

        int PerRow(int width) { return Math.Max(1, (width + Gap) / (Size_ + Gap)); }

        public int HeightFor(int width)
        {
            int rows = (SetUi.AvatarGlyphs.Length + PerRow(width) - 1) / PerRow(width);
            return rows * (Size_ + Gap) - Gap + 8;
        }

        Rectangle Cell(int i)
        {
            int per = PerRow(Width);
            return new Rectangle(4 + (i % per) * (Size_ + Gap), 4 + (i / per) * (Size_ + Gap), Size_, Size_);
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < SetUi.AvatarGlyphs.Length; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Location); if (i != hover) { hover = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexAt(e.Location);
            if (i < 0) return;
            SetCfg.Set("avatar", i.ToString(), false);
            Invalidate();
            if (Changed != null) Changed();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int sel = SetUi.AvatarIndex();
            string initial = Environment.UserName.Length > 0 ? Environment.UserName.Substring(0, 1).ToUpperInvariant() : "M";
            for (int i = 0; i < SetUi.AvatarGlyphs.Length; i++)
            {
                Rectangle r = Cell(i);
                SetUi.DrawAvatar(g, r, i, initial);
                if (i == sel)
                    using (var p = new Pen(SetUi.Accent, 2.5f)) g.DrawEllipse(p, r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
                else if (i == hover)
                    using (var p = new Pen(SetUi.Dim, 1.5f)) g.DrawEllipse(p, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
            }
        }
    }

    sealed class SetSide : Control
    {
        public sealed class Item
        {
            public string Key, Name, Glyph;
            public Color Color;
            public int Kind; // 0 page, 1 separator, 2 profile, 3 section label
        }

        public readonly List<Item> All = new List<Item>();
        List<Item> shown = new List<Item>();
        public string Selected;
        int hover = -1, off;
        public event Action<string> Picked;
        string iconFont;
        struct InkFit { public float Size; public Point Off; }
        static readonly Dictionary<string, InkFit> inkFits = new Dictionary<string, InkFit>();

        // Icon glyphs differ in drawn size and sit off the font's line box. Scale each so its visible pixels span ~13px
        // of the 24px plate, and shift it so those pixels are centred.
        static InkFit FitGlyph(string glyph, string fontName)
        {
            InkFit fit;
            if (inkFits.TryGetValue(glyph, out fit)) return fit;
            fit = new InkFit { Size = 9f };
            try
            {
                float size = 9f;
                for (int pass = 0; pass < 2; pass++)
                {
                    using (var font = new Font(fontName, size))
                    using (var bmp = new Bitmap(40, 40))
                    {
                        using (Graphics bg = Graphics.FromImage(bmp))
                        {
                            bg.Clear(Color.Black);
                            TextRenderer.DrawText(bg, glyph, font, new Rectangle(0, 0, 40, 40), Color.White, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        }
                        int minX = 40, minY = 40, maxX = -1, maxY = -1;
                        for (int y = 0; y < 40; y++)
                            for (int x = 0; x < 40; x++)
                                if (bmp.GetPixel(x, y).R > 90)
                                {
                                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                                }
                        if (maxX < 0) break;
                        int dim = Math.Max(maxX - minX + 1, maxY - minY + 1);
                        if (pass == 0) { size = Math.Max(7f, Math.Min(13f, size * 13f / dim)); continue; }
                        fit.Size = size;
                        fit.Off = new Point((int)Math.Round(20.0 - (minX + maxX + 1) / 2.0), (int)Math.Round(20.0 - (minY + maxY + 1) / 2.0));
                    }
                }
            }
            catch { }
            inkFits[glyph] = fit;
            return fit;
        }

        public SetSide()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = SetUi.Side;
            iconFont = SetUi.HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : (SetUi.HasFont("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null);
        }

        public void Filter(string q)
        {
            q = (q ?? "").Trim();
            shown = q.Length == 0 ? new List<Item>(All) : All.Where(i => i.Kind == 0 && i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            off = 0; offF = 0; offTarget = 0;
            if (vsync != null) vsync.Sleep();
            Invalidate();
        }

        static int HeightOf(Item i)
        {
            switch (i.Kind) { case 0: return 38; case 1: return 26; case 2: return 68; default: return 34; }
        }

        int Total() { int t = 0; foreach (Item i in shown) t += HeightOf(i); return t + 8; }
        public static bool BenchOn; public static readonly List<double> BGap = new List<double>(), BPaint = new List<double>();
        public int BenchTotal { get { return Total(); } }

        float offF, offTarget;
        VsyncDriver vsync; readonly FrameClock clock = new FrameClock();

        // Wheel input moves a target; a vsync-timed loop (monitor refresh rate, delta-time easing) glides the offset to it.
        public void Scroll(int delta)
        {
            offTarget = Math.Max(0, Math.Min(Math.Max(0, Total() - Height), offTarget + delta));
            if (vsync == null)
                vsync = new VsyncDriver(this, () =>
                {
                    double rawS = clock.Next(); if (BenchOn) BGap.Add(rawS * 1000.0); double dt = vsync.Frame(rawS);
                    double d = offTarget - offF;
                    if (Math.Abs(d) < 0.5) { offF = offTarget; vsync.Sleep(); }
                    else offF += (float)(d * (1 - Math.Pow(0.7, dt * 1000.0 / 8.0)));
                    int n = (int)Math.Round(offF);
                    if (n != off) { off = n; scrollInval = true; Invalidate(); scrollInval = false; Update(); }
                });
            if (!vsync.Active) { offF = off; offTarget = Math.Max(0, Math.Min(Math.Max(0, Total() - Height), off + delta)); clock.Reset(); vsync.Wake(); }
        }

        int IndexAt(int y)
        {
            y += off;
            int top = 0;
            for (int i = 0; i < shown.Count; i++)
            {
                int h = HeightOf(shown[i]);
                if (y >= top && y < top + h) return i;
                top += h;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Y);
            bool hand = i >= 0 && (shown[i].Kind == 0 || shown[i].Kind == 2);
            if (i >= 0 && shown[i].Kind != 0) i = -1;
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexAt(e.Y);
            if (i >= 0 && shown[i].Kind == 2) { Selected = "profile"; Invalidate(); if (Picked != null) Picked(Selected); return; }
            if (i < 0 || shown[i].Kind != 0) return;
            Selected = shown[i].Key;
            Invalidate();
            if (Picked != null) Picked(Selected);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var psw = System.Diagnostics.Stopwatch.StartNew();
            int w = Math.Max(1, Width), h = Math.Max(1, Total());
            if (cache == null || cacheDirty || cache.Width != w || cache.Height != h)
            {
                if (cache != null && (cache.Width != w || cache.Height != h)) { cache.Dispose(); cache = null; }
                if (cache == null) cache = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (Graphics cg = Graphics.FromImage(cache)) PaintList(cg);
                cacheDirty = false;
            }
            e.Graphics.Clear(SetUi.Side);
            e.Graphics.DrawImageUnscaled(cache, 0, -off);
            if (BenchOn) BPaint.Add(psw.Elapsed.TotalMilliseconds);
        }

        // The whole list is drawn once into a bitmap and only re-drawn when something changes (hover, selection, filter,
        // theme); scrolling just blits it, so a frame costs well under one refresh interval.
        Bitmap cache; bool cacheDirty = true, scrollInval;
        protected override void OnInvalidated(InvalidateEventArgs e) { if (!scrollInval) cacheDirty = true; base.OnInvalidated(e); }
        protected override void Dispose(bool disposing) { if (disposing && cache != null) { cache.Dispose(); cache = null; } base.Dispose(disposing); }

        void PaintList(Graphics g)
        {
            g.Clear(SetUi.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left;
            int y = 0;
            for (int idx = 0; idx < shown.Count; idx++)
            {
                Item it = shown[idx];
                int h = HeightOf(it);
                if (it.Kind == 1)
                {
                    using (var p = new Pen(SetUi.SepLine)) g.DrawLine(p, 16, y + h / 2, Width - 16, y + h / 2);
                }
                else if (it.Kind == 3)
                {
                    SetText.DrawText(g, it.Name, Typography.Label(), new Rectangle(18, y, Width - 24, h), SetUi.Dim, (f & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Bottom);
                }
                else if (it.Kind == 2)
                {
                    SetUi.DrawAvatar(g, new Rectangle(14, y + 12, 42, 42), SetUi.AvatarIndex(), it.Glyph);
                    SetText.DrawText(g, it.Name, Typography.Heading(), new Rectangle(68, y + 14, Width - 74, 22), SetUi.Text, f);
                    SetText.DrawText(g, it.Key, Typography.Caption(), new Rectangle(68, y + 35, Width - 74, 22), SetUi.Dim, f);
                }
                else
                {
                    bool sel = it.Key == Selected;
                    var row = new RectangleF(8, y + 1, Width - 16, h - 2);
                    if (sel || idx == hover)
                        using (var path = SetUi.Round(row, 10))
                        using (var b = new SolidBrush(sel ? Color.FromArgb(59, 130, 246) : SetUi.HoverFill))
                            g.FillPath(b, path);
                    var icon = new RectangleF(17, y + (h - 24) / 2f, 24, 24);
                    bool drawnIcon = SideIcons.Draw(g, it.Key, icon, it.Glyph, iconFont);
                    if (!drawnIcon)
                    using (var path = SetUi.Round(icon, 6.5f))
                    using (var b = new SolidBrush(SetUi.Tile(it.Color))) g.FillPath(b, path);
                    if (drawnIcon) { }
                    else if (iconFont != null && it.Glyph.Length == 1 && it.Glyph[0] >= 0xE000)
                    {
                        InkFit fit = FitGlyph(it.Glyph, iconFont);
                        using (var font = new Font(iconFont, fit.Size))
                        {
                            Rectangle gr = Rectangle.Round(icon);
                            gr.Offset(fit.Off.X, fit.Off.Y);
                            SetText.DrawText(g, it.Glyph, font, gr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        }
                    }
                    else
                    {
                        string letter = it.Name.Substring(0, 1);
                        SetText.DrawText(g, letter, Typography.Text(8.5f, Weight.Semibold), Rectangle.Round(icon), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    SetText.DrawText(g, it.Name, Typography.Menu(), new Rectangle(52, y, Width - 60, h), sel ? Color.White : SetUi.Text, f);
                }
                y += h;
            }
        }
    }

    // ---------- the window ----------

    // Panel that composes its children off-screen and presents them once, so moving it while scrolling doesn't flicker.
    sealed class SmoothPanel : Panel
    {
        public SmoothPanel() { DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    }

    // While the page glides, a bitmap snapshot of it is blitted instead of moving hundreds of child controls
    // (which re-laid-out and repainted every frame and left artifacts on pages with many toggles).
    sealed class SnapView : Control
    {
        public Bitmap Bmp; public int Off;
        public SnapView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Bmp != null) e.Graphics.DrawImageUnscaled(Bmp, 0, -Off);
            SettingsForm.SnapPaintCost(sw.Elapsed.TotalMilliseconds);
        }
        protected override void Dispose(bool d) { if (d && Bmp != null) { Bmp.Dispose(); Bmp = null; } base.Dispose(d); }
    }

    sealed class SettingsForm : Form, IMessageFilter
    {
        const int W = 960, H = 680, SideW = 300, TopH = 60, Margin = 30, Grip = 6;

        static SettingsForm instance;

        public static void ShowSettings(string page = null)
        {
            if (instance == null || instance.IsDisposed) instance = new SettingsForm();
            if (page != null) instance.GoTo(page, true);
            if (instance.Visible) { instance.WindowState = FormWindowState.Normal; instance.Activate(); return; }
            // Build everything off-screen at opacity 0, let it paint fully, then fade in.
            instance.Opacity = 0;
            instance.StartPosition = FormStartPosition.Manual;
            Rectangle av = instance.Avail(Screen.FromPoint(Cursor.Position));
            instance.Bounds = new Rectangle(av.Left + (av.Width - instance.Width) / 2, av.Top, instance.Width, av.Height);
            instance.CreateControl();
            instance.PerformLayout();
            instance.Show();
            instance.WindowState = FormWindowState.Normal;
            instance.Activate();
            instance.Update();
            var f = instance;
            Point home = f.Location;
            var sw = Stopwatch.StartNew();
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            t.Tick += (s, e) =>
            {
                if (f.IsDisposed || f.WindowState != FormWindowState.Normal) { t.Dispose(); if (!f.IsDisposed) f.Opacity = 1; return; }
                double k = Math.Min(1.0, sw.ElapsedMilliseconds / 180.0), ease = 1 - Math.Pow(1 - k, 3);
                f.Opacity = ease;
                f.Location = new Point(home.X, home.Y + (int)Math.Round(10 * (1 - ease)));
                if (k >= 1.0) t.Dispose();
            };
            f.Location = new Point(home.X, home.Y + 10);
            t.Start();
        }

        bool fadingOut, fadedOut;

        // Fade out (~160ms) before really closing; timer is disposed afterwards.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!fadedOut && Visible && WindowState == FormWindowState.Normal && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (fadingOut) return;
                fadingOut = true;
                double start = Opacity;
                var sw = Stopwatch.StartNew();
                var t = new System.Windows.Forms.Timer { Interval = 15 };
                t.Tick += (s, ev) =>
                {
                    double k = Math.Min(1.0, sw.ElapsedMilliseconds / 160.0);
                    if (IsDisposed) { t.Dispose(); return; }
                    Opacity = start * (1 - k);
                    if (k >= 1.0) { t.Dispose(); fadedOut = true; Close(); }
                };
                t.Start();
                return;
            }
            base.OnFormClosing(e);
        }

        readonly SetSide side = new SetSide();
        readonly Panel viewport = new SmoothPanel();
        readonly Panel body = new SmoothPanel();
        readonly TextBox search = new TextBox();
        readonly Panel searchHost = new Panel();
        readonly List<Control> flow = new List<Control>();
        readonly List<string> history = new List<string>();
        int historyAt = -1;
        int scroll;
        string current = "general";
        bool searchPlaceholder = true;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        static readonly string[][] SystemPages =
        {
            // key, title, glyph, then label|uri pairs
            new[] { "sys-display", "Display & Sound", "", "Display|ms-settings:display", "Night light|ms-settings:nightlight", "Sound|ms-settings:sound", "Multitasking|ms-settings:multitasking" },
            new[] { "sys-network", "Network & Bluetooth", "", "Wi-Fi|ms-settings:network-wifi", "Ethernet|ms-settings:network-ethernet", "VPN|ms-settings:network-vpn", "Bluetooth & devices|ms-settings:bluetooth", "Printers & scanners|ms-settings:printers", "Mouse|ms-settings:mousetouchpad", "Touchpad|ms-settings:devices-touchpad", "Typing|ms-settings:typing" },
            new[] { "sys-power", "Power & Battery", "", "Battery|ms-settings:batterysaver", "Power & sleep|ms-settings:powersleep", "Storage|ms-settings:storagesense" },
            new[] { "sys-personal", "Personalization", "", "Background|ms-settings:personalization-background", "Colors|ms-settings:colors", "Themes|ms-settings:themes", "Lock screen|ms-settings:lockscreen", "Taskbar|ms-settings:taskbar" },
            new[] { "sys-notif", "Notifications & Focus", "", "Notifications|ms-settings:notifications", "Focus|ms-settings:quiethours" },
            new[] { "sys-privacy", "Privacy & Security", "", "Privacy & security|ms-settings:privacy", "Windows Security|windowsdefender:", "Location|ms-settings:privacy-location", "Camera|ms-settings:privacy-webcam", "Microphone|ms-settings:privacy-microphone" },
            new[] { "sys-apps", "Apps", "", "Installed apps|ms-settings:appsfeatures", "Default apps|ms-settings:defaultapps", "Startup apps|ms-settings:startupapps" },
            new[] { "sys-accounts", "Accounts", "", "Your info|ms-settings:yourinfo", "Sign-in options|ms-settings:signinoptions", "Email & accounts|ms-settings:emailandaccounts" },
            new[] { "sys-time", "Time & Language", "", "Date & time|ms-settings:dateandtime", "Language & region|ms-settings:regionlanguage" },
            new[] { "sys-update", "Windows Update", "", "Windows Update|ms-settings:windowsupdate", "Recovery|ms-settings:recovery" }
        };

        public SettingsForm()
        {
            SetUi.Apply(SetUi.WantDark());
            Text = "MacDock Settings";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = SetUi.Content;
            int h = Math.Min(H, Screen.PrimaryScreen.WorkingArea.Height - 40);
            ClientSize = new Size(W, h);
            MinimumSize = new Size(720, 480);
            Region = new Region(SetUi.Round(new RectangleF(0, 0, W, h), 22));

            // Sidebar
            side.SetBounds(Grip, 100, SideW - Grip, h - 100 - Grip);
            side.Picked += key => GoTo(key, true);
            AddSidebarItems();
            Controls.Add(side);

            searchHost.SetBounds(14, 56, SideW - 28, 34);
            searchHost.BackColor = SetUi.Side;
            searchHost.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = SetUi.Round(new RectangleF(0.5f, 0.5f, searchHost.Width - 1, searchHost.Height - 1), 16.5f))
                {
                    using (var b = new SolidBrush(SetUi.SearchFill)) e.Graphics.FillPath(b, path);
                    using (var p = new Pen(SetUi.SearchLine)) e.Graphics.DrawPath(p, path);
                }
            };
            search.BorderStyle = BorderStyle.None;
            search.BackColor = SetUi.SearchFill;
            search.ForeColor = SetUi.Dim;
            search.Font = Typography.Body();
            search.Text = "Search";
            search.SetBounds(34, 0, searchHost.Width - 46, search.Height);
            search.Top = (searchHost.Height - search.Height) / 2 + 1;
            search.GotFocus += (s, e) => { if (searchPlaceholder) { search.Text = ""; search.ForeColor = SetUi.Text; searchPlaceholder = false; } };
            search.LostFocus += (s, e) => { if (search.Text.Length == 0) { searchPlaceholder = true; search.Text = "Search"; search.ForeColor = SetUi.Dim; } };
            search.TextChanged += (s, e) => { if (!searchPlaceholder) side.Filter(search.Text); };
            searchHost.Controls.Add(search);
            Controls.Add(searchHost);

            // Content
            viewport.SetBounds(SideW, TopH, W - SideW - Grip, h - TopH - Grip);
            viewport.BackColor = SetUi.Content;
            body.BackColor = SetUi.Content;
            viewport.Controls.Add(body);
            viewport.Resize += (s, e) => Relayout();
            Controls.Add(viewport);

            history.Add("general");
            historyAt = 0;
            side.Selected = "general";
            BuildPage("general");

            captureTimer.Tick += (s, e) => CancelCapture();
            Application.AddMessageFilter(this);
            FormClosed += (s, e) => { CancelCapture(); Application.RemoveMessageFilter(this); };
        }

        void AddSidebarItems()
        {
            Action<string, string, string, Color> page = (key, name, glyph, color) =>
                side.All.Add(new SetSide.Item { Kind = 0, Key = key, Name = name, Glyph = glyph, Color = color });

            string user = Environment.UserName;
            side.All.Add(new SetSide.Item { Kind = 2, Name = user, Key = "Launcher settings", Glyph = user.Length > 0 ? user.Substring(0, 1).ToUpperInvariant() : "M" });
            side.All.Add(new SetSide.Item { Kind = 3, Name = "MACDOCK" });
            page("general", "General", "", Color.FromArgb(142, 142, 147));
            page("appearance", "Appearance", "", Color.FromArgb(99, 99, 106));
            page("dock", "Dock", "", Color.FromArgb(90, 90, 96));
            page("menubar", "Menu Bar & Island", "", Color.FromArgb(120, 120, 128));
            page("launchpad", "Launchpad", "", Color.FromArgb(94, 92, 230));
            page("icons", "Icons", "", Color.FromArgb(255, 149, 0));
            side.All.Add(new SetSide.Item { Kind = 1 });
            side.All.Add(new SetSide.Item { Kind = 3, Name = "SYSTEM" });
            Color[] colors = { Color.FromArgb(10, 132, 255), Color.FromArgb(10, 132, 255), Color.FromArgb(48, 209, 88), Color.FromArgb(0, 122, 255), Color.FromArgb(255, 69, 58), Color.FromArgb(142, 142, 147), Color.FromArgb(10, 132, 255), Color.FromArgb(142, 142, 147), Color.FromArgb(255, 159, 10), Color.FromArgb(142, 142, 147) };
            for (int i = 0; i < SystemPages.Length; i++)
                page(SystemPages[i][0], SystemPages[i][1], SystemPages[i][2], colors[i % colors.Length]);
            page("sys-files", "File Explorer", "", Color.FromArgb(10, 132, 255));
            page("sys-about", "About This PC", "", Color.FromArgb(142, 142, 147));
            side.Filter("");
        }

        // ----- navigation -----

        public void GoTo(string key, bool record)
        {
            if (record)
            {
                while (history.Count > historyAt + 1) history.RemoveAt(history.Count - 1);
                if (history[historyAt] != key) { history.Add(key); historyAt = history.Count - 1; }
            }
            side.Selected = key == "tray" ? "menubar" : key;
            side.Invalidate();
            Bitmap from = null, to = null;
            bool fade = IsHandleCreated && Visible && viewport.Width > 0 && viewport.Height > 0 && key != current;
            if (fade)
            {
                try { from = new Bitmap(viewport.Width, viewport.Height); viewport.DrawToBitmap(from, new Rectangle(0, 0, from.Width, from.Height)); }
                catch { if (from != null) from.Dispose(); from = null; fade = false; }
            }
            if (fadeCover != null) { Controls.Remove(fadeCover); fadeCover.Dispose(); fadeCover = null; }
            if (fade) SendMessage(viewport.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW off: build without flicker
            BuildPage(key);
            if (fade)
            {
                SendMessage(viewport.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
                try { to = new Bitmap(viewport.Width, viewport.Height); viewport.DrawToBitmap(to, new Rectangle(0, 0, to.Width, to.Height)); }
                catch { if (to != null) to.Dispose(); to = null; }
            }
            if (fade && from != null && to != null) StartFade(from, to);
            else { if (from != null) from.Dispose(); if (to != null) to.Dispose(); viewport.Invalidate(true); }
            Invalidate(new Rectangle(SideW, 0, Width - SideW, TopH));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        Control fadeCover;

        // Clean cross-fade between category pages: old and new pages are snapshotted and blended on a cover; no layout is animated.
        void StartFade(Bitmap from, Bitmap to)
        {
            var cover = new FadeCover(from, to) { Bounds = viewport.Bounds };
            fadeCover = cover;
            Controls.Add(cover);
            cover.BringToFront();
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            var sw = Stopwatch.StartNew();
            t.Tick += (s, e) =>
            {
                if (cover.IsDisposed) { t.Dispose(); return; }
                double k = Math.Min(1.0, sw.ElapsedMilliseconds / 160.0);
                cover.Alpha = (float)(1 - Math.Pow(1 - k, 2));
                cover.Invalidate();
                if (k >= 1.0)
                {
                    t.Dispose();
                    if (fadeCover == cover) fadeCover = null;
                    Controls.Remove(cover); cover.Dispose();
                    viewport.Invalidate(true);
                }
            };
            t.Start();
        }

        sealed class FadeCover : Control
        {
            readonly Bitmap a, b; public float Alpha;
            public FadeCover(Bitmap from, Bitmap to)
            {
                a = from; b = to;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.DrawImageUnscaled(a, 0, 0);
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = Alpha });
                    g.DrawImage(b, new Rectangle(0, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
                }
            }
            protected override void Dispose(bool d) { if (d) { a.Dispose(); b.Dispose(); } base.Dispose(d); }
        }

        string TitleOf(string key)
        {
            if (key == "profile") return "Profile";
            if (key == "tray") return "Tray Icons";
            foreach (var i in side.All) if (i.Kind == 0 && i.Key == key) return i.Name;
            return "";
        }

        // ----- window chrome -----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(SetUi.Side)) g.FillRectangle(b, 0, 0, SideW, Height);
            using (var p = new Pen(SetUi.EdgeLine)) g.DrawLine(p, SideW, 0, SideW, Height);

            Color[] dots = { Color.FromArgb(255, 95, 87), Color.FromArgb(254, 188, 46), Color.FromArgb(40, 200, 64) };
            for (int i = 0; i < 3; i++)
                using (var b = new SolidBrush(dots[i])) g.FillEllipse(b, 20 + i * 26, 20, 16, 16);
            if (lightsHover)
                using (var p = new Pen(Color.FromArgb(150, 0, 0, 0), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, 24.5f, 24.5f, 31.5f, 31.5f); g.DrawLine(p, 31.5f, 24.5f, 24.5f, 31.5f);
                    g.DrawLine(p, 50.5f, 28f, 57.5f, 28f);
                    g.DrawLine(p, 76.5f, 28f, 83.5f, 28f); g.DrawLine(p, 80f, 24.5f, 80f, 31.5f);
                }
            using (var p = new Pen(SetUi.Dim, 1.6f)) { p.EndCap = LineCap.Round; g.DrawEllipse(p, 30, 66, 9, 9); g.DrawLine(p, 38, 74, 42, 78); } // magnifier in the search pill

            // back / forward pill
            var pill = new RectangleF(SideW + 14, 17, 58, 28);
            using (var path = SetUi.Round(pill, 13))
            {
                using (var b = new SolidBrush(SetUi.PillFill)) g.FillPath(b, path);
                using (var p = new Pen(SetUi.PillLine)) g.DrawPath(p, path);
            }
            Color backC = historyAt > 0 ? SetUi.Text : SetUi.NavDim;
            Color fwdC = historyAt < history.Count - 1 ? SetUi.Text : SetUi.NavDim;
            using (var p = new Pen(backC, 1.8f)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLines(p, new[] { new PointF(SideW + 31f, 24.5f), new PointF(SideW + 26f, 31f), new PointF(SideW + 31f, 37.5f) }); }
            using (var p = new Pen(fwdC, 1.8f)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; using (var sp = new Pen(SetUi.PillLine)) g.DrawLine(sp, SideW + 43, 23, SideW + 43, 39); g.DrawLines(p, new[] { new PointF(SideW + 55f, 24.5f), new PointF(SideW + 60f, 31f), new PointF(SideW + 55f, 37.5f) }); }

            SetText.DrawText(g, TitleOf(current == "tray" ? "tray" : side.Selected), Typography.Title(), new Rectangle(SideW + 90, 0, Math.Max(0, Width - SideW - 104), TopH), SetUi.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        bool lightsHover;
        void SetLightsHover(bool v) { if (v != lightsHover) { lightsHover = v; Invalidate(new Rectangle(10, 10, 90, 36)); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetLightsHover(e.X >= 14 && e.X < 92 && e.Y >= 14 && e.Y < 42); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetLightsHover(false); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.Y < 48 && e.X < 90)
            {
                int dot = (e.X - 20) / 26;
                if (e.X >= 20 && (e.X - 20) % 26 <= 16)
                {
                    if (dot == 0) { Close(); return; }
                    if (dot == 1) { WindowState = FormWindowState.Minimized; return; }
                    if (dot == 2) { MaximizedBounds = Avail();WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return; }
                }
            }
            if (e.X >= SideW + 14 && e.X < SideW + 72 && e.Y >= 17 && e.Y < 45)
            {
                if (e.X < SideW + 43 && historyAt > 0) { historyAt--; GoTo(history[historyAt], false); }
                else if (e.X >= SideW + 43 && historyAt < history.Count - 1) { historyAt++; GoTo(history[historyAt], false); }
                return;
            }
            if (e.Y < TopH)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 2, 0);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        protected override CreateParams CreateParams
        {
            // CS_DROPSHADOW, plus WS_THICKFRAME so the borderless window can be dragged by its edges.
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; cp.Style = (cp.Style | 0x40000) & ~0xC00000; cp.ExStyle |= 0x02000000; /* WS_EX_COMPOSITED: no child flicker */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyBackdrop();
        }

        void ApplyBackdrop()
        {
            if (!IsHandleCreated) return;
            Blur.Disable(Handle); // opaque charcoal like System Settings
        }

        // Switches the whole window between the dark (acrylic) and light (opaque) palettes and rebuilds the page.
        public void ApplyTheme(bool dark)
        {
            SetUi.Apply(dark);
            BackColor = SetUi.Content;
            searchHost.BackColor = SetUi.Side;
            search.BackColor = SetUi.SearchFill;
            search.ForeColor = searchPlaceholder ? SetUi.Dim : SetUi.Text;
            side.BackColor = SetUi.Side;
            viewport.BackColor = SetUi.Content;
            body.BackColor = SetUi.Content;
            ApplyBackdrop();
            BuildPage(current);
            searchHost.Invalidate();
            side.Invalidate();
            Invalidate(true);
        }

        public static void ReapplyTheme()
        {
            if (instance != null && !instance.IsDisposed && instance.IsHandleCreated)
                instance.BeginInvoke((Action)(() => instance.ApplyTheme(SetUi.WantDark()))); // after the clicked row's handler returns
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (side == null || viewport == null || Width < 100) return;
            Region = new Region(SetUi.Round(new RectangleF(0, 0, Width, Height), 22));
            side.SetBounds(Grip, 100, SideW - Grip, Height - 100 - Grip);
            viewport.SetBounds(SideW, TopH, Width - SideW - Grip, Height - TopH - Grip);
            Invalidate();
        }

        [StructLayout(LayoutKind.Sequential)] struct WINPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAX { public Point ptReserved; public Size ptMaxSize; public Point ptMaxPosition; public Size ptMinTrackSize; public Size ptMaxTrackSize; }

        // The screen's work area, minus the dock when it sits at the bottom: Settings never covers the dock.
        Rectangle Avail() { return Avail(Screen.FromControl(this)); }
        Rectangle Avail(Screen scr)
        {
            Rectangle wa = scr.WorkingArea;
            DockForm dock = DockForm.Instance;
            if (dock != null && !dock.IsDisposed && dock.Visible)
            {
                Rectangle db = dock.Bounds;
                if (db.IntersectsWith(wa) && db.Top > wa.Top + wa.Height / 2) wa.Height = Math.Max(300, db.Top - wa.Top);
            }
            return wa;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0083&& m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; } // WM_NCCALCSIZE: no frame
            if (m.Msg == 0x0086) { m.Result = (IntPtr)1; return; } // WM_NCACTIVATE: skip the frame repaint that draws a title bar
            if (m.Msg == 0x0085) { m.Result = IntPtr.Zero; return; } // WM_NCPAINT: never draw a caption/frame
            if (m.Msg == 0x0046 && WindowState == FormWindowState.Normal) // WM_WINDOWPOSCHANGING: stay above the dock
            {
                var wp = (WINPOS)Marshal.PtrToStructure(m.LParam, typeof(WINPOS));
                if ((wp.flags & 0x0002) == 0 || (wp.flags & 0x0001) == 0) // not both NOMOVE and NOSIZE
                {
                    Rectangle av = Avail();
                    int x = wp.x, y = wp.y, cx = wp.cx, cy = wp.cy;
                    if ((wp.flags & 0x0001) != 0) { cx = Width; cy = Height; } // NOSIZE: keep the current size
                    if (cy > av.Height) cy = av.Height;
                    if (cx > av.Width) cx = av.Width;
                    if (y + cy > av.Bottom) y = av.Bottom - cy;
                    if (y < av.Top) y = av.Top;
                    if (wp.x != x || wp.y != y || wp.cx != cx || wp.cy != cy)
                    {
                        if ((wp.flags & 0x0002) == 0 || (wp.flags & 0x0001) == 0) { wp.x = x; wp.y = y; wp.flags &= ~0x0002u; }
                        if ((wp.flags & 0x0001) == 0) { wp.cx = cx; wp.cy = cy; }
                        Marshal.StructureToPtr(wp, m.LParam, false);
                    }
                }
            }
            if (m.Msg == 0x0024) // WM_GETMINMAXINFO: maximizing fills the area above the dock, not the screen
            {
                base.WndProc(ref m);
                Rectangle av = Avail(), mon = Screen.FromControl(this).Bounds;
                var mm = (MINMAX)Marshal.PtrToStructure(m.LParam, typeof(MINMAX));
                mm.ptMaxPosition = new Point(av.Left - mon.Left, av.Top - mon.Top);
                mm.ptMaxSize = new Size(av.Width, av.Height);
                mm.ptMaxTrackSize = new Size(av.Width, av.Height);
                Marshal.StructureToPtr(mm, m.LParam, false);
                return;
            }
            if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal) // WM_NCHITTEST
            {
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                bool l = p.X < Grip, r = p.X >= Width - Grip, t = p.Y < Grip, b = p.Y >= Height - Grip;
                int hit = 0;
                if (t && l) hit = 13; else if (t && r) hit = 14; else if (b && l) hit = 16; else if (b && r) hit = 17;
                else if (l) hit = 10; else if (r) hit = 11; else if (t) hit = 12; else if (b) hit = 15;
                if (hit != 0) { m.Result = (IntPtr)hit; return; }
            }
            base.WndProc(ref m);
        }

        // Mouse wheel scrolls the page wherever the pointer is over the window.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || !Visible || !Bounds.Contains(Cursor.Position)) return false;
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (side.RectangleToScreen(side.ClientRectangle).Contains(Cursor.Position)) { side.Scroll(-delta * 60 / 120); return true; }
            Wheel(delta);
            return true;
        }

        // ----- scroll benchmark (MacDock.exe --scroll-bench <out.txt>) -----
        public static bool NoSnap;
        static readonly List<double> bGap = new List<double>(), bCost = new List<double>(), bPaint = new List<double>();
        static bool benching;

        internal static void SnapPaintCost(double ms) { if (benching) bPaint.Add(ms); }

        static string Stats(string name, List<double> v, double refMs)
        {
            if (v.Count == 0) return name + ": n=0";
            var s = new List<double>(v); s.Sort();
            double mean = 0; foreach (double x in s) mean += x; mean /= s.Count;
            int late = 0; foreach (double x in s) if (x > refMs * 1.5) late++;
            return string.Format("{0}: n={1} mean={2:0.00} p50={3:0.00} p95={4:0.00} p99={5:0.00} max={6:0.00} late(>1.5xrefresh)={7:0.0}%",
                name, s.Count, mean, s[s.Count / 2], s[(int)(s.Count * 0.95)], s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))], s[s.Count - 1], 100.0 * late / s.Count);
        }

        public static int ScrollBench(string outPath)
        {
            var lines = new List<string>();
            Application.EnableVisualStyles();
            ShowSettings();
            var f = instance;
            Action<int> pump = ms => { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(1); } };
            pump(600);
            var dm = new VsyncDriver(f, () => { });
            double refMs = dm.Dt * 1000.0; dm.Dispose();
            lines.Add("refresh interval ms: " + refMs.ToString("0.00") + " (" + (1000.0 / refMs).ToString("0") + " Hz); window " + f.Width + "x" + f.Height);
            benching = true;
            foreach (bool nosnap in new[] { true, false })
                foreach (string page in new[] { "general", "appearance", "dock", "menubar", "launchpad", "icons" })
                {
                    NoSnap = nosnap;
                    f.GoTo(page, false); pump(500);
                    bGap.Clear(); bCost.Clear(); bPaint.Clear();
                    // a fast flick: 5 wheel notches per 16 ms frame-ish for ~1.2 s, then down/up
                    for (int pass = 0; pass < 2; pass++)
                    {
                        int sign = pass == 0 ? -120 : 120;
                        for (int i = 0; i < 40; i++) { f.Wheel(sign); pump(30); }
                        pump(900);
                    }
                    lines.Add("");
                    lines.Add("== page=" + page + " body=" + f.body.Height + "px snapshot=" + (!nosnap));
                    lines.Add(Stats("  frame gap ms ", bGap, refMs));
                    lines.Add(Stats("  step cost ms ", bCost, refMs));
                    lines.Add(Stats("  snap paint ms", bPaint, refMs));
                }
            benching = false;
            f.Height = 560; pump(400);
            SetSide.BenchOn = true; SetSide.BGap.Clear(); SetSide.BPaint.Clear();
            for (int pass = 0; pass < 2; pass++) { for (int i = 0; i < 40; i++) { f.side.Scroll(pass == 0 ? 60 : -60); pump(30); } pump(900); }
            SetSide.BenchOn = false;
            lines.Add(""); lines.Add("== sidebar content=" + f.side.BenchTotal + "px view=" + f.side.Height + "px");
            lines.Add(Stats("  frame gap ms ", SetSide.BGap, refMs)); lines.Add(Stats("  paint ms     ", SetSide.BPaint, refMs));
            File.WriteAllLines(outPath, lines.ToArray());
            f.Close();
            return 0;
        }

        internal void Wheel(int delta)
        {
            // Accumulate fractional (touchpad) deltas and glide to the target instead of jumping a notch per tick.
            scrollTarget -= delta * 56f / 120f;
            ClampTarget();
            if (vsync == null)
                vsync = new VsyncDriver(this, () =>
                {
                    var cw = Stopwatch.StartNew();
                    double raw = vsyncClock.Next();
                    if (benching) bGap.Add(raw * 1000.0);
                    double dt = vsync.Frame(raw);
                    double d = scrollTarget - scrollF;
                    if (Math.Abs(d) < 0.5) { scrollF = scrollTarget; vsync.Sleep(); }
                    else scrollF += (float)(d * (1 - Math.Pow(0.7, dt * 1000.0 / 8.0))); // frame-rate independent ease
                    scroll = (int)Math.Round(scrollF);
                    ApplyScroll();
                    if (!vsync.Active) EndSnap(); // glide finished: put the real controls back
                    if (benching) { bCost.Add(cw.Elapsed.TotalMilliseconds); }
                });
            if (!vsync.Active) { scrollF = scroll; BeginSnap(); vsyncClock.Reset(); vsync.Wake(); }
        }

        SnapView snap;

        void BeginSnap()
        {
            EndSnap();
            int w = body.Width, h = body.Height;
            if (NoSnap || w < 1 || h <= viewport.Height || h > 14000) return;
            try
            {
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                body.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                snap = new SnapView { Bmp = bmp, Off = scroll, Bounds = viewport.ClientRectangle };
                viewport.Controls.Add(snap);
                snap.BringToFront();
                snap.Update();
            }
            catch { EndSnap(); }
        }

        void EndSnap()
        {
            if (snap == null) return;
            var s = snap; snap = null;
            body.Top = -scroll;
            body.Update();
            viewport.Controls.Remove(s);
            s.Dispose();
        }

        float scrollF, scrollTarget;
        VsyncDriver vsync; readonly FrameClock vsyncClock = new FrameClock();

        void ClampTarget()
        {
            float max = Math.Max(0, body.Height - viewport.Height);
            if (scrollTarget < 0) scrollTarget = 0;
            if (scrollTarget > max) scrollTarget = max;
        }

        // ----- page body -----

        void ClearBody()
        {
            HotkeyHook.Capture = null; // never leave key recording on across pages
            foreach (Control c in flow) c.Dispose();
            flow.Clear();
            body.Controls.Clear();
            scroll = 0; scrollF = 0; scrollTarget = 0;
            if (vsync != null) vsync.Sleep();
            EndSnap();
        }

        void Relayout()
        {
            EndSnap();
            int w = viewport.Width - 2 * Margin;
            int y = 6;
            for (int i = 0; i < flow.Count; i++)
            {
                Control c = flow[i];
                string kind = c.Tag as string;
                if (kind == "head") y += (i == 0 ? 0 : 26);
                int h = c.Height;
                if (kind == "grid") h = ((IFlowGrid)c).HeightFor(w);
                if (kind == "note")
                    h = SetText.MeasureText(c.Text, c.Font, new Size(w - 8, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 6;
                c.SetBounds(Margin, y, w, h);
                y += h + (kind == "head" ? 12 : 16);
            }
            body.Height = y + 16;
            body.Width = viewport.Width;
            ApplyScroll();
        }

        void ApplyScroll()
        {
            int max = Math.Max(0, body.Height - viewport.Height);
            if (scroll < 0) scroll = 0;
            if (scroll > max) scroll = max;
            ClampTarget();
            if (snap != null) { if (snap.Off != scroll) { snap.Off = scroll; snap.Invalidate(); snap.Update(); /* paint right after the vsync wake so it lands in the next DWM frame */ } return; }
            if (body.Top != -scroll) body.Top = -scroll;
        }

        void Head(string text)
        {
            var l = new CrispLabel();
            l.Text = text; l.Tag = "head"; l.AutoSize = false; l.Height = 30; l.UseMnemonic = false;
            l.Font = Typography.Text(12.5f, Weight.Semibold); l.ForeColor = SetUi.Text; l.BackColor = SetUi.Content;
            Add(l);
        }

        Label Note(string text)
        {
            var l = new CrispLabel();
            l.Text = text; l.Tag = "note"; l.AutoSize = false; l.UseMnemonic = false;
            l.Font = Typography.Secondary(); l.ForeColor = SetUi.Dim; l.BackColor = SetUi.Content;
            Add(l);
            return l;
        }

        // ----- live system controls (Display & Sound, Network & Bluetooth) -----

        Label statusNote;

        void SetStatus(string text)
        {
            if (statusNote == null || statusNote.IsDisposed) return;
            statusNote.ForeColor = string.IsNullOrEmpty(text) ? SetUi.Dim : Color.FromArgb(255, 159, 10);
            statusNote.Text = text ?? "";
            Relayout();
        }

        void OnUi(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        // Toggle that really changes something: state == null means unavailable (shown disabled). apply runs off the UI
        // thread; if it returns false the switch snaps back and failMsg is shown.
        SetRow ToggleAsync(SetCard card, string label, bool? state, Func<bool, bool> apply, string failMsg, Action afterOk)
        {
            var row = new SetRow(label);
            var t = new SetToggle();
            t.On = state == true;
            t.Enabled = state.HasValue;
            t.Changed += (s, e) =>
            {
                bool want = t.On;
                t.Enabled = false;
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool ok = false;
                    try { ok = apply(want); } catch { }
                    OnUi(() =>
                    {
                        if (t.IsDisposed) return;
                        t.Enabled = true;
                        if (!ok) { t.On = !want; SetStatus(failMsg); }
                        else { SetStatus(""); if (afterOk != null) afterOk(); }
                    });
                });
            };
            row.SetWidget(t);
            return card.Add(row);
        }

        SetRow SliderRow(SetCard card, string label, double value, bool enabled, Action<double> changed)
        {
            var row = new SetRow(label);
            var s = new SetSlider();
            s.Value = value;
            s.Enabled = enabled;
            s.Changed += changed;
            row.SetWidget(s);
            return card.Add(row);
        }

        void DisplayControls()
        {
            Head("Display");
            SetCard c = NewCard();
            int br = Brightness.Get();
            SliderRow(c, "Brightness", br < 0 ? 0.5 : br / 100.0, br >= 0, v => Brightness.SetLatest((int)Math.Round(v * 100)));
            bool? night = NightLight.IsOn();
            if (night.HasValue) ToggleAsync(c, "Night light", night, on => NightLight.Set(on), "Windows refused the Night light change.", null);
            else Open(c, "Night light", "ms-settings:nightlight");
            if (br < 0) Note("No adjustable display found. Windows only exposes brightness for built-in laptop panels, so external monitors can't be dimmed from here.");

            Head("Sound");
            c = NewCard();
            float vol = Audio.Volume;
            bool hasAudio = vol >= 0;
            SetToggle muteT = null;
            SliderRow(c, "Output volume", hasAudio ? vol : 0, hasAudio, v =>
            {
                Audio.Volume = (float)v;
                if (muteT != null && muteT.On) { Audio.Muted = false; muteT.On = false; }
            });
            SetRow muteRow = Toggle(c, "Mute", hasAudio && Audio.Muted, v => Audio.Muted = v);
            muteT = (SetToggle)muteRow.Widget;
            muteT.Enabled = hasAudio;
            if (!hasAudio) Note("No audio output device found.");
        }

        void NetworkControls()
        {
            Head("Wireless");
            SetCard c = NewCard();
            bool? wifi = DeviceFeed.RadioOn("WiFi"), bt = DeviceFeed.RadioOn("Bluetooth");
            Action refresh = () => { if (current == "sys-network") BuildPage("sys-network"); };
            const string refused = " Windows refused the change. It may need administrator rights, or a policy or hardware switch is blocking it.";
            ToggleAsync(c, "Wi-Fi", wifi, on => DeviceFeed.SetRadio("WiFi", on), "Couldn't change Wi-Fi." + refused, refresh);
            ToggleAsync(c, "Bluetooth", bt, on => DeviceFeed.SetRadio("Bluetooth", on), "Couldn't change Bluetooth." + refused, refresh);
            bool? air = wifi.HasValue || bt.HasValue ? (bool?)(wifi != true && bt != true) : null;
            ToggleAsync(c, "Airplane mode", air, on =>
            {
                bool ok = true;
                if (wifi.HasValue) ok &= DeviceFeed.SetRadio("WiFi", !on);
                if (bt.HasValue) ok &= DeviceFeed.SetRadio("Bluetooth", !on);
                return ok;
            }, "Couldn't change Airplane mode." + refused, refresh);
            string missing = (wifi == null ? "No Wi-Fi radio found on this PC. " : "") + (bt == null ? "No Bluetooth radio found on this PC." : "");
            statusNote = Note(missing.Trim());
            if (missing.Length == 0) Note("Airplane mode turns the Wi-Fi and Bluetooth radios off together, and back on again.");
        }

        // Picker row; apply runs off the UI thread and returns false if Windows refused (the picker then reverts).
        SetRow PickerRow(SetCard card, string label, string[] options, int index, Func<int, bool> apply, string failMsg)
        {
            var row = new SetRow(label);
            var pk = new SetPicker();
            pk.Options = options;
            pk.Index = index;
            pk.Enabled = index >= 0;
            int prev = index;
            pk.Changed += n =>
            {
                int old = prev;
                prev = n;
                pk.Enabled = false;
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool ok = false;
                    try { ok = apply(n); } catch { }
                    OnUi(() =>
                    {
                        if (pk.IsDisposed) return;
                        pk.Enabled = true;
                        if (!ok) { prev = old; pk.Index = old; SetStatus(failMsg); }
                        else SetStatus("");
                    });
                });
            };
            row.SetWidget(pk);
            return card.Add(row);
        }

        static readonly int[] TimeoutMinutes = { 0, 1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 120, 180, 240, 300 };

        static string TimeoutText(int m)
        {
            if (m == 0) return "Never";
            if (m < 60) return m == 1 ? "1 minute" : m + " minutes";
            return m % 60 == 0 ? (m / 60 == 1 ? "1 hour" : m / 60 + " hours") : (m / 60) + " h " + (m % 60) + " min";
        }

        // Option list for a timeout picker; a value set elsewhere (say 7 minutes) is slotted in so it shows as selected.
        static string[] TimeoutOptions(int current, out List<int> values)
        {
            values = new List<int>(TimeoutMinutes);
            if (current >= 0 && !values.Contains(current)) { values.Add(current); values.Sort(); }
            return values.Select(TimeoutText).ToArray();
        }

        void TimeoutRow(SetCard card, string label, int current, Func<int, bool> set)
        {
            List<int> values;
            string[] opts = TimeoutOptions(current, out values);
            PickerRow(card, label, opts, current < 0 ? -1 : values.IndexOf(current), i => set(values[i]), "Windows refused the change to \"" + label + "\".");
        }

        void PowerControls()
        {
            SystemInfo.UpdateFast();
            const string refused = " Windows refused the change, which may need administrator rights or be blocked by policy.";
            Head("Battery");
            SetCard c = NewCard();
            bool battery = SystemInfo.HasBattery;
            Action refresh = () => { if (current == "sys-power") BuildPage("sys-power"); };
            if (!battery) Info(c, "Battery", "None detected (desktop PC)");
            else
            {
                string state = SystemInfo.Charging ? "Charging" : (SystemInfo.OnAc ? "Plugged in" : "On battery");
                Info(c, "Battery level", SystemInfo.BatteryPercent + "%  -  " + state);
                if (!SystemInfo.OnAc && SystemInfo.BatterySecondsLeft > 0)
                    Info(c, "Time remaining", (SystemInfo.BatterySecondsLeft / 3600) + " h " + (SystemInfo.BatterySecondsLeft % 3600 / 60) + " min");
                Info(c, "Battery Saver now", SetSys.BatterySaverOn() ? "On" : "Off");
                int lvl = SetSys.BatterySaverLevel();
                ToggleAsync(c, "Turn on Battery Saver automatically", lvl < 0 ? (bool?)null : lvl > 0, on => SetSys.SetBatterySaverLevel(on ? 20 : 0), "Couldn't change Battery Saver." + refused, refresh);
                int[] levels = { 0, 10, 20, 30, 40, 50 };
                int li = Array.IndexOf(levels, lvl);
                string[] lo = levels.Select(l => l == 0 ? "Never" : l + "%").ToArray();
                if (lvl >= 0 && li < 0) { lo = lo.Concat(new[] { lvl + "%" }).ToArray(); li = lo.Length - 1; }
                PickerRow(c, "Turn on Battery Saver at", lo, lvl < 0 ? -1 : li, i => SetSys.SetBatterySaverLevel(i < levels.Length ? levels[i] : lvl), "Couldn't change the Battery Saver level." + refused);
                if (lvl < 0) Note("This PC doesn't expose Battery Saver's automatic level, so it is shown read-only.");
            }

            Head("Power mode");
            c = NewCard();
            int mode = SetSys.PowerMode();
            PickerRow(c, "Power mode", SetSys.OverlayNames, mode, i => SetSys.SetPowerMode(i), "Couldn't change the power mode." + refused);

            Head("Screen and sleep");
            c = NewCard();
            TimeoutRow(c, battery ? "Turn screen off - on battery" : "Turn screen off", SetSys.ScreenOffMinutes(false), m => SetSys.SetScreenOff(false, m));
            TimeoutRow(c, "Turn screen off - plugged in", SetSys.ScreenOffMinutes(true), m => SetSys.SetScreenOff(true, m));
            TimeoutRow(c, battery ? "Sleep - on battery" : "Sleep", SetSys.SleepMinutes(false), m => SetSys.SetSleep(false, m));
            TimeoutRow(c, "Sleep - plugged in", SetSys.SleepMinutes(true), m => SetSys.SetSleep(true, m));
            statusNote = Note("");
        }

        // ----- wallpapers -----

        void WallpaperControls()
        {
            Head("Wallpaper");
            string cur = SetSys.GetWallpaper();
            var grid = new SetWallGrid();
            grid.Tag = "grid";
            int fromPreset = SetSys.PresetOf(cur);
            bool hasCurrent = fromPreset < 0 && cur.Length > 0 && File.Exists(cur);
            if (hasCurrent)
            {
                var cell = new SetWallGrid.Cell { Title = "Current" };
                try
                {
                    using (var img = Image.FromFile(cur))
                    {
                        var t = new Bitmap(328, 206);
                        using (var g = Graphics.FromImage(t)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, t.Width, t.Height); }
                        cell.Thumb = t;
                    }
                }
                catch { }
                grid.Cells.Add(cell);
            }
            int first = grid.Cells.Count;
            for (int i = 0; i < SetSys.PresetCount; i++)
                grid.Cells.Add(new SetWallGrid.Cell { Title = SetSys.PresetName(i), Thumb = SetSys.Render(i, 328, 206) });
            grid.Selected = fromPreset >= 0 ? first + fromPreset : (hasCurrent ? 0 : -1);
            grid.Picked += idx =>
            {
                if (idx < first) return; // "Current" is already applied
                int preset = idx - first;
                ApplyWallpaper(() => SetSys.EnsureFile(preset));
            };
            Add(grid);

            SetCard c = NewCard();
            PickerRow(c, "Picture style", SetSys.StyleNames, SetSys.StyleIndex(), i => SetSys.SetStyle(i), "Couldn't change the picture style.");
            Link(c, "Choose from file...", null, () =>
            {
                using (var d = new OpenFileDialog())
                {
                    d.Title = "Choose a wallpaper";
                    d.Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files (*.*)|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) { string f = d.FileName; ApplyWallpaper(() => f); }
                }
            });
            statusNote = Note(cur.Length == 0 ? "No wallpaper is set." : "");
        }

        void ApplyWallpaper(Func<string> makePath)
        {
            SetStatus("Applying wallpaper...");
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = false;
                try { ok = SetSys.SetWallpaper(makePath()); } catch { }
                OnUi(() =>
                {
                    if (!ok) SetStatus("Couldn't set the wallpaper. Windows refused it or the file couldn't be read.");
                    else if (current == "sys-personal") BuildPage("sys-personal");
                });
            });
        }

        SetCard NewCard() { var c = new SetCard(); c.Tag = "card"; Add(c); return c; }

        void Add(Control c) { flow.Add(c); body.Controls.Add(c); }

        SetRow Toggle(SetCard card, string label, bool value, Action<bool> changed)
        {
            var row = new SetRow(label);
            var t = new SetToggle();
            t.On = value;
            t.Changed += (s, e) => changed(t.On);
            row.SetWidget(t);
            return card.Add(row);
        }

        SetRow Link(SetCard card, string label, string value, Action click)
        {
            var row = new SetRow(label);
            row.Value = value; row.Chevron = true; row.Click = click; row.Cursor = Cursors.Hand;
            return card.Add(row);
        }

        SetRow Info(SetCard card, string label, string value)
        {
            var row = new SetRow(label);
            row.Value = value;
            return card.Add(row);
        }

        SetRow Field(SetCard card, string label, string value, string hint, bool secret, Action<string> commit)
        {
            var row = new SetRow(label);
            var tb = new TextBox();
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = SetUi.Card; tb.ForeColor = SetUi.Text;
            tb.Font = Typography.Body();
            tb.TextAlign = HorizontalAlignment.Right;
            tb.Width = 260;
            tb.UseSystemPasswordChar = secret;
            tb.Text = value ?? "";
            string last = tb.Text;
            Action save = () => { string v = tb.Text.Trim(); if (v != last) { last = v; commit(v); } };
            tb.Leave += (s, e) => save();
            tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; save(); card.Focus(); } };
            row.SetWidget(tb);
            if (!string.IsNullOrEmpty(hint)) row.Value = null;
            return card.Add(row);
        }

        void Open(SetCard card, string label, string uri)
        {
            Link(card, label, null, () => SetUi.OpenTarget(uri));
        }

        void RestartRow(SetCard card)
        {
            Link(card, "Restart MacDock now", null, SetUi.RestartApp);
        }

        // A row that records a key combo: click it, press the shortcut (Esc cancels). Saved as "@<cfgKey> = Win+Tab".
        Action cancelCapture;
        readonly System.Windows.Forms.Timer captureTimer = new System.Windows.Forms.Timer();

        void CancelCapture()
        {
            captureTimer.Stop();
            if (HotkeyHook.Capture != null) HotkeyHook.Capture = null;
            if (cancelCapture != null) { var a = cancelCapture; cancelCapture = null; a(); }
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); CancelCapture(); }

        static readonly string[][] HotkeySlots =
        {
            new[] { "hotkey-spotlight", HotkeyHook.DefaultSpotlight, "Search (Spotlight)" },
            new[] { "hotkey-launchpad", HotkeyHook.DefaultLaunchpad, "Launchpad" }
        };

        void HotkeyRow(SetCard card, string label, string cfgKey, string def)
        {
            var row = Link(card, label, SetCfg.Get(cfgKey) ?? def, null);
            row.Click = () =>
            {
                CancelCapture();
                row.Value = "Press shortcut... (Esc cancels)";
                row.Invalidate();
                cancelCapture = () => { row.Value = SetCfg.Get(cfgKey) ?? def; row.Invalidate(); };
                captureTimer.Interval = 8000;
                captureTimer.Start();
                HotkeyHook.Capture = (mods, vk) =>
                {
                    HotkeyHook.Capture = null;
                    captureTimer.Stop();
                    cancelCapture = null;
                    if (vk == 0x1B && mods == 0) { row.Value = SetCfg.Get(cfgKey) ?? def; row.Invalidate(); return; }
                    string text = HotkeyHook.Format(mods, vk);
                    int m, k;
                    if (!HotkeyHook.TryParse(text, out m, out k)) { row.Value = SetCfg.Get(cfgKey) ?? def; row.Invalidate(); MessageBox.Show("Use at least one modifier key (Win, Ctrl, Alt or Shift) with the shortcut.", "MacDock"); return; }
                    // Refuse a combo another shortcut already uses.
                    foreach (string[] other in HotkeySlots)
                    {
                        if (other[0] == cfgKey) continue;
                        int om, ok;
                        if (HotkeyHook.TryParse(SetCfg.Get(other[0]) ?? other[1], out om, out ok) && om == m && ok == k)
                        {
                            row.Value = SetCfg.Get(cfgKey) ?? def; row.Invalidate();
                            MessageBox.Show(text + " is already used by " + other[2] + ". Pick a different combination.", "MacDock");
                            return;
                        }
                    }
                    SetCfg.Set(cfgKey, text, true);
                    HotkeyHook.Reload();
                    row.Value = text;
                    row.Invalidate();
                };
            };
            Link(card, "Reset " + label + " shortcut", def, () => { CancelCapture(); SetCfg.Set(cfgKey, null, true); HotkeyHook.Reload(); BuildPage(current); });
        }

        // ----- backup & restore -----

        static void WriteBackup(string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (ZipArchive z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (File.Exists(AppPaths.Config)) z.CreateEntryFromFile(AppPaths.Config, "dock.txt");
                if (File.Exists(AppPaths.IconMap)) z.CreateEntryFromFile(AppPaths.IconMap, "iconmap.txt");
                if (Directory.Exists(AppPaths.IconsDir))
                    foreach (string f in Directory.GetFiles(AppPaths.IconsDir, "*", SearchOption.AllDirectories))
                        z.CreateEntryFromFile(f, "icons/" + f.Substring(AppPaths.IconsDir.Length).TrimStart('\\', '/').Replace('\\', '/'));
            }
        }

        void DoBackup()
        {
            using (var d = new SaveFileDialog())
            {
                d.Title = "Back up MacDock settings";
                d.Filter = "Zip archive (*.zip)|*.zip";
                d.FileName = "MacDock-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    WriteBackup(d.FileName);
                    MessageBox.Show("Backup saved to\n" + d.FileName, "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("Backup failed:\n" + ex.Message, "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        static string SafeDestination(string name)
        {
            name = name.Replace('\\', '/');
            if (name == "dock.txt") return AppPaths.Config;
            if (name == "iconmap.txt") return AppPaths.IconMap;
            if (!name.StartsWith("icons/", StringComparison.OrdinalIgnoreCase) || name.EndsWith("/")) return null;
            string full = Path.GetFullPath(Path.Combine(AppPaths.IconsDir, name.Substring(6)));
            string root = Path.GetFullPath(AppPaths.IconsDir).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null; // never write outside the icons folder
        }

        void DoRestore()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Restore MacDock settings";
                d.Filter = "Zip archive (*.zip)|*.zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var plan = new List<KeyValuePair<ZipArchiveEntry, string>>();
                    using (ZipArchive z = ZipFile.OpenRead(d.FileName))
                    {
                        foreach (ZipArchiveEntry en in z.Entries)
                        {
                            string dest = SafeDestination(en.FullName);
                            if (dest != null) plan.Add(new KeyValuePair<ZipArchiveEntry, string>(en, dest));
                        }
                        if (plan.Count == 0) { MessageBox.Show("That zip has no MacDock settings (dock.txt, iconmap.txt or icons/).", "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                        string safety = Path.Combine(AppPaths.DataDir, "backups", "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                        Directory.CreateDirectory(Path.GetDirectoryName(safety));
                        WriteBackup(safety);

                        foreach (var item in plan)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(item.Value));
                            item.Key.ExtractToFile(item.Value, true);
                        }
                        HotkeyHook.Reload();
                        if (MessageBox.Show("Restored " + plan.Count + " file(s). Your previous files were saved to\n" + safety + "\n\nRestart MacDock now to apply everything?", "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            SetUi.RestartApp();
                        else ApplyTheme(SetUi.WantDark());
                    }
                }
                catch (Exception ex) { MessageBox.Show("Restore failed:\n" + ex.Message, "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        static Action Confirm(string question, Action action)
        {
            return () => { if (MessageBox.Show(question, "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) action(); };
        }

        const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        void BuildPage(string key)
        {
            ClearBody();
            current = key;
            SetCard c;
            switch (key)
            {
                case "general":
                    Head("Startup");
                    c = NewCard();
                    Toggle(c, "Open MacDock at login", SetUi.GetAutostart(), SetUi.SetAutostart);
                    Head("Application");
                    c = NewCard();
                    RestartRow(c);
                    Link(c, "Force stop MacDock", null, () =>
                    {
                        if (MessageBox.Show("Stop MacDock completely?\n\nThe dock will close and will not come back on its own until you start MacDock yourself.", "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            SetUi.ForceStop();
                    });
                    Head("Files");
                    c = NewCard();
                    Link(c, "Configuration file", "dock.txt", () => Process.Start("notepad.exe", "\"" + AppPaths.Config + "\""));
                    Link(c, "Open data folder", null, () => SetUi.OpenTarget(AppPaths.DataDir));
                    Note("Settings are saved to dock.txt in " + AppPaths.DataDir + ". Dock pins, the icon map and downloaded icons live there too.");
                    break;

                case "tray":
                    Head("Tray icons shown in the menu bar");
                    c = NewCard();
                    {
                        var traySel = TrayIcons.Selected();
                        var trayItems = TrayIcons.Cached;
                        foreach (TrayItem ti in trayItems)
                        {
                            TrayItem t2 = ti;
                            Toggle(c, t2.Title + (t2.Hidden ? "  (hidden)" : ""), traySel.Contains(t2.Key), v => { TrayIcons.SetSelected(t2.Key, v); });
                        }
                        if (trayItems.Count == 0) Note("No tray icons scanned yet. Press Scan tray icons.");
                        Link(c, "Scan tray icons (briefly opens the hidden-icons flyout)", null, () => TrayIcons.Scan(() => OnUi(() => BuildPage(current))));
                    }
                    break;

                case "profile":
                    Head("Avatar");
                    var grid = new SetAvatarGrid();
                    grid.Tag = "grid";
                    grid.Changed += () => side.Invalidate();
                    Add(grid);
                    Note("Pick an avatar. It is shown at the top of the sidebar and saved as @avatar in dock.txt.");
                    Head("Backup & Restore");
                    c = NewCard();
                    Link(c, "Back up settings...", null, DoBackup);
                    Link(c, "Restore from backup...", null, DoRestore);
                    Note("A backup is a .zip of dock.txt, iconmap.txt and the icons folder from " + AppPaths.DataDir + ". Restoring first saves your current files to a backups folder there, then offers to restart MacDock.");
                    break;

                case "appearance":
                    Head("Theme");
                    c = NewCard();
                    string theme = SetUi.ThemeSetting();
                    Toggle(c, "Match Windows appearance", theme == "system", v => { SetCfg.Set("theme", v ? "system" : (SetUi.Dark ? "dark" : "light"), false); ReapplyTheme(); });
                    Toggle(c, "Dark mode", SetUi.Dark, v => { SetCfg.Set("theme", v ? "dark" : "light", false); DarkMode.Set(v); ReapplyTheme(); });
                    Note("Dark mode also switches Windows apps to dark or light. This window, its sidebar icons and the colours follow it. Saved as @theme dark, light or system in dock.txt.");
                    Head("Typography");
                    c = NewCard();
                    Field(c, "Font", SetCfg.Get("font"), null, false, v => SetCfg.Set("font", v.Length == 0 ? null : v, true));
                    string shown = SetUi.HasFont("Inter") ? "Inter" : (SetUi.HasFont("Segoe UI Variable Text") ? "Segoe UI Variable" : "Segoe UI");
                    Info(c, "Default typeface", shown);
                    RestartRow(c);
                    Note("Leave Font empty to use Inter, then Segoe UI Variable. Enter any installed font name. Restart MacDock to apply.");
                    break;

                case "dock":
                    Head("Dock");
                    {
                        var prev = new SetDockPreview();
                        prev.Tag = "card";
                        prev.DockSize = DockForm.CurrentSize; prev.Magnify = DockForm.CurrentMagnify;
                        Add(prev);
                        double lastMag = DockForm.CurrentMagnify > 1.0 ? DockForm.CurrentMagnify : 1.75;
                        double sizeSpan = DockForm.MaxSize - DockForm.MinSize, magLo = 1.2, magSpan = DockForm.MaxMagnify - 1.2;
                        SetCard dc = NewCard();
                        SetSlider magSlider = null;
                        Action<int, double> push = (sz, mg) => { prev.DockSize = sz; prev.Magnify = mg; prev.Invalidate(); DockForm.Instance.ApplyDockSize(sz, mg); };
                        SliderRow(dc, "Size", (DockForm.CurrentSize - DockForm.MinSize) / sizeSpan, true,
                            v => push((int)Math.Round(DockForm.MinSize + v * sizeSpan), DockForm.CurrentMagnify));
                        bool magOn = DockForm.CurrentMagnify > 1.0 + 0.001;
                        SetRow mt = null;
                        mt = Toggle(dc, "Magnification", magOn, on =>
                        {
                            if (magSlider != null) magSlider.Enabled = on;
                            push(DockForm.CurrentSize, on ? lastMag : 1.0);
                        });
                        SetRow mr = SliderRow(dc, "Magnification amount", Math.Max(0, (lastMag - magLo) / magSpan), magOn, v =>
                        {
                            lastMag = magLo + v * magSpan;
                            push(DockForm.CurrentSize, lastMag);
                        });
                        magSlider = (SetSlider)mr.Widget;
                        Note("Small to large. The preview follows your pointer. These are the same settings as Dock Size and Magnification in the dock's right-click menu.");
                    }
                    c = NewCard();
                    Toggle(c, "Show Dock", SetCfg.GetBool("show-dock", true), v => { SetCfg.SetBool("show-dock", v); DockForm.Instance.ApplyDockMode(); });
                    Toggle(c, "Auto-hide Dock", SetCfg.GetBool("auto-hide-dock", false), v => { SetCfg.SetBool("auto-hide-dock", v); DockForm.Instance.ApplyDockMode(); });
                    Toggle(c, "Show Start icon in Dock", SetCfg.GetBool("show-start-icon", true), v => DockForm.Instance.SetStartIcon(v));
                    Toggle(c, "Genie effect", SetCfg.GetBool("genie", true), v => SetCfg.SetBool("genie", v));
                    Toggle(c, "Open folder fans on click instead of hover", SetCfg.GetBool("fan-click", false), v => SetCfg.SetBool("fan-click", v));
                    Note("With auto-hide off, the dock stays and maximized windows stop above it. With it on, the dock slides away while an app is in front and returns when the pointer reaches the bottom edge. Full-screen apps always hide it.");
                    Head("Windows taskbar");
                    c = NewCard();
                    Toggle(c, "Hide the Windows taskbar while MacDock runs", SetCfg.GetBool("hide-taskbar", true), v => SetCfg.SetBool("hide-taskbar", v));
                    Note("Applies immediately. The taskbar is restored when MacDock quits.");
                    Head("Pinned items");
                    c = NewCard();
                    Link(c, "Add folder to Dock...", null, () => DockForm.Instance.PromptAddFolder());
                    Link(c, "Add app or file to Dock...", null, () => DockForm.Instance.PromptAddApp());
                    Link(c, "Add website to Dock...", null, () => DockForm.Instance.PromptAddWebsite());
                    Link(c, "Edit Dock items...", "dock.txt", () => Process.Start("notepad.exe", "\"" + AppPaths.Config + "\""));
                    Note("Drag an app onto the dock to pin it, or right-click an icon for more options.");
                    break;

                case "menubar":
                    Head("Keyboard shortcuts");
                    c = NewCard();
                    Info(c, "Lock screen", "Win + L");
                    Head("Custom shortcuts");
                    c = NewCard();
                    HotkeyRow(c, "Search (Spotlight)", "hotkey-spotlight", HotkeyHook.DefaultSpotlight);
                    HotkeyRow(c, "Launchpad", "hotkey-launchpad", HotkeyHook.DefaultLaunchpad);
                    Note("Click a shortcut, then press the new combination. It needs a modifier (Win, Ctrl, Alt or Shift). Changes apply at once and are saved as @hotkey-spotlight and @hotkey-launchpad. Two shortcuts can't share a combination.");
                    Head("Menu bar & island");
                    c = NewCard();
                    Toggle(c, "Show Dynamic Island", SetCfg.GetBool("island", true), v => { SetCfg.SetBool("island", v); IslandForm.SetEnabled(v); });
                    Toggle(c, "Show CPU usage in status bar", SetCfg.GetBool("stats-cpu", true), v => SetCfg.SetBool("stats-cpu", v));
                    Toggle(c, "Show RAM usage in status bar", SetCfg.GetBool("stats-ram", true), v => SetCfg.SetBool("stats-ram", v));
                    Toggle(c, "Screenshot in status bar", ScreenCapture.BarShot, v => ScreenCapture.BarShot = v);
                    Toggle(c, "Screen record in status bar", ScreenCapture.BarRec, v => ScreenCapture.BarRec = v);
                    Toggle(c, "Screenshot tile in Control Center", ScreenCapture.TileShot, v => Panels.CcSetMember("shot", v));
                    Toggle(c, "Screen record tile in Control Center", ScreenCapture.TileRec, v => Panels.CcSetMember("rec", v));
                    Head("Windows tray icons in the menu bar");
                    c = NewCard();
                    Toggle(c, "Show tray icons in the menu bar", TrayIcons.BarOn, v => { TrayIcons.BarOn = v; });
                    Toggle(c, "Show the tray arrow", TrayIcons.Arrow, v => { TrayIcons.Arrow = v; });
                    Toggle(c, "Collapsible tray icons (fold into a small handle)", TrayIcons.Collapsible, v => { TrayIcons.Collapsible = v; });
                    Toggle(c, "Keep tray icons collapsed", TrayIcons.Collapsed, v => { TrayIcons.Collapsed = v; });
                    Link(c, "Choose tray icons", TrayIcons.Selected().Count + " shown", () => GoTo("tray", true));
                    Note("Pick which Windows tray icons appear in the menu bar, including the ones Windows hides in its overflow. Click an icon to send a left click to the original app, right-click for its menu. Windows 11 only exposes its tray through the on-screen icons, so scanning briefly opens the hidden-icons flyout; clicking an icon does the same for an instant.");
                    c = NewCard();
                    Link(c, "Open Windows notification settings", null, () => SetUi.OpenTarget("ms-settings:notifications"));
                    Link(c, "Open Focus settings", null, () => SetUi.OpenTarget("ms-settings:quiethours"));
                    Note("The notification island and menu bar items follow your Windows notification and Focus settings.");
                    break;

                case "launchpad":
                    Head("Trackpad gestures");
                    c = NewCard();
                    Toggle(c, "Four- and five-finger swipes", SetCfg.GetBool("gestures", true), v => SetCfg.SetBool("gestures", v));
                    RestartRow(c);
                    Note("Swipe down closes Launchpad. Needs a precision touchpad. Restart MacDock to apply.");
                    Head("Open now");
                    c = NewCard();
                    Link(c, "Launchpad (all apps)", null, () => MissionControl.Toggle(true));
                    break;

                case "icons":
                    Head("Icon pack");
                    c = NewCard();
                    Toggle(c, "Use macOS-style icons", SetCfg.GetBool("icons", true), v => SetCfg.SetBool("icons", v));
                    RestartRow(c);
                    Note("When on, Dock apps that have a matching icon in the icon folder use it. Restart MacDock to apply.");
                    Head("macOS icon downloads");
                    c = NewCard();
                    Field(c, "macosicons.com API key", SetCfg.Get("macosicons-key"), null, true, v => SetCfg.Set("macosicons-key", v.Length == 0 ? null : v, true));
                    Link(c, "Find macOS icons for Dock apps...", null, () => IconFetcher.FindIcons(DockForm.Instance.AppsWithoutMacIcon()));
                    Link(c, "Open icons folder", null, () => { try { Directory.CreateDirectory(AppPaths.IconsDir); } catch { } SetUi.OpenTarget(AppPaths.IconsDir); });
                    break;

                case "sys-personal":
                    BuildSystemLinks(key);
                    break;

                case "sys-files":
                    Head("File Explorer");
                    c = NewCard();
                    Toggle(c, "Show file name extensions", SetUi.RegInt(Advanced, "HideFileExt", 1) == 0, v => { SetUi.RegSet(Advanced, "HideFileExt", v ? 0 : 1); SetUi.BroadcastSettingChange("ShellState"); });
                    Toggle(c, "Show hidden files and folders", SetUi.RegInt(Advanced, "Hidden", 2) == 1, v => { SetUi.RegSet(Advanced, "Hidden", v ? 1 : 2); SetUi.BroadcastSettingChange("ShellState"); });
                    Note("Open Explorer windows pick this up after a refresh (F5).");
                    Head("More");
                    c = NewCard();
                    Link(c, "Folder options", null, () => Process.Start("control.exe", "folders"));
                    Open(c, "Open This PC", "shell:MyComputerFolder");
                    break;

                case "sys-about":
                    Head("This PC");
                    c = NewCard();
                    Info(c, "Device name", Environment.MachineName);
                    Info(c, "User", Environment.UserName);
                    Info(c, "Windows", Environment.OSVersion.VersionString);
                    Info(c, "System type", Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
                    Info(c, "Processors", Environment.ProcessorCount.ToString());
                    Head("More");
                    c = NewCard();
                    Open(c, "About (Windows Settings)", "ms-settings:about");
                    Link(c, "Task Manager", "Ctrl + Shift + Esc", () => Process.Start("taskmgr.exe"));
                    break;

                default:
                    BuildSystemLinks(key);
                    break;
            }
            Relayout();
        }

        void BuildSystemLinks(string key)
        {
            SetCard c;
            foreach (string[] p in SystemPages)
            {
                if (p[0] != key) continue;
                if (key == "sys-personal")
                {
                    Head("Appearance");
                    c = NewCard();
                    Toggle(c, "Dark mode", SetUi.RegInt(Personalize, "AppsUseLightTheme", 1) == 0, v =>
                    {
                        SetUi.RegSet(Personalize, "AppsUseLightTheme", v ? 0 : 1);
                        SetUi.RegSet(Personalize, "SystemUsesLightTheme", v ? 0 : 1);
                        SetUi.BroadcastSettingChange("ImmersiveColorSet");
                        if (SetUi.ThemeSetting() == "system") ReapplyTheme();
                    });
                    Toggle(c, "Transparency effects", SetUi.RegInt(Personalize, "EnableTransparency", 1) == 1, v =>
                    {
                        SetUi.RegSet(Personalize, "EnableTransparency", v ? 1 : 0);
                        SetUi.BroadcastSettingChange("ImmersiveColorSet");
                    });
                }
                statusNote = null;
                bool live = key == "sys-display" || key == "sys-network" || key == "sys-power" || key == "sys-personal";
                if (key == "sys-display") DisplayControls();
                else if (key == "sys-network") NetworkControls();
                else if (key == "sys-power") PowerControls();
                else if (key == "sys-personal") WallpaperControls();
                Head(live ? "More..." : p[1]);
                c = NewCard();
                for (int i = 3; i < p.Length; i++)
                {
                    int bar = p[i].IndexOf('|');
                    Open(c, p[i].Substring(0, bar), p[i].Substring(bar + 1));
                }
                if (key == "sys-power")
                {
                    Head("Power");
                    c = NewCard();
                    Link(c, "Sleep", null, () => Application.SetSuspendState(PowerState.Suspend, false, false));
                    Link(c, "Lock screen", "Win + L", () => Process.Start("rundll32.exe", "user32.dll,LockWorkStation"));
                    Link(c, "Restart...", null, Confirm("Restart this PC now?", () => Process.Start("shutdown", "/r /t 0")));
                    Link(c, "Shut down...", null, Confirm("Shut down this PC now?", () => Process.Start("shutdown", "/s /t 0")));
                }
                return;
            }
        }
    }
}
