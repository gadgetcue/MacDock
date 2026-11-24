//! Island extras ported from C# IslandExtras.cs / Island.cs: toasts (volume, brightness, Caps/Num Lock, Focus, water,
//! stand-up), Pomodoro / water / stand-up reminders, the three-tab panel (Notifications, Clipboard, Focus), privacy
//! dots (microphone / camera in use) and the Pomodoro ring on the idle pill.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::island::{Island, Mode};
use crate::*;
use std::collections::HashSet;
use std::time::{Duration, Instant};
use windows::Win32::UI::Input::KeyboardAndMouse::GetKeyState;

pub const T_EXTRA: usize = 27;

#[derive(Clone, PartialEq)]
pub enum Act {
    Tab(u8),
    PomoStart,
    PomoPause,
    PomoSkip,
    PomoStop,
    AddGlass,
    Copy(usize),
    Dismiss(i64),
    Open(String),
    ClearAll,
    MediaPlay,
    MediaNext,
}

#[derive(Clone, Default)]
pub struct Toast {
    pub text: String,
    pub sub: String,
    pub color: u32,
    pub kind: u8, // 1 volume, 2 key cap, 3 water, 4 stand, 5 timer, 6 brightness
    pub bar: f32, // 0..1, or -1 for none
    pub glyph: String,
    pub click: Option<Act>,
}

pub struct Extras {
    pub toast: Toast,
    pub tab: u8,
    pub hits: Vec<(f32, f32, f32, f32, Act)>,
    pub pomo: u8, // 0 off, 1 focus, 2 break
    pub paused: bool,
    pub pomo_end: Instant,
    pub pomo_left: f32,
    pub pomo_total: f32,
    pub next_water: Option<Instant>,
    pub next_stand: Option<Instant>,
    pub last_slow: Instant,
    pub caps_init: bool,
    pub last_caps: bool,
    pub last_num: bool,
    pub vol_init: bool,
    pub last_vol: f32,
    pub last_muted: bool,
    pub bright_init: bool,
    pub last_bright: i32,
    pub mic_on: bool,
    pub cam_on: bool,
    pub last_priv: Instant,
    pub dismissed: HashSet<i64>,
    pub swipe: Option<(i64, f32, f32)>, // note id, start x, current dx
    pub hover_act: Option<Act>,
    pub was_down: bool,
    pub left_since: Option<Instant>,
}

impl Extras {
    pub fn new() -> Extras {
        Extras { toast: Toast::default(), tab: 0, hits: vec![], pomo: 0, paused: false, pomo_end: Instant::now(), pomo_left: 0.0, pomo_total: 0.0, next_water: None, next_stand: None, last_slow: Instant::now(), caps_init: false, last_caps: false, last_num: false, vol_init: false, last_vol: 0.0, last_muted: false, bright_init: false, last_bright: -1, mic_on: false, cam_on: false, last_priv: Instant::now() - Duration::from_secs(10), dismissed: crate::notify::load_dismissed(), swipe: None, hover_act: None, was_down: false, left_since: None }
    }
}

fn opt_i(key: &str, def: i32, min: i32, max: i32) -> i32 {
    app().cfg.get_opt(key).and_then(|v| v.trim().parse::<i32>().ok()).unwrap_or(def).clamp(min, max)
}
fn opt_b(key: &str, def: bool) -> bool {
    app().cfg.get_opt(key).map(|v| !(v.ends_with("off") || v.ends_with("false") || v.ends_with("no"))).unwrap_or(def)
}
fn focus_min() -> i32 {
    opt_i("island-pomo-focus", 25, 1, 180)
}
fn break_min() -> i32 {
    opt_i("island-pomo-break", 5, 1, 60)
}
fn water_goal() -> i32 {
    opt_i("island-water-goal", 8, 1, 30)
}
fn water_every() -> i32 {
    opt_i("island-water-every", 60, 5, 240)
}
fn stand_every() -> i32 {
    opt_i("island-stand-every", 45, 5, 240)
}
fn water_count() -> i32 {
    let today = today_key();
    app().cfg.get_opt("island-water-count").and_then(|v| v.trim().split_once(':').map(|(d, n)| (d.to_string(), n.to_string()))).filter(|(d, _)| *d == today).and_then(|(_, n)| n.parse().ok()).unwrap_or(0)
}
fn set_water_count(n: i32) {
    app().cfg.set_option("island-water-count", &format!("{}:{}", today_key(), n.max(0)));
}
fn today_key() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    let t = unsafe { GetLocalTime() };
    format!("{:04}{:02}{:02}", t.wYear, t.wMonth, t.wDay)
}
fn clock(secs: f32) -> String {
    let s = secs.ceil().max(0.0) as i32;
    format!("{}:{:02}", s / 60, s % 60)
}

const RED: u32 = 0xFFFF453A;
const GREEN: u32 = 0xFF34C759;
const BLUE: u32 = 0xFF0A84FF;
const ORANGE: u32 = 0xFFFF9F0A;
const YELLOW: u32 = 0xFFFFD60A;
const WHITE: u32 = 0xFFFFFFFF;

impl Island {
    pub fn show_toast(&mut self, text: &str, sub: &str, color: u32, kind: u8, bar: f32, seconds: f32, glyph: &str, click: Option<Act>) {
        if !app().cfg.s.island_on || self.mode == Mode::Center || matches!(self.mode, Mode::Notif(_)) {
            return;
        }
        self.x.toast = Toast { text: text.into(), sub: sub.into(), color, kind, bar, glyph: glyph.into(), click };
        self.mode = Mode::Toast;
        self.until = Instant::now() + Duration::from_millis((seconds * 1000.0) as u64);
        self.need = true;
        self.kick_pub();
    }

    // ---------- Pomodoro ----------
    fn pomo_begin(&mut self, st: u8) {
        self.x.pomo = st;
        self.x.paused = false;
        self.x.pomo_total = (if st == 1 { focus_min() } else { break_min() }) as f32 * 60.0;
        self.x.pomo_end = Instant::now() + Duration::from_secs_f32(self.x.pomo_total);
        if st == 1 {
            self.x.next_stand = Some(self.x.pomo_end);
        }
    }
    fn pomo_remain(&self) -> f32 {
        if self.x.paused { self.x.pomo_left } else { self.x.pomo_end.saturating_duration_since(Instant::now()).as_secs_f32() }
    }
    fn pomo_start(&mut self) {
        self.pomo_begin(1);
        self.show_toast("Focus", &format!("{} min", focus_min()), RED, 5, -1.0, 2.5, "", None);
    }
    fn add_glass(&mut self) {
        let n = water_count() + 1;
        let goal = water_goal();
        set_water_count(n);
        self.x.next_water = Some(Instant::now() + Duration::from_secs(water_every() as u64 * 60));
        self.show_toast(if n >= goal { "Goal reached" } else { "Glass logged" }, &format!("{} / {}", n, goal), BLUE, 3, (n as f32 / goal as f32).min(1.0), 2.2, "", None);
    }

    /// Run an action (button / row / toast click).
    pub fn run_act(&mut self, act: Act) {
        let a = app();
        match act {
            Act::Tab(t) => self.x.tab = t,
            Act::PomoStart => self.pomo_start(),
            Act::PomoPause => {
                if self.x.pomo != 0 {
                    if self.x.paused {
                        self.x.pomo_end = Instant::now() + Duration::from_secs_f32(self.x.pomo_left);
                        self.x.paused = false;
                    } else {
                        self.x.pomo_left = self.pomo_remain();
                        self.x.paused = true;
                    }
                }
            }
            Act::PomoSkip => {
                if self.x.pomo == 1 {
                    self.pomo_begin(2);
                    self.show_toast("Break", &format!("{} min", break_min()), GREEN, 5, -1.0, 2.5, "", None);
                } else {
                    self.x.pomo = 0;
                }
            }
            Act::PomoStop => {
                self.x.pomo = 0;
                self.x.paused = false;
            }
            Act::AddGlass => self.add_glass(),
            Act::Copy(i) => {
                if let Some(t) = self.clips.get(i).cloned() {
                    crate::island::set_clipboard_text(&t);
                    self.mode = Mode::Idle;
                }
            }
            Act::Dismiss(id) => {
                self.x.dismissed.insert(id);
                crate::notify::save_dismissed(&self.x.dismissed);
            }
            Act::ClearAll => {
                for n in a.notes.recent() {
                    self.x.dismissed.insert(n.id);
                }
                crate::notify::save_dismissed(&self.x.dismissed);
            }
            Act::Open(id) => {
                // bring the app that sent the notification forward when it is running
                let low = id.split('!').next().unwrap_or("").split('_').next().unwrap_or("").to_lowercase();
                let link = a.notes.recent().into_iter().find(|n| n.app_id == id && !n.launch.is_empty()).map(|n| n.launch);
                if let Some(l) = link {
                    crate::sys::shell_open(&l);
                } else if let Some(w) = a.wins.iter().find(|w| !low.is_empty() && w.exe.contains(&low)) {
                    crate::dock::activate_hwnd(w.hwnd);
                } else {
                    crate::sys::shell_open(&format!("shell:AppsFolder\\{}", id));
                }
                self.mode = Mode::Idle;
            }
            Act::MediaPlay => crate::media::play_pause(),
            Act::MediaNext => crate::media::next(),
        }
        self.need = true;
        self.kick_pub();
    }

    // ---------- periodic checks (100 ms timer) ----------
    pub fn extras_tick(&mut self) {
        let a = app();
        if !a.cfg.s.island_on {
            return;
        }
        if self.mode == Mode::Center {
            self.panel_poll();
        }
        let now = Instant::now();
        // lock keys (UI thread: GetKeyState reflects the toggle state)
        let (caps, num) = unsafe { (GetKeyState(0x14) & 1 != 0, GetKeyState(0x90) & 1 != 0) };
        if !self.x.caps_init {
            self.x.caps_init = true;
            self.x.last_caps = caps;
            self.x.last_num = num;
        } else if opt_b("island-locks", true) {
            if caps != self.x.last_caps {
                self.show_toast("Caps Lock", if caps { "On" } else { "Off" }, if caps { YELLOW } else { WHITE }, 2, -1.0, 1.8, "A", None);
            } else if num != self.x.last_num {
                self.show_toast("Num Lock", if num { "On" } else { "Off" }, if num { YELLOW } else { WHITE }, 2, -1.0, 1.8, "1", None);
            }
        }
        self.x.last_caps = caps;
        self.x.last_num = num;
        // volume HUD
        if let Some((vol, muted)) = crate::status::get_volume() {
            if !self.x.vol_init {
                self.x.vol_init = true;
                self.x.last_vol = vol;
                self.x.last_muted = muted;
            } else if (vol - self.x.last_vol).abs() > 0.004 || muted != self.x.last_muted {
                self.x.last_vol = vol;
                self.x.last_muted = muted;
                if opt_b("island-volume", true) {
                    self.show_toast(if muted { "Muted" } else { "Volume" }, &if muted { String::new() } else { format!("{}%", (vol * 100.0).round()) }, WHITE, 1, if muted { 0.0 } else { vol }, 1.6, "", None);
                }
            }
        }
        // brightness HUD (internal panel IOCTL; cheap)
        if let Some(b) = crate::status::brightness_ioctl() {
            if !self.x.bright_init {
                self.x.bright_init = true;
                self.x.last_bright = b;
            } else if (b - self.x.last_bright).abs() >= 2 {
                self.x.last_bright = b;
                if opt_b("island-brightness", true) {
                    self.show_toast("Brightness", &format!("{}%", b), WHITE, 6, b as f32 / 100.0, 1.6, "", None);
                }
            }
        }
        // privacy dots (registry, every 2 s)
        if now.duration_since(self.x.last_priv).as_secs() >= 2 {
            self.x.last_priv = now;
            let (m, c) = (crate::status::capability_in_use("microphone"), crate::status::capability_in_use("webcam"));
            if (m, c) != (self.x.mic_on, self.x.cam_on) && opt_b("island-privacy", true) {
                self.x.mic_on = m;
                self.x.cam_on = c;
                self.need = true;
                self.kick_pub();
            }
        }
        // time-based: once a second
        if now.duration_since(self.x.last_slow).as_millis() < 1000 {
            return;
        }
        self.x.last_slow = now;
        if self.x.pomo != 0 && !self.x.paused && now >= self.x.pomo_end {
            unsafe {
                let _ = windows::Win32::System::Diagnostics::Debug::MessageBeep(windows::Win32::UI::WindowsAndMessaging::MB_ICONASTERISK);
            }
            if self.x.pomo == 1 {
                self.pomo_begin(2);
                self.show_toast("Focus done", &format!("Break {} min", break_min()), GREEN, 5, -1.0, 4.0, "", None);
            } else {
                self.x.pomo = 0;
                self.show_toast("Break over", "Ready to focus?", RED, 5, -1.0, 4.0, "", Some(Act::PomoStart));
            }
        }
        if self.x.pomo != 0 {
            self.need = true;
            self.kick_pub();
        }
        if self.x.next_water.is_none() {
            self.x.next_water = Some(now + Duration::from_secs(water_every() as u64 * 60));
        }
        if self.x.next_stand.is_none() {
            self.x.next_stand = Some(now + Duration::from_secs(stand_every() as u64 * 60));
        }
        if opt_b("island-water", true) && now >= self.x.next_water.unwrap() {
            self.x.next_water = Some(now + Duration::from_secs(water_every() as u64 * 60));
            let (n, goal) = (water_count(), water_goal());
            if n < goal && self.mode != Mode::Center {
                self.show_toast("Time for water", &format!("{} / {}", n, goal), BLUE, 3, -1.0, 6.0, "", Some(Act::AddGlass));
            }
        }
        if opt_b("island-stand", true) && now >= self.x.next_stand.unwrap() && self.x.pomo != 1 {
            self.x.next_stand = Some(now + Duration::from_secs(stand_every() as u64 * 60));
            self.show_toast("Stand up", "Stretch for a minute", ORANGE, 4, -1.0, 6.0, "", None);
        }
    }

    // ---------- sizes ----------
    pub fn panel_size(&self) -> (f32, f32) {
        let s = self.s;
        let a = app();
        let media = a.media.get();
        let mut h = 52.0 + if media.active { 50.0 } else { 0.0 };
        match self.x.tab {
            0 => {
                let n = a.notes.recent().into_iter().filter(|n| !self.x.dismissed.contains(&n.id)).count().min(5);
                h += if n == 0 { 56.0 } else { n as f32 * 48.0 + 34.0 };
            }
            1 => h += (self.clips.len().min(5).max(1)) as f32 * 40.0 + 10.0,
            _ => h += 3.0 * 44.0 + 10.0,
        }
        (440.0 * s, (h + 8.0) * s)
    }

    // ---------- drawing ----------
    pub fn draw_toast(&mut self, g: &mut Gfx, x: f32, y: f32, w: f32, h: f32) {
        let s = self.s;
        let t = self.x.toast.clone();
        let al = ((w - 200.0 * s) / (100.0 * s)).clamp(0.0, 1.0);
        let white = with_alpha(WHITE, al);
        let gray = with_alpha(argb(255, 160, 160, 168), al);
        let c = with_alpha(t.color, al);
        let mut ix = x + 18.0 * s;
        let cy = y + h / 2.0;
        match t.kind {
            1 => {
                g.fill_poly(&[(ix, cy - 3.0 * s), (ix + 5.0 * s, cy - 3.0 * s), (ix + 10.0 * s, cy - 8.0 * s), (ix + 10.0 * s, cy + 8.0 * s), (ix + 5.0 * s, cy + 3.0 * s), (ix, cy + 3.0 * s)], c);
                if t.bar > 0.01 {
                    g.arc(ix + 12.0 * s, cy, 4.0 * s, -60.0, 120.0, 1.6 * s, c);
                }
                if t.bar > 0.5 {
                    g.arc(ix + 12.0 * s, cy, 8.0 * s, -60.0, 120.0, 1.6 * s, c);
                }
            }
            2 => {
                g.stroke_round(ix, cy - 9.0 * s, 20.0 * s, 18.0 * s, 5.0 * s, 1.6 * s, c);
                g.text(&t.glyph, ix, cy - 9.0 * s, 20.0 * s, 19.0 * s, 10.0 * s, true, 1, true, false, c);
            }
            3 => {
                g.fill_poly(&[(ix + 8.0 * s, cy - 10.0 * s), (ix + 15.0 * s, cy + 1.0 * s), (ix + 1.0 * s, cy + 1.0 * s)], c);
                g.fill_circle(ix + 8.0 * s, cy + 3.0 * s, 7.0 * s, c);
            }
            4 => {
                g.fill_circle(ix + 9.5 * s, cy - 6.5 * s, 3.5 * s, c);
                for (a, b, d, e) in [(9.5, -3.0, 9.5, 4.0), (9.5, 4.0, 4.0, 10.0), (9.5, 4.0, 15.0, 10.0), (2.0, -7.0, 9.5, -1.0), (17.0, -7.0, 9.5, -1.0)] {
                    g.line(ix + a * s, cy + b * s, ix + d * s, cy + e * s, 1.6 * s, c);
                }
            }
            6 => {
                g.fill_circle(ix + 10.0 * s, cy, 5.0 * s, c);
                for i in 0..8 {
                    let an = i as f32 * std::f32::consts::PI / 4.0;
                    g.line(ix + 10.0 * s + an.cos() * 7.0 * s, cy + an.sin() * 7.0 * s, ix + 10.0 * s + an.cos() * 10.0 * s, cy + an.sin() * 10.0 * s, 1.6 * s, c);
                }
            }
            _ => {
                g.stroke_circle(ix + 9.0 * s, cy, 9.0 * s, 1.6 * s, c);
                g.line(ix + 9.0 * s, cy, ix + 9.0 * s, cy - 5.0 * s, 1.6 * s, c);
                g.line(ix + 9.0 * s, cy, ix + 13.0 * s, cy + 2.0 * s, 1.6 * s, c);
            }
        }
        ix += 32.0 * s;
        if t.bar >= 0.0 {
            g.text(&t.text, ix, y, 84.0 * s, h, 12.0 * s, true, 0, true, false, white);
            let (tx, tw) = (ix + 84.0 * s, x + w - 22.0 * s - (ix + 84.0 * s));
            g.fill_round(tx, cy - 3.0 * s, tw, 6.0 * s, 3.0 * s, with_alpha(argb(60, 255, 255, 255), al));
            if t.bar > 0.01 {
                g.fill_round(tx, cy - 3.0 * s, (tw * t.bar.min(1.0)).max(6.0 * s), 6.0 * s, 3.0 * s, with_alpha(WHITE, al));
            }
        } else {
            g.text(&t.text, ix, y, w - (ix - x) - 20.0 * s, h, 12.0 * s, true, 0, true, false, white);
            g.text(&t.sub, ix, y, w - (ix - x) - 20.0 * s, h, 11.0 * s, false, 2, true, false, gray);
        }
        if t.click.is_some() {
            self.x.hits.clear();
            self.x.hits.push((x, y, w, h, t.click.clone().unwrap()));
        }
    }

    /// Pomodoro ring around the idle pill and mic / camera dots.
    pub fn draw_idle_extras(&mut self, g: &mut Gfx, x: f32, y: f32, w: f32, h: f32) {
        let s = self.s;
        if self.x.pomo != 0 {
            let frac = if self.x.pomo_total <= 0.0 { 0.0 } else { (self.pomo_remain() / self.x.pomo_total).clamp(0.0, 1.0) };
            let c = if self.x.pomo == 1 { RED } else { GREEN };
            let (cx, cy, r) = (x + 14.0 * s, y + h / 2.0, h / 2.0 - 4.0 * s);
            g.stroke_circle(cx, cy, r, 2.0 * s, argb(50, 255, 255, 255));
            if frac > 0.002 {
                g.arc(cx, cy, r, -90.0, 360.0 * frac, 2.0 * s, if self.x.paused { argb(150, 255, 255, 255) } else { c });
            }
        }
        if self.x.mic_on || self.x.cam_on {
            let pulse = 0.65 + 0.35 * ((Instant::now().duration_since(self.born()).as_secs_f32() * 4.0).sin());
            let (cx, cy, r) = (x + w / 2.0, y + h / 2.0, 3.2 * s);
            let both = self.x.mic_on && self.x.cam_on;
            if self.x.mic_on {
                g.fill_circle(cx - r - if both { 4.5 * s } else { 0.0 }, cy, r, with_alpha(ORANGE, pulse));
            }
            if self.x.cam_on {
                g.fill_circle(cx - r + if both { 4.5 * s } else { 0.0 } + r, cy, r, with_alpha(GREEN, pulse));
            }
        }
    }

    pub fn draw_panel(&mut self, g: &mut Gfx, x: f32, y: f32, w: f32, _h: f32) {
        let a = app();
        let s = self.s;
        let al = ((w - 300.0 * s) / (100.0 * s)).clamp(0.0, 1.0);
        let white = with_alpha(WHITE, al);
        let dim = with_alpha(argb(255, 160, 160, 168), al);
        self.x.hits.clear();
        // tabs
        let names = ["Notifications", "Clipboard", "Focus"];
        let tw = (w - 24.0 * s) / 3.0;
        for (i, n) in names.iter().enumerate() {
            let tx = x + 12.0 * s + i as f32 * tw;
            let on = self.x.tab as usize == i;
            if on {
                g.fill_round(tx + 2.0 * s, y + 10.0 * s, tw - 4.0 * s, 28.0 * s, 14.0 * s, with_alpha(argb(255, 58, 58, 64), al));
            }
            g.text(n, tx, y + 10.0 * s, tw, 28.0 * s, 12.0 * s, on, 1, true, false, if on { white } else { dim });
            self.x.hits.push((tx, y + 10.0 * s, tw, 28.0 * s, Act::Tab(i as u8)));
        }
        let mut cy = y + 46.0 * s;
        // media row
        let m = a.media.get();
        if m.active {
            g.fill_round(x + 10.0 * s, cy, w - 20.0 * s, 44.0 * s, 12.0 * s, with_alpha(argb(255, 30, 30, 34), al));
            let t = if m.title.chars().count() > 30 { format!("{}...", m.title.chars().take(29).collect::<String>()) } else { m.title.clone() };
            g.text(&t, x + 22.0 * s, cy + 4.0 * s, w - 120.0 * s, 20.0 * s, 12.5 * s, true, 0, true, false, white);
            g.text(&m.artist, x + 22.0 * s, cy + 22.0 * s, w - 120.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, dim);
            let (bx, by) = (x + w - 66.0 * s, cy + 22.0 * s);
            if m.playing {
                g.fill_rect(bx - 4.0 * s, by - 7.0 * s, 3.5 * s, 14.0 * s, white);
                g.fill_rect(bx + 2.0 * s, by - 7.0 * s, 3.5 * s, 14.0 * s, white);
            } else {
                g.fill_poly(&[(bx - 5.0 * s, by - 7.0 * s), (bx - 5.0 * s, by + 7.0 * s), (bx + 7.0 * s, by)], white);
            }
            g.fill_poly(&[(bx + 22.0 * s, by - 6.0 * s), (bx + 22.0 * s, by + 6.0 * s), (bx + 31.0 * s, by)], white);
            g.fill_rect(bx + 32.0 * s, by - 6.0 * s, 2.0 * s, 12.0 * s, white);
            self.x.hits.push((bx - 12.0 * s, by - 14.0 * s, 28.0 * s, 28.0 * s, Act::MediaPlay));
            self.x.hits.push((bx + 16.0 * s, by - 14.0 * s, 30.0 * s, 28.0 * s, Act::MediaNext));
            cy += 50.0 * s;
        }
        match self.x.tab {
            0 => {
                let list: Vec<_> = a.notes.recent().into_iter().filter(|n| !self.x.dismissed.contains(&n.id)).take(5).collect();
                if list.is_empty() {
                    g.text("No notifications", x, cy, w, 40.0 * s, 12.5 * s, false, 1, true, false, dim);
                }
                for (i, n) in list.iter().enumerate() {
                    let ry = cy + i as f32 * 48.0 * s;
                    let off = match self.x.swipe {
                        Some((id, _, dx)) if id == n.id => dx,
                        _ => 0.0,
                    };
                    let fade = 1.0 - (off.abs() / (160.0 * s)).min(1.0);
                    let rx = x + 10.0 * s + off;
                    g.fill_round(rx, ry, w - 20.0 * s, 44.0 * s, 12.0 * s, with_alpha(argb(255, 30, 30, 34), al * fade));
                    let key = format!("t:shell:appsfolder\\{}", n.app_id.to_lowercase());
                    let tgt = format!("shell:AppsFolder\\{}", n.app_id);
                    let pack = pack_for(&a.icons, &a.cfg, &n.app_id, &n.app);
                    if let Some(img) = a.icons.get_or_logo(&key, &tgt, pack) {
                        g.image(&img, rx + 10.0 * s, ry + 7.0 * s, 30.0 * s, 30.0 * s, al * fade);
                    }
                    g.text(&n.title, rx + 50.0 * s, ry + 3.0 * s, w - 90.0 * s, 20.0 * s, 12.5 * s, true, 0, true, false, with_alpha(WHITE, al * fade));
                    g.text(&format!("{}: {}", a.lp.index.name_for(&n.app_id).unwrap_or_else(|| n.app.clone()), n.body), rx + 50.0 * s, ry + 22.0 * s, w - 90.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, with_alpha(argb(255, 160, 160, 168), al * fade));
                    self.x.hits.push((x + 10.0 * s, ry, w - 20.0 * s, 44.0 * s, Act::Open(n.app_id.clone())));
                }
                if !list.is_empty() {
                    let fy = cy + list.len() as f32 * 48.0 * s;
                    g.text("Clear All", x + w - 110.0 * s, fy, 96.0 * s, 26.0 * s, 11.5 * s, true, 2, true, false, with_alpha(BLUE, al));
                    self.x.hits.push((x + w - 110.0 * s, fy, 96.0 * s, 26.0 * s, Act::ClearAll));
                }
            }
            1 => {
                if self.clips.is_empty() {
                    g.text("Nothing copied yet", x, cy, w, 40.0 * s, 12.5 * s, false, 1, true, false, dim);
                }
                for (i, c) in self.clips.iter().take(5).enumerate() {
                    let ry = cy + i as f32 * 40.0 * s;
                    g.fill_round(x + 10.0 * s, ry, w - 20.0 * s, 36.0 * s, 10.0 * s, with_alpha(argb(255, 30, 30, 34), al));
                    let t: String = c.replace('\n', " ");
                    g.text(&t, x + 22.0 * s, ry, w - 44.0 * s, 36.0 * s, 12.0 * s, false, 0, true, false, white);
                    self.x.hits.push((x + 10.0 * s, ry, w - 20.0 * s, 36.0 * s, Act::Copy(i)));
                }
            }
            _ => self.draw_focus(g, x, cy, w, al),
        }
    }

    fn draw_focus(&mut self, g: &mut Gfx, x: f32, top: f32, w: f32, al: f32) {
        let s = self.s;
        let white = with_alpha(WHITE, al);
        let dim = with_alpha(argb(255, 160, 160, 168), al);
        let rh = 40.0 * s;
        let right = x + w - 18.0 * s;
        let btn = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, rx: f32, cy: f32, bw: f32, text: &str, act: Act| {
            let bh = 24.0 * s;
            g.fill_round(rx - bw, cy - bh / 2.0, bw, bh, bh / 2.0, with_alpha(argb(70, 255, 255, 255), al));
            g.text(text, rx - bw, cy - bh / 2.0, bw, bh, 11.0 * s, true, 1, true, false, white);
            hits.push((rx - bw, cy - bh / 2.0, bw, bh, act));
        };
        // Pomodoro row
        let cy = top + rh / 2.0;
        g.fill_round(x + 10.0 * s, top, w - 20.0 * s, rh, 10.0 * s, with_alpha(argb(255, 30, 30, 34), al));
        let t1 = if self.x.pomo == 0 { "Pomodoro".to_string() } else { format!("{}{}", if self.x.pomo == 1 { "Focus  " } else { "Break  " }, clock(self.pomo_remain())) };
        let t2 = if self.x.pomo == 0 { format!("{} min focus, {} min break", focus_min(), break_min()) } else if self.x.paused { "Paused".to_string() } else { "Running".to_string() };
        g.text(&t1, x + 24.0 * s, top + 2.0 * s, 190.0 * s, rh / 2.0 + 4.0 * s, 12.5 * s, true, 0, true, false, white);
        g.text(&t2, x + 24.0 * s, top + rh / 2.0 - 2.0 * s, 190.0 * s, rh / 2.0, 10.5 * s, false, 0, true, false, dim);
        let bw = 54.0 * s;
        if self.x.pomo == 0 {
            btn(g, &mut self.x.hits, right, cy, bw, "Start", Act::PomoStart);
        } else {
            btn(g, &mut self.x.hits, right, cy, bw, "Stop", Act::PomoStop);
            btn(g, &mut self.x.hits, right - bw - 6.0 * s, cy, bw, "Skip", Act::PomoSkip);
            btn(g, &mut self.x.hits, right - 2.0 * (bw + 6.0 * s), cy, bw, if self.x.paused { "Resume" } else { "Pause" }, Act::PomoPause);
        }
        // Water row
        let top2 = top + rh + 4.0 * s;
        let cy2 = top2 + rh / 2.0;
        g.fill_round(x + 10.0 * s, top2, w - 20.0 * s, rh, 10.0 * s, with_alpha(argb(255, 30, 30, 34), al));
        let (n, goal) = (water_count(), water_goal());
        g.text(&format!("Water  {} / {}", n, goal), x + 24.0 * s, top2 + 2.0 * s, 190.0 * s, rh / 2.0 + 4.0 * s, 12.5 * s, true, 0, true, false, white);
        g.text(&if opt_b("island-water", true) { format!("Reminds every {} min", water_every()) } else { "Reminders off".to_string() }, x + 24.0 * s, top2 + rh / 2.0 - 2.0 * s, 190.0 * s, rh / 2.0, 10.5 * s, false, 0, true, false, dim);
        btn(g, &mut self.x.hits, right, cy2, 70.0 * s, "+ Glass", Act::AddGlass);
        // Stand-up row
        let top3 = top2 + rh + 4.0 * s;
        g.fill_round(x + 10.0 * s, top3, w - 20.0 * s, rh, 10.0 * s, with_alpha(argb(255, 30, 30, 34), al));
        g.text("Stand up", x + 24.0 * s, top3 + 2.0 * s, 190.0 * s, rh / 2.0 + 4.0 * s, 12.5 * s, true, 0, true, false, white);
        let s2 = if !opt_b("island-stand", true) {
            "Reminders off".to_string()
        } else if self.x.pomo == 1 {
            "After this focus session".to_string()
        } else {
            let mins = self.x.next_stand.map(|t| t.saturating_duration_since(Instant::now()).as_secs() / 60 + 1).unwrap_or(stand_every() as u64);
            format!("Next in {} min", mins)
        };
        g.text(&s2, x + 24.0 * s, top3 + rh / 2.0 - 2.0 * s, 260.0 * s, rh / 2.0, 10.5 * s, false, 0, true, false, dim);
        let _ = cy;
    }

    // ---------- mouse ----------
    pub fn hit_act(&self, x: f32, y: f32) -> Option<Act> {
        self.x.hits.iter().rev().find(|h| x >= h.0 && x <= h.0 + h.2 && y >= h.1 && y <= h.1 + h.3).map(|h| h.4.clone())
    }
    pub fn mouse_down(&mut self, x: f32, y: f32) {
        if self.mode == Mode::Center && self.x.tab == 0 {
            if let Some(Act::Open(id)) = self.hit_act(x, y) {
                if let Some(n) = app().notes.recent().into_iter().find(|n| n.app_id == id && !self.x.dismissed.contains(&n.id)) {
                    self.x.swipe = Some((n.id, x, 0.0));
                }
            }
        }
    }
    pub fn mouse_move(&mut self, x: f32) {
        if let Some((id, x0, _)) = self.x.swipe {
            self.x.swipe = Some((id, x0, x - x0));
            self.need = true;
            self.kick_pub();
        }
    }
    /// Returns true when the click was consumed.
    pub fn mouse_up(&mut self, x: f32, y: f32) -> bool {
        let s = self.s;
        if let Some((id, _, dx)) = self.x.swipe.take() {
            if dx.abs() > 70.0 * s {
                self.x.dismissed.insert(id); // swiped away
                crate::notify::save_dismissed(&self.x.dismissed);
                self.need = true;
                self.kick_pub();
                return true;
            }
        }
        match self.mode {
            Mode::Center | Mode::Toast => {
                if let Some(act) = self.hit_act(x, y) {
                    if self.mode == Mode::Toast {
                        self.mode = Mode::Idle;
                    }
                    self.run_act(act);
                    return true;
                }
                false
            }
            _ => false,
        }
    }
}

impl Island {
    /// The panel never grabs focus, so clicks elsewhere cannot reach it: poll the pointer (100 ms) and collapse it
    /// when the user clicks outside, or leaves it for a moment.
    pub fn panel_poll(&mut self) {
        use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
        let s = self.s;
        let p = crate::sys::cursor_pos();
        let scr = crate::sys::screen_rect();
        let cx = (scr.left + scr.right) as f32 / 2.0;
        let (hw_, top, bottom) = (self.sw.v / 2.0 + 10.0 * s, scr.top as f32, scr.top as f32 + 4.0 * s + self.sh.v + 12.0 * s);
        let inside = (p.x as f32 - cx).abs() <= hw_ && (p.y as f32) >= top && (p.y as f32) <= bottom;
        let down = unsafe { GetAsyncKeyState(0x01) as u16 & 0x8000 != 0 };
        let now = Instant::now();
        let outside_click = down && !self.x.was_down && !inside;
        self.x.was_down = down;
        if inside {
            self.x.left_since = None;
        } else if self.x.left_since.is_none() {
            self.x.left_since = Some(now);
        }
        let away = self.x.left_since.map(|t| now.duration_since(t).as_millis() > 1600).unwrap_or(false);
        if outside_click || away {
            self.mode = Mode::Idle;
            self.x.left_since = None;
            self.x.swipe = None;
            self.need = true;
            self.kick_pub();
        }
    }
}
