// SF Symbols-style vector glyphs for Control Center. Each glyph is drawn centred on (0,0) in a 24-unit box and scaled
// by the graphics transform, so it stays sharp at any size or DPI.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MacDock
{
    static class CcGlyphs
    {
        // Circle fill of an active toggle, as in macOS Control Center (the glyph on it is white).
        public static Color Accent(string name)
        {
            switch (name)
            {
                case "moon": return Color.FromArgb(94, 92, 230);      // Focus: indigo
                case "warm":
                case "airplane": return Color.FromArgb(255, 149, 0);  // orange
                case "saver": return Color.FromArgb(242, 184, 0);     // Low Power: yellow
                case "rec": return Color.FromArgb(255, 59, 48);       // recording: red
                case "localsend":
                case "wifi":
                case "bluetooth":
                default: return Color.FromArgb(10, 132, 255);
            }
        }

        public static void Draw(Graphics g, string name, float cx, float cy, float scale, Color c)
        {
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            using (var b = new SolidBrush(c))
            using (var p = new Pen(c, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                Paint(g, name, b, p);
            g.Restore(state);
        }

        static void Paint(Graphics g, string name, SolidBrush b, Pen p)
        {
            switch (name)
            {
                case "wifi":
                    p.Width = 2.9f;
                    Arc(g, p, 0, 6.8f, 13.2f, 225, 90);
                    Arc(g, p, 0, 6.8f, 8.1f, 225, 90);
                    g.FillPie(b, -4.4f, 2.4f, 8.8f, 8.8f, 225, 90);
                    break;
                case "bluetooth":
                    p.Width = 2.1f;
                    g.DrawLines(p, Pts(-5.2f, -4.8f, 5.2f, 4.8f, 0, 9.6f, 0, -9.6f, 5.2f, -4.8f, -5.2f, 4.8f));
                    break;
                case "bluetooth-connected": // rune with a dot on each side
                    Paint(g, "bluetooth", b, p);
                    g.FillEllipse(b, -9.8f, -1.5f, 3, 3);
                    g.FillEllipse(b, 6.8f, -1.5f, 3, 3);
                    break;
                case "bluetooth-off": // dimmed rune with a slash
                    using (var faint = new Pen(Color.FromArgb(b.Color.A * 45 / 100, b.Color), 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(faint, Pts(-5.2f, -4.8f, 5.2f, 4.8f, 0, 9.6f, 0, -9.6f, 5.2f, -4.8f, -5.2f, 4.8f));
                    p.Width = 2.1f;
                    g.DrawLine(p, -8f, -8f, 8f, 8f);
                    break;
                case "localsend": // AirDrop: rings open at the bottom
                    p.Width = 1.8f;
                    Arc(g, p, 0, 0, 10.2f, 128, 284);
                    Arc(g, p, 0, 0, 6.5f, 134, 272);
                    Arc(g, p, 0, 0, 2.9f, 145, 250);
                    g.FillEllipse(b, -1.2f, -1.2f, 2.4f, 2.4f);
                    break;
                case "moon": // moon.fill
                    using (GraphicsPath m = Crescent(-0.6f, 0.6f, 10f, 5.2f, -4.4f, 8.4f)) g.FillPath(b, m);
                    break;
                case "warm": // sun.max.fill
                    Sun(g, b, p, 4.6f, 7.5f, 9.9f);
                    break;
                case "sunmin":
                    Sun(g, b, p, 3.4f, 6.2f, 7.0f);
                    break;
                case "dark": // circle.lefthalf.filled
                    p.Width = 1.8f;
                    g.DrawEllipse(p, -9.2f, -9.2f, 18.4f, 18.4f);
                    g.FillPie(b, -9.2f, -9.2f, 18.4f, 18.4f, 90, 180);
                    break;
                case "shot": // camera.viewfinder
                    p.Width = 1.8f;
                    for (int i = 0; i < 4; i++)
                    {
                        GraphicsState s = g.Save();
                        g.RotateTransform(i * 90);
                        using (var corner = new GraphicsPath())
                        {
                            corner.AddLine(-10f, -5.2f, -10f, -6.8f);
                            corner.AddArc(-10f, -10f, 6.4f, 6.4f, 180, 90);
                            corner.AddLine(-6.8f, -10f, -5.2f, -10f);
                            g.DrawPath(p, corner);
                        }
                        g.Restore(s);
                    }
                    using (GraphicsPath cam = Round(-6.4f, -3.4f, 12.8f, 9.2f, 2.2f))
                    {
                        cam.AddEllipse(-2.7f, -1.5f, 5.4f, 5.4f); // lens hole
                        g.FillPath(b, cam);
                    }
                    g.FillEllipse(b, -1.3f, -0.1f, 2.6f, 2.6f);
                    using (GraphicsPath top = Round(-2.8f, -5.4f, 5.6f, 2.6f, 1f)) g.FillPath(b, top);
                    break;
                case "rec": // record.circle
                    p.Width = 1.8f;
                    g.DrawEllipse(p, -9.6f, -9.6f, 19.2f, 19.2f);
                    g.FillEllipse(b, -5.6f, -5.6f, 11.2f, 11.2f);
                    break;
                case "project": // display
                    p.Width = 1.8f;
                    using (GraphicsPath s = Round(-10.5f, -8.5f, 21f, 14f, 2.6f)) g.DrawPath(p, s);
                    g.FillRectangle(b, -2.2f, 5.5f, 4.4f, 3.4f);
                    g.DrawLine(p, -4.8f, 9f, 4.8f, 9f);
                    break;
                case "mirror": // rectangle.on.rectangle
                {
                    p.Width = 1.8f;
                    GraphicsState s = g.Save();
                    using (GraphicsPath hole = Round(-5.4f, -4.4f, 17f, 14f, 3.6f)) g.SetClip(hole, CombineMode.Exclude);
                    using (GraphicsPath back = Round(-10.4f, -8.6f, 15f, 11.8f, 2.8f)) g.DrawPath(p, back);
                    g.Restore(s);
                    using (GraphicsPath front = Round(-3.8f, -2.8f, 14.4f, 11.4f, 2.8f)) { g.FillPath(b, front); g.DrawPath(p, front); }
                    break;
                }
                case "stage": // Stage Manager: a window with the recent apps beside it
                    using (GraphicsPath w = Round(-3.8f, -8.5f, 14.8f, 17f, 3.2f)) g.FillPath(b, w);
                    for (int i = 0; i < 3; i++)
                        using (GraphicsPath s = Round(-11f, -7.9f + i * 5.9f, 4.4f, 4f, 1.3f)) g.FillPath(b, s);
                    break;
                case "sidebar": // sidebar.right
                    p.Width = 1.8f;
                    using (GraphicsPath w = Round(-10.5f, -8f, 21f, 16f, 3.6f)) g.DrawPath(p, w);
                    g.DrawLine(p, 3f, -8f, 3f, 8f);
                    p.Width = 1.4f;
                    g.DrawLine(p, 5.6f, -4.4f, 7.8f, -4.4f);
                    g.DrawLine(p, 5.6f, -1.2f, 7.8f, -1.2f);
                    break;
                case "grid": // square.grid.2x2.fill
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 2; j++)
                            using (GraphicsPath s = Round(-9.6f + i * 10.6f, -9.6f + j * 10.6f, 8.6f, 8.6f, 2.6f)) g.FillPath(b, s);
                    break;
                case "airplay": // airplay.video
                {
                    p.Width = 1.8f;
                    GraphicsState s = g.Save();
                    using (var hole = new GraphicsPath())
                    {
                        hole.AddPolygon(Pts(0, -0.6f, 9.4f, 11f, -9.4f, 11f));
                        g.SetClip(hole, CombineMode.Exclude);
                    }
                    using (GraphicsPath w = Round(-10.5f, -8.5f, 21f, 15f, 3f)) g.DrawPath(p, w);
                    g.Restore(s);
                    Solid(g, b, p, 1.4f, 0, 2.2f, 5.6f, 8.8f, -5.6f, 8.8f);
                    break;
                }
                case "mute": // speaker.slash.fill
                {
                    GraphicsState s = g.Save();
                    using (var cut = new GraphicsPath())
                    {
                        cut.AddPolygon(Pts(-8.4f, -11f, -5f, -11f, 11.6f, 9f, 8.2f, 9f));
                        g.SetClip(cut, CombineMode.Exclude);
                    }
                    Speaker(g, b, p, -2f);
                    p.Width = 1.8f;
                    Arc(g, p, 1.2f, 0, 5.2f, -48, 96);
                    g.Restore(s);
                    p.Width = 1.9f;
                    g.DrawLine(p, -8.4f, -9f, 8.6f, 9.4f);
                    break;
                }
                case "speaker": // speaker.fill
                    Speaker(g, b, p, 1.8f);
                    break;
                case "speakerwave": // speaker.wave.3.fill
                    Speaker(g, b, p, -4f);
                    p.Width = 1.6f;
                    Arc(g, p, -0.6f, 0, 3.6f, -48, 96);
                    Arc(g, p, -0.6f, 0, 7.1f, -48, 96);
                    Arc(g, p, -0.6f, 0, 10.6f, -48, 96);
                    break;
                case "lock": // lock.fill
                    p.Width = 2.2f;
                    using (var sh = new GraphicsPath())
                    {
                        sh.AddLine(-4.6f, 0, -4.6f, -4.4f);
                        sh.AddArc(-4.6f, -9.4f, 9.2f, 9.6f, 180, 180);
                        sh.AddLine(4.6f, -4.4f, 4.6f, 0);
                        g.DrawPath(p, sh);
                    }
                    using (GraphicsPath body = Round(-7.4f, -1.4f, 14.8f, 11.4f, 2.8f)) g.FillPath(b, body);
                    break;
                case "saver": // battery.50
                    p.Width = 1.5f;
                    using (GraphicsPath o = Round(-10.6f, -5.6f, 18.8f, 11.2f, 3.4f)) g.DrawPath(p, o);
                    using (GraphicsPath f = Round(-8.6f, -3.6f, 9.4f, 7.2f, 1.8f)) g.FillPath(b, f);
                    g.FillPie(b, 7.6f, -2.4f, 3.8f, 4.8f, -90, 180);
                    break;
                case "airplane": // nose pointing right
                    Solid(g, b, p, 1.1f,
                        10.2f, 0, 9f, -1.3f, 3.2f, -1.4f, -2.4f, -9.4f, -4.6f, -9.4f, -1.9f, -1.5f, -6.6f, -1.4f, -8.6f, -4f, -10.2f, -4f,
                        -9f, 0, -10.2f, 4f, -8.6f, 4f, -6.6f, 1.4f, -1.9f, 1.5f, -4.6f, 9.4f, -2.4f, 9.4f, 3.2f, 1.4f, 9f, 1.3f);
                    break;
                case "play":
                    Solid(g, b, p, 2.2f, -5.4f, -8f, 8.4f, 0, -5.4f, 8f);
                    break;
                case "pause":
                    using (GraphicsPath l = Round(-6.8f, -8.5f, 4.8f, 17f, 1.5f)) g.FillPath(b, l);
                    using (GraphicsPath r = Round(2f, -8.5f, 4.8f, 17f, 1.5f)) g.FillPath(b, r);
                    break;
                case "forward":
                case "backward":
                    if (name == "backward") g.ScaleTransform(-1, 1);
                    Solid(g, b, p, 1.8f, -9.8f, -5.8f, 0, 0, -9.8f, 5.8f);
                    Solid(g, b, p, 1.8f, 0.8f, -5.8f, 10.6f, 0, 0.8f, 5.8f);
                    break;
            }
        }

        static PointF[] Pts(params float[] v)
        {
            var a = new PointF[v.Length / 2];
            for (int i = 0; i < a.Length; i++) a[i] = new PointF(v[i * 2], v[i * 2 + 1]);
            return a;
        }

        // Filled polygon with rounded corners (fill plus a round-joined outline).
        static void Solid(Graphics g, Brush b, Pen p, float round, params float[] v)
        {
            PointF[] a = Pts(v);
            p.Width = round;
            g.FillPolygon(b, a);
            g.DrawPolygon(p, a);
        }

        static void Arc(Graphics g, Pen p, float x, float y, float r, float start, float sweep)
        {
            g.DrawArc(p, x - r, y - r, r * 2, r * 2, start, sweep);
        }

        static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            return Layered.RoundedRect(new RectangleF(x, y, w, h), r);
        }

        static void Sun(Graphics g, Brush b, Pen p, float disc, float from, float to)
        {
            g.FillEllipse(b, -disc, -disc, disc * 2, disc * 2);
            p.Width = 2f;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                float x = (float)Math.Cos(a), y = (float)Math.Sin(a);
                g.DrawLine(p, x * from, y * from, x * to, y * to);
            }
        }

        static void Speaker(Graphics g, Brush b, Pen p, float x)
        {
            Solid(g, b, p, 1.6f, x - 6f, -3.2f, x - 2.8f, -3.2f, x + 2f, -7.6f, x + 2f, 7.6f, x - 2.8f, 3.2f, x - 6f, 3.2f);
        }

        // Disc (x, y, R) with a second disc offset by (dx, dy), radius r, taken out of it.
        static GraphicsPath Crescent(float x, float y, float R, float dx, float dy, float r)
        {
            double d = Math.Sqrt(dx * dx + dy * dy), th = Math.Atan2(dy, dx) * 180 / Math.PI;
            double al = Math.Acos((d * d + R * R - r * r) / (2 * d * R)) * 180 / Math.PI;
            double be = Math.Acos((d * d + r * r - R * R) / (2 * d * r)) * 180 / Math.PI;
            var path = new GraphicsPath();
            path.AddArc(x - R, y - R, R * 2, R * 2, (float)(th + al), (float)(360 - 2 * al));
            path.AddArc(x + dx - r, y + dy - r, r * 2, r * 2, (float)(th + 180 + be), (float)(-2 * be));
            path.CloseFigure();
            return path;
        }
    }
}
