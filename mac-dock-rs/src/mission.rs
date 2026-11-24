//! Mission Control: live DWM thumbnails of every open window laid out in a grid, animated from their real
//! positions. Thumbnails render on the blurred back window; labels and hover chrome on the layered window above.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

struct Thumb {
    hwnd: isize,
    handle: isize,
    from: RECT,
    to: (f32, f32, f32, f32),
    title: String,
    exe: String,
    minimized: bool,
}

pub struct Mission {
    back: HWND,
    top: HWND,
    gfx: Option<Gfx>,
    w: i32,
    h: i32,
    scr: RECT,
    open: Spring,
    pub visible: bool,
    thumbs: Vec<Thumb>,
    hover: i32,
    timer: bool,
    last: std::time::Instant,
    need: bool,
}

impl Mission {
    pub fn new() -> Mission {
        Mission { back: HWND::default(), top: HWND::default(), gfx: None, w: 0, h: 0, scr: RECT::default(), open: Spring::new(0.0), visible: false, thumbs: vec![], hover: -1, timer: false, last: std::time::Instant::now(), need: true }
    }
    pub fn dirty(&mut self) {
        if self.visible {
            self.need = true;
            self.kick();
        }
    }
    pub fn start_gestures(&mut self) {}
    fn kick(&mut self) {
        if !self.timer && !self.top.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_MC));
            }
        }
    }
    pub fn toggle(&mut self) {
        if self.visible {
            self.close();
        } else {
            self.show();
        }
    }
    fn ensure(&mut self) {
        if self.top.is_invalid() {
            self.back = win::create(ID_PILL, false, true, true, true, 0, 0, 10, 10);
            self.top = win::create(ID_MISSION, true, true, false, false, 0, 0, 10, 10);
        }
        self.scr = screen_rect();
        let (w, h) = (self.scr.right - self.scr.left, self.scr.bottom - self.scr.top);
        if w != self.w || h != self.h || self.gfx.is_none() {
            self.w = w;
            self.h = h;
            self.gfx = Gfx::new(w, h);
        }
    }
    fn drop_thumbs(&mut self) {
        for t in self.thumbs.drain(..) {
            if t.handle != 0 {
                unsafe {
                    let _ = DwmUnregisterThumbnail(t.handle);
                }
            }
        }
    }
    pub fn show(&mut self) {
        let a = app();
        self.ensure();
        a.lp.close();
        a.spot.close();
        self.drop_thumbs();
        crate::refresh_windows();
        let s = a.scale;
        let wins: Vec<Win> = a.wins.clone();
        let n = wins.len();
        if n == 0 {
            return;
        }
        // grid: pick columns so cells are roughly 16:10
        let area_w = self.w as f32 - 120.0 * s;
        let area_h = self.h as f32 - 220.0 * s;
        let mut best = (1usize, 1usize, 0.0f32);
        for cols in 1..=n {
            let rows = (n + cols - 1) / cols;
            let cw = area_w / cols as f32;
            let ch = area_h / rows as f32;
            let sz = (cw / 1.6).min(ch);
            if sz > best.2 {
                best = (cols, rows, sz);
            }
        }
        let (cols, rows, _) = best;
        let cw = area_w / cols as f32;
        let ch = area_h / rows as f32;
        let me = std::process::id();
        for (i, w) in wins.iter().enumerate() {
            let mut r = RECT::default();
            unsafe {
                let _ = GetWindowRect(hw(w.hwnd), &mut r);
            }
            let (ww, wh) = (((r.right - r.left) as f32).max(80.0), ((r.bottom - r.top) as f32).max(60.0));
            let (c, rr) = (i % cols, i / cols);
            // last row centered
            let in_row = if rr == rows - 1 { n - (rows - 1) * cols } else { cols };
            let row_off = (cols - in_row) as f32 * cw / 2.0;
            let pad = 22.0 * s;
            let label_h = 36.0 * s;
            let k = ((cw - pad * 2.0) / ww).min((ch - label_h - pad) / wh).min(0.95);
            let (tw, th) = (ww * k, wh * k);
            let x = 60.0 * s + row_off + c as f32 * cw + (cw - tw) / 2.0;
            let y = 110.0 * s + rr as f32 * ch + (ch - label_h - th) / 2.0;
            let mut handle = 0isize;
            if !w.minimized {
                unsafe {
                    if let Ok(hnd) = DwmRegisterThumbnail(self.back, hw(w.hwnd)) {
                        handle = hnd;
                    }
                }
            }
            let _ = me;
            self.thumbs.push(Thumb { hwnd: w.hwnd, handle, from: r, to: (x, y, tw, th), title: w.title.clone(), exe: w.exe.clone(), minimized: w.minimized });
        }
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        self.hover = -1;
        unsafe {
            set_acrylic(self.back, if a.cfg.s.dark { 0xB0101010 } else { 0xB0DADADA }, false);
            win::set_pos(self.back, self.scr.left, self.scr.top, self.w, self.h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
            win::set_pos(self.top, self.scr.left, self.scr.top, self.w, self.h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
            win::show_noactivate(self.back);
            let _ = ShowWindow(self.top, SW_SHOW);
        }
        crate::sys::activate(hv(self.top));
        self.need = true;
        self.kick();
    }
    pub fn close(&mut self) {
        if !self.visible {
            return;
        }
        self.visible = false;
        self.open.goal = 0.0;
        self.kick();
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let moving = self.open.step(dt, 230.0, 28.0);
        if !self.visible && self.open.v < 0.02 {
            self.drop_thumbs();
            win::hide(self.top);
            win::hide(self.back);
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_MC));
            }
            return;
        }
        if !moving && !self.need {
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_MC));
            }
            return;
        }
        self.need = false;
        self.update_thumbs();
        self.draw();
    }

    fn update_thumbs(&mut self) {
        let t = self.open.v.clamp(0.0, 1.05);
        for (i, th) in self.thumbs.iter().enumerate() {
            if th.handle == 0 {
                continue;
            }
            let (fx, fy, fw, fh) = (th.from.left as f32 - self.scr.left as f32, th.from.top as f32 - self.scr.top as f32, (th.from.right - th.from.left) as f32, (th.from.bottom - th.from.top) as f32);
            let (tx, ty, tw, tth) = th.to;
            let bump = if self.hover == i as i32 { 1.025 } else { 1.0 };
            let (cx, cy) = (tx + tw / 2.0, ty + tth / 2.0);
            let (w_, h_) = (lerp(fw, tw * bump, t), lerp(fh, tth * bump, t));
            let (x, y) = (lerp(fx + fw / 2.0, cx, t) - w_ / 2.0, lerp(fy + fh / 2.0, cy, t) - h_ / 2.0);
            let props = DWM_THUMBNAIL_PROPERTIES {
                dwFlags: DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                rcDestination: RECT { left: x as i32, top: y as i32, right: (x + w_) as i32, bottom: (y + h_) as i32 },
                opacity: 255,
                fVisible: true.into(),
                ..Default::default()
            };
            unsafe {
                let _ = DwmUpdateThumbnailProperties(th.handle, &props);
            }
        }
    }

    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let (w, h) = (self.w as f32, self.h as f32);
        let t = self.open.v.clamp(0.0, 1.0);
        let fg = if dark { argb(255, 255, 255, 255) } else { argb(255, 20, 20, 20) };
        g.fill_rect(0.0, 0.0, w, h, argb(1, 0, 0, 0));
        for (i, th) in self.thumbs.iter().enumerate() {
            let (x, y, tw, tth) = th.to;
            let hov = self.hover == i as i32;
            if th.minimized {
                g.fill_round(x, y, tw, tth, 12.0 * s, with_alpha(argb(40, 255, 255, 255), t));
                let key = format!("exe:{}", th.exe);
                let pack = pack_for(&a.icons, &a.cfg, &th.exe, &crate::dock::nice_name(&th.exe));
                if let Some(img) = a.icons.get_or_logo(&key, &th.exe, pack) {
                    let isz = tw.min(tth) * 0.5;
                    g.image(&img, x + (tw - isz) / 2.0, y + (tth - isz) / 2.0, isz, isz, t);
                }
            }
            if hov {
                g.stroke_round(x - 5.0 * s, y - 5.0 * s, tw + 10.0 * s, tth + 10.0 * s, 10.0 * s, 3.0 * s, with_alpha(argb(230, 10, 132, 255), t));
                // close button
                g.fill_circle(x - 2.0 * s, y - 2.0 * s, 10.0 * s, with_alpha(argb(255, 90, 90, 94), t));
                g.line(x - 5.5 * s, y - 5.5 * s, x + 1.5 * s, y + 1.5 * s, 1.6 * s, argb(255, 255, 255, 255));
                g.line(x + 1.5 * s, y - 5.5 * s, x - 5.5 * s, y + 1.5 * s, 1.6 * s, argb(255, 255, 255, 255));
            }
            if t > 0.5 {
                let al = ((t - 0.5) * 2.0).clamp(0.0, 1.0);
                let ly = y + tth + 8.0 * s;
                let key = format!("exe:{}", th.exe);
                let pack = pack_for(&a.icons, &a.cfg, &th.exe, &crate::dock::nice_name(&th.exe));
                let title = if th.title.chars().count() > 40 { format!("{}...", th.title.chars().take(39).collect::<String>()) } else { th.title.clone() };
                let tw_ = g.measure(&title, 12.5 * s, false).min(tw + 40.0 * s);
                let total = tw_ + 24.0 * s;
                let lx = x + (tw - total) / 2.0;
                if let Some(img) = a.icons.get_or_logo(&key, &th.exe, pack) {
                    g.image(&img, lx, ly, 18.0 * s, 18.0 * s, al);
                }
                g.text(&title, lx + 24.0 * s, ly - 1.0 * s, tw_ + 4.0, 20.0 * s, 12.5 * s, false, 0, true, false, with_alpha(fg, al));
            }
        }
        if t > 0.6 {
            g.text(if self.thumbs.is_empty() { "No open windows" } else { "" }, 0.0, h / 2.0, w, 30.0 * s, 18.0 * s, false, 1, true, false, with_alpha(fg, t));
        }
        g.present_layered(self.top, self.scr.left, self.scr.top, 255);
        self.gfx = Some(g);
    }

    fn at(&self, x: f32, y: f32) -> i32 {
        for (i, th) in self.thumbs.iter().enumerate() {
            let (tx, ty, tw, tth) = th.to;
            if x >= tx - 6.0 && x <= tx + tw + 6.0 && y >= ty - 6.0 && y <= ty + tth + 40.0 {
                return i as i32;
            }
        }
        -1
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let mc = &mut app().mc;
    match m {
        WM_TIMER if wp.0 == T_MC => {
            mc.tick();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            let i = mc.at(x as f32, y as f32);
            if i != mc.hover {
                mc.hover = i;
                mc.need = true;
                mc.kick();
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let i = mc.at(x as f32, y as f32);
            if i >= 0 {
                let th = &mc.thumbs[i as usize];
                let s = app().scale;
                let (tx, ty) = (th.to.0, th.to.1);
                let on_x = (x as f32 - (tx - 2.0 * s)).abs() < 11.0 * s && (y as f32 - (ty - 2.0 * s)).abs() < 11.0 * s;
                let hwnd_ = th.hwnd;
                if on_x {
                    close_window(hwnd_);
                    mc.thumbs[i as usize].title = String::new();
                    mc.close();
                } else {
                    mc.close();
                    crate::dock::activate_hwnd(hwnd_);
                }
            } else {
                mc.close();
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == VK_ESCAPE.0 {
                mc.close();
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if mc.visible && mc.open.v > 0.9 {
                let fgw = GetForegroundWindow();
                if fgw != h && !fgw.is_invalid() {
                    mc.close();
                }
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
