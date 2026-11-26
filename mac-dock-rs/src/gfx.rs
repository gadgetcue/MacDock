//! Canvas: GDI+ for anti-aliased shapes and images, GDI for text (GDI+ text enumerates every installed
//! font on first use: ~6-28 s on this PC), drawn into a premultiplied-alpha DIB and pushed with
//! UpdateLayeredWindow. Never creates a font by name more than once per size.
use std::ptr::null_mut;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::Graphics::GdiPlus::*;
use windows::Win32::UI::WindowsAndMessaging::*;

pub fn argb(a: u8, r: u8, g: u8, b: u8) -> u32 {
    ((a as u32) << 24) | ((r as u32) << 16) | ((g as u32) << 8) | b as u32
}
pub fn with_alpha(c: u32, a: f32) -> u32 {
    let na = (((c >> 24) & 0xFF) as f32 * a.clamp(0.0, 1.0)).round() as u32;
    (na << 24) | (c & 0x00FF_FFFF)
}

static INTER_REGULAR: &[u8] = include_bytes!("../assets/fonts/Inter-Regular.ttf");
static INTER_BOLD: &[u8] = include_bytes!("../assets/fonts/Inter-Bold.ttf");
static INTER_MEDIUM: &[u8] = include_bytes!("../assets/fonts/Inter-Medium.ttf");
static INTER_SEMIBOLD: &[u8] = include_bytes!("../assets/fonts/Inter-SemiBold.ttf");
static INIT: std::sync::Once = std::sync::Once::new();
static mut TOKEN: usize = 0;

/// GDI+ startup + embedded Inter faces (process-local, nothing installed system-wide). Idempotent.
pub fn init() {
    INIT.call_once(|| unsafe {
        let input = GdiplusStartupInput { GdiplusVersion: 1, ..Default::default() };
        let mut t = 0usize;
        GdiplusStartup(&mut t, &input, null_mut());
        TOKEN = t;
        for f in [INTER_REGULAR, INTER_MEDIUM, INTER_SEMIBOLD, INTER_BOLD] {
            let mut n = 0u32;
            AddFontMemResourceEx(f.as_ptr() as *const _, f.len() as u32, None, &mut n);
        }
        // warm GDI font matching off the UI thread: the first CreateFont on this PC (thousands of user fonts) took ~0.7 s
        std::thread::spawn(|| {
            let dc = CreateCompatibleDC(None);
            for face in ["Inter", "Inter SemiBold"] {
                let mut lf = LOGFONTW::default();
                lf.lfHeight = -16;
                lf.lfQuality = CLEARTYPE_QUALITY;
                for (i, ch) in face.encode_utf16().enumerate() {
                    lf.lfFaceName[i] = ch;
                }
                let f = CreateFontIndirectW(&lf);
                let old = SelectObject(dc, f.into());
                let w: Vec<u16> = "Abc 123".encode_utf16().collect();
                let mut sz = SIZE::default();
                let _ = GetTextExtentPoint32W(dc, &w, &mut sz);
                SelectObject(dc, old);
                let _ = DeleteObject(f.into());
            }
            let _ = DeleteDC(dc);
        });
    });
}

/// Raw premultiplied BGRA pixels, produced on any thread, turned into an `Img` on the UI thread.
pub struct Pixels {
    pub w: i32,
    pub h: i32,
    pub bgra: Vec<u8>, // premultiplied
}

impl Pixels {
    /// Decode an image file (png/jpg/bmp/ico) with GDI+ into premultiplied BGRA.
    pub fn from_file(path: &str) -> Option<Pixels> {
        init();
        unsafe {
            let wide = crate::util::wide(path);
            let mut bmp: *mut GpBitmap = null_mut();
            if GdipCreateBitmapFromFile(windows::core::PCWSTR(wide.as_ptr()), &mut bmp) != Status(0) || bmp.is_null() {
                return None;
            }
            let r = Self::from_gpbitmap(bmp);
            GdipDisposeImage(bmp as *mut GpImage);
            r
        }
    }
    unsafe fn from_gpbitmap(bmp: *mut GpBitmap) -> Option<Pixels> {
        let (mut w, mut h) = (0u32, 0u32);
        GdipGetImageWidth(bmp as *mut GpImage, &mut w);
        GdipGetImageHeight(bmp as *mut GpImage, &mut h);
        if w == 0 || h == 0 || w > 8192 || h > 8192 {
            return None;
        }
        let rect = Rect { X: 0, Y: 0, Width: w as i32, Height: h as i32 };
        let mut bd = BitmapData::default();
        if GdipBitmapLockBits(bmp, &rect, ImageLockModeRead.0 as u32, 0x000E200B, &mut bd) != Status(0) {
            return None;
        }
        let mut out = vec![0u8; (w * h * 4) as usize];
        for y in 0..h as usize {
            let src = (bd.Scan0 as *const u8).offset(y as isize * bd.Stride as isize);
            std::ptr::copy_nonoverlapping(src, out.as_mut_ptr().add(y * w as usize * 4), w as usize * 4);
        }
        GdipBitmapUnlockBits(bmp, &mut bd);
        Some(Pixels { w: w as i32, h: h as i32, bgra: out })
    }
    /// From a 32-bit top-down/bottom-up DIB HBITMAP with straight or pre-multiplied alpha.
    pub fn from_hbitmap(hbm: HBITMAP) -> Option<Pixels> {
        unsafe {
            let mut bm = BITMAP::default();
            if GetObjectW(hbm.into(), std::mem::size_of::<BITMAP>() as i32, Some(&mut bm as *mut _ as *mut _)) == 0 {
                return None;
            }
            let (w, h) = (bm.bmWidth, bm.bmHeight.abs());
            if w <= 0 || h <= 0 {
                return None;
            }
            let mut bi = BITMAPINFO::default();
            bi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            let mut buf = vec![0u8; (w * h * 4) as usize];
            let dc = GetDC(None);
            let n = GetDIBits(dc, hbm, 0, h as u32, Some(buf.as_mut_ptr() as *mut _), &mut bi, DIB_RGB_COLORS);
            ReleaseDC(None, dc);
            if n == 0 {
                return None;
            }
            // Shell icons are straight alpha: premultiply. If every alpha is 0 (no alpha channel) force opaque.
            let any = buf.chunks_exact(4).any(|p| p[3] != 0);
            for p in buf.chunks_exact_mut(4) {
                if !any {
                    p[3] = 255;
                } else {
                    let a = p[3] as u32;
                    p[0] = (p[0] as u32 * a / 255) as u8;
                    p[1] = (p[1] as u32 * a / 255) as u8;
                    p[2] = (p[2] as u32 * a / 255) as u8;
                }
            }
            Some(Pixels { w, h, bgra: buf })
        }
    }
    /// Crop fully transparent border and make it a square-padded image (keeps icon centered).
    pub fn trim_square(self) -> Pixels {
        let (w, h) = (self.w as usize, self.h as usize);
        let (mut x0, mut y0, mut x1, mut y1) = (w, h, 0usize, 0usize);
        for y in 0..h {
            for x in 0..w {
                if self.bgra[(y * w + x) * 4 + 3] > 8 {
                    x0 = x0.min(x);
                    y0 = y0.min(y);
                    x1 = x1.max(x);
                    y1 = y1.max(y);
                }
            }
        }
        if x1 < x0 || y1 < y0 || (x0 == 0 && y0 == 0 && x1 == w - 1 && y1 == h - 1) {
            return self;
        }
        let (cw, ch) = (x1 - x0 + 1, y1 - y0 + 1);
        let s = cw.max(ch);
        let mut out = vec![0u8; s * s * 4];
        let (ox, oy) = ((s - cw) / 2, (s - ch) / 2);
        for y in 0..ch {
            let src = ((y0 + y) * w + x0) * 4;
            let dst = ((oy + y) * s + ox) * 4;
            out[dst..dst + cw * 4].copy_from_slice(&self.bgra[src..src + cw * 4]);
        }
        Pixels { w: s as i32, h: s as i32, bgra: out }
    }
}

/// A GDI+ bitmap owned by the UI thread.
/// Largest size (px) icons are drawn at in the dock; bigger sources get a pre-filtered smaller twin so the
/// per-frame scaling stays cheap while Launchpad and previews still use the full-resolution bitmap.
pub static SMALL_PX: std::sync::atomic::AtomicI32 = std::sync::atomic::AtomicI32::new(128);

pub struct Img {
    bmp: *mut GpBitmap,
    _buf: Vec<u8>, // GDI+ keeps pointing at this
    pub w: i32,
    pub h: i32,
    small: Option<Box<Img>>,
}
impl Img {
    fn raw(w: i32, h: i32, buf: Vec<u8>) -> Option<Img> {
        init();
        unsafe {
            let mut bmp: *mut GpBitmap = null_mut();
            if GdipCreateBitmapFromScan0(w, h, w * 4, 0x000E200B, Some(buf.as_ptr()), &mut bmp) != Status(0) || bmp.is_null() {
                return None;
            }
            Some(Img { bmp, _buf: buf, w, h, small: None })
        }
    }
    pub fn from_gfx(g: &Gfx) -> Option<Img> {
        let p = g.to_pixels();
        Img::raw(p.w, p.h, p.bgra)
    }
    pub fn from_pixels(p: Pixels) -> Option<Img> {
        let sp = SMALL_PX.load(std::sync::atomic::Ordering::Relaxed);
        let small = if p.w.max(p.h) as f32 > sp as f32 * 1.5 {
            let twin = Pixels { w: p.w, h: p.h, bgra: p.bgra.clone() }.fit(sp);
            Img::raw(twin.w, twin.h, twin.bgra).map(Box::new)
        } else {
            None
        };
        let mut img = Img::raw(p.w, p.h, p.bgra)?;
        img.small = small;
        Some(img)
    }
    fn pick(&self, drawn: f32) -> &Img {
        match &self.small {
            Some(s) if drawn <= s.w.max(s.h) as f32 * 1.12 => s,
            _ => self,
        }
    }
}
impl Drop for Img {
    fn drop(&mut self) {
        unsafe {
            GdipDisposeImage(self.bmp as *mut GpImage);
        }
    }
}

unsafe fn make_dib(dc: HDC, w: i32, h: i32) -> Option<(HBITMAP, *mut u8)> {
    let mut bi = BITMAPINFO::default();
    bi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB.0;
    let mut bits: *mut std::ffi::c_void = null_mut();
    let dib = CreateDIBSection(Some(dc), &bi, DIB_RGB_COLORS, &mut bits, None, 0).ok()?;
    Some((dib, bits as *mut u8))
}

pub struct Gfx {
    dc: HDC,
    dib: HBITMAP,
    old: HGDIOBJ,
    bits: *mut u8,
    bmp: *mut GpBitmap,
    g: *mut GpGraphics,
    mdc: HDC,
    mdib: HBITMAP,
    mold: HGDIOBJ,
    mbits: *mut u8,
    fonts: Vec<(i32, bool, HFONT)>,
    pub w: i32,
    pub h: i32,
}

impl Gfx {
    pub fn new(w: i32, h: i32) -> Option<Gfx> {
        init();
        let (w, h) = (w.max(1), h.max(1));
        unsafe {
            let dc = CreateCompatibleDC(None);
            let (dib, bits) = make_dib(dc, w, h)?;
            let old = SelectObject(dc, dib.into());
            let mut bmp: *mut GpBitmap = null_mut();
            GdipCreateBitmapFromScan0(w, h, w * 4, 0x000E200B, Some(bits as *const u8), &mut bmp);
            let mut g: *mut GpGraphics = null_mut();
            GdipGetImageGraphicsContext(bmp as *mut GpImage, &mut g);
            GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
            GdipSetInterpolationMode(g, InterpolationModeHighQualityBilinear);
            GdipSetPixelOffsetMode(g, PixelOffsetModeHighQuality);
            Some(Gfx { dc, dib, old, bits, bmp, g, mdc: HDC::default(), mdib: HBITMAP::default(), mold: HGDIOBJ::default(), mbits: null_mut(), fonts: Vec::new(), w, h })
        }
    }

    pub fn clear(&mut self, c: u32) {
        unsafe {
            GdipGraphicsClear(self.g, c);
        }
    }
    pub fn clip(&mut self, x: i32, y: i32, w: i32, h: i32) {
        unsafe {
            GdipSetClipRectI(self.g, x, y, w, h, CombineModeReplace);
        }
    }
    pub fn unclip(&mut self) {
        unsafe {
            GdipResetClip(self.g);
        }
    }
    unsafe fn brush(c: u32) -> *mut GpSolidFill {
        let mut b: *mut GpSolidFill = null_mut();
        GdipCreateSolidFill(c, &mut b);
        b
    }
    unsafe fn rr_path(x: f32, y: f32, w: f32, h: f32, r: f32) -> *mut GpPath {
        let r = r.min(w / 2.0).min(h / 2.0).max(0.01);
        let d = r * 2.0;
        let mut p: *mut GpPath = null_mut();
        GdipCreatePath(FillModeAlternate, &mut p);
        GdipAddPathArc(p, x, y, d, d, 180.0, 90.0);
        GdipAddPathArc(p, x + w - d, y, d, d, 270.0, 90.0);
        GdipAddPathArc(p, x + w - d, y + h - d, d, d, 0.0, 90.0);
        GdipAddPathArc(p, x, y + h - d, d, d, 90.0, 90.0);
        GdipClosePathFigure(p);
        p
    }
    pub fn fill_rect(&mut self, x: f32, y: f32, w: f32, h: f32, c: u32) {
        unsafe {
            let b = Self::brush(c);
            GdipFillRectangle(self.g, b as *mut GpBrush, x, y, w, h);
            GdipDeleteBrush(b as *mut GpBrush);
        }
    }
    pub fn fill_round(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, c: u32) {
        unsafe {
            let p = Self::rr_path(x, y, w, h, r);
            let b = Self::brush(c);
            GdipFillPath(self.g, b as *mut GpBrush, p);
            GdipDeleteBrush(b as *mut GpBrush);
            GdipDeletePath(p);
        }
    }
    /// Vertical gradient fill inside a rounded rect.
    pub fn fill_round_grad(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, top: u32, bottom: u32) {
        unsafe {
            let p = Self::rr_path(x, y, w, h, r);
            let mut b: *mut GpLineGradient = null_mut();
            let r1 = RectF { X: x, Y: y, Width: w.max(1.0), Height: h.max(1.0) };
            GdipCreateLineBrushFromRect(&r1, top, bottom, LinearGradientModeVertical, WrapModeTileFlipXY, &mut b);
            GdipFillPath(self.g, b as *mut GpBrush, p);
            GdipDeleteBrush(b as *mut GpBrush);
            GdipDeletePath(p);
        }
    }
    pub fn stroke_round(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, wd: f32, c: u32) {
        unsafe {
            let p = Self::rr_path(x, y, w, h, r);
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipDrawPath(self.g, pen, p);
            GdipDeletePen(pen);
            GdipDeletePath(p);
        }
    }
    /// Soft drop shadow made of stacked translucent rounded rects.
    pub fn shadow(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, spread: f32, alpha: u8) {
        let steps = 6;
        for i in 0..steps {
            let t = i as f32 / steps as f32;
            let g = spread * (1.0 - t);
            let a = (alpha as f32 * 0.16 * (1.0 - t * 0.5)) as u8;
            self.fill_round(x - g, y - g + spread * 0.35, w + 2.0 * g, h + 2.0 * g, r + g, argb(a, 0, 0, 0));
        }
    }
    pub fn fill_circle(&mut self, cx: f32, cy: f32, r: f32, c: u32) {
        unsafe {
            let b = Self::brush(c);
            GdipFillEllipse(self.g, b as *mut GpBrush, cx - r, cy - r, r * 2.0, r * 2.0);
            GdipDeleteBrush(b as *mut GpBrush);
        }
    }
    pub fn stroke_circle(&mut self, cx: f32, cy: f32, r: f32, wd: f32, c: u32) {
        unsafe {
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipDrawEllipse(self.g, pen, cx - r, cy - r, r * 2.0, r * 2.0);
            GdipDeletePen(pen);
        }
    }
    pub fn arc(&mut self, cx: f32, cy: f32, r: f32, start: f32, sweep: f32, wd: f32, c: u32) {
        unsafe {
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipDrawArc(self.g, pen, cx - r, cy - r, r * 2.0, r * 2.0, start, sweep);
            GdipDeletePen(pen);
        }
    }
    pub fn line(&mut self, x1: f32, y1: f32, x2: f32, y2: f32, wd: f32, c: u32) {
        unsafe {
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipDrawLine(self.g, pen, x1, y1, x2, y2);
            GdipDeletePen(pen);
        }
    }
    pub fn polyline(&mut self, pts: &[(f32, f32)], wd: f32, c: u32) {
        unsafe {
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipSetPenLineJoin(pen, LineJoinRound);
            let v: Vec<PointF> = pts.iter().map(|p| PointF { X: p.0, Y: p.1 }).collect();
            GdipDrawLines(self.g, pen, v.as_ptr(), v.len() as i32);
            GdipDeletePen(pen);
        }
    }
    pub fn fill_poly(&mut self, pts: &[(f32, f32)], c: u32) {
        unsafe {
            let b = Self::brush(c);
            let v: Vec<PointF> = pts.iter().map(|p| PointF { X: p.0, Y: p.1 }).collect();
            GdipFillPolygon(self.g, b as *mut GpBrush, v.as_ptr(), v.len() as i32, FillModeAlternate);
            GdipDeleteBrush(b as *mut GpBrush);
        }
    }

    /// Draw an image scaled into the box with optional alpha (0..1).
    pub fn image(&mut self, img: &Img, x: f32, y: f32, w: f32, h: f32, alpha: f32) {
        let img = img.pick(w.max(h));
        unsafe {
            if alpha >= 0.995 {
                GdipDrawImageRect(self.g, img.bmp as *mut GpImage, x, y, w, h);
            } else if alpha > 0.0 {
                let mut ia: *mut GpImageAttributes = null_mut();
                GdipCreateImageAttributes(&mut ia);
                let mut m = ColorMatrix::default();
                m.m[0 * 5 + 0] = 1.0;
                m.m[1 * 5 + 1] = 1.0;
                m.m[2 * 5 + 2] = 1.0;
                m.m[3 * 5 + 3] = alpha;
                m.m[4 * 5 + 4] = 1.0;
                GdipSetImageAttributesColorMatrix(ia, ColorAdjustTypeBitmap, true, &m, std::ptr::null(), ColorMatrixFlagsDefault);
                GdipDrawImageRectRect(self.g, img.bmp as *mut GpImage, x, y, w, h, 0.0, 0.0, img.w as f32, img.h as f32, UnitPixel, ia, 0, std::ptr::null_mut());
                GdipDisposeImageAttributes(ia);
            }
        }
    }
    /// Draw an image with a vertical-flip + horizontal squash for the genie (4-point parallelogram transform).
    pub fn image_quad(&mut self, img: &Img, dst: [(f32, f32); 3], alpha: f32) {
        unsafe {
            let pts = [PointF { X: dst[0].0, Y: dst[0].1 }, PointF { X: dst[1].0, Y: dst[1].1 }, PointF { X: dst[2].0, Y: dst[2].1 }];
            let mut ia: *mut GpImageAttributes = null_mut();
            GdipCreateImageAttributes(&mut ia);
            let mut m = ColorMatrix::default();
            m.m[0 * 5 + 0] = 1.0;
            m.m[1 * 5 + 1] = 1.0;
            m.m[2 * 5 + 2] = 1.0;
            m.m[3 * 5 + 3] = alpha;
            m.m[4 * 5 + 4] = 1.0;
            GdipSetImageAttributesColorMatrix(ia, ColorAdjustTypeBitmap, true, &m, std::ptr::null(), ColorMatrixFlagsDefault);
            GdipDrawImagePointsRect(self.g, img.bmp as *mut GpImage, pts.as_ptr(), 3, 0.0, 0.0, img.w as f32, img.h as f32, UnitPixel, ia, 0, std::ptr::null_mut());
            GdipDisposeImageAttributes(ia);
        }
    }

    fn font(&mut self, size: f32, bold: bool) -> HFONT {
        let key = (size * 10.0) as i32;
        if let Some(f) = self.fonts.iter().find(|f| f.0 == key && f.1 == bold) {
            return f.2;
        }
        unsafe {
            let mut lf = LOGFONTW::default();
            lf.lfHeight = -(size.round() as i32);
            lf.lfWeight = 400; // weight comes from the face: Inter (Regular) or Inter SemiBold
            lf.lfQuality = CLEARTYPE_QUALITY;
            for (i, ch) in (if bold { "Inter SemiBold" } else { "Inter" }).encode_utf16().enumerate() {
                lf.lfFaceName[i] = ch;
            }
            let f = CreateFontIndirectW(&lf);
            self.fonts.push((key, bold, f));
            f
        }
    }

    /// Text width in px (single line).
    pub fn measure(&mut self, s: &str, size: f32, bold: bool) -> f32 {
        unsafe {
            self.ensure_mask();
            let f = self.font(size, bold);
            let old = SelectObject(self.mdc, f.into());
            let ws: Vec<u16> = s.encode_utf16().collect();
            let mut sz = SIZE::default();
            let _ = GetTextExtentPoint32W(self.mdc, &ws, &mut sz);
            SelectObject(self.mdc, old);
            sz.cx as f32
        }
    }

    unsafe fn ensure_mask(&mut self) {
        if self.mdc.is_invalid() {
            self.mdc = CreateCompatibleDC(None);
            if let Some((d, b)) = make_dib(self.mdc, self.w, self.h) {
                self.mdib = d;
                self.mbits = b;
                self.mold = SelectObject(self.mdc, d.into());
                SetBkMode(self.mdc, TRANSPARENT);
                SetTextColor(self.mdc, COLORREF(0x00FFFFFF));
            }
        }
    }

    /// align: 0 left, 1 center, 2 right. `wrap` = multi-line (top aligned); else single line, optionally vcentered.
    pub fn text(&mut self, s: &str, x: f32, y: f32, w: f32, h: f32, size: f32, bold: bool, align: i32, vcenter: bool, wrap: bool, c: u32) {
        if s.is_empty() || (c >> 24) == 0 {
            return;
        }
        unsafe {
            self.ensure_mask();
            if self.mbits.is_null() {
                return;
            }
            let font = self.font(size, bold);
            let x0 = (x.floor() as i32).max(0);
            let y0 = (y.floor() as i32).max(0);
            let x1 = ((x + w).ceil() as i32).min(self.w);
            let y1 = ((y + h).ceil() as i32).min(self.h);
            if x1 <= x0 || y1 <= y0 {
                return;
            }
            let _ = PatBlt(self.mdc, x0, y0, x1 - x0, y1 - y0, BLACKNESS);
            let oldf = SelectObject(self.mdc, font.into());
            let mut flags = DT_NOPREFIX | match align {
                1 => DT_CENTER,
                2 => DT_RIGHT,
                _ => DT_LEFT,
            };
            if wrap {
                flags |= DT_WORDBREAK;
            } else {
                flags |= DT_SINGLELINE | DT_END_ELLIPSIS;
                if vcenter {
                    flags |= DT_VCENTER;
                }
            }
            let mut rc = RECT { left: x0, top: y0, right: x1, bottom: y1 };
            let mut ws: Vec<u16> = crate::util::safe_text(s).encode_utf16().collect();
            DrawTextW(self.mdc, &mut ws, &mut rc, flags);
            SelectObject(self.mdc, oldf);
            let ca = (c >> 24) & 0xFF;
            let (sr, sg, sb) = ((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
            for yy in y0..y1 {
                let row = (yy * self.w) as usize * 4;
                for xx in x0..x1 {
                    let o = row + xx as usize * 4;
                    // gray coverage from the max channel (ClearType off for layered per-pixel alpha text)
                    let cov = (*self.mbits.add(o) as u32).max(*self.mbits.add(o + 1) as u32).max(*self.mbits.add(o + 2) as u32);
                    let cv = cov * ca / 255;
                    if cv == 0 {
                        continue;
                    }
                    let d = self.bits.add(o);
                    let inv = 255 - cv;
                    // premultiplied source-over with source = color * cv/255
                    *d = ((sb * cv + *d as u32 * inv) / 255).min(255) as u8;
                    *d.add(1) = ((sg * cv + *d.add(1) as u32 * inv) / 255).min(255) as u8;
                    *d.add(2) = ((sr * cv + *d.add(2) as u32 * inv) / 255).min(255) as u8;
                    *d.add(3) = (cv + *d.add(3) as u32 * inv / 255).min(255) as u8;
                }
            }
        }
    }

    /// Copy the canvas out as premultiplied pixels.
    pub fn to_pixels(&self) -> Pixels {
        let n = (self.w * self.h * 4) as usize;
        let mut v = vec![0u8; n];
        unsafe { std::ptr::copy_nonoverlapping(self.bits, v.as_mut_ptr(), n); }
        Pixels { w: self.w, h: self.h, bgra: v }
    }

    /// Push the canvas to a layered window at (x, y) with per-pixel alpha.
    pub fn present_layered(&self, hwnd: HWND, x: i32, y: i32, alpha: u8) {
        unsafe {
            let pos = POINT { x, y };
            let size = SIZE { cx: self.w, cy: self.h };
            let src = POINT { x: 0, y: 0 };
            let bf = BLENDFUNCTION { BlendOp: 0, BlendFlags: 0, SourceConstantAlpha: alpha, AlphaFormat: 1 };
            if let Err(e) = UpdateLayeredWindow(hwnd, None, Some(&pos), Some(&size), Some(self.dc), Some(&src), COLORREF(0), Some(&bf), ULW_ALPHA) { crate::util::log(&format!("ULW fail {:?}", e)); }
        }
    }
    /// Blit to a normal window DC.
    pub fn present(&self, dst: HDC) {
        unsafe {
            let _ = BitBlt(dst, 0, 0, self.w, self.h, Some(self.dc), 0, 0, SRCCOPY);
        }
    }
}

impl Drop for Gfx {
    fn drop(&mut self) {
        unsafe {
            for f in &self.fonts {
                let _ = DeleteObject(f.2.into());
            }
            if !self.mdc.is_invalid() {
                SelectObject(self.mdc, self.mold);
                let _ = DeleteObject(self.mdib.into());
                let _ = DeleteDC(self.mdc);
            }
            GdipDeleteGraphics(self.g);
            GdipDisposeImage(self.bmp as *mut GpImage);
            SelectObject(self.dc, self.old);
            let _ = DeleteObject(self.dib.into());
            let _ = DeleteDC(self.dc);
        }
    }
}

/// Convenience for windows that own an HWND + canvas.
#[allow(dead_code)]
pub fn dummy_hwnd() -> HWND {
    HWND::default()
}
#[allow(dead_code)]
pub fn _unused(_: HICON) {}

impl Pixels {
    /// Area-average downscale (premultiplied). Returns self when already small enough.
    pub fn fit(self, max: i32) -> Pixels {
        if self.w <= max && self.h <= max {
            return self;
        }
        let k = max as f32 / self.w.max(self.h) as f32;
        let (nw, nh) = (((self.w as f32 * k).round() as i32).max(1), ((self.h as f32 * k).round() as i32).max(1));
        let mut out = vec![0u8; (nw * nh * 4) as usize];
        let (sx, sy) = (self.w as f32 / nw as f32, self.h as f32 / nh as f32);
        for y in 0..nh {
            let (y0, y1) = (y as f32 * sy, (y + 1) as f32 * sy);
            for x in 0..nw {
                let (x0, x1) = (x as f32 * sx, (x + 1) as f32 * sx);
                let mut acc = [0f32; 4];
                let mut wsum = 0f32;
                let mut yy = y0.floor() as i32;
                while (yy as f32) < y1 && yy < self.h {
                    let wy = ((yy + 1) as f32).min(y1) - (yy as f32).max(y0);
                    let mut xx = x0.floor() as i32;
                    while (xx as f32) < x1 && xx < self.w {
                        let wx = ((xx + 1) as f32).min(x1) - (xx as f32).max(x0);
                        let wgt = wx * wy;
                        let o = ((yy * self.w + xx) * 4) as usize;
                        for c in 0..4 {
                            acc[c] += self.bgra[o + c] as f32 * wgt;
                        }
                        wsum += wgt;
                        xx += 1;
                    }
                    yy += 1;
                }
                let o = ((y * nw + x) * 4) as usize;
                if wsum > 0.0 {
                    for c in 0..4 {
                        out[o + c] = (acc[c] / wsum).round().min(255.0) as u8;
                    }
                }
            }
        }
        Pixels { w: nw, h: nh, bgra: out }
    }
}

impl Gfx {
    /// Render another window's content into this canvas (PrintWindow, full content) and force opaque alpha.
    pub fn print_window(&mut self, hwnd: HWND) -> bool {
        unsafe {
            let ok = windows::Win32::Storage::Xps::PrintWindow(hwnd, self.dc, windows::Win32::Storage::Xps::PRINT_WINDOW_FLAGS(2)).as_bool();
            if ok {
                let n = (self.w * self.h) as usize;
                for i in 0..n {
                    *self.bits.add(i * 4 + 3) = 255;
                }
            }
            ok
        }
    }
    /// Draw a source sub-rectangle (sx, sy, sw, sh) of an image into a parallelogram (genie strips).
    pub fn image_quad_src(&mut self, img: &Img, dst: [(f32, f32); 3], src: (f32, f32, f32, f32), alpha: f32) {
        unsafe {
            let pts = [PointF { X: dst[0].0, Y: dst[0].1 }, PointF { X: dst[1].0, Y: dst[1].1 }, PointF { X: dst[2].0, Y: dst[2].1 }];
            let mut ia: *mut GpImageAttributes = null_mut();
            GdipCreateImageAttributes(&mut ia);
            let mut m = ColorMatrix::default();
            m.m[0] = 1.0;
            m.m[6] = 1.0;
            m.m[12] = 1.0;
            m.m[18] = alpha;
            m.m[24] = 1.0;
            GdipSetImageAttributesColorMatrix(ia, ColorAdjustTypeBitmap, true, &m, std::ptr::null(), ColorMatrixFlagsDefault);
            GdipSetImageAttributesWrapMode(ia, WrapModeTileFlipXY, 0, false);
            GdipDrawImagePointsRect(self.g, img.bmp as *mut GpImage, pts.as_ptr(), 3, src.0, src.1, src.2, src.3, UnitPixel, ia, 0, null_mut());
            GdipDisposeImageAttributes(ia);
        }
    }
}

impl Gfx {
    /// Rotate subsequent drawing by `deg` (clockwise) about (px, py). Pair with `reset_tf`.
    pub fn rot_about(&mut self, px: f32, py: f32, deg: f32) {
        unsafe {
            GdipResetWorldTransform(self.g);
            GdipTranslateWorldTransform(self.g, px, py, MatrixOrderPrepend);
            GdipRotateWorldTransform(self.g, deg, MatrixOrderPrepend);
            GdipTranslateWorldTransform(self.g, -px, -py, MatrixOrderPrepend);
        }
    }
    /// Translate to (tx, ty) then scale uniformly by k (drawing is then centred on the origin).
    pub fn scale_at(&mut self, tx: f32, ty: f32, k: f32) {
        unsafe {
            GdipResetWorldTransform(self.g);
            GdipTranslateWorldTransform(self.g, tx, ty, MatrixOrderPrepend);
            GdipScaleWorldTransform(self.g, k, k, MatrixOrderPrepend);
        }
    }
    pub fn reset_tf(&mut self) {
        unsafe {
            GdipResetWorldTransform(self.g);
        }
    }
    pub fn bezier(&mut self, p: [(f32, f32); 4], wd: f32, c: u32) {
        unsafe {
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen1(c, wd, UnitPixel, &mut pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipDrawBezier(self.g, pen, p[0].0, p[0].1, p[1].0, p[1].1, p[2].0, p[2].1, p[3].0, p[3].1);
            GdipDeletePen(pen);
        }
    }
    /// 1:1 pixel copy of a pre-rendered layer (no resampling, premultiplied source-over).
    pub fn blit_exact(&mut self, img: &Img, x: i32, y: i32) {
        unsafe {
            let mut mode = InterpolationModeDefault;
            GdipGetInterpolationMode(self.g, &mut mode);
            GdipSetInterpolationMode(self.g, InterpolationModeNearestNeighbor);
            GdipDrawImageRectRectI(self.g, img.bmp as *mut GpImage, x, y, img.w, img.h, 0, 0, img.w, img.h, UnitPixel, std::ptr::null_mut(), 0, std::ptr::null_mut());
            GdipSetInterpolationMode(self.g, mode);
        }
    }
    pub fn set_fast(&mut self, fast: bool) {
        unsafe {
            GdipSetInterpolationMode(self.g, if fast { InterpolationModeBilinear } else { InterpolationModeHighQualityBicubic });
        }
    }
}

// ---------- Liquid Glass look ----------
static ACCENT: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0xFF0A84FF);
pub fn accent() -> u32 {
    ACCENT.load(std::sync::atomic::Ordering::Relaxed)
}
pub fn set_accent(c: u32) {
    ACCENT.store(c, std::sync::atomic::Ordering::Relaxed);
}
/// Accent colours as in macOS: (key, label, 0xAARRGGBB).
pub const ACCENTS: &[(&str, &str, u32)] = &[
    ("blue", "Blue", 0xFF0A84FF),
    ("purple", "Purple", 0xFFBF5AF2),
    ("pink", "Pink", 0xFFFF375F),
    ("red", "Red", 0xFFFF453A),
    ("orange", "Orange", 0xFFFF9F0A),
    ("yellow", "Yellow", 0xFFFFD60A),
    ("green", "Green", 0xFF32D74B),
    ("graphite", "Graphite", 0xFF8E8E93),
];
pub fn accent_for(key: &str) -> u32 {
    ACCENTS.iter().find(|a| a.0 == key).map(|a| a.2).unwrap_or(0xFF0A84FF)
}

impl Gfx {
    /// Vertical multi-stop stroke of a rounded rect (colours at positions 0..1 top to bottom).
    fn stroke_round_v(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, wd: f32, stops: &[(f32, u32)]) {
        unsafe {
            let p = Self::rr_path(x, y, w, h, r);
            let (p1, p2) = (PointF { X: x, Y: y }, PointF { X: x, Y: y + h.max(1.0) });
            let mut b: *mut GpLineGradient = null_mut();
            GdipCreateLineBrush(&p1, &p2, stops[0].1, stops[stops.len() - 1].1, WrapModeTileFlipXY, &mut b);
            let cols: Vec<u32> = stops.iter().map(|s| s.1).collect();
            let pos: Vec<f32> = stops.iter().map(|s| s.0).collect();
            GdipSetLinePresetBlend(b, cols.as_ptr(), pos.as_ptr(), stops.len() as i32);
            let mut pen: *mut GpPen = null_mut();
            GdipCreatePen2(b as *mut GpBrush, wd, UnitPixel, &mut pen);
            GdipDrawPath(self.g, pen, p);
            GdipDeletePen(pen);
            GdipDeleteBrush(b as *mut GpBrush);
            GdipDeletePath(p);
        }
    }
    /// Liquid Glass finish over a live-blurred backdrop: top sheen, lit rim (bright top, soft bottom), a faint chromatic
    /// fringe at the sides, corner specular glints and a warm bottom bounce. `k` scales everything (0..1).
    pub fn liquid_glass(&mut self, x: f32, y: f32, w: f32, h: f32, r: f32, dark: bool, k: f32) {
        let wh = |a: f32| argb((a * k).clamp(0.0, 255.0) as u8, 255, 255, 255);
        let (sheen, rim_top, rim_bot) = if dark { (40.0, 170.0, 90.0) } else { (95.0, 235.0, 150.0) };
        unsafe {
            let p = Self::rr_path(x, y, w, h, r);
            GdipSetClipPath(self.g, p, CombineModeReplace);
            GdipDeletePath(p);
        }
        // sheen (upper half) and bounce light (lower third)
        self.fill_round_grad(x, y, w, h * 0.55, r, wh(sheen), wh(0.0));
        self.fill_round_grad(x, y + h * 0.62, w, h * 0.38, r, wh(0.0), wh(if dark { 26.0 } else { 46.0 }));
        // soft inner shadow just inside the rim: reads as glass thickness
        self.stroke_round(x + 2.2, y + 2.2, w - 4.4, h - 4.4, (r - 2.2).max(1.0), 2.2, argb((if dark { 34.0 } else { 20.0 } * k) as u8, 0, 0, 0));
        self.unclip();
        // chromatic fringe (cyan left, magenta right) as a cheap stand-in for refraction dispersion
        self.stroke_round(x + 0.1, y + 0.5, w - 1.0, h - 1.0, r, 1.0, argb((30.0 * k) as u8, 90, 200, 255));
        self.stroke_round(x + 0.9, y + 0.5, w - 1.0, h - 1.0, r, 1.0, argb((26.0 * k) as u8, 255, 120, 190));
        // lit rim and a second, dimmer rim inside
        self.stroke_round_v(x + 0.5, y + 0.5, w - 1.0, h - 1.0, r, 1.2, &[(0.0, wh(rim_top)), (0.3, wh(rim_top * 0.28)), (0.7, wh(rim_bot * 0.22)), (1.0, wh(rim_bot))]);
        self.stroke_round_v(x + 1.7, y + 1.7, w - 3.4, h - 3.4, (r - 1.2).max(1.0), 1.0, &[(0.0, wh(rim_top * 0.45)), (0.5, wh(0.0)), (1.0, wh(rim_bot * 0.3))]);
        // corner glints, top-left and bottom-right
        let rr = r.min(w / 2.0).min(h / 2.0);
        if rr > 4.0 {
            self.arc(x + rr, y + rr, rr - 1.1, 196.0, 58.0, 2.0, wh(rim_top * 0.95));
            self.arc(x + w - rr, y + h - rr, rr - 1.1, 16.0, 58.0, 1.8, wh(rim_bot * 0.75));
            self.arc(x + w - rr, y + rr, rr - 1.1, 292.0, 36.0, 1.4, wh(rim_top * 0.5));
        }
        // short top-edge streak
        let (x0, x1) = (x + rr + 2.0, x + w - rr - 2.0);
        if x1 > x0 {
            self.line(x0, y + 1.6, x1, y + 1.6, 1.0, wh(rim_top * 0.35));
        }
    }
}

/// Icon style applied to every icon bitmap (premultiplied BGRA). 0 default, 1 dark, 2 clear, 3 tinted.
static ICON_STYLE: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(0);
pub fn set_icon_style(key: &str) {
    let v = match key { "dark" => 1, "clear" => 2, "tinted" => 3, _ => 0 };
    ICON_STYLE.store(v, std::sync::atomic::Ordering::Relaxed);
}
impl Pixels {
    pub fn styled(mut self) -> Pixels {
        let style = ICON_STYLE.load(std::sync::atomic::Ordering::Relaxed);
        if style == 0 {
            return self;
        }
        let ac = accent();
        let (ar, ag, ab) = (((ac >> 16) & 255) as f32 / 255.0, ((ac >> 8) & 255) as f32 / 255.0, (ac & 255) as f32 / 255.0);
        for c in self.bgra.chunks_exact_mut(4) {
            let a = c[3] as f32;
            if a == 0.0 {
                continue;
            }
            let af = a / 255.0;
            let (r, g, b) = ((c[2] as f32 / a).min(1.0), (c[1] as f32 / a).min(1.0), (c[0] as f32 / a).min(1.0));
            let lum = 0.299 * r + 0.587 * g + 0.114 * b;
            let (nr, ng, nb, na) = match style {
                1 => (r * 0.46 + 0.02, g * 0.46 + 0.02, b * 0.5 + 0.03, af), // dark: deep, slightly lifted glyph colours
                2 => {
                    // clear: frosted translucent glass keeping the glyph
                    let m = 0.55;
                    (r * (1.0 - m) + lum * m + 0.28, g * (1.0 - m) + lum * m + 0.28, b * (1.0 - m) + lum * m + 0.30, af * 0.78)
                }
                _ => {
                    // tinted: luminance carried by the accent colour
                    let t = (0.18 + lum * 0.95).min(1.0);
                    (ar * t + (t * t) * 0.25, ag * t + (t * t) * 0.25, ab * t + (t * t) * 0.25, af)
                }
            };
            let pm = |v: f32| (v.clamp(0.0, 1.0) * na * 255.0).round() as u8;
            c[2] = pm(nr);
            c[1] = pm(ng);
            c[0] = pm(nb);
            c[3] = (na * 255.0).round() as u8;
        }
        self
    }
}
