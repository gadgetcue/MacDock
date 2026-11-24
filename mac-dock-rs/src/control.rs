//! Control Center, laid out like the C# build: no outer panel, a 2-column grid of glass pill tiles (media tile spanning
//! two rows, Wi-Fi, Bluetooth, LocalSend, Focus, Night Light, Project, Screenshot, Record, Dark Mode, Lock Screen,
//! Battery Saver, Airplane Mode), then Display and Sound slider tiles and an "Edit Controls" pill.
use crate::gfx::*;
use crate::status;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

#[derive(Clone, Copy, PartialEq)]
enum Id {
    Wifi,
    Bluetooth,
    Share,
    Focus,
    Bright,
    Volume,
    Night,
    Project,
    Saver,
    Airplane,
    Screenshot,
    Record,
    Dark,
    Lock,
    Edit,
    Prev,
    Play,
    Next,
    Output,
}

#[derive(Clone, Copy, PartialEq)]
enum Glyph {
    Wifi,
    Bluetooth,
    Share,
    Focus,
    Sun,
    Project,
    Camera,
    Record,
    Dark,
    Lock,
    Saver,
    Plane,
}

pub struct ControlCenter {
    hwnd: HWND,
    gfx: Option<Gfx>,
    w: i32,
    h: i32,
    pos: (i32, i32),
    pub visible: bool,
    open: Spring,
    timer: bool,
    last: std::time::Instant,
    need: bool,
    hits: Vec<(f32, f32, f32, f32, Id)>,
    drag: Option<Id>,
    brightness: f32,
    volume: f32,
    closed_at: std::time::Instant,
    dark_mode: bool,
}

fn dark_mode_on() -> bool {
    use windows::Win32::System::Registry::*;
    unsafe {
        let mut v = 1u32;
        let mut sz = 4u32;
        let key = wide(&format!("Software{0}Microsoft{0}Windows{0}CurrentVersion{0}Themes{0}Personalize", char::from(92u8)));
        let name = wide("AppsUseLightTheme");
        if RegGetValueW(HKEY_CURRENT_USER, pw(&key), pw(&name), RRF_RT_REG_DWORD, None, Some(&mut v as *mut _ as *mut _), Some(&mut sz)).is_ok() {
            return v == 0;
        }
        true
    }
}
fn set_dark_mode(dark: bool) {
    use windows::Win32::System::Registry::*;
    unsafe {
        let key = wide(&format!("Software{0}Microsoft{0}Windows{0}CurrentVersion{0}Themes{0}Personalize", char::from(92u8)));
        let mut h = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, pw(&key), None, KEY_WRITE, &mut h).is_ok() {
            let v: u32 = if dark { 0 } else { 1 };
            for n in ["AppsUseLightTheme", "SystemUsesLightTheme"] {
                let nm = wide(n);
                let _ = RegSetValueExW(h, pw(&nm), None, REG_DWORD, Some(&v.to_le_bytes()));
            }
            let _ = RegCloseKey(h);
        }
        let t = wide("ImmersiveColorSet");
        let mut res = 0usize;
        let _ = SendMessageTimeoutW(HWND(0xFFFF as *mut _), 0x001A, WPARAM(0), LPARAM(t.as_ptr() as isize), SMTO_ABORTIFHUNG, 500, Some(&mut res));
    }
}

impl ControlCenter {
    pub fn new() -> ControlCenter {
        ControlCenter { hwnd: HWND::default(), gfx: None, w: 0, h: 0, pos: (0, 0), visible: false, open: Spring::new(0.0), timer: false, last: std::time::Instant::now(), need: true, hits: vec![], drag: None, brightness: 0.7, volume: 0.5, closed_at: std::time::Instant::now() - std::time::Duration::from_secs(5), dark_mode: true }
    }
    pub fn dirty(&mut self) {
        if self.visible && self.drag.is_none() {
            self.volume = app().stat.get().volume;
            self.need = true;
            self.kick();
        }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            crate::clock::start(crate::clock::CC);
        }
    }
    pub fn toggle(&mut self) {
        if self.visible {
            self.close();
        } else if self.closed_at.elapsed().as_millis() > 350 {
            self.show();
        }
    }
    pub fn show(&mut self) {
        let a = app();
        let s = a.scale;
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_CC, true, true, false, false, 0, 0, 10, 10);
        }
        let scr = screen_rect();
        let m = 12.0 * s;
        let (w, h) = ((332.0 * s + 2.0 * m) as i32, (7.0 * 74.0 * s + 2.0 * 64.0 * s + 56.0 * s + 2.0 * m + 8.0 * s) as i32);
        if w != self.w || h != self.h || self.gfx.is_none() {
            self.w = w;
            self.h = h;
            self.gfx = Gfx::new(w, h);
        }
        self.pos = (scr.right - w - (4.0 * s) as i32, scr.top + a.bar.height() + (2.0 * s) as i32 - m as i32);
        self.volume = a.stat.get().volume;
        if let Some(b) = status::brightness_ioctl() {
            self.brightness = b as f32 / 100.0;
        }
        self.dark_mode = dark_mode_on();
        a.lp.close();
        a.spot.close();
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        unsafe {
            let _ = ShowWindow(self.hwnd, SW_SHOW);
        }
        crate::sys::activate(hv(self.hwnd));
        self.need = true;
        self.kick();
    }
    pub fn close(&mut self) {
        if !self.visible {
            return;
        }
        self.visible = false;
        self.closed_at = std::time::Instant::now();
        self.open.goal = 0.0;
        self.kick();
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let moving = self.open.step(dt, 380.0, 32.0);
        if !self.visible && self.open.v < 0.03 {
            win::hide(self.hwnd);
            self.timer = false;
            crate::clock::stop(crate::clock::CC);
            return;
        }
        if !moving && !self.need {
            self.timer = false;
            crate::clock::stop(crate::clock::CC);
            return;
        }
        self.need = false;
        self.draw();
    }

    fn glyph(g: &mut Gfx, k: Glyph, cx: f32, cy: f32, s: f32, c: u32) {
        let w = 1.7 * s;
        match k {
            Glyph::Wifi => {
                g.fill_circle(cx, cy + 6.0 * s, 1.6 * s, c);
                for r in [5.0, 9.0, 13.0] {
                    g.arc(cx, cy + 6.0 * s, r * s, 225.0, 90.0, w, c);
                }
            }
            Glyph::Bluetooth => g.polyline(&[(cx - 4.5 * s, cy - 4.5 * s), (cx + 4.5 * s, cy + 4.5 * s), (cx, cy + 9.0 * s), (cx, cy - 9.0 * s), (cx + 4.5 * s, cy - 4.5 * s), (cx - 4.5 * s, cy + 4.5 * s)], w, c),
            Glyph::Share => {
                g.fill_circle(cx, cy, 2.4 * s, c);
                g.stroke_circle(cx, cy, 6.5 * s, w, c);
                for i in 0..8 {
                    let a = i as f32 * std::f32::consts::FRAC_PI_4;
                    g.line(cx + a.cos() * 8.5 * s, cy + a.sin() * 8.5 * s, cx + a.cos() * 10.0 * s, cy + a.sin() * 10.0 * s, w, c);
                }
            }
            Glyph::Focus => {
                g.stroke_circle(cx, cy, 8.0 * s, w, c);
                g.fill_circle(cx, cy, 4.5 * s, c);
            }
            Glyph::Sun => {
                g.fill_circle(cx, cy, 4.5 * s, c);
                for i in 0..8 {
                    let a = i as f32 * std::f32::consts::FRAC_PI_4;
                    g.line(cx + a.cos() * 7.5 * s, cy + a.sin() * 7.5 * s, cx + a.cos() * 10.0 * s, cy + a.sin() * 10.0 * s, w, c);
                }
            }
            Glyph::Project => {
                g.stroke_round(cx - 9.0 * s, cy - 7.0 * s, 13.0 * s, 10.0 * s, 2.0 * s, w, c);
                g.fill_round(cx - 2.0 * s, cy - 2.0 * s, 11.0 * s, 9.0 * s, 2.0 * s, c);
            }
            Glyph::Camera => {
                g.stroke_round(cx - 9.0 * s, cy - 6.0 * s, 18.0 * s, 13.0 * s, 3.0 * s, w, c);
                g.stroke_circle(cx, cy + 0.5 * s, 3.5 * s, w, c);
            }
            Glyph::Record => {
                g.stroke_circle(cx, cy, 9.0 * s, w, c);
                g.fill_circle(cx, cy, 4.5 * s, c);
            }
            Glyph::Dark => {
                g.stroke_circle(cx, cy, 8.5 * s, w, c);
                g.arc(cx, cy, 4.2 * s, 90.0, 180.0, 8.4 * s, c);
            }
            Glyph::Lock => {
                g.fill_round(cx - 7.0 * s, cy - 2.0 * s, 14.0 * s, 10.0 * s, 2.5 * s, c);
                g.arc(cx, cy - 2.0 * s, 4.5 * s, 180.0, 180.0, w, c);
            }
            Glyph::Saver => {
                g.stroke_round(cx - 9.0 * s, cy - 5.0 * s, 16.0 * s, 10.0 * s, 2.5 * s, w, c);
                g.fill_round(cx + 8.0 * s, cy - 2.0 * s, 2.0 * s, 4.0 * s, 1.0 * s, c);
                g.fill_circle(cx - 1.0 * s, cy, 2.2 * s, c);
            }
            Glyph::Plane => g.fill_poly(&[(cx - 10.0 * s, cy + 1.0 * s), (cx + 10.0 * s, cy - 6.0 * s), (cx + 4.0 * s, cy), (cx + 6.0 * s, cy + 8.0 * s), (cx + 1.0 * s, cy + 3.0 * s), (cx - 4.0 * s, cy + 4.0 * s)], c),
        }
    }

    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let st = a.stat.get();
        let media = a.media.get();
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let t = self.open.v.clamp(0.0, 1.0);
        let al = |v: f32| ((v * t) as u8);
        let m = 12.0 * s;
        let sc = 0.97 + 0.03 * t;
        let (cw, th, gap) = (161.0 * s, 64.0 * s, 10.0 * s);
        let x0 = m + (332.0 * s) * (1.0 - sc) / 2.0 + 0.0;
        let tile_fill = if dark { argb(al(214.0), 28, 28, 33) } else { argb(al(220.0), 246, 246, 249) };
        let tile_rim = if dark { argb(al(46.0), 255, 255, 255) } else { argb(al(70.0), 255, 255, 255) };
        let fg = if dark { argb(al(255.0), 246, 246, 248) } else { argb(al(255.0), 28, 28, 32) };
        let dim = if dark { argb(al(160.0), 255, 255, 255) } else { argb(al(150.0), 0, 0, 0) };
        let accent = argb(al(255.0), 10, 132, 255);
        self.hits.clear();
        let mut y = m;
        // left/right column helper
        let tile = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Id)>, col: usize, row_y: f32, id: Id, title: &str, sub: &str, on: bool, gl: Glyph, chevron: bool| {
            let x = x0 + col as f32 * (cw + gap);
            g.fill_round(x, row_y, cw, th, th * 0.46, tile_fill);
            g.stroke_round(x + 0.5, row_y + 0.5, cw - 1.0, th - 1.0, th * 0.46, 1.0, tile_rim);
            let (icx, icy, ir) = (x + 14.0 * s + 19.0 * s, row_y + th / 2.0, 19.0 * s);
            g.fill_circle(icx, icy, ir, if on { argb(al(255.0), 250, 250, 252) } else { argb(al(255.0), 78, 78, 86) });
            Self::glyph(g, gl, icx, icy, s, if on { accent } else { argb(al(255.0), 245, 245, 247) });
            let tx = x + 14.0 * s + 38.0 * s + 10.0 * s;
            let tw = cw - (tx - x) - if chevron { 22.0 * s } else { 12.0 * s };
            if sub.is_empty() {
                g.text(title, tx, row_y, tw, th, 14.0 * s, true, 0, true, false, fg);
            } else {
                g.text(title, tx, row_y + 11.0 * s, tw, 22.0 * s, 14.0 * s, true, 0, true, false, fg);
                g.text(sub, tx, row_y + 32.0 * s, tw, 20.0 * s, 11.5 * s, false, 0, true, false, dim);
            }
            if chevron {
                g.polyline(&[(x + cw - 20.0 * s, row_y + th / 2.0 - 5.0 * s), (x + cw - 15.0 * s, row_y + th / 2.0), (x + cw - 20.0 * s, row_y + th / 2.0 + 5.0 * s)], 1.6 * s, fg);
            }
            hits.push((x, row_y, cw, th, id));
        };
        // media tile (spans two rows, left)
        {
            let (x, mh) = (x0, th * 2.0 + gap);
            g.fill_round(x, y, cw, mh, th * 0.46, tile_fill);
            g.stroke_round(x + 0.5, y + 0.5, cw - 1.0, mh - 1.0, th * 0.46, 1.0, tile_rim);
            g.fill_round(x + 16.0 * s, y + 16.0 * s, 44.0 * s, 44.0 * s, 9.0 * s, argb(al(255.0), 58, 58, 64));
            let title = if media.active { media.title.clone() } else { "Not Playing".to_string() };
            let t = if title.chars().count() > 9 { format!("{}...", title.chars().take(8).collect::<String>()) } else { title };
            g.text(&t, x + 68.0 * s, y + 16.0 * s, cw - 76.0 * s, 24.0 * s, 13.5 * s, true, 0, true, false, fg);
            if media.active {
                g.text(&media.artist, x + 68.0 * s, y + 38.0 * s, cw - 76.0 * s, 20.0 * s, 11.0 * s, false, 0, true, false, dim);
            }
            let cy = y + mh - 36.0 * s;
            let cxm = x + cw / 2.0;
            let tri = |g: &mut Gfx, cx: f32, dir: f32| {
                g.fill_poly(&[(cx - 3.0 * s * dir, cy - 7.0 * s), (cx - 3.0 * s * dir, cy + 7.0 * s), (cx + 8.0 * s * dir, cy)], fg);
                g.fill_poly(&[(cx - 12.0 * s * dir, cy - 7.0 * s), (cx - 12.0 * s * dir, cy + 7.0 * s), (cx - 1.0 * s * dir, cy)], fg);
            };
            tri(&mut g, cxm - 44.0 * s, -1.0);
            if media.playing {
                g.fill_rect(cxm - 6.0 * s, cy - 8.0 * s, 4.5 * s, 16.0 * s, fg);
                g.fill_rect(cxm + 2.0 * s, cy - 8.0 * s, 4.5 * s, 16.0 * s, fg);
            } else {
                g.fill_poly(&[(cxm - 6.0 * s, cy - 9.0 * s), (cxm - 6.0 * s, cy + 9.0 * s), (cxm + 9.0 * s, cy)], fg);
            }
            tri(&mut g, cxm + 44.0 * s, 1.0);
            self.hits.push((cxm - 62.0 * s, cy - 16.0 * s, 36.0 * s, 32.0 * s, Id::Prev));
            self.hits.push((cxm - 16.0 * s, cy - 16.0 * s, 32.0 * s, 32.0 * s, Id::Play));
            self.hits.push((cxm + 26.0 * s, cy - 16.0 * s, 36.0 * s, 32.0 * s, Id::Next));
        }
        let rows = |i: usize| y + i as f32 * (th + gap);
        tile(&mut g, &mut self.hits, 1, rows(0), Id::Wifi, "Wi-Fi", if st.wifi { "Connected" } else if st.ethernet { "Ethernet" } else { "Off" }, st.wifi || st.ethernet, Glyph::Wifi, true);
        tile(&mut g, &mut self.hits, 1, rows(1), Id::Bluetooth, "Bluetooth", "Settings", true, Glyph::Bluetooth, true);
        tile(&mut g, &mut self.hits, 0, rows(2), Id::Share, "LocalSend", "", false, Glyph::Share, false);
        tile(&mut g, &mut self.hits, 1, rows(2), Id::Focus, "Focus", "Off", false, Glyph::Focus, false);
        tile(&mut g, &mut self.hits, 0, rows(3), Id::Night, "Night Light", "Settings", false, Glyph::Sun, false);
        tile(&mut g, &mut self.hits, 1, rows(3), Id::Project, "Project", "", false, Glyph::Project, false);
        tile(&mut g, &mut self.hits, 0, rows(4), Id::Screenshot, "Screenshot", "Capture screen", false, Glyph::Camera, false);
        tile(&mut g, &mut self.hits, 1, rows(4), Id::Record, "Record", if crate::capture::recording() { "Stop" } else { "Start" }, crate::capture::recording(), Glyph::Record, false);
        tile(&mut g, &mut self.hits, 0, rows(5), Id::Dark, "Dark Mode", if self.dark_mode { "On" } else { "Off" }, self.dark_mode, Glyph::Dark, false);
        tile(&mut g, &mut self.hits, 1, rows(5), Id::Lock, "Lock Screen", "", false, Glyph::Lock, false);
        tile(&mut g, &mut self.hits, 0, rows(6), Id::Saver, "Battery Saver", if st.saver { "On" } else { "Off" }, st.saver, Glyph::Saver, false);
        tile(&mut g, &mut self.hits, 1, rows(6), Id::Airplane, "Airplane", "Settings", false, Glyph::Plane, false);
        let mut sy = rows(7);
        // slider tiles
        let slider = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Id)>, sy: f32, id: Id, label: &str, v: f32, left_small: Glyph, right_big: Glyph, output: bool| {
            let sw = cw * 2.0 + gap;
            g.fill_round(x0, sy, sw, th, th * 0.46, tile_fill);
            g.stroke_round(x0 + 0.5, sy + 0.5, sw - 1.0, th - 1.0, th * 0.46, 1.0, tile_rim);
            g.text(label, x0 + 20.0 * s, sy + 8.0 * s, sw - 40.0 * s, 22.0 * s, 14.0 * s, true, 0, true, false, fg);
            let ty = sy + 44.0 * s;
            let tx0 = x0 + 34.0 * s;
            let tx1 = x0 + sw - if output { 78.0 * s } else { 52.0 * s };
            g.fill_circle(x0 + 24.0 * s, ty, 2.4 * s, fg);
            let _ = left_small;
            g.fill_round(tx0, ty - 2.5 * s, tx1 - tx0, 5.0 * s, 2.5 * s, if dark { argb(al(255.0), 78, 78, 86) } else { argb(al(255.0), 200, 200, 208) });
            g.fill_round(tx0, ty - 2.5 * s, ((tx1 - tx0) * v).max(5.0 * s), 5.0 * s, 2.5 * s, argb(al(255.0), 250, 250, 252));
            // right end: large glyph
            let big = if right_big == Glyph::Sun { (x0 + sw - 26.0 * s, 13.0 * s) } else { (x0 + sw - if output { 64.0 * s } else { 26.0 * s }, 11.0 * s) };
            if right_big == Glyph::Sun {
                g.fill_circle(big.0, ty, big.1 * 0.62, argb(al(255.0), 250, 250, 252));
            } else {
                g.fill_poly(&[(big.0 - 8.0 * s, ty - 3.0 * s), (big.0 - 4.0 * s, ty - 3.0 * s), (big.0 + 1.0 * s, ty - 8.0 * s), (big.0 + 1.0 * s, ty + 8.0 * s), (big.0 - 4.0 * s, ty + 3.0 * s), (big.0 - 8.0 * s, ty + 3.0 * s)], fg);
                g.arc(big.0 + 1.0 * s, ty, 5.0 * s, -50.0, 100.0, 1.6 * s, fg);
            }
            if output {
                g.fill_circle(x0 + sw - 30.0 * s, ty, 14.0 * s, argb(al(255.0), 78, 78, 86));
                g.stroke_round(x0 + sw - 38.0 * s, ty - 7.0 * s, 16.0 * s, 11.0 * s, 2.0 * s, 1.5 * s, fg);
                hits.push((x0 + sw - 46.0 * s, ty - 16.0 * s, 32.0 * s, 32.0 * s, Id::Output));
            }
            hits.push((tx0 - 6.0 * s, sy + 26.0 * s, tx1 - tx0 + 12.0 * s, 36.0 * s, id));
        };
        slider(&mut g, &mut self.hits, sy, Id::Bright, "Display", self.brightness, Glyph::Sun, Glyph::Sun, false);
        sy += th + gap;
        slider(&mut g, &mut self.hits, sy, Id::Volume, "Sound", self.volume, Glyph::Sun, Glyph::Focus, true);
        sy += th + gap;
        // Edit Controls pill
        let (pw_, ph) = (116.0 * s, 30.0 * s);
        let px = x0 + (cw * 2.0 + gap - pw_) / 2.0;
        g.fill_round(px, sy, pw_, ph, ph / 2.0, tile_fill);
        g.stroke_round(px + 0.5, sy + 0.5, pw_ - 1.0, ph - 1.0, ph / 2.0, 1.0, tile_rim);
        g.text("Edit Controls", px, sy, pw_, ph, 12.5 * s, false, 1, true, false, fg);
        self.hits.push((px, sy, pw_, ph, Id::Edit));
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }

    fn hit(&self, x: f32, y: f32) -> Option<(Id, f32, f32, f32, f32)> {
        self.hits.iter().rev().find(|h| x >= h.0 && x <= h.0 + h.2 && y >= h.1 && y <= h.1 + h.3).map(|h| (h.4, h.0, h.1, h.2, h.3))
    }

    fn slide(&mut self, id: Id, x: f32, r: (f32, f32, f32, f32), finish: bool) {
        let s = app().scale;
        let (tx, tw) = (r.0 + 6.0 * s, r.2 - 12.0 * s);
        let v = ((x - tx) / tw).clamp(0.0, 1.0);
        match id {
            Id::Volume => {
                self.volume = v;
                status::set_volume(v);
            }
            Id::Bright => {
                self.brightness = v;
                if finish {
                    status::set_brightness_async((v * 100.0).round().max(1.0) as i32);
                }
            }
            _ => {}
        }
        self.need = true;
        self.kick();
    }

    fn press(&mut self, id: Id) {
        let a = app();
        let combo = |mods: &[VIRTUAL_KEY], key: VIRTUAL_KEY| unsafe {
            let mk = |vk: VIRTUAL_KEY, up: bool| INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) }, ..Default::default() } } };
            let mut v = vec![];
            for m in mods {
                v.push(mk(*m, false));
            }
            v.push(mk(key, false));
            v.push(mk(key, true));
            for m in mods.iter().rev() {
                v.push(mk(*m, true));
            }
            let _ = SendInput(&v, std::mem::size_of::<INPUT>() as i32);
        };
        match id {
            Id::Wifi => shell_open("ms-settings:network-wifi"),
            Id::Bluetooth => shell_open("ms-settings:bluetooth"),
            Id::Focus => shell_open("ms-settings:quietmomentshome"),
            Id::Share => {
                self.close();
                if let Some(p) = crate::settings::pick_file("All files\0*.*\0\0") {
                    a.lspick.open(vec![p]);
                }
            }
            Id::Night => shell_open("ms-settings:nightlight"),
            Id::Saver => shell_open("ms-settings:batterysaver"),
            Id::Airplane => shell_open("ms-settings:network-airplanemode"),
            Id::Project => {
                self.close();
                combo(&[VK_LWIN], VK_P);
            }
            Id::Screenshot => {
                self.close();
                crate::capture::screenshot();
            }
            Id::Record => {
                self.close();
                crate::capture::toggle_record();
            }
            Id::Dark => {
                self.dark_mode = !self.dark_mode;
                set_dark_mode(self.dark_mode);
                a.cfg.s.dark = self.dark_mode;
                a.cfg.set_option("theme", if self.dark_mode { "dark" } else { "light" });
                win::enable_dark_menus(self.dark_mode);
                a.dock.apply_theme();
                a.bar.apply_theme(self.dark_mode);
            }
            Id::Lock => {
                self.close();
                unsafe {
                    let _ = windows::Win32::System::Shutdown::LockWorkStation();
                }
            }
            Id::Edit => {
                self.close();
                a.set.show(2);
            }
            Id::Output => shell_open("ms-settings:sound"),
            Id::Play => crate::media::play_pause(),
            Id::Next => crate::media::next(),
            Id::Prev => crate::media::prev(),
            _ => {}
        }
        self.need = true;
        self.kick();
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let c = &mut app().cc;
    match m {
        WM_LBUTTONDOWN => {
            let (x, y) = win::lparam_xy(lp);
            if let Some((id, rx, ry, rw, rh)) = c.hit(x as f32, y as f32) {
                if matches!(id, Id::Volume | Id::Bright) {
                    c.drag = Some(id);
                    SetCapture(h);
                    c.slide(id, x as f32, (rx, ry, rw, rh), false);
                } else {
                    c.press(id);
                }
            }
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            if let Some(id) = c.drag {
                let (x, _) = win::lparam_xy(lp);
                if let Some(r) = c.hits.iter().find(|h| h.4 == id).map(|h| (h.0, h.1, h.2, h.3)) {
                    c.slide(id, x as f32, r, false);
                }
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if let Some(id) = c.drag.take() {
                let _ = ReleaseCapture();
                let (x, _) = win::lparam_xy(lp);
                if let Some(r) = c.hits.iter().find(|h| h.4 == id).map(|h| (h.0, h.1, h.2, h.3)) {
                    c.slide(id, x as f32, r, true);
                }
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == VK_ESCAPE.0 {
                c.close();
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if c.visible && c.open.v > 0.8 {
                let fgw = GetForegroundWindow();
                if fgw != h {
                    c.close();
                }
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
