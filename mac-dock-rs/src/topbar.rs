//! Menu bar: logo menu, active app name, File/Edit/View/Window/Help, CPU/RAM/net readouts, volume, Wi-Fi,
//! battery, Spotlight, Control Center, notifications, clock. Redraws only when what it shows changes.
use crate::config::Config;
use crate::gfx::*;
use crate::sys::*;
use crate::win;
use crate::*;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

struct Hit {
    x0: f32,
    x1: f32,
    id: u32,
}

pub struct TopBar {
    pub hwnd: HWND,
    pill: HWND,
    gfx: Option<Gfx>,
    s: f32,
    w: i32,
    h: i32,
    hits: Vec<Hit>,
    hover: u32,
    sig: String,
    shown: bool,
    app_name: String,
    scr: RECT,
    pub clock_x: f32,
    avatar: Option<Img>,
    tray_imgs: Vec<(String, Img)>,
    tray_ver: u32,
}

fn clock_text() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    let t = unsafe { GetLocalTime() };
    const D: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const M: [&str; 12] = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    let h12 = if t.wHour % 12 == 0 { 12 } else { t.wHour % 12 };
    format!("{} {} {}  {}:{:02} {}", D[t.wDayOfWeek as usize % 7], t.wDay, M[(t.wMonth as usize + 11) % 12], h12, t.wMinute, if t.wHour < 12 { "AM" } else { "PM" })
}

impl TopBar {
    pub fn new() -> TopBar {
        TopBar { hwnd: HWND::default(), pill: HWND::default(), gfx: None, s: 1.0, w: 0, h: 0, hits: vec![], hover: 0, sig: String::new(), shown: true, app_name: String::new(), scr: RECT::default(), clock_x: 0.0, avatar: None, tray_imgs: vec![], tray_ver: 0 }
    }
    pub fn height(&self) -> i32 {
        self.h
    }
    pub fn create(&mut self, scale: f32, cfg: &Config) {
        self.pill = win::create(ID_BARPILL, false, true, true, true, 0, 0, 10, 10);
        self.hwnd = win::create(ID_BAR, true, true, true, false, 0, 0, 10, 10);
        self.relayout(scale);
        self.apply_theme(cfg.s.dark);
        if cfg.s.bar_show {
            win::show_noactivate(self.pill);
            win::show_noactivate(self.hwnd);
        } else {
            self.shown = false;
        }
        unsafe {
            SetTimer(Some(self.hwnd), T_BAR, 1000, None);
        }
        self.on_foreground();
    }
    pub fn apply_theme(&mut self, dark: bool) {
        set_acrylic(self.pill, if dark { 0x8C101010 } else { 0x8CF2F2F2 }, false);
        self.sig.clear();
    }
    pub fn relayout(&mut self, scale: f32) {
        self.s = scale;
        self.scr = screen_rect();
        self.w = self.scr.right - self.scr.left;
        self.h = (28.0 * scale).round() as i32;
        self.gfx = Gfx::new(self.w, self.h);
        win::set_pos(self.pill, self.scr.left, self.scr.top, self.w, self.h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        win::set_pos(self.hwnd, self.scr.left, self.scr.top, self.w, self.h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        self.sig.clear();
        self.draw();
    }
    pub fn set_fullscreen(&mut self, fs: bool) {
        if fs {
            win::hide(self.hwnd);
            win::hide(self.pill);
        } else if app().cfg.s.bar_show {
            win::show_noactivate(self.pill);
            win::show_noactivate(self.hwnd);
            self.sig.clear();
            self.draw();
        }
    }
    pub fn set_visible(&mut self, v: bool) {
        self.shown = v;
        if v {
            win::show_noactivate(self.pill);
            win::show_noactivate(self.hwnd);
            self.sig.clear();
            self.draw();
        } else {
            win::hide(self.hwnd);
            win::hide(self.pill);
        }
    }
    pub fn dirty(&mut self) {
        self.draw();
    }
    pub fn on_foreground(&mut self) {
        let a = app();
        let own = std::env::current_exe().map(|p| p.to_string_lossy().to_lowercase()).unwrap_or_default();
        if foreground_info().map(|f| f.1 == own).unwrap_or(false) {
            return;
        }
        let name = match foreground_info() {
            Some((_, exe)) if !exe.is_empty() => {
                let item = a.dock.items.iter().find(|i| (!i.exe.is_empty() && i.exe == exe) || (!i.suffix.is_empty() && exe.ends_with(&i.suffix)) || (!i.pkg.is_empty() && exe.contains(&i.pkg)));
                match item {
                    Some(i) => i.label.clone(),
                    None => crate::dock::nice_name(&exe),
                }
            }
            _ => "Desktop".into(),
        };
        let name = if name.eq_ignore_ascii_case("explorer") { "Finder".to_string() } else { name };
        if name != self.app_name {
            self.app_name = name;
            self.draw();
        }
    }

    fn draw(&mut self) {
        if !self.shown || !app().cfg.s.bar_show || self.gfx.is_none() {
            return;
        }
        let a = app();
        let st = a.stat.get();
        let clock = clock_text();
        if self.avatar.is_none() {
            if let Some(p) = crate::account::take() {
                self.avatar = Img::from_pixels(p);
            }
        }
        let had_avatar = self.avatar.is_some();
        let tv = crate::tray::VERSION.load(std::sync::atomic::Ordering::Relaxed);
        if tv != self.tray_ver {
            self.tray_ver = tv;
            self.tray_imgs.clear();
            if a.cfg.ob("tray-bar", false) {
                let sel = crate::tray::selected(&a.cfg);
                for t in crate::tray::cached() {
                    if sel.contains(&t.key) {
                        if let Some(img) = crate::tray::pixels_of(&t.key).and_then(Img::from_pixels) {
                            self.tray_imgs.push((t.key.clone(), img));
                        }
                    }
                }
            }
            self.sig.clear();
        }
        let sig = format!("{}|{}|{}|{:.0}|{:.0}|{:.0}|{}|{}|{}|{:.0}|{}|{}|{}", clock, self.hover, self.app_name, st.cpu, st.ram, st.volume * 10.0, st.battery, st.charging, st.wifi, st.down_kbps / 10.0, st.muted, a.cfg.s.dark, st.ac) + if had_avatar { "|av" } else { "" };
        if sig == self.sig {
            return;
        }
        self.sig = sig;
        let dark = a.cfg.s.dark;
        let s = self.s;
        let fg = if dark { argb(240, 255, 255, 255) } else { argb(235, 20, 20, 20) };
        let dim = if dark { argb(170, 255, 255, 255) } else { argb(150, 20, 20, 20) };
        let mut g = self.gfx.take().unwrap();
        g.clear(0);
        let hh = self.h as f32;
        g.line(0.0, hh - 0.5, self.w as f32, hh - 0.5, 1.0, if dark { argb(26, 255, 255, 255) } else { argb(30, 0, 0, 0) });
        self.hits.clear();
        let fs = 13.0 * s;
        let pad = 10.0 * s;
        let mut x = 14.0 * s;
        let hover = self.hover;
        let hl = |g: &mut Gfx, x0: f32, x1: f32, id: u32| {
            if hover == id {
                g.fill_round(x0, 3.0 * s, x1 - x0, hh - 6.0 * s, 6.0 * s, if dark { argb(38, 255, 255, 255) } else { argb(34, 0, 0, 0) });
            }
        };
        // logo
        let lw = 24.0 * s;
        hl(&mut g, x - 5.0 * s, x + lw - 5.0 * s, 1);
        let c = fg;
        let q = 4.4 * s;
        let (lx, ly) = (x + 1.5 * s, hh / 2.0 - q - 0.5 * s);
        for (dx, dy) in [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)] {
            g.fill_rect(lx + dx * (q + 1.2 * s), ly + dy * (q + 1.2 * s), q, q, c);
        }
        self.hits.push(Hit { x0: x - 5.0 * s, x1: x + lw - 5.0 * s, id: 1 });
        x += lw + 4.0 * s;
        // app name (bold)
        let name = self.app_name.clone();
        let nw = g.measure(&name, fs, true) + pad;
        g.text(&name, x, 0.0, nw + 4.0, hh, fs, true, 0, true, false, fg);
        self.hits.push(Hit { x0: x - 4.0 * s, x1: x + nw, id: 2 });
        x += nw + 6.0 * s;
        for (i, m) in ["File", "Edit", "View", "Window", "Help"].iter().enumerate() {
            let w_ = g.measure(m, fs, false) + pad;
            let id = 10 + i as u32;
            hl(&mut g, x - 5.0 * s, x + w_ - 5.0 * s, id);
            g.text(m, x, 0.0, w_, hh, fs, false, 0, true, false, fg);
            self.hits.push(Hit { x0: x - 5.0 * s, x1: x + w_ - 5.0 * s, id });
            x += w_;
        }
        // right side: blocks laid out from the right edge inward, in the saved @bar-order (Settings > Menu Bar)
        let order = bar_order(&a.cfg);
        let extra = match a.cfg.os("bar-spacing", "normal").to_lowercase().as_str() {
            "compact" => -3.0,
            "roomy" => 6.0,
            "wide" => 12.0,
            _ => 0.0,
        } * s;
        let mut rx = self.w as f32 - 14.0 * s;
        let icon_w = 26.0 * s;
        let cy = hh / 2.0;
        for blk in order.iter() {
            match blk.as_str() {
                "clock" => {
                    let clock = clock_text();
                    let cw = g.measure(&clock, fs, false) + pad;
                    hl(&mut g, rx - cw + 5.0 * s, rx + 5.0 * s, 28);
                    g.text(&clock, rx - cw, 0.0, cw, hh, fs, false, 2, true, false, fg);
                    self.hits.push(Hit { x0: rx - cw, x1: rx + 5.0 * s, id: 28 });
                    self.clock_x = rx - cw;
                    rx -= cw + 6.0 * s;
                }
                "profile" => {
                    if !a.cfg.ob("bar-profile", true) {
                        continue;
                    }
                    rx -= icon_w;
                    hl(&mut g, rx, rx + icon_w, 30);
                    self.hits.push(Hit { x0: rx, x1: rx + icon_w, id: 30 });
                    let (px, r) = (rx + icon_w / 2.0, 8.5 * s);
                    if let Some(av) = &self.avatar {
                        g.image(av, px - r, cy - r, r * 2.0, r * 2.0, 1.0);
                        continue;
                    }
                    g.fill_circle(px, cy, r, argb(255, 120, 120, 134));
                    let initial = std::env::var("USERNAME").ok().and_then(|u| u.chars().next()).map(|c| c.to_uppercase().to_string()).unwrap_or_else(|| "U".into());
                    g.text(&initial, px - r, cy - r, r * 2.0, r * 2.0, 10.0 * s, true, 1, true, false, argb(255, 255, 255, 255));
                }
                "bell" => {
                    if !a.cfg.ob("bar-bell", true) {
                        continue;
                    }
                    let bx = slot(&mut g, 27, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.arc(bx, cy - 0.5 * s, 4.6 * s, 180.0, 180.0, 1.6 * s, fg);
                    g.line(bx - 4.6 * s, cy - 0.5 * s, bx - 5.6 * s, cy + 3.4 * s, 1.6 * s, fg);
                    g.line(bx + 4.6 * s, cy - 0.5 * s, bx + 5.6 * s, cy + 3.4 * s, 1.6 * s, fg);
                    g.line(bx - 5.6 * s, cy + 3.4 * s, bx + 5.6 * s, cy + 3.4 * s, 1.6 * s, fg);
                }
                "screenshot" => {
                    if !a.cfg.ob("bar-screenshot", false) {
                        continue;
                    }
                    let bx = slot(&mut g, 31, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.stroke_round(bx - 6.0 * s, cy - 5.0 * s, 12.0 * s, 10.0 * s, 2.0 * s, 1.5 * s, fg);
                    g.stroke_circle(bx, cy, 2.4 * s, 1.4 * s, fg);
                }
                "record" => {
                    if !a.cfg.ob("bar-record", false) {
                        continue;
                    }
                    let bx = slot(&mut g, 32, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.stroke_circle(bx, cy, 6.0 * s, 1.5 * s, fg);
                    g.fill_circle(bx, cy, 3.0 * s, argb(255, 255, 69, 58));
                }
                "control" => {
                    let ccx = slot(&mut g, 26, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.stroke_round(ccx - 6.5 * s, cy - 5.5 * s, 9.0 * s, 4.4 * s, 2.2 * s, 1.3 * s, fg);
                    g.fill_circle(ccx - 4.3 * s, cy - 3.3 * s, 1.5 * s, fg);
                    g.stroke_round(ccx - 2.5 * s, cy + 1.1 * s, 9.0 * s, 4.4 * s, 2.2 * s, 1.3 * s, fg);
                    g.fill_circle(ccx + 4.3 * s, cy + 3.3 * s, 1.5 * s, fg);
                }
                "search" => {
                    let sx = slot(&mut g, 25, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.stroke_circle(sx - 1.0 * s, cy - 1.0 * s, 4.6 * s, 1.6 * s, fg);
                    g.line(sx + 2.4 * s, cy + 2.4 * s, sx + 6.0 * s, cy + 6.0 * s, 1.7 * s, fg);
                }
                "battery" => {
                    if st.battery < 0 {
                        continue;
                    }
                    let txt = format!("{}%", st.battery);
                    let tw = g.measure(&txt, fs, false) + 4.0 * s;
                    let bw = 22.0 * s + tw + 10.0 * s;
                    rx -= bw;
                    hl(&mut g, rx, rx + bw, 24);
                    self.hits.push(Hit { x0: rx, x1: rx + bw, id: 24 });
                    g.text(&txt, rx + 4.0 * s, 0.0, tw, hh, fs, false, 0, true, false, fg);
                    let bxx = rx + 4.0 * s + tw + 2.0 * s;
                    g.stroke_round(bxx, cy - 5.0 * s, 20.0 * s, 10.0 * s, 3.0 * s, 1.2 * s, dim);
                    g.fill_round(bxx + 21.0 * s, cy - 2.0 * s, 2.0 * s, 4.0 * s, 1.0 * s, dim);
                    let lvl = (st.battery as f32 / 100.0).clamp(0.0, 1.0);
                    let col = if st.charging || st.ac { argb(255, 52, 199, 89) } else if st.battery <= 20 { argb(255, 255, 69, 58) } else { fg };
                    g.fill_round(bxx + 2.0 * s, cy - 3.2 * s, (16.0 * s * lvl).max(1.5 * s), 6.4 * s, 1.6 * s, col);
                }
                "wifi" => {
                    let wx = slot(&mut g, 23, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    let col = if st.wifi || st.ethernet { fg } else { dim };
                    let by = cy + 4.0 * s;
                    g.fill_circle(wx, by, 1.5 * s, col);
                    for r in [4.5, 8.0, 11.5] {
                        g.arc(wx, by, r * s, 225.0, 90.0, 1.6 * s, col);
                    }
                }
                "sound" => {
                    let vx = slot(&mut g, 22, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.fill_poly(&[(vx - 6.0 * s, cy - 2.5 * s), (vx - 3.0 * s, cy - 2.5 * s), (vx + 1.0 * s, cy - 5.5 * s), (vx + 1.0 * s, cy + 5.5 * s), (vx - 3.0 * s, cy + 2.5 * s), (vx - 6.0 * s, cy + 2.5 * s)], fg);
                    if st.muted || st.volume <= 0.001 {
                        g.line(vx + 3.5 * s, cy - 3.0 * s, vx + 8.0 * s, cy + 3.0 * s, 1.5 * s, fg);
                    } else {
                        g.arc(vx + 1.0 * s, cy, 4.0 * s, -45.0, 90.0, 1.5 * s, fg);
                        if st.volume > 0.5 {
                            g.arc(vx + 1.0 * s, cy, 7.0 * s, -45.0, 90.0, 1.5 * s, fg);
                        }
                    }
                }
                "bluetooth" => {
                    if !a.cfg.ob("bar-bluetooth", true) {
                        continue;
                    }
                    let bx = slot(&mut g, 33, &mut self.hits, &mut rx, icon_w, hh, hover, dark, s);
                    g.polyline(&[(bx - 3.5 * s, cy - 3.5 * s), (bx + 3.5 * s, cy + 3.5 * s), (bx, cy + 7.0 * s), (bx, cy - 7.0 * s), (bx + 3.5 * s, cy - 3.5 * s), (bx - 3.5 * s, cy + 3.5 * s)], 1.4 * s, fg);
                }
                "tray" => {
                    if !a.cfg.ob("tray-bar", false) {
                        continue;
                    }
                    let collapsible = a.cfg.ob("tray-collapsible", false);
                    let collapsed = collapsible && a.cfg.ob("tray-collapsed", true);
                    if !collapsed {
                        for (i, (_, img)) in self.tray_imgs.iter().enumerate().rev() {
                            let bx = slot(&mut g, 100 + i as u32, &mut self.hits, &mut rx, 22.0 * s, hh, hover, dark, s);
                            g.image(img, bx - 8.0 * s, cy - 8.0 * s, 16.0 * s, 16.0 * s, 1.0);
                        }
                    }
                    if a.cfg.ob("tray-arrow", true) {
                        let bx = slot(&mut g, 40, &mut self.hits, &mut rx, 18.0 * s, hh, hover, dark, s);
                        let d = if collapsed { 1.0 } else { -1.0 };
                        g.polyline(&[(bx - 2.0 * s * d, cy - 4.0 * s), (bx + 2.0 * s * d, cy), (bx - 2.0 * s * d, cy + 4.0 * s)], 1.5 * s, fg);
                    }
                }
                "stats" => {
                    let mut parts: Vec<String> = vec![];
                    if a.cfg.ob("stats-cpu", true) {
                        parts.push(format!("CPU {:.0}%", st.cpu));
                    }
                    if a.cfg.ob("stats-ram", true) {
                        parts.push(format!("RAM {:.0}%", st.ram));
                    }
                    if a.cfg.ob("stats-disk", false) {
                        parts.push(format!("Disk {:.0}%", st.disk_pct));
                    }
                    if a.cfg.ob("stats-net", true) {
                        let sp = |k: f32| if k >= 1024.0 { format!("{:.1}MB/s", k / 1024.0) } else { format!("{:.0}KB/s", k) };
                        let arrows = a.cfg.ob("stats-net-arrows", true);
                        let mode = a.cfg.os("stats-net-mode", "both");
                        let d = if arrows { format!("\u{2193} {}", sp(st.down_kbps)) } else { sp(st.down_kbps) };
                        let u = if arrows { format!("\u{2191} {}", sp(st.up_kbps)) } else { sp(st.up_kbps) };
                        parts.push(match mode.as_str() {
                            "down" => d,
                            "up" => u,
                            _ => format!("{}  {}", d, u),
                        });
                    }
                    if a.cfg.ob("stats-weather", false) {
                        let w = crate::status::weather();
                        if !w.is_empty() {
                            parts.push(w);
                        }
                    }
                    for p in parts.iter().rev() {
                        let pw_ = g.measure(p, fs, false) + 12.0 * s;
                        rx -= pw_;
                        g.text(p, rx, 0.0, pw_, hh, fs, false, 2, true, false, dim);
                        self.hits.push(Hit { x0: rx, x1: rx + pw_, id: 20 });
                    }
                }
                _ => {}
            }
            rx -= extra;
        }
        g.present_layered(self.hwnd, self.scr.left, self.scr.top, 255);
        self.gfx = Some(g);
    }

    fn hit(&self, x: f32) -> u32 {
        self.hits.iter().find(|h| x >= h.x0 && x <= h.x1).map(|h| h.id).unwrap_or(0)
    }
    fn item_x(&self, id: u32) -> f32 {
        self.hits.iter().find(|h| h.id == id).map(|h| h.x0).unwrap_or(0.0)
    }

    fn tray_click(&mut self, id: u32, mode: u8) {
        let a = app();
        if let Some((key, _)) = self.tray_imgs.get(id as usize - 100) {
            let key = key.clone();
            let win = crate::tray::find_app_window(&key, &a.wins);
            crate::tray::click(key, mode, win);
        }
    }

    fn click(&mut self, id: u32) {
        let a = app();
        let x = (self.scr.left as f32 + self.item_x(id)) as i32;
        let y = self.scr.top + self.h;
        match id {
            1 => apple_menu(x, y),
            10..=14 => app_menu(id, x, y),
            2 => {}
            20 => run_cmd("taskmgr.exe", ""),
            21 | 26 => a.cc.toggle(),
            22 | 23 | 24 | 30 | 33 => {
                let cx = x + ((self.hits.iter().find(|h| h.id == id).map(|h| h.x1 - h.x0).unwrap_or(26.0)) / 2.0) as i32;
                let kind = match id {
                    22 => "sound",
                    23 => "wifi",
                    24 => "battery",
                    33 => "bluetooth",
                    _ => "profile",
                };
                a.panel.toggle(kind, cx);
            }
            25 => a.spot.toggle(),
            27 | 28 => a.island.toggle_center(),
            40 => {
                let c = a.cfg.ob("tray-collapsed", true);
                a.cfg.set_option("tray-collapsed", if c { "off" } else { "on" });
                self.sig.clear();
                self.draw();
            }
            100..=199 => self.tray_click(id, 0),
            31 => crate::capture::screenshot(),
            32 => crate::capture::toggle_record(),
            _ => {}
        }
    }
}

fn key_combo(mods: &[VIRTUAL_KEY], key: VIRTUAL_KEY) {
    unsafe {
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
    }
}

fn confirm(text: &str) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::*;
    let t = wide(text);
    let c = wide("MacDock");
    unsafe { MessageBoxW(None, pw(&t), pw(&c), MB_YESNO | MB_ICONQUESTION | MB_TOPMOST).0 == 6 }
}

fn apple_menu(x: i32, y: i32) {
    let a = app();
    let items: Vec<(u32, String, bool, bool)> = vec![
        (1, "About This PC".into(), true, false),
        (0, String::new(), true, false),
        (2, "System Settings...".into(), true, false),
        (3, "MacDock Settings...".into(), true, false),
        (4, "Task Manager (Force Quit)...".into(), true, false),
        (0, String::new(), true, false),
        (5, "Sleep".into(), true, false),
        (6, "Restart...".into(), true, false),
        (7, "Shut Down...".into(), true, false),
        (0, String::new(), true, false),
        (8, "Lock Screen".into(), true, false),
        (9, "Log Out...".into(), true, false),
        (0, String::new(), true, false),
        (10, "Quit MacDock".into(), true, false),
    ];
    let id = popup_below(a.msg, x, y, &items);
    match id {
        1 => shell_open("ms-settings:about"),
        2 => shell_open("ms-settings:"),
        3 => a.set.show(0),
        4 => run_cmd("taskmgr.exe", ""),
        5 => run_cmd("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
        6 => {
            if confirm("Restart the PC now?") {
                run_cmd("shutdown.exe", "/r /t 0");
            }
        }
        7 => {
            if confirm("Shut down the PC now?") {
                run_cmd("shutdown.exe", "/s /t 0");
            }
        }
        8 => unsafe {
            let _ = windows::Win32::System::Shutdown::LockWorkStation();
        },
        9 => {
            if confirm("Log out now?") {
                run_cmd("shutdown.exe", "/l");
            }
        }
        10 => quit(),
        _ => {}
    }
}

/// Popup that drops down from the bar (TPM_TOPALIGN).
fn popup_below(owner: HWND, x: i32, y: i32, items: &[(u32, String, bool, bool)]) -> u32 {
    unsafe {
        let Ok(m) = CreatePopupMenu() else { return 0 };
        for (id, text, en, chk) in items {
            if *id == 0 && text.is_empty() {
                let _ = AppendMenuW(m, MF_SEPARATOR, 0, windows::core::PCWSTR::null());
            } else {
                let mut f = MF_STRING;
                if !*en {
                    f |= MF_GRAYED;
                }
                if *chk {
                    f |= MF_CHECKED;
                }
                let t = wide(text);
                let _ = AppendMenuW(m, f, *id as usize, pw(&t));
            }
        }
        let _ = SetForegroundWindow(owner);
        let r = TrackPopupMenu(m, TPM_RETURNCMD | TPM_NONOTIFY | TPM_TOPALIGN | TPM_LEFTALIGN, x, y, Some(0), owner, None);
        let _ = PostMessageW(Some(owner), WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(m);
        r.0 as u32
    }
}

fn app_menu(id: u32, x: i32, y: i32) {
    let a = app();
    let e = |i: u32, t: &str| (i, t.to_string(), true, false);
    let sep = (0u32, String::new(), true, false);
    // restore focus to the app the menu acts on before sending shortcuts
    let target = a.fg;
    let items: Vec<(u32, String, bool, bool)> = match id {
        10 => vec![e(1, "New Window\tCtrl+N"), e(2, "New Tab\tCtrl+T"), e(3, "Open...\tCtrl+O"), sep.clone(), e(4, "Close Window\tCtrl+W"), e(5, "Save\tCtrl+S"), e(6, "Print...\tCtrl+P"), sep.clone(), e(7, "Quit (Alt+F4)")],
        11 => vec![e(10, "Undo\tCtrl+Z"), e(11, "Redo\tCtrl+Y"), sep.clone(), e(12, "Cut\tCtrl+X"), e(13, "Copy\tCtrl+C"), e(14, "Paste\tCtrl+V"), e(15, "Select All\tCtrl+A"), sep.clone(), e(16, "Find...\tCtrl+F")],
        12 => vec![e(20, "Reload\tF5"), e(21, "Enter Full Screen\tF11"), sep.clone(), e(22, "Zoom In\tCtrl++"), e(23, "Zoom Out\tCtrl+-"), e(24, "Actual Size\tCtrl+0")],
        13 => {
            let mut v = vec![e(30, "Minimize\tWin+Down"), e(31, "Zoom (Maximize)\tWin+Up"), sep.clone(), e(32, "Mission Control"), e(33, "Launchpad")];
            if !a.wins.is_empty() {
                v.push(sep.clone());
                for (i, w) in a.wins.iter().enumerate().take(14) {
                    let t = if w.title.chars().count() > 44 { format!("{}...", w.title.chars().take(43).collect::<String>()) } else { w.title.clone() };
                    v.push((100 + i as u32, t, true, w.hwnd == a.fg));
                }
            }
            v
        }
        _ => vec![e(40, "Search Windows (Spotlight)"), e(41, "MacDock Settings..."), e(42, "About MacDock")],
    };
    let r = popup_below(a.msg, x, y, &items);
    if r == 0 {
        return;
    }
    let a = app();
    let vk = |c: char| VIRTUAL_KEY(c as u16);
    let refocus = || {
        if target != 0 {
            activate(target);
            std::thread::sleep(std::time::Duration::from_millis(120));
        }
    };
    match r {
        1 | 2 | 3 | 4 | 5 | 6 | 7 | 10..=16 | 20..=24 => {
            refocus();
            match r {
                1 => key_combo(&[VK_CONTROL], vk('N')),
                2 => key_combo(&[VK_CONTROL], vk('T')),
                3 => key_combo(&[VK_CONTROL], vk('O')),
                4 => key_combo(&[VK_CONTROL], vk('W')),
                5 => key_combo(&[VK_CONTROL], vk('S')),
                6 => key_combo(&[VK_CONTROL], vk('P')),
                7 => key_combo(&[VK_MENU], VK_F4),
                10 => key_combo(&[VK_CONTROL], vk('Z')),
                11 => key_combo(&[VK_CONTROL], vk('Y')),
                12 => key_combo(&[VK_CONTROL], vk('X')),
                13 => key_combo(&[VK_CONTROL], vk('C')),
                14 => key_combo(&[VK_CONTROL], vk('V')),
                15 => key_combo(&[VK_CONTROL], vk('A')),
                16 => key_combo(&[VK_CONTROL], vk('F')),
                20 => key_combo(&[], VK_F5),
                21 => key_combo(&[], VK_F11),
                22 => key_combo(&[VK_CONTROL], VK_OEM_PLUS),
                23 => key_combo(&[VK_CONTROL], VK_OEM_MINUS),
                _ => key_combo(&[VK_CONTROL], vk('0')),
            }
        }
        30 => {
            if target != 0 {
                crate::sys::minimize(target)
            }
        }
        31 => unsafe {
            if target != 0 {
                let _ = ShowWindow(hw(target), SW_MAXIMIZE);
            }
        },
        32 => a.mc.toggle(),
        33 => a.lp.toggle(),
        40 => a.spot.toggle(),
        41 => a.set.show(0),
        42 => a.set.show(99),
        100..=130 => {
            if let Some(w) = a.wins.get(r as usize - 100) {
                crate::dock::activate_hwnd(w.hwnd);
            }
        }
        _ => {}
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM, id: isize) -> LRESULT {
    if id == ID_BARPILL {
        return match m {
            WM_NCHITTEST => LRESULT(-1),
            WM_ERASEBKGND => LRESULT(1),
            _ => DefWindowProcW(h, m, wp, lp),
        };
    }
    let b = &mut app().bar;
    match m {
        WM_TIMER if wp.0 == T_BAR => {
            b.draw();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, _) = win::lparam_xy(lp);
            let id = b.hit(x as f32);
            if id != b.hover {
                if b.hover == 0 {
                    let mut t = TRACKMOUSEEVENT { cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32, dwFlags: TME_LEAVE, hwndTrack: h, dwHoverTime: 0 };
                    let _ = TrackMouseEvent(&mut t);
                }
                b.hover = id;
                b.draw();
            }
            LRESULT(0)
        }
        x if x == WM_MOUSELEAVE => {
            b.hover = 0;
            b.draw();
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            let (x, _) = win::lparam_xy(lp);
            let id = b.hit(x as f32);
            if id != 0 {
                b.click(id);
            }
            LRESULT(0)
        }
        WM_RBUTTONUP | WM_LBUTTONDBLCLK => {
            let (x, _) = win::lparam_xy(lp);
            let id = b.hit(x as f32);
            if (100..200).contains(&id) {
                b.tray_click(id, if m == WM_RBUTTONUP { 1 } else { 2 });
            }
            LRESULT(0)
        }
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

pub const DEFAULT_ORDER: [&str; 13] = ["clock", "profile", "bell", "screenshot", "record", "control", "search", "battery", "wifi", "sound", "bluetooth", "stats", "tray"];

/// Saved order (@bar-order) with anything missing appended in the default order and unknown ids dropped.
pub fn bar_order(cfg: &crate::config::Config) -> Vec<String> {
    let mut res: Vec<String> = vec![];
    for id in cfg.os("bar-order", "").split(',') {
        let t = id.trim().to_lowercase();
        if DEFAULT_ORDER.contains(&t.as_str()) && !res.contains(&t) {
            res.push(t);
        }
    }
    for id in DEFAULT_ORDER {
        if !res.iter().any(|r| r == id) {
            res.push(id.to_string());
        }
    }
    res
}

/// Reserve one icon slot from the right edge; returns its center x.
fn slot(g: &mut Gfx, id: u32, hits: &mut Vec<Hit>, rx: &mut f32, icon_w: f32, hh: f32, hover: u32, dark: bool, s: f32) -> f32 {
    *rx -= icon_w;
    if hover == id {
        g.fill_round(*rx, 3.0 * s, icon_w, hh - 6.0 * s, 6.0 * s, if dark { argb(38, 255, 255, 255) } else { argb(34, 0, 0, 0) });
    }
    hits.push(Hit { x0: *rx, x1: *rx + icon_w, id });
    *rx + icon_w / 2.0
}
