//! Menu bar icon popups (Wi-Fi, Bluetooth, Sound, Battery, Profile): a small glass panel under the icon built from rows
//! (title with switch, info, header, selectable items, buttons, slider). Slow data (networks, devices) is filled in on a
//! worker thread, like the C# Panels.
use crate::gfx::*;
use crate::netinfo;
use crate::status;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use std::sync::{Arc, Mutex};

#[derive(Clone, PartialEq)]
pub enum PAct {
    None,
    Switch(bool, bool), // (is wifi, turn on)
    WifiNet(String, bool), // profile or name, currently connected
    Rescan,
    Open(String),
    Lock,
    SignOut,
}

#[derive(Clone)]
pub enum Row {
    Title(String, Option<(bool, PAct)>), // title, optional switch (state, action when toggled)
    Info(String, String),
    Header(String),
    Sep,
    Item(String, String, bool, PAct), // name, sub, selected/connected, action
    Button(String, PAct),
    Volume(f32),
}

type Pending = Arc<Mutex<Option<(String, Vec<Row>)>>>;

pub struct Panel {
    hwnd: HWND,
    gfx: Option<Gfx>,
    pub kind: String,
    rows: Vec<Row>,
    pos: (i32, i32),
    size: (i32, i32),
    pub visible: bool,
    open: Spring,
    timer: bool,
    last: std::time::Instant,
    hits: Vec<(f32, f32, f32, f32, PAct, i32)>, // rect, action, row index (-1: none)
    slider: Option<(f32, f32, f32)>,           // track x, y, w
    drag_vol: bool,
    pending: Pending,
    closed_at: std::time::Instant,
    anchor_x: i32,
    volume: f32,
}

const W: f32 = 330.0;

impl Panel {
    pub fn new() -> Panel {
        Panel { hwnd: HWND::default(), gfx: None, kind: String::new(), rows: vec![], pos: (0, 0), size: (0, 0), visible: false, open: Spring::new(0.0), timer: false, last: std::time::Instant::now(), hits: vec![], slider: None, drag_vol: false, pending: Arc::new(Mutex::new(None)), closed_at: std::time::Instant::now() - std::time::Duration::from_secs(5), anchor_x: 0, volume: 0.5 }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            crate::clock::start(crate::clock::PANEL);
        }
    }
    pub fn toggle(&mut self, kind: &str, anchor_x: i32) {
        if self.visible && self.kind == kind {
            self.close();
            return;
        }
        if self.closed_at.elapsed().as_millis() < 300 && self.kind == kind {
            return;
        }
        self.show(kind, anchor_x);
    }
    pub fn show(&mut self, kind: &str, anchor_x: i32) {
        let a = app();
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_PANEL, true, true, false, false, 0, 0, 10, 10);
        }
        a.cc.close();
        a.lp.close();
        a.spot.close();
        self.kind = kind.to_string();
        self.anchor_x = anchor_x;
        self.volume = a.stat.get().volume;
        self.rows = Self::placeholder(kind);
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        self.layout();
        unsafe {
            let _ = ShowWindow(self.hwnd, SW_SHOW);
        }
        crate::sys::activate(hv(self.hwnd));
        self.kick();
        self.load_async(kind);
    }
    pub fn close(&mut self) {
        if self.visible {
            self.visible = false;
            self.closed_at = std::time::Instant::now();
            self.open.goal = 0.0;
            self.kick();
        }
    }
    fn placeholder(kind: &str) -> Vec<Row> {
        match kind {
            "wifi" => vec![Row::Title("Wi-Fi".into(), None), Row::Info("Status".into(), "Scanning...".into())],
            "bluetooth" => vec![Row::Title("Bluetooth".into(), None), Row::Info("Status".into(), "Checking...".into())],
            "sound" => vec![Row::Title("Sound".into(), None), Row::Volume(0.5), Row::Sep, Row::Header("Output".into()), Row::Info("Devices".into(), "Looking...".into())],
            "battery" => Self::battery_rows(),
            _ => Self::profile_rows(),
        }
    }
    fn battery_rows() -> Vec<Row> {
        let st = app().stat.get();
        let mut ps = windows::Win32::System::Power::SYSTEM_POWER_STATUS::default();
        unsafe {
            let _ = windows::Win32::System::Power::GetSystemPowerStatus(&mut ps);
        }
        let mut r = vec![Row::Title("Battery".into(), None)];
        if st.battery < 0 {
            r.push(Row::Info("Power".into(), "No battery (plugged in)".into()));
        } else {
            r.push(Row::Info("Charge".into(), format!("{}%", st.battery)));
            r.push(Row::Info("Status".into(), if st.charging { "Charging".into() } else if st.ac { "Plugged in".into() } else { "On battery".into() }));
            if ps.BatteryLifeTime != 0xFFFF_FFFF && !st.ac {
                let m = ps.BatteryLifeTime / 60;
                r.push(Row::Info("Time left".into(), format!("{}h {:02}m", m / 60, m % 60)));
            }
            r.push(Row::Info("Battery saver".into(), if st.saver { "On".into() } else { "Off".into() }));
        }
        r.push(Row::Sep);
        r.push(Row::Button("Battery Settings...".into(), PAct::Open("ms-settings:batterysaver".into())));
        r
    }
    fn profile_rows() -> Vec<Row> {
        let user = std::env::var("USERNAME").unwrap_or_else(|_| "User".into());
        let pc = std::env::var("COMPUTERNAME").unwrap_or_default();
        vec![
            Row::Title(user, None),
            Row::Info("Computer".into(), pc),
            Row::Sep,
            Row::Button("Account Settings...".into(), PAct::Open("ms-settings:yourinfo".into())),
            Row::Button("Lock Screen".into(), PAct::Lock),
            Row::Button("Sign Out".into(), PAct::SignOut),
        ]
    }

    /// Fill the slow rows on a worker thread, then ask the UI to swap them in.
    fn load_async(&mut self, kind: &str) {
        let pending = self.pending.clone();
        let (kind, msg) = (kind.to_string(), app().msg.0 as isize);
        std::thread::spawn(move || {
            let rows = match kind.as_str() {
                "wifi" => {
                    let scanned = netinfo::wifi_scan_timeout(5000);
                    let radio = if scanned.as_ref().map(|v| !v.is_empty()).unwrap_or(false) { Some(true) } else { netinfo::radio_on_timeout(true, 1800).or(if scanned.is_some() { Some(true) } else { None }) };
                    let nets = if radio == Some(false) { Some(vec![]) } else { scanned };
                    let mut r = vec![Row::Title("Wi-Fi".into(), radio.map(|on| (on, PAct::Switch(true, !on))))];
                    match (&nets, radio) {
                        (None, _) => r.push(Row::Info("Wi-Fi".into(), "Not available on this PC".into())),
                        (Some(n), rad) => {
                            let cur = n.iter().find(|x| x.connected);
                            r.push(Row::Info("Status".into(), match (cur, rad) {
                                (Some(c), _) => format!("Connected to {}", c.name),
                                (None, Some(false)) => "Off".into(),
                                _ => "Not connected".into(),
                            }));
                            if rad != Some(false) {
                                r.push(Row::Sep);
                                r.push(Row::Header("Networks".into()));
                                for net in n.iter().take(8) {
                                    let sub = if net.connected { "Connected - click to disconnect".to_string() } else { format!("{}{}", if net.secure { "Secured" } else { "Open" }, if net.signal > 0 { format!("  {}%", net.signal) } else { String::new() }) };
                                    let key = if net.profile.is_empty() { net.name.clone() } else { net.profile.clone() };
                                    r.push(Row::Item(net.name.clone(), sub, net.connected, PAct::WifiNet(key, net.connected)));
                                }
                                if n.is_empty() {
                                    r.push(Row::Info("Networks".into(), "None found".into()));
                                }
                            }
                        }
                    }
                    r.push(Row::Sep);
                    r.push(Row::Button("Rescan".into(), PAct::Rescan));
                    r.push(Row::Button("Network Settings...".into(), PAct::Open("ms-settings:network-wifi".into())));
                    r
                }
                "bluetooth" => {
                    let radio = netinfo::radio_on_timeout(false, 3500);
                    let mut r = vec![Row::Title("Bluetooth".into(), radio.map(|on| (on, PAct::Switch(false, !on))))];
                    match radio {
                        None => r.push(Row::Info("Bluetooth".into(), "Not available on this PC".into())),
                        Some(false) => r.push(Row::Info("Status".into(), "Off".into())),
                        Some(true) => {
                            r.push(Row::Info("Status".into(), "On".into()));
                            r.push(Row::Sep);
                            r.push(Row::Header("Paired devices".into()));
                            let devs = netinfo::bt_devices_timeout(5000);
                            for (name, conn) in devs.iter().take(8) {
                                r.push(Row::Item(name.clone(), if *conn { "Connected".into() } else { "Paired".into() }, *conn, PAct::Open("ms-settings:bluetooth".into())));
                            }
                            if devs.is_empty() {
                                r.push(Row::Info("Devices".into(), "None paired".into()));
                            }
                        }
                    }
                    r.push(Row::Sep);
                    r.push(Row::Button("Bluetooth Settings...".into(), PAct::Open("ms-settings:bluetooth".into())));
                    r
                }
                "sound" => {
                    let mut r = vec![Row::Title("Sound".into(), None), Row::Volume(status::get_volume().map(|v| v.0).unwrap_or(0.5)), Row::Sep, Row::Header("Output".into())];
                    for (name, def) in netinfo::audio_outputs().into_iter().take(8) {
                        r.push(Row::Item(name, if def { "Default".into() } else { String::new() }, def, PAct::Open("ms-settings:sound".into())));
                    }
                    r.push(Row::Sep);
                    r.push(Row::Button("Sound Settings...".into(), PAct::Open("ms-settings:sound".into())));
                    r
                }
                _ => return,
            };
            *pending.lock().unwrap() = Some((kind, rows));
            unsafe {
                let _ = PostMessageW(Some(HWND(msg as *mut _)), WM_PANEL, WPARAM(0), LPARAM(0));
            }
        });
    }
    pub fn apply_pending(&mut self) {
        let taken = self.pending.lock().unwrap().take();
        if let Some((kind, rows)) = taken {
            if self.visible && self.kind == kind {
                self.rows = rows;
                self.layout();
                self.draw();
            }
        }
    }

    fn row_h(r: &Row, s: f32) -> f32 {
        s * match r {
            Row::Title(..) => 46.0,
            Row::Info(..) => 30.0,
            Row::Header(..) => 28.0,
            Row::Sep => 12.0,
            Row::Item(..) => 46.0,
            Row::Button(..) => 38.0,
            Row::Volume(..) => 42.0,
        }
    }
    fn layout(&mut self) {
        let a = app();
        let s = a.scale;
        let m = 28.0 * s;
        let total: f32 = self.rows.iter().map(|r| Self::row_h(r, s)).sum::<f32>() + 16.0 * s;
        let (w, h) = ((W * s + 2.0 * m) as i32, (total + 2.0 * m) as i32);
        let scr = screen_rect();
        if self.gfx.is_none() || self.size != (w, h) {
            self.gfx = Gfx::new(w, h);
            self.size = (w, h);
        }
        let x = (self.anchor_x - w / 2).clamp(scr.left, scr.right - w + (m as i32 / 2));
        self.pos = (x, scr.top + a.bar.height() - (m as i32) + (4.0 * s) as i32);
        win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let moving = self.open.step(dt, 400.0, 32.0);
        if !self.visible && self.open.v < 0.03 {
            win::hide(self.hwnd);
            self.timer = false;
            crate::clock::stop(crate::clock::PANEL);
            return;
        }
        if !moving {
            self.timer = false;
            crate::clock::stop(crate::clock::PANEL);
            self.draw();
            return;
        }
        self.draw();
    }

    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let t = self.open.v.clamp(0.0, 1.0);
        let al = |v: f32| (v * t) as u8;
        let m = 28.0 * s;
        let (w, h) = (g.w as f32 - 2.0 * m, g.h as f32 - 2.0 * m);
        g.shadow(m, m, w, h, 16.0 * s, 22.0 * s, al(190.0));
        g.fill_round(m, m, w, h, 16.0 * s, if dark { argb(al(240.0), 30, 30, 34) } else { argb(al(242.0), 246, 246, 249) });
        g.stroke_round(m + 0.5, m + 0.5, w - 1.0, h - 1.0, 16.0 * s, 1.0, argb(al(46.0), 255, 255, 255));
        let fg = if dark { argb(al(255.0), 245, 245, 247) } else { argb(al(255.0), 28, 28, 32) };
        let dim = if dark { argb(al(150.0), 255, 255, 255) } else { argb(al(150.0), 0, 0, 0) };
        let accent = argb(al(255.0), 10, 132, 255);
        self.hits.clear();
        self.slider = None;
        let mut y = m + 8.0 * s;
        let (x0, x1) = (m + 18.0 * s, m + w - 18.0 * s);
        for (i, r) in self.rows.clone().iter().enumerate() {
            let rh = Self::row_h(r, s);
            match r {
                Row::Title(t, sw) => {
                    g.text(t, x0, y, x1 - x0 - 60.0 * s, rh, 17.0 * s, true, 0, true, false, fg);
                    if let Some((on, act)) = sw {
                        let (tw, th) = (42.0 * s, 24.0 * s);
                        let (tx, ty) = (x1 - tw, y + rh / 2.0 - th / 2.0);
                        g.fill_round(tx, ty, tw, th, th / 2.0, if *on { argb(al(255.0), 52, 199, 89) } else { argb(al(255.0), 90, 90, 98) });
                        g.fill_circle(if *on { tx + tw - th / 2.0 } else { tx + th / 2.0 }, ty + th / 2.0, th / 2.0 - 2.0 * s, argb(al(255.0), 255, 255, 255));
                        self.hits.push((tx, ty, tw, th, act.clone(), i as i32));
                    }
                }
                Row::Info(k, v) => {
                    g.text(k, x0, y, 120.0 * s, rh, 13.0 * s, false, 0, true, false, dim);
                    g.text(v, x0 + 100.0 * s, y, x1 - x0 - 100.0 * s, rh, 13.0 * s, false, 2, true, false, fg);
                }
                Row::Header(t) => g.text(&t.to_uppercase(), x0, y + 4.0 * s, x1 - x0, rh - 4.0 * s, 10.5 * s, true, 0, true, false, dim),
                Row::Sep => g.line(x0, y + rh / 2.0, x1, y + rh / 2.0, 1.0, argb(al(40.0), 255, 255, 255)),
                Row::Item(name, sub, sel, act) => {
                    g.text(name, x0 + 24.0 * s, y + 4.0 * s, x1 - x0 - 34.0 * s, 22.0 * s, 14.0 * s, false, 0, true, false, fg);
                    if !sub.is_empty() {
                        g.text(sub, x0 + 24.0 * s, y + 23.0 * s, x1 - x0 - 34.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, dim);
                    }
                    if *sel {
                        g.polyline(&[(x0 + 3.0 * s, y + rh / 2.0), (x0 + 8.0 * s, y + rh / 2.0 + 5.0 * s), (x0 + 16.0 * s, y + rh / 2.0 - 5.0 * s)], 2.0 * s, accent);
                    }
                    self.hits.push((m + 6.0 * s, y, w - 12.0 * s, rh, act.clone(), i as i32));
                }
                Row::Button(t, act) => {
                    g.text(t, x0, y, x1 - x0, rh, 13.5 * s, false, 0, true, false, accent);
                    self.hits.push((m + 6.0 * s, y, w - 12.0 * s, rh, act.clone(), i as i32));
                }
                Row::Volume(v) => {
                    let v = if self.drag_vol { self.volume } else { *v };
                    let (tx, tw, ty) = (x0 + 22.0 * s, x1 - x0 - 44.0 * s, y + rh / 2.0);
                    g.fill_round(tx, ty - 2.5 * s, tw, 5.0 * s, 2.5 * s, argb(al(255.0), 80, 80, 88));
                    g.fill_round(tx, ty - 2.5 * s, (tw * v).max(5.0 * s), 5.0 * s, 2.5 * s, argb(al(255.0), 250, 250, 252));
                    g.fill_circle(tx + tw * v, ty, 7.5 * s, argb(al(255.0), 255, 255, 255));
                    self.slider = Some((tx, ty, tw));
                }
            }
            y += rh;
        }
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }

    fn act(&mut self, act: PAct) {
        let a = app();
        let kind = self.kind.clone();
        match act {
            PAct::None => {}
            PAct::Open(t) => {
                self.close();
                shell_open(&t);
            }
            PAct::Lock => {
                self.close();
                unsafe {
                    let _ = windows::Win32::System::Shutdown::LockWorkStation();
                }
            }
            PAct::SignOut => {
                self.close();
                run_cmd("shutdown.exe", "/l");
            }
            PAct::Rescan => {
                self.rows = Self::placeholder(&kind);
                self.layout();
                self.load_async(&kind);
            }
            PAct::Switch(wifi, on) => {
                let k = kind.clone();
                let msg = a.msg.0 as isize;
                self.rows.iter_mut().for_each(|r| {
                    if let Row::Title(_, Some((st, _))) = r {
                        *st = on;
                    }
                });
                self.draw();
                std::thread::spawn(move || {
                    netinfo::set_radio(wifi, on);
                    std::thread::sleep(std::time::Duration::from_millis(if on { 2200 } else { 400 }));
                    unsafe {
                        let _ = PostMessageW(Some(HWND(msg as *mut _)), WM_PANEL_RELOAD, WPARAM(0), LPARAM(0));
                    }
                    let _ = k;
                });
            }
            PAct::WifiNet(key, connected) => {
                let msg = a.msg.0 as isize;
                std::thread::spawn(move || {
                    if connected {
                        netinfo::wifi_disconnect();
                    } else if !netinfo::wifi_connect(&key) {
                        // no saved profile: let Windows ask for the password
                        shell_open("ms-availablenetworks:");
                    }
                    std::thread::sleep(std::time::Duration::from_millis(2200));
                    unsafe {
                        let _ = PostMessageW(Some(HWND(msg as *mut _)), WM_PANEL_RELOAD, WPARAM(0), LPARAM(0));
                    }
                });
            }
        }
    }
    pub fn reload(&mut self) {
        if self.visible {
            let k = self.kind.clone();
            self.load_async(&k);
        }
    }
    fn hit(&self, x: f32, y: f32) -> Option<PAct> {
        self.hits.iter().rev().find(|h| x >= h.0 && x <= h.0 + h.2 && y >= h.1 && y <= h.1 + h.3).map(|h| h.4.clone())
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let p = &mut app().panel;
    match m {
        WM_LBUTTONDOWN => {
            let (x, y) = win::lparam_xy(lp);
            if let Some((tx, ty, tw)) = p.slider {
                if (y as f32 - ty).abs() < 16.0 * app().scale && x as f32 >= tx - 10.0 && x as f32 <= tx + tw + 10.0 {
                    p.drag_vol = true;
                    SetCapture(h);
                    p.volume = ((x as f32 - tx) / tw).clamp(0.0, 1.0);
                    status::set_volume(p.volume);
                    p.draw();
                    return LRESULT(0);
                }
            }
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            if p.drag_vol {
                if let Some((tx, _, tw)) = p.slider {
                    let (x, _) = win::lparam_xy(lp);
                    p.volume = ((x as f32 - tx) / tw).clamp(0.0, 1.0);
                    status::set_volume(p.volume);
                    p.draw();
                }
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if p.drag_vol {
                p.drag_vol = false;
                let _ = ReleaseCapture();
                return LRESULT(0);
            }
            let (x, y) = win::lparam_xy(lp);
            if let Some(act) = p.hit(x as f32, y as f32) {
                p.act(act);
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == 0x1B {
                p.close();
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if p.visible && p.open.v > 0.8 && GetForegroundWindow() != h {
                p.close();
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
