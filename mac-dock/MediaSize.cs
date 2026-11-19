// Real pixel size of an image or video from its header alone (no decoding), so a fan tile can follow the file's aspect ratio
// even when Windows hands back a square thumbnail with padding around the picture.
using System;
using System.Drawing;
using System.IO;

namespace MacDock
{
    static class MediaSize
    {
        public static Size? Probe(string path)
        {
            try
            {
                string e = Path.GetExtension(path).ToLowerInvariant();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Size? s = null;
                    if (e == ".png") s = Png(fs);
                    else if (e == ".jpg" || e == ".jpeg") s = Jpeg(fs);
                    else if (e == ".gif") s = Gif(fs);
                    else if (e == ".bmp") s = Bmp(fs);
                    else if (e == ".mp4" || e == ".mov" || e == ".m4v") s = Mp4(fs);
                    return s.HasValue && s.Value.Width > 0 && s.Value.Height > 0 ? s : null;
                }
            }
            catch { return null; }
        }

        static int U16le(byte[] b, int o) { return b[o] | b[o + 1] << 8; }
        static int U32be(byte[] b, int o) { return b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]; }

        static Size? Png(Stream s)
        {
            var b = new byte[24]; if (s.Read(b, 0, 24) < 24 || b[1] != 0x50) return null;
            return new Size(U32be(b, 16), U32be(b, 20));
        }
        static Size? Gif(Stream s)
        {
            var b = new byte[10]; if (s.Read(b, 0, 10) < 10) return null;
            return new Size(U16le(b, 6), U16le(b, 8));
        }
        static Size? Bmp(Stream s)
        {
            var b = new byte[26]; if (s.Read(b, 0, 26) < 26) return null;
            return new Size(BitConverter.ToInt32(b, 18), Math.Abs(BitConverter.ToInt32(b, 22)));
        }
        static Size? Jpeg(Stream s)
        {
            var bs = new BufferedStream(s, 8192);
            if (bs.ReadByte() != 0xFF || bs.ReadByte() != 0xD8) return null;
            for (int guard = 0; guard < 400; guard++)
            {
                int c; do { c = bs.ReadByte(); } while (c != 0xFF && c >= 0);
                do { c = bs.ReadByte(); } while (c == 0xFF);
                if (c < 0) return null;
                if (c == 0xD8 || c == 0x01 || (c >= 0xD0 && c <= 0xD7)) continue;
                int hi = bs.ReadByte(), lo = bs.ReadByte(); if (lo < 0) return null;
                int len = hi << 8 | lo;
                if (c >= 0xC0 && c <= 0xCF && c != 0xC4 && c != 0xC8 && c != 0xCC)
                {
                    var b = new byte[5]; if (bs.Read(b, 0, 5) < 5) return null;
                    return new Size(b[3] << 8 | b[4], b[1] << 8 | b[2]);
                }
                for (int k = 0; k < len - 2; k++) if (bs.ReadByte() < 0) return null;
            }
            return null;
        }

        // MP4/MOV: moov > trak > tkhd carries the display width/height (16.16) and a rotation matrix.
        static Size? Mp4(Stream s) { return Boxes(s, 0, s.Length, 0); }

        static Size? Boxes(Stream s, long start, long end, int depth)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                s.Position = pos;
                var h = new byte[16]; int got = s.Read(h, 0, 16); if (got < 8) return null;
                long size = (uint)U32be(h, 0); string type = System.Text.Encoding.ASCII.GetString(h, 4, 4); long hdr = 8;
                if (size == 1 && got >= 16) { size = ((long)(uint)U32be(h, 8) << 32) | (uint)U32be(h, 12); hdr = 16; }
                else if (size == 0) size = end - pos;
                if (size < hdr) return null;
                if (type == "moov" || type == "trak")
                {
                    if (depth < 3) { Size? r = Boxes(s, pos + hdr, Math.Min(end, pos + size), depth + 1); if (r.HasValue) return r; }
                }
                else if (type == "tkhd")
                {
                    s.Position = pos + hdr;
                    var b = new byte[92]; int n = s.Read(b, 0, 92); if (n < 84) return null;
                    int ver = b[0], o = ver == 1 ? 32 + 8 + 4 + 8 : 20 + 4 + 8; // skip times, track id, duration, reserved
                    // version 0: flags(4) create(4) modify(4) id(4) rsv(4) duration(4) rsv(8) layer(2) alt(2) vol(2) rsv(2) matrix(36) w(4) h(4)
                    o = ver == 1 ? 4 + 8 + 8 + 4 + 4 + 8 + 8 + 2 + 2 + 2 + 2 : 4 + 4 + 4 + 4 + 4 + 4 + 8 + 2 + 2 + 2 + 2;
                    if (o + 44 > n) return null;
                    int a = U32be(b, o), bb = U32be(b, o + 4);
                    double w = (uint)U32be(b, o + 36) / 65536.0, hh = (uint)U32be(b, o + 40) / 65536.0;
                    if (w > 0 && hh > 0)
                    {
                        bool rot = a == 0 && bb != 0; // 90 or 270 degree rotation: the picture is shown sideways
                        return rot ? new Size((int)Math.Round(hh), (int)Math.Round(w)) : new Size((int)Math.Round(w), (int)Math.Round(hh));
                    }
                }
                pos += size;
            }
            return null;
        }

        // The part of a thumbnail t that holds the picture, when its real aspect is known: Windows fits the picture centred inside
        // the thumbnail box and pads the rest. Returns null when t already has the right shape.
        public static Bitmap CropToAspect(Bitmap t, Size real)
        {
            double sc = Math.Min((double)t.Width / real.Width, (double)t.Height / real.Height);
            int cw = Math.Max(1, (int)Math.Round(real.Width * sc)), ch = Math.Max(1, (int)Math.Round(real.Height * sc));
            if (Math.Abs(cw - t.Width) <= 1 && Math.Abs(ch - t.Height) <= 1) return null;
            int x = (t.Width - cw) / 2, y = (t.Height - ch) / 2;
            return t.Clone(new Rectangle(x, y, cw, ch), t.PixelFormat);
        }
    }
}
