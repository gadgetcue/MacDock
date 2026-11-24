//! Genie minimize/restore: a port of the C# Genie.cs. Triggered by the system's minimize/restore events (title-bar
//! buttons, shortcuts and dock clicks alike). The window is drawn as up to 96 live DWM thumbnail slices on a
//! click-through glass overlay under the dock; bottom slices lead and the top follows (Delay 0.44) so the window bends
//! into a funnel and is pulled into its dock icon (Pull curve: 0.35*smoothstep + 0.65*x^2.2). 500 ms minimize, 420 ms
//! restore, fade over the last 10%. Frames are paced by the vsync clock. Styles: genie (default), scale, fade.
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::UI::Controls::MARGINS;

const MAX_SLICES: usize = 96;
const MINIMIZE_MS: f64 = 500.0;
const RESTORE_MS: f64 = 420.0;
const DELAY: f64 = 0.44;
const FADE_FROM: f64 = 0.90;

pub struct Genie {
    hwnd: HWND,
    scr: RECT,
    rest: (f64, f64, f64, f64), // l, t, w, h
    target: (f64, f64),
    minimizing: bool,
    running: bool,
    src: (i32, i32),
    thumbs: Vec<isize>,
    last: Vec<[i64; 3]>,
    slices: usize,
    frame_t: f64,
    started: std::time::Instant,
    last_tick: std::time::Instant,
    hidden_window: isize,
    hidden_style: isize,
    style: String,
    watchdog: std::time::Instant,
}

fn ease(x: f64) -> f64 {
    let x = x.clamp(0.0, 1.0);
    x * x * (3.0 - 2.0 * x)
}
fn pull(x: f64) -> f64 {
    let x = x.clamp(0.0, 1.0);
    0.35 * x * x * (3.0 - 2.0 * x) + 0.65 * x.powf(2.2)
}

impl Genie {
    pub fn new() -> Genie {
        Genie { hwnd: HWND::default(), scr: RECT::default(), rest: (0.0, 0.0, 0.0, 0.0), target: (0.0, 0.0), minimizing: true, running: false, src: (0, 0), thumbs: vec![], last: vec![], slices: MAX_SLICES, frame_t: 0.0, started: std::time::Instant::now(), last_tick: std::time::Instant::now(), hidden_window: 0, hidden_style: 0, style: "genie".into(), watchdog: std::time::Instant::now() }
    }
    fn ensure(&mut self) {
        let scr = screen_rect();
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_GENIE, false, true, true, true, scr.left, scr.top, scr.right - scr.left, scr.bottom - scr.top);
            unsafe {
                let m = MARGINS { cxLeftWidth: -1, cxRightWidth: -1, cyTopHeight: -1, cyBottomHeight: -1 };
                let _ = DwmExtendFrameIntoClientArea(self.hwnd, &m);
            }
        }
        if scr.right != self.scr.right || scr.bottom != self.scr.bottom || scr.left != self.scr.left {
            self.scr = scr;
            win::set_pos(self.hwnd, scr.left, scr.top, scr.right - scr.left, scr.bottom - scr.top, None, SWP_NOZORDER);
        }
        self.scr = scr;
    }

    fn animatable(h: isize) -> bool {
        unsafe {
            let hwnd = hw(h);
            if !IsWindowVisible(hwnd).as_bool() && !IsIconic(hwnd).as_bool() {
                return false;
            }
            if GetAncestor(hwnd, GA_ROOT) != hwnd {
                return false;
            }
            let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
            let style = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
            if ex & WS_EX_TOOLWINDOW.0 != 0 && ex & WS_EX_APPWINDOW.0 == 0 {
                return false;
            }
            if style & WS_CAPTION.0 != WS_CAPTION.0 && ex & WS_EX_APPWINDOW.0 == 0 {
                return false;
            }
            if let Ok(o) = GetWindow(hwnd, GW_OWNER) {
                if !o.is_invalid() && ex & WS_EX_APPWINDOW.0 == 0 {
                    return false;
                }
            }
            let mut pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut pid));
            if pid == GetCurrentProcessId() {
                return false;
            }
            let mut cloaked = 0u32;
            if DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4).is_ok() && cloaked != 0 {
                return false;
            }
            let mut cls = [0u16; 64];
            let n = GetClassNameW(hwnd, &mut cls);
            let c = String::from_utf16_lossy(&cls[..n.max(0) as usize]);
            !matches!(c.as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd" | "XamlExplorerHostIslandWindow")
        }
    }

    /// Where the window sits when open (a minimized window reports a tiny off-screen rect: use its saved placement).
    fn rest_rect(h: isize) -> Option<(f64, f64, f64, f64)> {
        unsafe {
            let mut wp = WINDOWPLACEMENT { length: std::mem::size_of::<WINDOWPLACEMENT>() as u32, ..Default::default() };
            GetWindowPlacement(hw(h), &mut wp).ok()?;
            let r = if wp.flags.0 & 2 != 0 {
                // WPF_RESTORETOMAXIMIZED: the work area
                let mut wa = RECT::default();
                let _ = SystemParametersInfoW(SPI_GETWORKAREA, 0, Some(&mut wa as *mut _ as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0));
                wa
            } else {
                wp.rcNormalPosition
            };
            let (w, hh) = ((r.right - r.left) as f64, (r.bottom - r.top) as f64);
            if w > 40.0 && hh > 40.0 { Some((r.left as f64, r.top as f64, w, hh)) } else { None }
        }
    }

    /// System minimize (start) / restore (end) event for `h`.
    pub fn on_event(&mut self, h: isize, start: bool) {
        let a = app();
        if !a.cfg.s.genie || a.fullscreen || a.test && false {
            return;
        }
        if !Self::animatable(h) {
            return;
        }
        let Some(rest) = Self::rest_rect(h) else { return };
        if !start {
            let mut cls = [0u16; 64];
            let n = unsafe { GetClassNameW(hw(h), &mut cls) };
            if String::from_utf16_lossy(&cls[..n.max(0) as usize]) == "ApplicationFrameWindow" {
                return; // Store windows cannot be made see-through: minimize effect only
            }
        }
        let target = a.dock.anchor_for(&|it: &crate::dock::Item| it.wins.contains(&h)).map(|(cx, top, sz)| (cx as f64, (top + sz / 2.0) as f64)).unwrap_or_else(|| {
            let s = screen_rect();
            (((s.left + s.right) / 2) as f64, (s.bottom - 44) as f64)
        });
        self.cancel();
        if !self.begin(h, rest, target, start) {
            self.cancel();
        }
    }

    fn begin(&mut self, source: isize, rest: (f64, f64, f64, f64), target: (f64, f64), minimizing: bool) -> bool {
        self.ensure();
        self.rest = rest;
        self.target = target;
        self.minimizing = minimizing;
        self.style = app().cfg.s.genie_style.clone();
        unsafe {
            let mut first = 0isize;
            match DwmRegisterThumbnail(self.hwnd, hw(source)) {
                Ok(t) => first = t,
                Err(_) => return false,
            }
            let sz = match DwmQueryThumbnailSourceSize(first) {
                Ok(s) => s,
                Err(_) => {
                    let _ = DwmUnregisterThumbnail(first);
                    return false;
                }
            };
            if sz.cx < 40 || sz.cy < 40 {
                let _ = DwmUnregisterThumbnail(first);
                return false;
            }
            self.src = (sz.cx, sz.cy);
            self.slices = ((rest.3 / 9.0) as usize).clamp(40, MAX_SLICES);
            self.last = vec![[i64::MIN; 3]; MAX_SLICES];
            self.thumbs = vec![first];
            for _ in 1..self.slices {
                match DwmRegisterThumbnail(self.hwnd, hw(source)) {
                    Ok(t) => self.thumbs.push(t),
                    Err(_) => return false,
                }
            }
        }
        self.frame_t = 0.0;
        self.running = true;
        if !minimizing {
            self.hide_real(source);
        }
        self.layout(0.0);
        unsafe {
            let dock = app().dock.hwnd;
            let _ = SetWindowPos(self.hwnd, Some(dock), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        self.started = std::time::Instant::now();
        self.last_tick = self.started;
        self.watchdog = self.started;
        crate::clock::start(crate::clock::GENIE);
        true
    }

    pub fn cancel(&mut self) {
        if !self.running && self.thumbs.is_empty() {
            return;
        }
        crate::clock::stop(crate::clock::GENIE);
        self.running = false;
        self.show_real();
        unsafe {
            for t in self.thumbs.drain(..) {
                let _ = DwmUnregisterThumbnail(t);
            }
        }
        win::hide(self.hwnd);
    }

    pub fn tick(&mut self) {
        if !self.running {
            crate::clock::stop(crate::clock::GENIE);
            return;
        }
        let total = if self.minimizing { MINIMIZE_MS } else { RESTORE_MS };
        let now = self.started.elapsed().as_secs_f64() * 1000.0;
        let dt_ms = (self.last_tick.elapsed().as_secs_f64() * 1000.0).clamp(4.0, 34.0);
        self.last_tick = std::time::Instant::now();
        // even pacing, as in the C# step(): a late/early tick must not jump the animation
        let adv = now - self.frame_t;
        if adv > dt_ms * 2.5 {
            self.frame_t = now;
        } else {
            self.frame_t += adv.clamp(dt_ms * 0.75, dt_ms * 1.35);
        }
        self.frame_t = self.frame_t.min(now + dt_ms);
        let t = (self.frame_t / total).min(1.0);
        self.layout(t);
        if self.frame_t >= total || self.watchdog.elapsed().as_secs() >= 2 {
            self.cancel();
        }
    }

    /// While a window is being restored it is made fully transparent so only the effect is seen (put back in cancel()).
    fn hide_real(&mut self, w: isize) {
        unsafe {
            let ex = GetWindowLongPtrW(hw(w), GWL_EXSTYLE);
            if ex & 0x80000 != 0 {
                return; // already layered: leave it alone
            }
            self.hidden_style = ex;
            SetWindowLongPtrW(hw(w), GWL_EXSTYLE, ex | 0x80000);
            let _ = SetLayeredWindowAttributes(hw(w), windows::Win32::Foundation::COLORREF(0), 0, LWA_ALPHA);
            self.hidden_window = w;
        }
    }
    fn show_real(&mut self) {
        let w = std::mem::take(&mut self.hidden_window);
        if w == 0 {
            return;
        }
        unsafe {
            if !IsWindow(Some(hw(w))).as_bool() {
                return;
            }
            let _ = SetLayeredWindowAttributes(hw(w), windows::Win32::Foundation::COLORREF(0), 255, LWA_ALPHA);
            SetWindowLongPtrW(hw(w), GWL_EXSTYLE, self.hidden_style);
            let _ = RedrawWindow(Some(hw(w)), None, None, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME | RDW_UPDATENOW);
        }
    }

    fn layout(&mut self, t: f64) {
        let progress = pull(if self.minimizing { t } else { 1.0 - t });
        let scale_fx = self.style == "scale";
        let fade_fx = self.style == "fade";
        let icon_w = 54.0 * app().scale as f64 / 1.0;
        let neck_y = self.target.1;
        let n = self.slices;
        let alpha = if progress <= FADE_FROM { 255u8 } else { (255.0 * (1.0 - (progress - FADE_FROM) / (1.0 - FADE_FROM)).max(0.0)).round() as u8 };
        let (rl, rt, rw, rh) = self.rest;
        let (mut x0, mut x1, mut y) = (vec![0.0f64; n + 1], vec![0.0f64; n + 1], vec![0.0f64; n + 1]);
        for k in 0..=n {
            let row = k as f64 / n as f64;
            let delay = (1.0 - row) * DELAY;
            let u = if scale_fx || fade_fx { ease(progress) } else { ease((progress - delay) / (1.0 - DELAY)) };
            let y_rest = rt + row * rh;
            let cx = rl + rw / 2.0;
            let half_rest = rw / 2.0;
            let mut centre = cx + (self.target.0 - cx) * u;
            let mut half = half_rest + (icon_w / 2.0 - half_rest) * u;
            x0[k] = centre - half;
            x1[k] = centre + half;
            y[k] = y_rest + (neck_y - y_rest) * u;
            if scale_fx {
                centre = cx + (self.target.0 - cx) * u;
                half = half_rest * (1.0 - 0.92 * u);
                x0[k] = centre - half;
                x1[k] = centre + half;
            } else if fade_fx {
                let sc = 1.0 - 0.06 * u;
                centre = cx;
                half = half_rest * sc;
                y[k] = rt + rh / 2.0 + (y_rest - rt - rh / 2.0) * sc;
                x0[k] = centre - half;
                x1[k] = centre + half;
            }
        }
        let (sl, st) = (self.scr.left as f64, self.scr.top as f64);
        for i in 0..n {
            let top = y[i].round() as i32 - st as i32;
            let mut bottom = y[i + 1].round() as i32 - st as i32;
            let folded = y[i + 1] - y[i] < 0.5;
            if bottom <= top {
                bottom = top + 1;
            } else if i < n - 1 {
                bottom += 1; // 1 px overlap: no hairline seams between bands
            }
            let left = ((x0[i] + x0[i + 1]) / 2.0).round() as i32 - sl as i32;
            let mut right = ((x1[i] + x1[i + 1]) / 2.0).round() as i32 - sl as i32;
            if right <= left {
                right = left + 1;
            }
            let (sw, sh) = self.src;
            let a = ((left as i64) << 32) | right as u32 as i64;
            let b = ((top as i64) << 32) | bottom as u32 as i64;
            let c = ((alpha as i64) << 8) | (!folded) as i64 | 0x10000;
            if self.last[i] == [a, b, c] {
                continue;
            }
            self.last[i] = [a, b, c];
            let props = DWM_THUMBNAIL_PROPERTIES {
                dwFlags: DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                rcDestination: RECT { left, top, right, bottom },
                rcSource: RECT { left: 0, top: sh * i as i32 / n as i32, right: sw, bottom: sh * (i as i32 + 1) / n as i32 },
                opacity: alpha,
                fVisible: (!folded).into(),
                ..Default::default()
            };
            unsafe {
                let _ = DwmUpdateThumbnailProperties(self.thumbs[i], &props);
            }
        }
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match m {
        WM_NCHITTEST => LRESULT(-1),
        WM_ERASEBKGND => {
            // black client area = see-through glass once the frame is extended
            let mut r = RECT::default();
            let _ = GetClientRect(h, &mut r);
            FillRect(HDC(wp.0 as *mut _), &r, HBRUSH(GetStockObject(BLACK_BRUSH).0));
            LRESULT(1)
        }
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
