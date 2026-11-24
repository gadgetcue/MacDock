//! Dynamic Island: black pill over the menu bar. Springs open for notifications, power events, copied text,
//! now playing and a hover peek; click it for the notification list. Redraws only while it moves.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::notify::Note;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::Foundation::*;
use windows::Win32::System::DataExchange::*;
use windows::Win32::System::Memory::*;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;

#[derive(Clone, PartialEq)]
pub(crate) enum Mode {
    Idle,
    Notif(i64),
    Clip,
    Power,
    Peek,
    Center,
    Toast,
}

pub struct Island {
    pub(crate) hwnd: HWND,
    pub(crate) gfx: Option<Gfx>,
    pub(crate) s: f32,
    pub(crate) w: i32,
    pub(crate) h: i32,
    pub(crate) scr: RECT,
    pub(crate) mode: Mode,
    pub(crate) until: std::time::Instant,
    pub(crate) cur: Option<Note>,
    pub(crate) clip_text: String,
    pub clips: Vec<String>,
    pub(crate) power_text: String,
    pub(crate) sw: Spring,
    pub(crate) sh: Spring,
    pub(crate) timer: bool,
    pub(crate) last: std::time::Instant,
    pub(crate) need: bool,
    pub(crate) hover: bool,
    pub(crate) hidden: bool,
    pub(crate) last_ac: Option<bool>,
    pub(crate) bar_h: f32,
    pub(crate) t_anim: f32,
    pub(crate) x: crate::islandx::Extras,
    born_at: std::time::Instant,
}

impl Island {
    pub fn new() -> Island {
        Island { hwnd: HWND::default(), gfx: None, s: 1.0, w: 0, h: 0, scr: RECT::default(), mode: Mode::Idle, until: std::time::Instant::now(), cur: None, clip_text: String::new(), clips: vec![], power_text: String::new(), sw: Spring::new(120.0), sh: Spring::new(20.0), timer: false, last: std::time::Instant::now(), need: true, hover: false, hidden: false, last_ac: None, bar_h: 28.0, t_anim: 0.0, x: crate::islandx::Extras::new(), born_at: std::time::Instant::now() }
    }
    pub fn create(&mut self, s: f32) {
        self.hwnd = win::create(ID_ISLAND, true, true, true, false, 0, 0, 10, 10);
        self.relayout(s);
        if app().cfg.s.island_on {
            win::show_noactivate(self.hwnd);
        }
        unsafe {
            windows::Win32::System::DataExchange::AddClipboardFormatListener(app().msg).ok();
            SetTimer(Some(self.hwnd), crate::islandx::T_EXTRA, 100, None);
        }
    }
    pub fn relayout(&mut self, s: f32) {
        self.s = s;
        self.scr = screen_rect();
        self.bar_h = app().bar.height().max((28.0 * s) as i32) as f32;
        self.w = (520.0 * s) as i32;
        self.h = (360.0 * s) as i32 + (30.0 * s) as i32;
        self.gfx = Gfx::new(self.w, self.h);
        let x = self.scr.left + ((self.scr.right - self.scr.left) - self.w) / 2;
        win::set_pos(self.hwnd, x, self.scr.top, self.w, self.h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        self.need = true;
        self.kick();
    }
    pub fn set_fullscreen(&mut self, fs: bool) {
        self.hidden = fs;
        if fs {
            win::hide(self.hwnd);
        } else if app().cfg.s.island_on {
            win::show_noactivate(self.hwnd);
            self.need = true;
            self.kick();
        }
    }
    pub fn set_enabled(&mut self, on: bool) {
        if on && !self.hidden {
            win::show_noactivate(self.hwnd);
            self.need = true;
            self.kick();
        } else {
            win::hide(self.hwnd);
        }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_ISLAND));
            }
        }
    }
    pub fn dirty(&mut self) {
        let a = app();
        let st = a.stat.get();
        if let Some(prev) = self.last_ac {
            if prev != st.ac && st.battery >= 0 {
                self.power_text = if st.ac { format!("Charging  {}%", st.battery) } else { format!("On Battery  {}%", st.battery) };
                self.flash(Mode::Power, 3000);
            }
        }
        self.last_ac = Some(st.ac);
        self.need = true;
        self.kick();
    }
    pub fn on_windows(&mut self) {}
    pub fn demo(&mut self) {
        self.cur = Some(Note { id: -1, app_id: "Claude_pzs8sxrjxfjjc!Claude".into(), app: "Claude".into(), title: "Dynamic Island".into(), body: "Notifications spring open here".into(), launch: String::new() });
        self.flash(Mode::Notif(-1), 4000);
    }
    fn flash(&mut self, m: Mode, ms: u64) {
        if self.mode == Mode::Center {
            return;
        }
        self.mode = m;
        self.until = std::time::Instant::now() + std::time::Duration::from_millis(ms);
        self.need = true;
        self.kick();
    }
    pub fn on_notification(&mut self) {
        let a = app();
        let fresh = a.notes.take_fresh();
        if let Some(n) = fresh.last().cloned() {
            self.cur = Some(n.clone());
            self.flash(Mode::Notif(n.id), 5000);
        }
    }
    pub fn on_clipboard(&mut self) {
        unsafe {
            for _ in 0..3 {
                if OpenClipboard(None).is_ok() {
                    if let Ok(h) = GetClipboardData(13) {
                        let p = GlobalLock(HGLOBAL(h.0)) as *const u16;
                        if !p.is_null() {
                            let mut n = 0;
                            while *p.add(n) != 0 && n < 4000 {
                                n += 1;
                            }
                            let t = String::from_utf16_lossy(std::slice::from_raw_parts(p, n)).trim().to_string();
                            let _ = GlobalUnlock(HGLOBAL(h.0));
                            if !t.is_empty() {
                                self.clips.retain(|c| *c != t);
                                self.clips.insert(0, t.clone());
                                self.clips.truncate(12);
                                self.clip_text = t;
                            }
                        }
                    }
                    let _ = CloseClipboard();
                    break;
                }
                std::thread::sleep(std::time::Duration::from_millis(15));
            }
        }
        if !self.clip_text.is_empty() {
            self.flash(Mode::Clip, 1800);
        }
    }
    pub fn toggle_center(&mut self) {
        self.x.was_down = true; // the click that opens it must not count as an outside click
        self.x.left_since = None;
        if self.mode == Mode::Center {
            self.mode = Mode::Idle;
        } else {
            self.mode = Mode::Center;
        }
        self.need = true;
        self.kick();
    }

    fn target(&self) -> (f32, f32) {
        let s = self.s;
        let a = app();
        let media = a.media.get();
        match &self.mode {
            Mode::Idle => {
                let d = (self.bar_h - 10.0).max(18.0);
                (d, d)
            }
            Mode::Notif(_) => (400.0 * s, 70.0 * s),
            Mode::Clip => (300.0 * s, 50.0 * s),
            Mode::Power => (250.0 * s, 50.0 * s),
            Mode::Peek => (300.0 * s, if media.active { 84.0 * s } else { 54.0 * s }),
            Mode::Center => self.panel_size(),
            Mode::Toast => (320.0 * s, 48.0 * s),
        }
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        if self.mode != Mode::Idle && self.mode != Mode::Center && self.mode != Mode::Peek && now > self.until {
            self.mode = Mode::Idle;
        }
        if self.mode == Mode::Peek && !self.hover {
            self.mode = Mode::Idle;
        }
        let (tw, th) = self.target();
        self.sw.goal = tw;
        self.sh.goal = th;
        let m1 = self.sw.step(dt, 420.0, 26.0);
        let m2 = self.sh.step(dt, 420.0, 26.0);
        let a = app();
        let eq = a.media.get().playing && self.mode == Mode::Idle;
        if eq {
            self.t_anim += dt;
        }
        let timed = self.mode != Mode::Idle && self.mode != Mode::Center;
        if !(m1 || m2 || self.need || eq || timed) {
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_ISLAND));
            }
            return;
        }
        self.need = false;
        self.draw();
    }

    fn draw(&mut self) {
        if self.hidden || !app().cfg.s.island_on {
            return;
        }
        let a = app();
        let s = self.s;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let w = self.sw.v;
        let h = self.sh.v;
        let x = (self.w as f32 - w) / 2.0;
        let y = if h < self.bar_h - 2.0 { ((self.bar_h - h) / 2.0).max(2.0) } else { 4.0 * s };
        let r = (h / 2.0).min(if self.mode == Mode::Center { 26.0 * s } else { 999.0 });
        g.shadow(x, y, w, h, r, 12.0 * s, 120);
        g.fill_round(x, y, w, h, r, argb(255, 0, 0, 0));
        let white = argb(255, 255, 255, 255);
        let dim = argb(255, 160, 160, 168);
        let expanded = w > 200.0 * s;
        match self.mode.clone() {
            Mode::Idle => {
                self.draw_idle_extras(&mut g, x, y, w, h);
                let m = a.media.get();
                if m.active && m.playing {
                    // equalizer inside the round dot
                    for i in 0..3 {
                        let ph = self.t_anim * 7.0 + i as f32 * 1.9;
                        let bh = (0.35 + 0.65 * ph.sin().abs()) * h * 0.42;
                        g.fill_round(x + w / 2.0 - 5.5 * s + i as f32 * 4.5 * s, y + h / 2.0 - bh / 2.0, 2.6 * s, bh, 1.3 * s, argb(255, 255, 255, 255));
                    }
                }
            }
            Mode::Notif(_) if expanded => {
                if let Some(n) = &self.cur {
                    let key = format!("t:shell:appsfolder\\{}", n.app_id.to_lowercase());
                    let tgt = format!("shell:AppsFolder\\{}", n.app_id);
                    let pack = pack_for(&a.icons, &a.cfg, &n.app_id, &n.app);
                    if let Some(img) = a.icons.get_or_logo(&key, &tgt, pack) {
                        g.image(&img, x + 16.0 * s, y + h / 2.0 - 17.0 * s, 34.0 * s, 34.0 * s, ((w - 300.0 * s) / (100.0 * s)).clamp(0.0, 1.0));
                    }
                    let al = ((w - 300.0 * s) / (80.0 * s)).clamp(0.0, 1.0);
                    g.text(&n.app.to_uppercase(), x + 62.0 * s, y + 8.0 * s, w - 80.0 * s, 14.0 * s, 9.5 * s, false, 0, true, false, with_alpha(dim, al));
                    g.text(&n.title, x + 62.0 * s, y + 21.0 * s, w - 80.0 * s, 20.0 * s, 13.0 * s, true, 0, true, false, with_alpha(white, al));
                    g.text(&n.body, x + 62.0 * s, y + 41.0 * s, w - 80.0 * s, 20.0 * s, 12.0 * s, false, 0, true, false, with_alpha(dim, al));
                }
            }
            Mode::Clip if expanded => {
                let al = ((w - 200.0 * s) / (80.0 * s)).clamp(0.0, 1.0);
                let t = if self.clip_text.chars().count() > 34 { format!("{}...", self.clip_text.chars().take(33).collect::<String>()) } else { self.clip_text.clone() };
                g.text("Copied", x + 18.0 * s, y + 6.0 * s, 100.0 * s, 16.0 * s, 10.0 * s, false, 0, true, false, with_alpha(dim, al));
                g.text(&t.replace('\n', " "), x + 18.0 * s, y + 22.0 * s, w - 36.0 * s, 22.0 * s, 13.0 * s, false, 0, true, false, with_alpha(white, al));
            }
            Mode::Power if expanded => {
                let al = ((w - 160.0 * s) / (80.0 * s)).clamp(0.0, 1.0);
                g.text(&self.power_text, x, y, w, h, 14.0 * s, true, 1, true, false, with_alpha(argb(255, 52, 199, 89), al));
            }
            Mode::Peek if expanded => {
                let st = a.stat.get();
                let m = a.media.get();
                let al = ((w - 200.0 * s) / (80.0 * s)).clamp(0.0, 1.0);
                let line = if st.battery >= 0 { format!("{}%{}   CPU {:.0}%   RAM {:.0}%", st.battery, if st.ac { " (charging)" } else { "" }, st.cpu, st.ram) } else { format!("CPU {:.0}%   RAM {:.0}%", st.cpu, st.ram) };
                g.text(&line, x, y + 8.0 * s, w, 22.0 * s, 12.5 * s, false, 1, true, false, with_alpha(white, al));
                if m.active {
                    let t = if m.title.chars().count() > 34 { format!("{}...", m.title.chars().take(33).collect::<String>()) } else { m.title.clone() };
                    g.text(&format!("{}  -  {}", t, m.artist), x + 10.0 * s, y + 32.0 * s, w - 20.0 * s, 20.0 * s, 11.5 * s, false, 1, true, false, with_alpha(dim, al));
                } else {
                    g.text("Nothing playing", x, y + 28.0 * s, w, 20.0 * s, 11.0 * s, false, 1, true, false, with_alpha(dim, al));
                }
            }
            Mode::Center if w > 300.0 * s => {
                self.draw_panel(&mut g, x, y, w, h);
            }
            Mode::Toast if w > 200.0 * s => {
                self.draw_toast(&mut g, x, y, w, h);
            }
            _ => {}
        }
        g.present_layered(self.hwnd, self.scr.left + (((self.scr.right - self.scr.left) - self.w) / 2), self.scr.top, 255);
        self.gfx = Some(g);
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let i = &mut app().island;
    match m {
        WM_TIMER if wp.0 == T_ISLAND => {
            i.tick();
            LRESULT(0)
        }
        WM_TIMER if wp.0 == crate::islandx::T_EXTRA => {
            i.extras_tick();
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            let (x, y) = win::lparam_xy(lp);
            i.mouse_down(x as f32, y as f32);
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            i.mouse_move(x as f32);
            let (cx, cw) = (i.w as f32 / 2.0, i.sw.v);
            let inside = (x as f32 - cx).abs() < cw / 2.0 && (y as f32) < 4.0 * i.s + i.sh.v;
            if inside && !i.hover {
                i.hover = true;
                let mut t = TRACKMOUSEEVENT { cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32, dwFlags: TME_LEAVE, hwndTrack: h, dwHoverTime: 0 };
                let _ = TrackMouseEvent(&mut t);
                if i.mode == Mode::Idle {
                    i.mode = Mode::Peek;
                }
                i.kick();
            }
            LRESULT(0)
        }
        x if x == WM_MOUSELEAVE => {
            i.hover = false;
            if i.mode == Mode::Peek {
                i.mode = Mode::Idle;
            }
            i.kick();
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            if i.mouse_up(x as f32, y as f32) {
                return LRESULT(0);
            }
            let inside = (x as f32 - i.w as f32 / 2.0).abs() < i.sw.v / 2.0 && (y as f32) < 4.0 * i.s + i.sh.v;
            if inside && i.mode != Mode::Center {
                i.toggle_center();
            } else if i.mode == Mode::Center && !inside {
                i.mode = Mode::Idle;
                i.kick();
            }
            LRESULT(0)
        }
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

impl Island {
    /// Show a one-off message in the island (LocalSend progress, etc.).
    pub fn custom(&mut self, app_name: &str, title: &str, body: &str) {
        self.cur = Some(Note { id: -2, app_id: "MacDock".into(), app: app_name.into(), title: title.into(), body: body.into(), launch: String::new() });
        self.flash(Mode::Notif(-2), 4500);
    }
}

impl Island {
    pub(crate) fn kick_pub(&mut self) {
        self.kick();
    }
    pub(crate) fn born(&self) -> std::time::Instant {
        self.born_at
    }
}

/// Put text on the clipboard (Clipboard tab rows re-copy an old item).
pub fn set_clipboard_text(t: &str) {
    use windows::Win32::System::DataExchange::*;
    use windows::Win32::System::Memory::*;
    unsafe {
        if OpenClipboard(None).is_ok() {
            let _ = EmptyClipboard();
            let w: Vec<u16> = t.encode_utf16().chain(std::iter::once(0)).collect();
            if let Ok(hm) = GlobalAlloc(GMEM_MOVEABLE, w.len() * 2) {
                let p = GlobalLock(hm) as *mut u16;
                std::ptr::copy_nonoverlapping(w.as_ptr(), p, w.len());
                let _ = GlobalUnlock(hm);
                let _ = SetClipboardData(13, Some(windows::Win32::Foundation::HANDLE(hm.0)));
            }
            let _ = CloseClipboard();
        }
    }
}
