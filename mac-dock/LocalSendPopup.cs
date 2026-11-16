using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacDock
{
    // "Send with LocalSend": dim backdrop plus a centred, custom-drawn macOS-style card. Everything is scaled by the monitor DPI and
    // text goes through TextRenderer (ClearType) so it stays sharp. Devices are found automatically (quick multicast, then a subnet scan,
    // then a light re-announce every few seconds while open).
    sealed class LocalSendPopup : Form
    {
        static LocalSendPopup current;
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        readonly string[] files;
        readonly Form dim;
        readonly Timer poll = new Timer { Interval = 700 };
        readonly Timer anim = new Timer { Interval = 90 };
        List<LsPeer> peers = new List<LsPeer>();
        string key = "", message = "";
        int hoverRow = -1, hoverBtn = -1, pressRow = -1, dots, ticks;
        bool sending, scanning;
        float s;
        Rectangle cancelRect, rescanRect;
        const int MaxRows = 5;

        static readonly Color Panel = Color.FromArgb(40, 41, 48), Line = Color.FromArgb(62, 64, 74), Txt = Color.FromArgb(245, 246, 250), Sub = Color.FromArgb(150, 154, 168),
            Accent = Color.FromArgb(10, 132, 255), Row = Color.FromArgb(52, 54, 63), Pill = Color.FromArgb(66, 68, 80);

        public static void Open(string[] paths)
        {
            paths = (paths ?? new string[0]).Where(File.Exists).ToArray();
            if (paths.Length == 0) return;
            if (current != null && !current.IsDisposed) current.Close();
            current = new LocalSendPopup(paths);
            current.ShowAll();
        }

        LocalSendPopup(string[] files)
        {
            this.files = files;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            StartPosition = FormStartPosition.Manual; DoubleBuffered = true; BackColor = Panel;
            s = DeviceDpi / 96f;
            ApplyLayout();

            Rectangle area = Screen.FromPoint(Cursor.Position).Bounds;
            dim = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual, Bounds = area, BackColor = Color.Black, Opacity = 0.45 };
            dim.Click += (a, b) => Close();
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);

            KeyDown += (a, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            poll.Tick += (a, e) => RefreshPeers();
            anim.Tick += (a, e) => { dots = (dots + 1) % 4; if (scanning || sending) Invalidate(); };
            FormClosed += (a, e) => { poll.Stop(); anim.Stop(); try { dim.Close(); } catch { } if (current == this) current = null; };
        }

        int V(int px) { return (int)Math.Round(px * s); }
        int RowCount { get { return Math.Max(2, Math.Min(MaxRows, peers.Count)); } }

        void ApplyLayout()
        {
            int w = V(400), h = V(112) + RowCount * V(58) + V(70);
            Point center = Visible ? new Point(Left + Width / 2, Top + Height / 2) : Point.Empty;
            Size = new Size(w, h);
            if (Visible) Location = new Point(center.X - w / 2, center.Y - h / 2);
            Rectangle foot = new Rectangle(V(20), h - V(54), w - V(40), V(36));
            cancelRect = new Rectangle(foot.Right - V(96), foot.Top, V(96), foot.Height);
            rescanRect = new Rectangle(foot.Left, foot.Top, V(96), foot.Height);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // DWMWCP_ROUND: smooth corners + shadow on Windows 11
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); s = e.DeviceDpiNew / 96f; ApplyLayout(); Invalidate(); }

        void ShowAll()
        {
            dim.Show(); Show(); Activate();
            try { SetForegroundWindow(Handle); } catch { }
            poll.Start(); anim.Start();
            AutoScan();
        }

        // Quick multicast now, a full subnet scan right after, then a light re-announce every ~6 s while the popup is open.
        void AutoScan()
        {
            scanning = true;
            Task.Run(() =>
            {
                try { LocalSendNode n = LocalSend.Node; if (n != null) { n.Discover(false); System.Threading.Thread.Sleep(1200); n.Discover(true); } } catch { }
                try { if (!IsDisposed) BeginInvoke(new Action(() => { scanning = false; Invalidate(); })); } catch { }
            });
        }

        void Rescan()
        {
            if (scanning || sending) return;
            AutoScan();
        }

        void RefreshPeers()
        {
            if (IsDisposed || sending) return;
            if (++ticks % 8 == 0 && !scanning) { scanning = true; Task.Run(() => { try { LocalSendNode nn = LocalSend.Node; if (nn != null) nn.Discover(false); } catch { } try { if (!IsDisposed) BeginInvoke(new Action(() => { scanning = false; Invalidate(); })); } catch { } }); }
            LocalSendNode n = LocalSend.Node;
            if (n == null) { message = LocalSend.Status; Invalidate(); return; }
            List<LsPeer> now = n.Peers();
            string k = string.Join("|", now.Select(p => p.Fingerprint + p.Alias));
            if (k == key) return;
            key = k; peers = now; hoverRow = -1;
            ApplyLayout(); Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(V(20), V(112) + i * V(58), Width - V(40), V(52)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Panel);
            using (var p = new Pen(Line, 1)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            var flagsC = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            var flagsL = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

            // header
            using (Font tf = Typography.Text(13.5f, Weight.Semibold))
                TextRenderer.DrawText(g, "Send with LocalSend", tf, new Rectangle(0, V(18), Width, V(28)), Txt, Panel, flagsC);
            string what = files.Length == 1 ? Path.GetFileName(files[0]) : files.Length + " files";
            using (Font sf = Typography.Text(10f))
                TextRenderer.DrawText(g, what, sf, new Rectangle(V(24), V(48), Width - V(48), V(20)), Sub, Panel, flagsC);
            bool anim3 = !sending && scanning;
            string st = sending ? message : (peers.Count == 0 ? "Searching for nearby devices" : (scanning ? "Scanning" : "Choose a device"));
            using (Font sf = Typography.Text(10f))
                DrawAnimated(g, st, sf, new Rectangle(V(24), V(78), Width - V(48), V(20)), Sub, Panel, anim3, dots);

            // device rows
            int shown = Math.Min(MaxRows, peers.Count);
            for (int i = 0; i < RowCount; i++)
            {
                Rectangle r = RowRect(i);
                if (i >= shown)
                {
                    if (i == 0 && peers.Count == 0)
                        using (Font sf = Typography.Text(10f)) TextRenderer.DrawText(g, "Open LocalSend on the other device", sf, r, Sub, Panel, flagsC);
                    continue;
                }
                LsPeer pr = peers[i];
                bool hot = i == hoverRow && !sending;
                using (GraphicsPath gp = Round(r, V(10))) using (var br = new SolidBrush(hot ? (pressRow == i ? Color.FromArgb(10, 100, 210) : Accent) : Row)) g.FillPath(br, gp);
                Color nameC = hot ? Color.White : Txt, subC = hot ? Color.FromArgb(220, 235, 255) : Sub, rowBack = hot ? Accent : Row;
                if (hot && pressRow == i) rowBack = Color.FromArgb(10, 100, 210);
                int d = V(34);
                Rectangle av = new Rectangle(r.Left + V(10), r.Top + (r.Height - d) / 2, d, d);
                using (var br = new LinearGradientBrush(av, Color.FromArgb(120, 170, 255), Color.FromArgb(40, 100, 230), 90f)) g.FillEllipse(br, av);
                string letter = string.IsNullOrEmpty(pr.Alias) ? "?" : pr.Alias.Substring(0, 1).ToUpperInvariant();
                using (Font lf = Typography.Text(11f, Weight.Semibold)) TextRenderer.DrawText(g, letter, lf, av, Color.White, Color.Transparent, flagsC);
                int tx = av.Right + V(12);
                using (Font nf = Typography.Text(10.5f, Weight.Semibold)) TextRenderer.DrawText(g, pr.Alias, nf, new Rectangle(tx, r.Top + V(7), r.Right - tx - V(10), V(20)), nameC, rowBack, flagsL);
                string sub = pr.Ip + (string.IsNullOrEmpty(pr.Model) ? "" : "  ·  " + pr.Model);
                using (Font sf = Typography.Text(9.25f)) TextRenderer.DrawText(g, sub, sf, new Rectangle(tx, r.Top + V(26), r.Right - tx - V(10), V(18)), subC, rowBack, flagsL);
            }

            // footer buttons
            DrawPill(g, rescanRect, scanning ? "Scanning" : "Rescan", hoverBtn == 1, false);
            DrawPill(g, cancelRect, "Cancel", hoverBtn == 2, true);
        }

        // Text plus a "..." whose dots appear one by one. The layout always reserves the full three dots, so the text never shifts.
        void DrawAnimated(Graphics g, string text, Font f, Rectangle r, Color fore, Color back, bool animate, int visibleDots)
        {
            var fl = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
            int tw = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), fl).Width;
            int dw = TextRenderer.MeasureText(g, ".", f, new Size(int.MaxValue, int.MaxValue), fl).Width;
            int total = tw; // centre on the text alone; the dots hang off its right edge
            int x = r.Left + (r.Width - total) / 2;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, r.Top, tw + 2, r.Height), fore, back, fl);
            for (int i = 0; animate && i < Math.Min(3, visibleDots); i++)
                TextRenderer.DrawText(g, ".", f, new Rectangle(x + tw + i * dw, r.Top, dw + 2, r.Height), fore, back, fl);
        }

        void DrawPill(Graphics g, Rectangle r, string text, bool hot, bool primaryStyle)
        {
            Color back = hot ? Color.FromArgb(84, 87, 100) : Pill;
            using (GraphicsPath gp = Round(r, r.Height / 2)) using (var br = new SolidBrush(back)) g.FillPath(br, gp);
            using (Font f = Typography.Text(10f, Weight.Semibold))
                DrawAnimated(g, text, f, r, Txt, back, text == "Scanning", dots);
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2; var p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        int RowAt(Point p) { for (int i = 0; i < Math.Min(MaxRows, peers.Count); i++) if (RowRect(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int r = RowAt(e.Location), b = rescanRect.Contains(e.Location) ? 1 : cancelRect.Contains(e.Location) ? 2 : -1;
            if (r != hoverRow || b != hoverBtn) { hoverRow = r; hoverBtn = b; Cursor = (r >= 0 || b >= 0) ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverRow = hoverBtn = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressRow = RowAt(e.Location); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int r = RowAt(e.Location), pr = pressRow; pressRow = -1;
            if (cancelRect.Contains(e.Location)) { Close(); return; }
            if (rescanRect.Contains(e.Location)) { Rescan(); return; }
            if (r >= 0 && r == pr && !sending) SendTo(peers[r]);
            Invalidate();
            base.OnMouseUp(e);
        }

        void SendTo(LsPeer peer)
        {
            sending = true; message = "Sending to " + peer.Alias + "..."; hoverRow = -1; Invalidate();
            string[] f = files;
            Task.Run(() =>
            {
                string result;
                try { result = LocalSend.Node.SendFiles(peer, f, st => { LocalSend.Status = st; message = st; try { if (!IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { } }); }
                catch (Exception ex) { result = "Failed: " + ex.Message; }
                LocalSend.Status = result;
                try
                {
                    if (!IsDisposed) BeginInvoke(new Action(() =>
                    {
                        message = result; Invalidate();
                        var t = new Timer { Interval = 1800 };
                        t.Tick += (a, b) => { t.Stop(); t.Dispose(); if (!IsDisposed) Close(); };
                        t.Start();
                    }));
                }
                catch { }
            });
        }
    }
}
