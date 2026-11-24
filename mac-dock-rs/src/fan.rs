//! Folder "Fan": a faithful port of the C# DownloadsFan. Newest 10 items rise out of the dock icon along a curve
//! (x = origin + i^1.6 * 2.5, step 66, icon 60), easeOutCubic over 240 ms with a 6 ms/item stagger, icons grow
//! from 0.35 to 1.0, labels tilt up the arc (1 + 1.9 deg per item, max 18) and appear after 55%; close folds back
//! in 110 ms (smoothstep). Hover pops an icon by 0.34 x icon size per side. Frames are driven by the vsync clock.
use crate::gfx::*;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use std::rc::Rc;
use std::time::Instant;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

const MAX_ITEMS: usize = 10;
const ICON: f32 = 60.0;
const STEP: f32 = 66.0;
const LABEL_H: f32 = 22.0;
const WIN_W: f32 = 900.0;

struct FItem {
    name: String,
    path: String,
    label: String,
    rect: (f32, f32, f32, f32), // final icon rect (window coords)
    angle: f32,
    lw: f32,
    lbl: Option<Rc<Img>>,
    lbl_hot: Option<Rc<Img>>,
    hot: f32,
}

pub struct Fan {
    hwnd: HWND,
    gfx: Option<Gfx>,
    pub visible: bool,
    pub folder: String,
    items: Vec<FItem>,
    s: f32,
    size: (i32, i32),
    pos: (i32, i32),
    origin: (f32, f32),
    anchor: (f32, f32), // screen: dock icon center x, top y
    born: Instant,
    close_at: Option<Instant>,
    hover: i32,
    more_hot: bool,
    more_grow: f32,
    more_rect: Option<(f32, f32, f32, f32)>,
    last: Instant,
    left_since: Option<Instant>,
    was_down: bool,
    timer: bool,
    need: bool,
}

fn ease_out_cubic(t: f32) -> f32 {
    let u = 1.0 - t.clamp(0.0, 1.0);
    1.0 - u * u * u
}
fn smooth(t: f32) -> f32 {
    let c = t.clamp(0.0, 1.0);
    c * c * (3.0 - 2.0 * c)
}

impl Fan {
    pub fn new() -> Fan {
        Fan { hwnd: HWND::default(), gfx: None, visible: false, folder: String::new(), items: vec![], s: 1.0, size: (0, 0), pos: (0, 0), origin: (0.0, 0.0), anchor: (0.0, 0.0), born: Instant::now(), close_at: None, hover: -1, more_hot: false, more_grow: 0.0, more_rect: None, last: Instant::now(), left_since: None, was_down: false, timer: false, need: true }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = Instant::now();
            crate::clock::start(crate::clock::FAN);
        }
    }
    pub fn dirty(&mut self) {
        if self.visible {
            self.need = true;
            self.kick();
        }
    }
    pub fn close(&mut self) {
        if self.visible && self.close_at.is_none() {
            self.close_at = Some(Instant::now());
            self.kick();
        }
    }
    fn progress(&self, i: usize) -> f32 {
        if let Some(c) = self.close_at {
            let cu = (c.elapsed().as_secs_f32() * 1000.0 / 110.0).clamp(0.0, 1.0);
            return 1.0 - smooth(cu);
        }
        let ms = self.born.elapsed().as_secs_f32() * 1000.0;
        ease_out_cubic((ms - i as f32 * 6.0) / 240.0)
    }
    fn animating(&self) -> bool {
        self.close_at.is_some() || self.born.elapsed().as_secs_f32() * 1000.0 < self.items.len() as f32 * 6.0 + 260.0
    }

    /// `cx` = dock icon center x (screen), `top` = dock icon top y (screen).
    pub fn open(&mut self, folder: &str, cx: f32, top: f32) {
        if self.visible && self.close_at.is_none() && self.folder == folder {
            self.close();
            return;
        }
        let a = app();
        // geometry is in device pixels exactly like the C# fan (60 px icons, 66 px step); only the text follows DPI
        let s = 1.0f32;
        self.s = s;
        self.folder = folder.to_string();
        let dir = crate::folderpop::resolve(folder);
        let mut v: Vec<(std::time::SystemTime, String, String)> = vec![];
        if let Ok(rd) = std::fs::read_dir(&dir) {
            for e in rd.flatten().take(5000) {
                let name = e.file_name().to_string_lossy().to_string();
                if name.starts_with('.') || name.eq_ignore_ascii_case("desktop.ini") || name.starts_with('~') {
                    continue;
                }
                let t = e.metadata().ok().and_then(|m| m.modified().ok()).unwrap_or(std::time::UNIX_EPOCH);
                v.push((t, name, e.path().to_string_lossy().to_string()));
            }
        }
        v.sort_by(|a, b| b.0.cmp(&a.0));
        v.truncate(MAX_ITEMS);
        // window: sized for a full stack so it can appear immediately
        let height = (MAX_ITEMS as f32 * STEP + ICON + 40.0) * s;
        let scr = screen_rect();
        let bar = a.bar.height() as f32;
        let wf = WIN_W * s;
        let left = (cx - 580.0 * s).min(scr.right as f32 - wf).max(scr.left as f32);
        let wtop = (top - height).max(scr.top as f32 + bar);
        let h = (top - wtop).max(1.0);
        self.pos = (left as i32, wtop as i32);
        self.size = (wf as i32, h as i32);
        self.origin = (cx - left, h);
        self.anchor = (cx, top);
        self.gfx = Gfx::new(self.size.0, self.size.1);
        self.items.clear();
        let mut m = match Gfx::new(64, 64) {
            Some(m) => m,
            None => return,
        };
        let lh = LABEL_H * s;
        for (i, (_, name, path)) in v.into_iter().enumerate() {
            let curve = 2.5 * s;
            let ccx = (self.origin.0 + (i as f32).powf(1.6) * curve).clamp(ICON * s / 2.0 + 4.0, wf - ICON * s / 2.0 - 4.0);
            let ccy = h - ICON * s / 2.0 - 6.0 * s - i as f32 * STEP * s;
            let rect = (ccx - ICON * s / 2.0, ccy - ICON * s / 2.0, ICON * s, ICON * s);
            let angle = (1.0 + 1.9 * i as f32).min(18.0);
            let max_w = (rect.0 - 14.0 - 10.0) / angle.to_radians().cos() - 16.0 - 6.0;
            let fs = 12.67 * app().scale;
            let mut label = name.clone();
            if m.measure(&label, fs, false) > max_w {
                let chars: Vec<char> = name.chars().collect();
                let mut n = chars.len();
                while n > 1 && m.measure(&format!("{}...", chars[..n].iter().collect::<String>()), fs, false) > max_w {
                    n -= 1;
                }
                label = format!("{}...", chars[..n].iter().collect::<String>().trim_end());
            }
            let lw = m.measure(&label, fs, false) + 28.0 * s;
            let mk = |hot: bool, text: &str| -> Option<Rc<Img>> {
                let mut g = Gfx::new(lw.ceil() as i32, lh.ceil() as i32)?;
                g.clear(0);
                let (bg, r) = if hot { (argb(215, 52, 52, 58), 0) } else { (argb(190, 30, 30, 34), 0) };
                let _ = r;
                g.fill_round(0.5, 0.5, lw - 1.0, lh - 1.0, lh / 2.0, bg);
                g.stroke_round(0.5, 0.5, lw - 1.0, lh - 1.0, lh / 2.0, 1.0, argb(55, 255, 255, 255));
                g.text(text, 0.0, 0.0, lw, lh, fs, false, 1, true, false, argb(255, 250, 250, 252));
                Img::from_pixels(g.to_pixels()).map(Rc::new)
            };
            let (lbl, lbl_hot) = (mk(false, &label), mk(true, &label));
            self.items.push(FItem { name, path, label, rect, angle, lw, lbl, lbl_hot, hot: 0.0 });
        }
        self.more_rect = None;
        if !self.items.is_empty() {
            let i = self.items.len();
            let curve = 2.5 * s;
            let ccx = (self.origin.0 + (i as f32).powf(1.6) * curve).clamp(ICON * s / 2.0 + 4.0, wf - ICON * s / 2.0 - 4.0);
            let ccy = h - ICON * s / 2.0 - 6.0 * s - i as f32 * STEP * s;
            if ccy >= 26.0 * s {
                self.more_rect = Some((ccx - 22.0 * s, ccy - 22.0 * s, 44.0 * s, 44.0 * s));
            }
        }
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_FAN, true, true, true, false, 0, 0, 10, 10);
        }
        a.lp.close();
        a.fp.close();
        self.visible = true;
        self.close_at = None;
        self.hover = -1;
        self.more_hot = false;
        self.more_grow = 0.0;
        self.left_since = None;
        self.was_down = unsafe { GetAsyncKeyState(0x01) as u16 & 0x8000 != 0 };
        win::set_pos(self.hwnd, self.pos.0, self.pos.1, self.size.0, self.size.1, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        self.born = Instant::now();
        self.draw();
        win::show_noactivate(self.hwnd);
        self.born = Instant::now(); // the fan-out starts from the first frame actually on screen
        self.need = true;
        self.kick();
    }

    fn item_hit(&self, i: usize) -> (f32, f32, f32, f32) {
        let it = &self.items[i];
        let r = it.rect;
        let (lx, lw) = (r.0 - it.lw - 12.0, it.lw + 12.0);
        let left = lx.min(r.0);
        (left, r.1.min(r.1 + (r.3 - LABEL_H * self.s) / 2.0), r.0 + r.2 - left, r.3)
    }

    pub fn tick(&mut self) {
        if !self.visible {
            crate::clock::stop(crate::clock::FAN);
            self.timer = false;
            return;
        }
        let now = Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        if let Some(c) = self.close_at {
            if c.elapsed().as_millis() >= 120 {
                self.visible = false;
                self.close_at = None;
                crate::clock::stop(crate::clock::FAN);
                self.timer = false;
                win::hide(self.hwnd);
                return;
            }
            self.draw();
            return;
        }
        // --- polling like the C# fan: cursor, buttons ---
        let p = cursor_pos();
        let (lx, ly) = (p.x as f32 - self.pos.0 as f32, p.y as f32 - self.pos.1 as f32);
        let animating = self.animating();
        let mut idx = -1i32;
        if !animating {
            for i in 0..self.items.len() {
                let h = self.item_hit(i);
                if lx >= h.0 && lx <= h.0 + h.2 && ly >= h.1 && ly <= h.1 + h.3 {
                    idx = i as i32;
                    break;
                }
            }
        }
        if idx != self.hover {
            self.hover = idx;
            self.need = true;
        }
        let mh = !animating && self.more_rect.map(|r| lx >= r.0 && lx <= r.0 + r.2 && ly >= r.1 && ly <= r.1 + r.3).unwrap_or(false);
        if mh != self.more_hot {
            self.more_hot = mh;
            self.need = true;
        }
        // hover pop: 0.3 per 60 Hz frame, expressed as a rate so it feels the same at any refresh
        let k = 1.0 - (-dt * 21.4).exp();
        for (i, it) in self.items.iter_mut().enumerate() {
            let tgt = if i as i32 == self.hover { 1.0 } else { 0.0 };
            let d = tgt - it.hot;
            if d.abs() > 0.01 {
                it.hot += d * k;
                self.need = true;
            } else {
                it.hot = tgt;
            }
        }
        let mt = if self.more_hot { 1.0 } else { 0.0 };
        let md = mt - self.more_grow;
        if md.abs() > 0.01 {
            self.more_grow += md * k;
            self.need = true;
        } else {
            self.more_grow = mt;
        }
        // stay open while the cursor is near the dock icon, over an item, or animating
        let near_icon = (p.x as f32 - self.anchor.0).abs() < 44.0 * self.s && p.y as f32 >= self.anchor.1 - 8.0 * self.s;
        let inflated = (0..self.items.len()).any(|i| {
            let h = self.item_hit(i);
            lx >= h.0 - 6.0 && lx <= h.0 + h.2 + 6.0 && ly >= h.1 - 6.0 && ly <= h.1 + h.3 + 6.0
        });
        let near = near_icon || idx >= 0 || self.more_hot || animating || inflated;
        if !near {
            let since = *self.left_since.get_or_insert(now);
            if since.elapsed().as_millis() > 250 {
                self.close();
                return;
            }
        } else {
            self.left_since = None;
        }
        let down = unsafe { GetAsyncKeyState(0x01) as u16 & 0x8000 != 0 };
        if down && !self.was_down {
            if idx >= 0 {
                let path = self.items[idx as usize].path.clone();
                self.was_down = true;
                self.close();
                shell_open(&path);
                return;
            } else if self.more_hot {
                let f = crate::folderpop::resolve(&self.folder);
                self.was_down = true;
                self.close();
                shell_open(&f);
                return;
            } else if !near_icon {
                self.was_down = true;
                self.close();
                return;
            }
        }
        self.was_down = down;
        let rdown = unsafe { GetAsyncKeyState(0x02) as u16 & 0x8000 != 0 };
        if rdown && idx >= 0 {
            let path = self.items[idx as usize].path.clone();
            let msg = app().msg;
            let r = win::popup_menu(msg, p.x, p.y, &[(1, "Open".into(), true, false), (2, "Show in Explorer".into(), true, false), (3, "Move to Trash".into(), true, false)]);
            match r {
                1 => shell_open(&path),
                2 => run_cmd("explorer.exe", &format!("/select,\"{}\"", path)),
                3 => crate::dock::recycle(&path),
                _ => {}
            }
            self.close();
            return;
        }
        if animating || self.need {
            self.need = false;
            self.draw();
        }
    }

    fn draw(&mut self) {
        let a = app();
        let s = self.s;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let animating = self.animating();
        g.set_fast(animating);
        let n = self.items.len();
        let (ox, oy) = (self.origin.0 - ICON * s / 2.0, self.origin.1 - ICON * s / 2.0);
        for k in (0..n).rev() {
            // far ones first, hovered one last (on top)
            let mut i = k;
            if self.hover >= 0 && (self.hover as usize) < n {
                if k == 0 {
                    i = self.hover as usize;
                } else if k <= self.hover as usize {
                    i = k - 1;
                }
            }
            let pr = self.progress(i).clamp(0.0, 1.0);
            let r = self.items[i].rect;
            let sc = 0.35 + 0.65 * pr;
            let mut sz = ICON * s * sc;
            let cx = ox + (r.0 - ox) * pr + ICON * s / 2.0;
            let cy = oy + (r.1 - oy) * pr + ICON * s / 2.0;
            let gi = ICON * s * 0.34 * self.items[i].hot;
            sz += 2.0 * gi;
            let (rl, rt) = (cx - sz / 2.0, cy - sz / 2.0);
            let alpha = (pr * 1.6).min(1.0);
            let angle = self.items[i].angle;
            let key = format!("t:{}", self.items[i].path.to_lowercase());
            let path = self.items[i].path.clone();
            let pack = None;
            if let Some(img) = a.icons.get_or_logo(&key, &path, pack) {
                g.rot_about(cx, cy, angle * 0.6);
                g.image(&img, rl, rt, sz, sz, alpha);
                g.reset_tf();
            }
            if pr > 0.55 {
                let la = ((pr - 0.55) / 0.45).min(1.0);
                let lw = self.items[i].lw;
                let lift = (ICON * s * if i <= 5 { 0.0073 * (i * i) as f32 } else { 0.18 + 0.045 * (i as f32 - 5.0) }).round();
                let lr = (rl - lw - 14.0, rt + (sz - LABEL_H * s) / 2.0 - lift, lw, LABEL_H * s);
                let (px, py) = (lr.0 + lr.2, lr.1 + lr.3 / 2.0);
                let img = if i as i32 == self.hover { self.items[i].lbl_hot.clone() } else { self.items[i].lbl.clone() };
                if let Some(img) = img {
                    g.rot_about(px, py, angle);
                    g.image(&img, lr.0, lr.1, lr.2, lr.3, la);
                    g.reset_tf();
                }
            }
        }
        if let Some(mr) = self.more_rect {
            let mp = self.progress(n).clamp(0.0, 1.0);
            if mp > 0.01 {
                let ms = (0.35 + 0.65 * mp) * (1.0 + 0.34 * self.more_grow);
                let mcx = self.origin.0 + (mr.0 + mr.2 / 2.0 - self.origin.0) * mp;
                let mcy = self.origin.1 + (mr.1 + mr.3 / 2.0 - self.origin.1) * mp;
                let mal = (mp * 1.6).min(1.0);
                g.scale_at(mcx, mcy, ms);
                let (hw_, hh) = (mr.2 / 2.0, mr.3 / 2.0);
                let fill = argb(((if self.more_hot { 225.0 } else { 190.0 }) * mal) as u8, 70, 70, 78);
                g.fill_circle(0.0, 0.0, hw_, fill);
                g.stroke_circle(0.0, 0.0, hw_ - 0.5, 1.0, argb((70.0 * mal) as u8, 255, 255, 255));
                let u = mr.2 / 44.0;
                let col = argb((235.0 * mal) as u8, 255, 255, 255);
                g.bezier([(-9.0 * u, 9.0 * u), (-9.5 * u, -1.0 * u), (-6.0 * u, -5.0 * u), (3.0 * u, -5.0 * u)], (3.4 * u).max(2.4), col);
                g.fill_poly(&[(1.5 * u, -12.0 * u), (11.5 * u, -5.0 * u), (1.5 * u, 2.0 * u)], col);
                g.reset_tf();
                let _ = hh;
                if self.more_hot {
                    let fs = 12.67 * app().scale;
                    let mw = g.measure("Open in Explorer", fs, false) + 28.0 * s;
                    let ma = (1.0 + 1.9 * n as f32).min(18.0);
                    let rise = mw * ma.to_radians().sin();
                    let lr = ((mr.0 - 14.0 - mw).max(4.0), (rise + 2.0).max(mr.1 + (mr.3 - LABEL_H * s) / 2.0 - 10.0), mw, LABEL_H * s);
                    g.rot_about(lr.0 + lr.2, lr.1 + lr.3 / 2.0, ma);
                    g.fill_round(lr.0, lr.1, lr.2, lr.3, lr.3 / 2.0, argb(235, 60, 60, 66));
                    g.text("Open in Explorer", lr.0, lr.1, lr.2, lr.3, fs, false, 1, true, false, argb(250, 250, 250, 252));
                    g.reset_tf();
                }
            }
        }
        if n == 0 {
            g.fill_round(self.origin.0 - 80.0 * s, self.size.1 as f32 - 70.0 * s, 160.0 * s, 28.0 * s, 8.0 * s, argb(220, 38, 38, 42));
            g.text("Folder is empty", self.origin.0 - 80.0 * s, self.size.1 as f32 - 70.0 * s, 160.0 * s, 28.0 * s, 12.0 * s, false, 1, true, false, argb(255, 235, 235, 240));
        }
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match m {
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
