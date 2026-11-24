//! Spotlight: Alt+Space search for apps, open windows, quick maths and the web.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

#[derive(Clone)]
struct Res {
    title: String,
    sub: String,
    kind: u8, // 0 app, 1 window, 2 calc, 3 web, 4 command
    target: String,
    hwnd: isize,
}

pub struct Spotlight {
    hwnd: HWND,
    gfx: Option<Gfx>,
    pub visible: bool,
    query: String,
    results: Vec<Res>,
    sel: usize,
    w: i32,
    h: i32,
    pos: (i32, i32),
    open: Spring,
    timer: bool,
    last: std::time::Instant,
    need: bool,
}

fn calc(expr: &str) -> Option<f64> {
    struct P<'a> {
        s: &'a [u8],
        i: usize,
    }
    impl<'a> P<'a> {
        fn ws(&mut self) {
            while self.i < self.s.len() && self.s[self.i] == b' ' {
                self.i += 1;
            }
        }
        fn expr(&mut self) -> Option<f64> {
            let mut v = self.term()?;
            loop {
                self.ws();
                match self.s.get(self.i) {
                    Some(b'+') => {
                        self.i += 1;
                        v += self.term()?;
                    }
                    Some(b'-') => {
                        self.i += 1;
                        v -= self.term()?;
                    }
                    _ => return Some(v),
                }
            }
        }
        fn term(&mut self) -> Option<f64> {
            let mut v = self.pow()?;
            loop {
                self.ws();
                match self.s.get(self.i) {
                    Some(b'*') | Some(b'x') => {
                        self.i += 1;
                        v *= self.pow()?;
                    }
                    Some(b'/') => {
                        self.i += 1;
                        v /= self.pow()?;
                    }
                    Some(b'%') => {
                        self.i += 1;
                        v %= self.pow()?;
                    }
                    _ => return Some(v),
                }
            }
        }
        fn pow(&mut self) -> Option<f64> {
            let b = self.unary()?;
            self.ws();
            if self.s.get(self.i) == Some(&b'^') {
                self.i += 1;
                let e = self.pow()?;
                return Some(b.powf(e));
            }
            Some(b)
        }
        fn unary(&mut self) -> Option<f64> {
            self.ws();
            if self.s.get(self.i) == Some(&b'-') {
                self.i += 1;
                return Some(-self.unary()?);
            }
            if self.s.get(self.i) == Some(&b'(') {
                self.i += 1;
                let v = self.expr()?;
                self.ws();
                if self.s.get(self.i) != Some(&b')') {
                    return None;
                }
                self.i += 1;
                return Some(v);
            }
            let st = self.i;
            while self.i < self.s.len() && (self.s[self.i].is_ascii_digit() || self.s[self.i] == b'.') {
                self.i += 1;
            }
            if st == self.i {
                return None;
            }
            std::str::from_utf8(&self.s[st..self.i]).ok()?.parse().ok()
        }
    }
    if !expr.chars().any(|c| "+-*/^%x".contains(c)) || !expr.chars().any(|c| c.is_ascii_digit()) {
        return None;
    }
    let mut p = P { s: expr.as_bytes(), i: 0 };
    let v = p.expr()?;
    p.ws();
    if p.i == p.s.len() && v.is_finite() { Some(v) } else { None }
}

impl Spotlight {
    pub fn new() -> Spotlight {
        Spotlight { hwnd: HWND::default(), gfx: None, visible: false, query: String::new(), results: vec![], sel: 0, w: 0, h: 0, pos: (0, 0), open: Spring::new(0.0), timer: false, last: std::time::Instant::now(), need: true }
    }
    pub fn dirty(&mut self) {
        if self.visible {
            self.search();
            self.need = true;
            self.kick();
        }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_SPOT));
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
    pub fn show(&mut self) {
        let a = app();
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_SPOTLIGHT, true, true, false, false, 0, 0, 10, 10);
        }
        a.lp.close();
        a.mc.close();
        a.lp.index.ensure(a.msg.0 as isize);
        self.query.clear();
        self.sel = 0;
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        self.search();
        self.layout();
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
        self.open.goal = 0.0;
        self.kick();
    }

    fn layout(&mut self) {
        let a = app();
        let s = a.scale;
        let scr = screen_rect();
        let n = self.results.len();
        let w = (680.0 * s) as i32 + (60.0 * s) as i32;
        let h = ((70.0 + n as f32 * 46.0 + if n > 0 { 12.0 } else { 0.0 }) * s) as i32 + (60.0 * s) as i32;
        if w != self.w || h != self.h || self.gfx.is_none() {
            self.w = w;
            self.h = h;
            self.gfx = Gfx::new(w, h);
        }
        let sw = scr.right - scr.left;
        self.pos = (scr.left + (sw - w) / 2, scr.top + ((scr.bottom - scr.top) as f32 * 0.2) as i32 - (30.0 * s) as i32);
        unsafe {
            win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        }
    }

    fn search(&mut self) {
        let a = app();
        let q = self.query.trim().to_lowercase();
        let mut out: Vec<Res> = vec![];
        if q.is_empty() {
            self.results = out;
            return;
        }
        if let Some(v) = calc(&q) {
            let t = if v.fract().abs() < 1e-9 { format!("{}", v as i64) } else { format!("{}", (v * 1e8).round() / 1e8) };
            out.push(Res { title: format!("= {}", t), sub: "Calculator".into(), kind: 2, target: t, hwnd: 0 });
        }
        let mut score: Vec<(i32, Res)> = vec![];
        for e in a.lp.index.snapshot() {
            let sc = if e.lower == q { 0 } else if e.lower.starts_with(&q) { 1 } else if e.lower.split(' ').any(|w| w.starts_with(&q)) { 2 } else if e.lower.contains(&q) { 3 } else { continue };
            score.push((sc, Res { title: e.name, sub: "Application".into(), kind: 0, target: e.target, hwnd: 0 }));
        }
        for w in a.wins.iter() {
            if w.title.to_lowercase().contains(&q) {
                score.push((2, Res { title: w.title.clone(), sub: format!("Open window - {}", crate::dock::nice_name(&w.exe)), kind: 1, target: w.exe.clone(), hwnd: w.hwnd }));
            }
        }
        for (name, tg) in [("System Settings", "ms-settings:"), ("Lock Screen", "@lock"), ("Sleep", "@sleep"), ("Restart", "@restart"), ("Shut Down", "@shutdown"), ("Mission Control", "@mc"), ("Launchpad", "@lp"), ("MacDock Settings", "@settings")] {
            if name.to_lowercase().contains(&q) {
                score.push((3, Res { title: name.into(), sub: "Command".into(), kind: 4, target: tg.into(), hwnd: 0 }));
            }
        }
        score.sort_by_key(|x| x.0);
        out.extend(score.into_iter().map(|x| x.1).take(7));
        if out.len() < 8 {
            out.push(Res { title: format!("Search the web for \"{}\"", self.query.trim()), sub: "Web".into(), kind: 3, target: self.query.trim().to_string(), hwnd: 0 });
        }
        out.truncate(8);
        self.results = out;
        self.sel = 0;
    }

    fn run(&mut self, i: usize) {
        let Some(r) = self.results.get(i).cloned() else { return };
        self.close();
        let a = app();
        match r.kind {
            0 => shell_open(&r.target),
            1 => crate::dock::activate_hwnd(r.hwnd),
            2 => unsafe {
                use windows::Win32::System::DataExchange::*;
                use windows::Win32::System::Memory::*;
                if OpenClipboard(None).is_ok() {
                    let _ = EmptyClipboard();
                    let w: Vec<u16> = r.target.encode_utf16().chain(std::iter::once(0)).collect();
                    if let Ok(hm) = GlobalAlloc(GMEM_MOVEABLE, w.len() * 2) {
                        let p = GlobalLock(hm) as *mut u16;
                        std::ptr::copy_nonoverlapping(w.as_ptr(), p, w.len());
                        let _ = GlobalUnlock(hm);
                        let _ = SetClipboardData(13, Some(windows::Win32::Foundation::HANDLE(hm.0)));
                    }
                    let _ = CloseClipboard();
                }
            },
            3 => {
                let q: String = r.target.chars().map(|c| if c.is_ascii_alphanumeric() { c.to_string() } else { format!("%{:02X}", c as u32 & 0xFF) }).collect();
                shell_open(&format!("https://www.google.com/search?q={}", q));
            }
            _ => match r.target.as_str() {
                "@lock" => unsafe {
                    let _ = windows::Win32::System::Shutdown::LockWorkStation();
                },
                "@sleep" => run_cmd("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
                "@restart" => run_cmd("shutdown.exe", "/r /t 5"),
                "@shutdown" => run_cmd("shutdown.exe", "/s /t 5"),
                "@mc" => a.mc.toggle(),
                "@lp" => a.lp.toggle(),
                "@settings" => a.set.show(0),
                t => shell_open(t),
            },
        }
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let moving = self.open.step(dt, 360.0, 32.0);
        if !self.visible && self.open.v < 0.03 {
            win::hide(self.hwnd);
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_SPOT));
            }
            return;
        }
        if !moving && !self.need {
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_SPOT));
            }
            return;
        }
        self.need = false;
        self.draw();
    }

    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let t = self.open.v.clamp(0.0, 1.0);
        let m = 30.0 * s;
        let pw_ = self.w as f32 - 2.0 * m;
        let ph = self.h as f32 - 2.0 * m;
        let al = |v: f32| ((v * t) as u8);
        g.shadow(m, m, pw_, ph, 18.0 * s, 26.0 * s, al(200.0));
        g.fill_round(m, m, pw_, ph, 18.0 * s, if dark { argb(al(238.0), 30, 30, 33) } else { argb(al(240.0), 246, 246, 248) });
        g.stroke_round(m + 0.5, m + 0.5, pw_ - 1.0, ph - 1.0, 18.0 * s, 1.0, argb(al(50.0), 255, 255, 255));
        let fg = if dark { argb(al(255.0), 245, 245, 245) } else { argb(al(255.0), 25, 25, 25) };
        let dim = if dark { argb(al(150.0), 255, 255, 255) } else { argb(al(150.0), 0, 0, 0) };
        // field
        g.stroke_circle(m + 30.0 * s, m + 32.0 * s, 8.0 * s, 2.2 * s, dim);
        g.line(m + 36.0 * s, m + 38.0 * s, m + 43.0 * s, m + 45.0 * s, 2.2 * s, dim);
        let (txt, col, sz) = if self.query.is_empty() { ("Spotlight Search".to_string(), dim, 22.0 * s) } else { (self.query.clone(), fg, 22.0 * s) };
        g.text(&txt, m + 58.0 * s, m + 8.0 * s, pw_ - 80.0 * s, 48.0 * s, sz, false, 0, true, false, col);
        if !self.query.is_empty() {
            let tw = g.measure(&self.query, sz, false);
            g.fill_rect(m + 58.0 * s + tw + 2.0, m + 18.0 * s, 1.8 * s, 28.0 * s, fg);
        }
        if !self.results.is_empty() {
            g.line(m + 16.0 * s, m + 64.0 * s, m + pw_ - 16.0 * s, m + 64.0 * s, 1.0, argb(al(40.0), 255, 255, 255));
        }
        for (i, r) in self.results.iter().enumerate() {
            let y = m + 70.0 * s + i as f32 * 46.0 * s;
            if i == self.sel {
                g.fill_round(m + 10.0 * s, y, pw_ - 20.0 * s, 42.0 * s, 10.0 * s, argb(al(255.0), 10, 132, 255));
            }
            let on = if i == self.sel { argb(al(255.0), 255, 255, 255) } else { fg };
            let isz = 30.0 * s;
            let ix = m + 22.0 * s;
            let iy = y + 6.0 * s;
            match r.kind {
                0 | 1 => {
                    let tgt = r.target.clone();
                    let key = if r.kind == 0 { format!("t:{}", tgt.to_lowercase()) } else { format!("exe:{}", tgt) };
                    let pack = pack_for(&a.icons, &a.cfg, &tgt, &r.title);
                    if let Some(img) = a.icons.get_or_logo(&key, &tgt, pack) {
                        g.image(&img, ix, iy, isz, isz, t);
                    }
                }
                2 => {
                    g.fill_round(ix, iy, isz, isz, 7.0 * s, argb(al(255.0), 255, 149, 0));
                    g.text("=", ix, iy, isz, isz, 20.0 * s, true, 1, true, false, argb(al(255.0), 255, 255, 255));
                }
                _ => {
                    g.fill_round(ix, iy, isz, isz, 7.0 * s, argb(al(255.0), 90, 90, 100));
                    g.text(if r.kind == 3 { "G" } else { ">" }, ix, iy, isz, isz, 16.0 * s, true, 1, true, false, argb(al(255.0), 255, 255, 255));
                }
            }
            g.text(&r.title, ix + isz + 14.0 * s, y + 3.0 * s, pw_ - 130.0 * s, 22.0 * s, 15.0 * s, false, 0, true, false, on);
            g.text(&r.sub, ix + isz + 14.0 * s, y + 22.0 * s, pw_ - 130.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, if i == self.sel { argb(al(200.0), 255, 255, 255) } else { dim });
        }
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let sp = &mut app().spot;
    match m {
        WM_TIMER if wp.0 == T_SPOT => {
            sp.tick();
            LRESULT(0)
        }
        WM_KEYDOWN => {
            let vk = wp.0 as u16;
            if vk == VK_ESCAPE.0 {
                sp.close();
            } else if vk == VK_RETURN.0 {
                let i = sp.sel;
                sp.run(i);
            } else if vk == VK_DOWN.0 && sp.sel + 1 < sp.results.len() {
                sp.sel += 1;
            } else if vk == VK_UP.0 && sp.sel > 0 {
                sp.sel -= 1;
            } else if vk == VK_BACK.0 {
                sp.query.pop();
                sp.search();
                sp.layout();
            }
            sp.need = true;
            sp.kick();
            LRESULT(0)
        }
        WM_CHAR => {
            let c = char::from_u32(wp.0 as u32).unwrap_or(' ');
            if !c.is_control() {
                sp.query.push(c);
                sp.search();
                sp.layout();
                sp.need = true;
                sp.kick();
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (_, y) = win::lparam_xy(lp);
            let s = app().scale;
            let i = ((y as f32 - 30.0 * s - 70.0 * s) / (46.0 * s)).floor() as i32;
            if i >= 0 && (i as usize) < sp.results.len() {
                sp.run(i as usize);
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if sp.visible && sp.open.v > 0.9 {
                let fgw = GetForegroundWindow();
                if fgw != h && !fgw.is_invalid() {
                    sp.close();
                }
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
