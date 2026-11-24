//! Hover window previews, laid out like the C# WindowPreview: 260x160 tiles (live DWM thumbnail scaled to fit, never
//! enlarged) with a 26 px title, 12 px padding, 10 px gap, wrapping into centered rows, a hover highlight and a round
//! close button (28 px, 8 px inset). Minimized windows show their app icon instead of a thumbnail. The popup stays open
//! while the pointer is over it or in the 76 px band between it and the dock icon.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::sys::*;
use crate::win;
use crate::*;
use windows::Win32::Graphics::Dwm::*;

const TILE_W: f32 = 260.0;
const THUMB_H: f32 = 160.0;
const TITLE_H: f32 = 26.0;
const PAD: f32 = 12.0;
const GAP: f32 = 10.0;
const X_SIZE: f32 = 28.0;
const X_INSET: f32 = 8.0;

struct Th {
    hwnd: isize,
    handle: isize,
    cell: (f32, f32, f32, f32), // tile cell (x, y, w, h) in popup coordinates
    title: String,
    exe: String,
    minimized: bool,
}

pub struct Preview {
    back: HWND,
    top: HWND,
    gfx: Option<Gfx>,
    pos: (i32, i32),
    size: (i32, i32),
    zone_bottom: i32, // screen y down to which the popup stays open (covers the dock icon band)
    thumbs: Vec<Th>,
    pub visible: bool,
    key: String,
    pending: Option<String>,
    hover: i32,
    hover_x: bool,
}

impl Preview {
    pub fn new() -> Preview {
        Preview { back: HWND::default(), top: HWND::default(), gfx: None, pos: (0, 0), size: (0, 0), zone_bottom: 0, thumbs: vec![], visible: false, key: String::new(), pending: None, hover: -1, hover_x: false }
    }
    fn ensure(&mut self) {
        if self.top.is_invalid() {
            self.back = win::create(ID_PILL, false, true, true, true, 0, 0, 10, 10);
            unsafe {
                let round: i32 = 2;
                let _ = DwmSetWindowAttribute(self.back, DWMWINDOWATTRIBUTE(33), &round as *const _ as *const _, 4);
            }
            self.top = win::create(ID_PREVIEW, true, true, true, false, 0, 0, 10, 10);
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
    /// Called by the dock when the hovered item changes (key = item key, empty = none).
    pub fn hover_item(&mut self, key: &str) {
        if !app().cfg.ob("window-previews", true) {
            return;
        }
        let msg = app().msg;
        if key.is_empty() {
            self.pending = None;
            unsafe {
                SetTimer(Some(msg), T_PREVIEW_HIDE, 220, None);
            }
            return;
        }
        if self.visible && self.key == key {
            unsafe {
                let _ = KillTimer(Some(msg), T_PREVIEW_HIDE);
            }
            return;
        }
        self.pending = Some(key.to_string());
        unsafe {
            let _ = KillTimer(Some(msg), T_PREVIEW_HIDE);
            SetTimer(Some(msg), T_PREVIEW, if self.visible { 90 } else { 380 }, None);
        }
    }
    pub fn on_show_timer(&mut self) {
        let msg = app().msg;
        unsafe {
            let _ = KillTimer(Some(msg), T_PREVIEW);
        }
        let Some(key) = self.pending.take() else { return };
        self.show(&key);
    }
    pub fn on_hide_timer(&mut self) {
        let msg = app().msg;
        unsafe {
            let _ = KillTimer(Some(msg), T_PREVIEW_HIDE);
        }
        if self.visible {
            let p = cursor_pos();
            let inside = p.x >= self.pos.0 && p.x <= self.pos.0 + self.size.0 && p.y >= self.pos.1 && p.y <= self.zone_bottom;
            if inside {
                unsafe {
                    SetTimer(Some(msg), T_PREVIEW_HIDE, 220, None);
                }
                return;
            }
        }
        self.hide();
    }
    pub fn hide(&mut self) {
        if !self.visible {
            return;
        }
        self.visible = false;
        self.hover = -1;
        self.drop_thumbs();
        win::hide(self.top);
        win::hide(self.back);
    }

    fn show(&mut self, key: &str) {
        let a = app();
        let Some(it) = a.dock.items.iter().find(|i| i.key == key) else { return };
        if it.wins.is_empty() || a.lp.visible || a.mc.visible || a.dock.dragging() || a.fan.visible {
            return;
        }
        let wins: Vec<(isize, String, String, bool)> = it.wins.iter().filter_map(|h| a.wins.iter().find(|w| w.hwnd == *h).map(|w| (w.hwnd, w.title.clone(), w.exe.clone(), w.minimized))).take(12).collect();
        let Some((cx, top, _)) = a.dock.anchor_for(&|i: &crate::dock::Item| i.key == key) else { return };
        if wins.is_empty() {
            return;
        }
        self.ensure();
        self.drop_thumbs();
        let scr = screen_rect();
        let n = wins.len();
        let cell_h = THUMB_H + TITLE_H;
        let area_w = (scr.right - scr.left) as f32;
        let per_row = ((area_w - 40.0 - PAD * 2.0 + GAP) / (TILE_W + GAP)).floor().max(1.0).min(n as f32) as usize;
        let rows = (n + per_row - 1) / per_row;
        let w = per_row as f32 * TILE_W + (per_row as f32 - 1.0) * GAP + PAD * 2.0;
        let h = rows as f32 * cell_h + (rows as f32 - 1.0) * GAP + PAD * 2.0;
        let x = (cx - w / 2.0).clamp(scr.left as f32 + 4.0, scr.right as f32 - w - 4.0);
        let y = (top - h - 8.0).max(scr.top as f32 + a.bar.height() as f32 + 4.0);
        self.pos = (x as i32, y as i32);
        self.size = (w as i32, h as i32);
        self.zone_bottom = (top + 76.0) as i32;
        self.gfx = Gfx::new(self.size.0, self.size.1);
        let hwnd_back = self.back;
        for (i, (hwnd, title, exe, minimized)) in wins.iter().enumerate() {
            let (r, c) = (i / per_row, i % per_row);
            let in_row = (n - r * per_row).min(per_row);
            let row_off = (per_row - in_row) as f32 * (TILE_W + GAP) / 2.0; // short last row is centered
            let cell = (PAD + row_off + c as f32 * (TILE_W + GAP), PAD + r as f32 * (cell_h + GAP), TILE_W, cell_h);
            let mut handle = 0isize;
            if !*minimized {
                unsafe {
                    if let Ok(hnd) = DwmRegisterThumbnail(hwnd_back, hw(*hwnd)) {
                        let (sw, sh) = match DwmQueryThumbnailSourceSize(hnd) {
                            Ok(s) if s.cx > 0 && s.cy > 0 => (s.cx as f32, s.cy as f32),
                            _ => (TILE_W, THUMB_H),
                        };
                        let (aw, ah) = (TILE_W - 12.0, THUMB_H - 12.0);
                        let k = (aw / sw).min(ah / sh).min(1.0);
                        let (dw, dh) = ((sw * k).max(1.0), (sh * k).max(1.0));
                        let dx = (self.pos.0 as f32 - self.pos.0 as f32) + cell.0 + (TILE_W - dw) / 2.0;
                        let dy = cell.1 + (THUMB_H - dh) / 2.0;
                        let props = DWM_THUMBNAIL_PROPERTIES { dwFlags: DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY, rcDestination: RECT { left: dx as i32, top: dy as i32, right: (dx + dw) as i32, bottom: (dy + dh) as i32 }, opacity: 255, fVisible: true.into(), ..Default::default() };
                        let _ = DwmUpdateThumbnailProperties(hnd, &props);
                        handle = hnd;
                    }
                }
            }
            self.thumbs.push(Th { hwnd: *hwnd, handle, cell, title: title.clone(), exe: exe.clone(), minimized: *minimized });
        }
        self.key = key.to_string();
        let dark = a.cfg.s.dark;
        unsafe {
            set_acrylic(self.back, if dark { 0x8C181818 } else { 0x8CF0F0F0 }, false);
            win::set_pos(self.back, self.pos.0, self.pos.1, self.size.0, self.size.1, Some(HWND_TOPMOST), SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        self.hover = -1;
        self.visible = true;
        self.redraw();
        unsafe {
            let _ = SetWindowPos(self.top, Some(HWND_TOPMOST), self.pos.0, self.pos.1, self.size.0, self.size.1, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    fn x_rect(cell: &(f32, f32, f32, f32)) -> (f32, f32, f32, f32) {
        (cell.0 + X_INSET, cell.1 + X_INSET, X_SIZE, X_SIZE)
    }

    fn redraw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let (w, h) = (g.w as f32, g.h as f32);
        g.stroke_round(0.5, 0.5, w - 1.0, h - 1.0, 14.0, 1.0, if dark { argb(60, 255, 255, 255) } else { argb(90, 255, 255, 255) });
        g.fill_rect(0.0, 0.0, w, h, argb(1, 0, 0, 0));
        let fg = if dark { argb(255, 240, 240, 244) } else { argb(255, 30, 30, 34) };
        for (i, t) in self.thumbs.iter().enumerate() {
            let hov = self.hover == i as i32;
            if hov {
                g.fill_round(t.cell.0, t.cell.1, t.cell.2, t.cell.3, 10.0, if dark { argb(34, 255, 255, 255) } else { argb(40, 0, 0, 0) });
            }
            if t.minimized {
                let key = format!("exe:{}", t.exe);
                let pack = pack_for(&a.icons, &a.cfg, &t.exe, &crate::dock::nice_name(&t.exe));
                if let Some(img) = a.icons.get_or_logo(&key, &t.exe, pack) {
                    g.image(&img, t.cell.0 + (TILE_W - 56.0) / 2.0, t.cell.1 + (THUMB_H - 56.0) / 2.0, 56.0, 56.0, 1.0);
                }
            }
            let title = if t.title.chars().count() > 34 { format!("{}...", t.title.chars().take(33).collect::<String>()) } else { t.title.clone() };
            g.text(&title, t.cell.0 + 10.0, t.cell.1 + THUMB_H - 2.0, TILE_W - 20.0, TITLE_H, 12.0 * s, false, 1, true, false, fg);
            if hov {
                let (xx, xy, xs, _) = Self::x_rect(&t.cell);
                let c = if self.hover_x { argb(255, 255, 69, 58) } else { argb(230, 70, 70, 76) };
                g.fill_circle(xx + xs / 2.0, xy + xs / 2.0, xs / 2.0, c);
                g.stroke_circle(xx + xs / 2.0, xy + xs / 2.0, xs / 2.0 - 0.7, 1.5, argb(200, 255, 255, 255));
                let m = 5.0;
                g.line(xx + xs / 2.0 - m, xy + xs / 2.0 - m, xx + xs / 2.0 + m, xy + xs / 2.0 + m, 1.8, argb(255, 255, 255, 255));
                g.line(xx + xs / 2.0 + m, xy + xs / 2.0 - m, xx + xs / 2.0 - m, xy + xs / 2.0 + m, 1.8, argb(255, 255, 255, 255));
            }
        }
        g.present_layered(self.top, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }

    fn at(&self, x: f32, y: f32) -> i32 {
        for (i, t) in self.thumbs.iter().enumerate() {
            if x >= t.cell.0 && x <= t.cell.0 + t.cell.2 && y >= t.cell.1 && y <= t.cell.1 + t.cell.3 {
                return i as i32;
            }
        }
        -1
    }
    fn on_x(&self, i: i32, x: f32, y: f32) -> bool {
        if i < 0 {
            return false;
        }
        let r = Self::x_rect(&self.thumbs[i as usize].cell);
        x >= r.0 && x <= r.0 + r.2 && y >= r.1 && y <= r.1 + r.3
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let p = &mut app().preview;
    match m {
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            let (i, ox) = {
                let i = p.at(x as f32, y as f32);
                (i, p.on_x(i, x as f32, y as f32))
            };
            if i != p.hover || ox != p.hover_x {
                p.hover = i;
                p.hover_x = ox;
                p.redraw();
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let i = p.at(x as f32, y as f32);
            if i >= 0 {
                let hw_ = p.thumbs[i as usize].hwnd;
                if p.on_x(i, x as f32, y as f32) {
                    close_window(hw_);
                    p.hide();
                } else {
                    p.hide();
                    crate::dock::activate_hwnd(hw_);
                }
            }
            LRESULT(0)
        }
        WM_MBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let i = p.at(x as f32, y as f32);
            if i >= 0 {
                close_window(p.thumbs[i as usize].hwnd);
                p.hide();
            }
            LRESULT(0)
        }
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
