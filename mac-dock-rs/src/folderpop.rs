//! Dock folder popup: Fan, Grid or List of the folder's newest items (style kept in fanstyle.txt, like the C# dock).
use crate::gfx::*;
use crate::icons::pack_for;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Style {
    Fan,
    Grid,
    List,
}

struct Entry {
    name: String,
    path: String,
    dir: bool,
}

pub struct FolderPop {
    hwnd: HWND,
    gfx: Option<Gfx>,
    pub visible: bool,
    folder: String,
    entries: Vec<Entry>,
    style: Style,
    pos: (i32, i32),
    size: (i32, i32),
    open: Spring,
    hover: i32,
    hits: Vec<(f32, f32, f32, f32, i32)>, // x,y,w,h,idx (-1 = "Open in Explorer")
    timer: bool,
    last: std::time::Instant,
    need: bool,
    anchor_x: f32,
}

pub fn style_of(cfg: &crate::config::Config) -> Style {
    match std::fs::read_to_string(cfg.path("fanstyle.txt")).unwrap_or_default().trim().to_lowercase().as_str() {
        "grid" => Style::Grid,
        "list" => Style::List,
        _ => Style::Fan,
    }
}
pub fn set_style(cfg: &crate::config::Config, s: Style) {
    let _ = std::fs::write(cfg.path("fanstyle.txt"), match s {
        Style::Fan => "fan",
        Style::Grid => "grid",
        Style::List => "list",
    });
}

impl FolderPop {
    pub fn new() -> FolderPop {
        FolderPop { hwnd: HWND::default(), gfx: None, visible: false, folder: String::new(), entries: vec![], style: Style::Fan, pos: (0, 0), size: (0, 0), open: Spring::new(0.0), hover: -1, hits: vec![], timer: false, last: std::time::Instant::now(), need: true, anchor_x: 0.0 }
    }
    fn kick(&mut self) {
        if !self.timer && !self.hwnd.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_FP));
            }
        }
    }
    pub fn dirty(&mut self) {
        if self.visible {
            self.need = true;
            self.kick();
        }
    }
    pub fn close(&mut self) {
        if self.visible {
            self.visible = false;
            self.open.goal = 0.0;
            self.kick();
        }
    }
    /// Open the popup for `folder`, above the dock item whose center is `cx` and top is `top` (screen px).
    pub fn open(&mut self, folder: &str, cx: f32, top: f32) {
        if self.visible && self.folder == folder {
            self.close();
            return;
        }
        let a = app();
        let s = a.scale;
        self.style = style_of(&a.cfg);
        if self.style == Style::Fan {
            self.visible = false;
            a.fan.open(folder, cx, top);
            return;
        }
        a.fan.close();
        self.folder = folder.to_string();
        self.entries.clear();
        let dir = if folder.to_lowercase().starts_with("shell:") { resolve_shell_folder(folder) } else { folder.to_string() };
        let mut v: Vec<(std::time::SystemTime, Entry)> = vec![];
        if let Ok(rd) = std::fs::read_dir(&dir) {
            for e in rd.flatten().take(4000) {
                let name = e.file_name().to_string_lossy().to_string();
                if name.starts_with('.') || name.eq_ignore_ascii_case("desktop.ini") || name.starts_with('~') {
                    continue;
                }
                let md = e.metadata().ok();
                let t = md.as_ref().and_then(|m| m.modified().ok()).unwrap_or(std::time::UNIX_EPOCH);
                v.push((t, Entry { name, path: e.path().to_string_lossy().to_string(), dir: md.map(|m| m.is_dir()).unwrap_or(false) }));
            }
        }
        v.sort_by(|a, b| b.0.cmp(&a.0));
        let limit = match self.style {
            Style::Fan => 9,
            Style::Grid => 20,
            Style::List => 14,
        };
        self.entries = v.into_iter().take(limit).map(|x| x.1).collect();
        // window geometry
        let n = self.entries.len().max(1) as f32;
        let m = 30.0 * s;
        let (w, h) = match self.style {
            Style::Grid => (5.0 * 112.0 * s + 28.0 * s, ((n / 5.0).ceil() * 112.0 + 28.0 + 40.0) * s),
            Style::List => (300.0 * s, (n * 38.0 + 24.0 + 40.0) * s),
            Style::Fan => (330.0 * s, (n * 46.0 + 70.0) * s),
        };
        let (ww, hh) = ((w + 2.0 * m) as i32, (h + 2.0 * m) as i32);
        if self.gfx.is_none() || self.size != (ww, hh) {
            self.gfx = Gfx::new(ww, hh);
            self.size = (ww, hh);
        }
        let scr = screen_rect();
        let x = ((cx - ww as f32 / 2.0) as i32).clamp(scr.left, scr.right - ww);
        self.anchor_x = cx - x as f32;
        self.pos = (x, (top - hh as f32 + m - 4.0 * s) as i32);
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_FOLDER, true, true, false, false, 0, 0, 10, 10);
        }
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        self.hover = -1;
        let (px, py) = self.pos;
        win::set_pos(self.hwnd, px, py, ww, hh, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        unsafe {
            let _ = ShowWindow(self.hwnd, SW_SHOW);
        }
        crate::sys::activate(hv(self.hwnd));
        self.need = true;
        self.kick();
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let moving = self.open.step(dt, 330.0, 26.0);
        if !self.visible && self.open.v < 0.03 {
            win::hide(self.hwnd);
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_FP));
            }
            return;
        }
        if !moving && !self.need {
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_FP));
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
        let t = self.open.v.clamp(0.0, 1.2);
        let ta = t.clamp(0.0, 1.0);
        let m = 30.0 * s;
        let (w, h) = (self.size.0 as f32 - 2.0 * m, self.size.1 as f32 - 2.0 * m);
        let fg = if dark { argb(255, 245, 245, 247) } else { argb(255, 28, 28, 32) };
        let dim = if dark { argb(255, 160, 160, 170) } else { argb(255, 110, 110, 120) };
        self.hits.clear();
        let n = self.entries.len();
        let origin_y = m + h; // bottom of the panel (toward the dock)
        match self.style {
            Style::Fan => {
                // items rise from the dock icon along a curve to the upper left
                for i in 0..n {
                    let p = ((t * 1.25) - i as f32 * 0.07).clamp(0.0, 1.0);
                    let e = &self.entries[i];
                    let rise = (i as f32 * 46.0 + 22.0) * s;
                    let y = origin_y - 44.0 * s - lerp(0.0, rise, p) - 8.0 * s;
                    let curve = ((i as f32) * (i as f32) * 2.6) * s;
                    let x = m + 10.0 * s + curve * p;
                    let (rw, rh) = (h.min(300.0 * s) * 0.0 + 300.0 * s - curve, 40.0 * s);
                    let hov = self.hover == i as i32;
                    g.fill_round(x, y, rw, rh, 12.0 * s, with_alpha(if hov { argb(255, 10, 132, 255) } else if dark { argb(240, 40, 40, 46) } else { argb(240, 250, 250, 252) }, p * ta));
                    self.draw_icon(&mut g, a, e, x + 6.0 * s, y + 4.0 * s, 32.0 * s, p * ta);
                    g.text(&e.name, x + 46.0 * s, y, rw - 54.0 * s, rh, 13.0 * s, false, 0, true, false, with_alpha(if hov { argb(255, 255, 255, 255) } else { fg }, p * ta));
                    self.hits.push((x, y, rw, rh, i as i32));
                }
                let by = origin_y - 40.0 * s;
                g.text("Open in Explorer", m, by, w, 34.0 * s, 12.5 * s, false, 1, true, false, with_alpha(dim, ta));
                self.hits.push((m, by, w, 34.0 * s, -1));
            }
            Style::Grid => {
                g.shadow(m, m, w, h, 18.0 * s, 24.0 * s, (190.0 * ta) as u8);
                g.fill_round(m, m, w, h, 18.0 * s, if dark { argb((238.0 * ta) as u8, 30, 30, 34) } else { argb((240.0 * ta) as u8, 246, 246, 248) });
                g.stroke_round(m + 0.5, m + 0.5, w - 1.0, h - 1.0, 18.0 * s, 1.0, argb((46.0 * ta) as u8, 255, 255, 255));
                let cell = 112.0 * s;
                for (i, e) in self.entries.iter().enumerate() {
                    let (c, r) = (i % 5, i / 5);
                    let x = m + 14.0 * s + c as f32 * cell;
                    let y = m + 14.0 * s + r as f32 * cell;
                    if self.hover == i as i32 {
                        g.fill_round(x + 4.0 * s, y + 2.0 * s, cell - 8.0 * s, cell - 4.0 * s, 10.0 * s, with_alpha(argb(255, 10, 132, 255), 0.85 * ta));
                    }
                    self.draw_icon(&mut g, a, e, x + (cell - 56.0 * s) / 2.0, y + 8.0 * s, 56.0 * s, ta);
                    g.text(&e.name, x + 4.0 * s, y + 68.0 * s, cell - 8.0 * s, 36.0 * s, 11.5 * s, false, 1, false, true, with_alpha(fg, ta));
                    self.hits.push((x, y, cell, cell, i as i32));
                }
                g.text("Open in Explorer", m, m + h - 36.0 * s, w, 30.0 * s, 12.0 * s, false, 1, true, false, with_alpha(dim, ta));
                self.hits.push((m, m + h - 36.0 * s, w, 30.0 * s, -1));
            }
            Style::List => {
                g.shadow(m, m, w, h, 14.0 * s, 24.0 * s, (190.0 * ta) as u8);
                g.fill_round(m, m, w, h, 14.0 * s, if dark { argb((238.0 * ta) as u8, 30, 30, 34) } else { argb((240.0 * ta) as u8, 246, 246, 248) });
                g.stroke_round(m + 0.5, m + 0.5, w - 1.0, h - 1.0, 14.0 * s, 1.0, argb((46.0 * ta) as u8, 255, 255, 255));
                for (i, e) in self.entries.iter().enumerate() {
                    let y = m + 10.0 * s + i as f32 * 38.0 * s;
                    if self.hover == i as i32 {
                        g.fill_round(m + 8.0 * s, y, w - 16.0 * s, 36.0 * s, 8.0 * s, with_alpha(argb(255, 10, 132, 255), 0.9 * ta));
                    }
                    self.draw_icon(&mut g, a, e, m + 16.0 * s, y + 4.0 * s, 28.0 * s, ta);
                    g.text(&e.name, m + 54.0 * s, y, w - 70.0 * s, 36.0 * s, 13.0 * s, false, 0, true, false, with_alpha(if self.hover == i as i32 { argb(255, 255, 255, 255) } else { fg }, ta));
                    self.hits.push((m + 8.0 * s, y, w - 16.0 * s, 36.0 * s, i as i32));
                }
                g.text("Open in Explorer", m, m + h - 34.0 * s, w, 30.0 * s, 12.0 * s, false, 1, true, false, with_alpha(dim, ta));
                self.hits.push((m, m + h - 34.0 * s, w, 30.0 * s, -1));
            }
        }
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }

    fn draw_icon(&self, g: &mut Gfx, a: &mut App, e: &Entry, x: f32, y: f32, sz: f32, al: f32) {
        let key = format!("t:{}", e.path.to_lowercase());
        let pack = if e.dir { None } else { pack_for(&a.icons, &a.cfg, &e.path, &e.name) };
        if let Some(img) = a.icons.get_or_logo(&key, &e.path, pack) {
            g.image(&img, x, y, sz, sz, al);
        }
    }

    fn at(&self, x: f32, y: f32) -> i32 {
        self.hits.iter().find(|h| x >= h.0 && x <= h.0 + h.2 && y >= h.1 && y <= h.1 + h.3).map(|h| h.4).unwrap_or(-2)
    }
}

pub fn resolve(f: &str) -> String {
    resolve_shell_folder(f)
}
fn resolve_shell_folder(f: &str) -> String {
    let up = std::env::var("USERPROFILE").unwrap_or_default();
    match f.to_lowercase().as_str() {
        "shell:downloads" => format!("{}\\Downloads", up),
        "shell:desktop" => format!("{}\\Desktop", up),
        "shell:personal" | "shell:documents" => format!("{}\\Documents", up),
        "shell:my pictures" | "shell:pictures" => format!("{}\\Pictures", up),
        "shell:my music" => format!("{}\\Music", up),
        "shell:my video" => format!("{}\\Videos", up),
        _ => f.to_string(),
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let f = &mut app().fp;
    match m {
        WM_TIMER if wp.0 == T_FP => {
            f.tick();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            let i = f.at(x as f32, y as f32);
            if i != f.hover {
                f.hover = i;
                f.need = true;
                f.kick();
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let i = f.at(x as f32, y as f32);
            if i == -1 {
                let p = f.folder.clone();
                f.close();
                shell_open(&p);
            } else if i >= 0 {
                if let Some(e) = f.entries.get(i as usize) {
                    let p = e.path.clone();
                    f.close();
                    shell_open(&p);
                }
            } else {
                f.close();
            }
            LRESULT(0)
        }
        WM_RBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let mut p = POINT { x, y };
            let _ = windows::Win32::Graphics::Gdi::ClientToScreen(h, &mut p);
            let a = app();
            let cur = a.fp.style;
            let r = win::popup_menu(h, p.x, p.y, &[(1, "Fan".into(), true, cur == Style::Fan), (2, "Grid".into(), true, cur == Style::Grid), (3, "List".into(), true, cur == Style::List)]);
            if r != 0 {
                let st = match r {
                    1 => Style::Fan,
                    2 => Style::Grid,
                    _ => Style::List,
                };
                set_style(&a.cfg, st);
                let (folder, cx) = (a.fp.folder.clone(), a.fp.anchor_x + a.fp.pos.0 as f32);
                let top = (a.fp.pos.1 + a.fp.size.1) as f32 - 26.0 * a.scale;
                a.fp.visible = false;
                a.fp.open(&folder, cx, top);
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == VK_ESCAPE.0 {
                f.close();
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if f.visible && f.open.v > 0.8 {
                let fgw = GetForegroundWindow();
                if fgw != h {
                    f.close();
                }
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
