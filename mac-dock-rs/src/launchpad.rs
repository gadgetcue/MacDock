//! Launchpad: every installed app (shell:AppsFolder, enumerated on a worker thread), paged grid, type-to-search,
//! blur backdrop. Also the shared app index used by Spotlight, and the folder grid popup for dock folders.
use crate::gfx::*;
use crate::icons::pack_for;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use std::sync::{Arc, Mutex};
use windows::core::PWSTR;
use windows::Win32::Graphics::Gdi::ClientToScreen;
use windows::Win32::System::Com::*;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::Shell::*;

#[derive(Clone)]
pub struct AppEntry {
    pub name: String,
    pub target: String, // shell:AppsFolder\...
    pub lower: String,
    icon_key: String,
}

pub struct AppIndex {
    pub apps: Arc<Mutex<Vec<AppEntry>>>,
    started: bool,
}
impl AppIndex {
    pub fn new() -> AppIndex {
        AppIndex { apps: Arc::new(Mutex::new(vec![])), started: false }
    }
    pub fn ensure(&mut self, notify: isize) {
        if self.started {
            return;
        }
        self.started = true;
        let out = self.apps.clone();
        std::thread::spawn(move || unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
            let list = enum_apps();
            log(&format!("app index: {} apps", list.len()));
            *out.lock().unwrap() = list;
            let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_ICONS_READY, WPARAM(2), LPARAM(0));
        });
    }
    pub fn snapshot(&self) -> Vec<AppEntry> {
        self.apps.lock().map(|g| g.clone()).unwrap_or_default()
    }
}

unsafe fn enum_apps() -> Vec<AppEntry> {
    let mut out: Vec<AppEntry> = vec![];
    let wp = wide("shell:AppsFolder");
    let Ok(folder) = SHCreateItemFromParsingName::<_, _, IShellItem>(pw(&wp), None) else { return out };
    let Ok(en) = folder.BindToHandler::<_, IEnumShellItems>(None, &BHID_EnumItems) else { return out };
    loop {
        let mut item: [Option<IShellItem>; 1] = [None];
        let mut fetched = 0u32;
        if en.Next(&mut item, Some(&mut fetched)).is_err() || fetched == 0 {
            break;
        }
        let Some(it) = item[0].take() else { break };
        let get = |s: SIGDN| -> String {
            match it.GetDisplayName(s) {
                Ok(p) => {
                    let v = p.to_string().unwrap_or_default();
                    CoTaskMemFree(Some(p.0 as *const _));
                    v
                }
                Err(_) => String::new(),
            }
        };
        let name = get(SIGDN_NORMALDISPLAY);
        let mut parse = get(SIGDN_DESKTOPABSOLUTEPARSING);
        if name.is_empty() || parse.is_empty() {
            continue;
        }
        if !parse.to_lowercase().starts_with("shell:appsfolder") {
            parse = format!("shell:AppsFolder\\{}", parse);
        }
        let l = name.to_lowercase();
        if l.contains("uninstall") || l.ends_with(" help") || l.contains("readme") || l.contains("release notes") {
            continue;
        }
        let icon_key = format!("t:{}", parse.to_lowercase());
        out.push(AppEntry { lower: l, name, target: parse, icon_key });
    }
    out.sort_by(|a, b| a.lower.cmp(&b.lower));
    out.dedup_by(|a, b| a.lower == b.lower);
    let _ = PWSTR::null();
    out
}

pub struct Launchpad {
    top: HWND,
    back: HWND,
    gfx: Option<Gfx>,
    w: i32,
    h: i32,
    scr: RECT,
    pub open: Spring,
    pub visible: bool,
    page: f32,
    page_goal: usize,
    query: String,
    hover: i32,
    timer: bool,
    last: std::time::Instant,
    cells: Vec<(f32, f32, f32, f32, usize)>, // x,y,w,h, index into filtered
    filtered: Vec<AppEntry>,
    cols: usize,
    rows: usize,
    pub index: AppIndex,
    need: bool,
}

impl Launchpad {
    pub fn new() -> Launchpad {
        Launchpad { top: HWND::default(), back: HWND::default(), gfx: None, w: 0, h: 0, scr: RECT::default(), open: Spring::new(0.0), visible: false, page: 0.0, page_goal: 0, query: String::new(), hover: -1, timer: false, last: std::time::Instant::now(), cells: vec![], filtered: vec![], cols: 7, rows: 5, index: AppIndex::new(), need: true }
    }
    pub fn dirty(&mut self, index_changed: bool) {
        if self.visible {
            self.need = true;
            if index_changed {
                self.refilter();
            }
            self.kick();
        }
    }
    fn kick(&mut self) {
        if !self.timer && !self.top.is_invalid() {
            self.timer = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_LP));
            }
        }
    }
    fn ensure(&mut self) {
        if self.top.is_invalid() {
            self.back = win::create(ID_PILL, false, true, true, true, 0, 0, 10, 10);
            self.top = win::create(ID_LAUNCHPAD, true, true, false, false, 0, 0, 10, 10);
        }
        self.scr = screen_rect();
        let (w, h) = (self.scr.right - self.scr.left, self.scr.bottom - self.scr.top);
        if w != self.w || h != self.h || self.gfx.is_none() {
            self.w = w;
            self.h = h;
            self.gfx = Gfx::new(w, h);
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
        self.ensure();
        a.mc.close();
        a.spot.close();
        self.index.ensure(a.msg.0 as isize);
        self.query.clear();
        self.page = 0.0;
        self.page_goal = 0;
        self.visible = true;
        self.open = Spring::new(0.0);
        self.open.goal = 1.0;
        self.refilter();
        unsafe {
            set_acrylic(self.back, if a.cfg.s.dark { 0x99101010 } else { 0x99E8E8E8 }, false);
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
    fn refilter(&mut self) {
        let all = self.index.snapshot();
        let q = self.query.to_lowercase();
        self.filtered = if q.is_empty() { all } else { all.into_iter().filter(|e| e.lower.contains(&q)).collect() };
    }
    fn per_page(&self) -> usize {
        self.cols * self.rows
    }
    fn pages(&self) -> usize {
        ((self.filtered.len() + self.per_page() - 1) / self.per_page()).max(1)
    }

    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let mut moving = self.open.step(dt, 260.0, 30.0);
        let mut pg = Spring { v: self.page, vel: 0.0, goal: self.page_goal as f32 };
        if (pg.goal - pg.v).abs() > 0.002 {
            self.page += (pg.goal - pg.v) * (1.0 - (-dt * 14.0).exp());
            moving = true;
        } else {
            self.page = pg.goal;
        }
        pg.v = self.page;
        if !self.visible && self.open.v < 0.02 {
            win::hide(self.top);
            win::hide(self.back);
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_LP));
            }
            return;
        }
        if !moving && !self.need {
            self.timer = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_LP));
            }
            return;
        }
        self.need = false;
        self.draw();
    }

    fn draw(&mut self) {
        let a = app();
        let dark = a.cfg.s.dark;
        let s = a.scale;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let (w, h) = (self.w as f32, self.h as f32);
        let t = self.open.v.clamp(0.0, 1.0);
        let fg = if dark { argb(255, 255, 255, 255) } else { argb(255, 20, 20, 20) };
        // hit-catching veil
        g.fill_rect(0.0, 0.0, w, h, argb(1, 0, 0, 0));
        // search field
        let sw = 280.0 * s;
        let sh = 32.0 * s;
        let sx = (w - sw) / 2.0;
        let sy = 62.0 * s - (1.0 - t) * 20.0 * s;
        g.fill_round(sx, sy, sw, sh, sh / 2.0, with_alpha(if dark { argb(40, 255, 255, 255) } else { argb(60, 0, 0, 0) }, t));
        g.stroke_round(sx + 0.5, sy + 0.5, sw - 1.0, sh - 1.0, sh / 2.0, 1.0, with_alpha(argb(50, 255, 255, 255), t));
        g.stroke_circle(sx + 18.0 * s, sy + sh / 2.0 - 1.0, 5.0 * s, 1.5 * s, with_alpha(argb(170, 255, 255, 255), t));
        g.line(sx + 21.5 * s, sy + sh / 2.0 + 2.5 * s, sx + 25.0 * s, sy + sh / 2.0 + 6.0 * s, 1.5 * s, with_alpha(argb(170, 255, 255, 255), t));
        let (txt, col) = if self.query.is_empty() { ("Search".to_string(), with_alpha(argb(150, 255, 255, 255), t)) } else { (self.query.clone(), with_alpha(fg, t)) };
        g.text(&txt, sx + 38.0 * s, sy, sw - 50.0 * s, sh, 14.0 * s, false, 0, true, false, col);
        if !self.query.is_empty() {
            let tw = g.measure(&self.query, 14.0 * s, false);
            g.fill_rect(sx + 38.0 * s + tw + 1.0, sy + 8.0 * s, 1.5 * s, sh - 16.0 * s, with_alpha(fg, t));
        }
        // grid
        let grid_w = (w * 0.80).min(1700.0 * s);
        let top = 140.0 * s;
        let bottom = 96.0 * s;
        let cell_w = grid_w / self.cols as f32;
        let cell_h = (h - top - bottom) / self.rows as f32;
        let icon = (cell_w.min(cell_h) * 0.60).min(112.0 * s);
        let left = (w - grid_w) / 2.0;
        let per = self.per_page();
        let scale = 1.0 + (1.0 - t) * 0.06;
        self.cells.clear();
        let pages = self.pages();
        let cur = self.page;
        for pg in 0..pages {
            let po = (pg as f32 - cur) * w;
            if po.abs() > w * 1.05 {
                continue;
            }
            for k in 0..per {
                let idx = pg * per + k;
                if idx >= self.filtered.len() {
                    break;
                }
                let (c, r) = (k % self.cols, k / self.cols);
                let mut cx = left + c as f32 * cell_w + cell_w / 2.0 + po;
                let mut cy = top + r as f32 * cell_h + cell_h / 2.0 - 8.0 * s;
                // scale about the screen center for the open animation
                cx = w / 2.0 + (cx - w / 2.0) * scale;
                cy = h / 2.0 + (cy - h / 2.0) * scale;
                let isz = icon * scale;
                let e = &self.filtered[idx];
                let hov = self.hover == idx as i32;
                if hov {
                    g.fill_round(cx - cell_w / 2.0 + 6.0 * s, cy - isz / 2.0 - 8.0 * s, cell_w - 12.0 * s, isz + 40.0 * s, 14.0 * s, with_alpha(argb(34, 255, 255, 255), t));
                }
                match a.icons.get_with_pack(&e.icon_key, &e.target, |icons| {
                    pack_for(icons, &a.cfg, &e.target, &e.name)
                }) {
                    Some(img) => g.image(&img, cx - isz / 2.0, cy - isz / 2.0, isz, isz, t),
                    None => g.fill_round(cx - isz * 0.4, cy - isz * 0.4, isz * 0.8, isz * 0.8, isz * 0.18, with_alpha(argb(26, 255, 255, 255), t)), // skeleton until the icon is ready: no logo flash or size pop
                }
                g.text(&e.name, cx - cell_w / 2.0 + 4.0 * s, cy + isz / 2.0 + 6.0 * s, cell_w - 8.0 * s, 20.0 * s, 12.5 * s, false, 1, false, false, with_alpha(fg, t));
                if po.abs() < w * 0.5 {
                    self.cells.push((cx - cell_w / 2.0, cy - isz / 2.0 - 8.0 * s, cell_w, isz + 40.0 * s, idx));
                }
            }
        }
        if self.filtered.is_empty() {
            g.text("No Results", 0.0, h / 2.0 - 20.0 * s, w, 40.0 * s, 20.0 * s, false, 1, true, false, with_alpha(fg, t * 0.7));
        }
        // page dots
        if pages > 1 {
            let dw = 18.0 * s;
            let x0 = (w - dw * pages as f32) / 2.0;
            for p in 0..pages {
                let on = (p as f32 - cur).abs() < 0.5;
                g.fill_circle(x0 + dw * p as f32 + dw / 2.0, h - 60.0 * s, 3.6 * s, with_alpha(if on { argb(255, 255, 255, 255) } else { argb(90, 255, 255, 255) }, t));
            }
        }
        g.present_layered(self.top, self.scr.left, self.scr.top, 255);
        self.gfx = Some(g);
    }

    fn cell_at(&self, x: f32, y: f32) -> i32 {
        for (cx, cy, cw, ch, idx) in &self.cells {
            if x >= *cx && x <= cx + cw && y >= *cy && y <= cy + ch {
                return *idx as i32;
            }
        }
        -1
    }

    fn launch(&mut self, idx: usize) {
        if let Some(e) = self.filtered.get(idx).cloned() {
            self.close();
            shell_open(&e.target);
        }
    }
}

pub fn open_folder_fan(_path: &str, _label: &str) -> bool {
    false
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let l = &mut app().lp;
    match m {
        WM_TIMER if wp.0 == T_LP => {
            l.tick();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            let c = l.cell_at(x as f32, y as f32);
            if c != l.hover {
                l.hover = c;
                l.need = true;
                l.kick();
            }
            LRESULT(0)
        }
        x if x == WM_MOUSELEAVE => LRESULT(0),
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let c = l.cell_at(x as f32, y as f32);
            if c >= 0 {
                l.launch(c as usize);
            } else {
                let s = app().scale;
                if (y as f32) > 110.0 * s {
                    l.close();
                }
            }
            LRESULT(0)
        }
        WM_RBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let c = l.cell_at(x as f32, y as f32);
            if c >= 0 {
                if let Some(e) = l.filtered.get(c as usize).cloned() {
                    let mut p = POINT { x, y };
                    let _ = ClientToScreen(h, &mut p);
                    let r = win::popup_menu(h, p.x, p.y, &[(1, "Open".into(), true, false), (2, "Keep in Dock".into(), true, false)]);
                    let a = app();
                    if r == 1 {
                        a.lp.launch(c as usize);
                    } else if r == 2 {
                        a.cfg.add_pin(&e.target, 0);
                        a.cfg.reload();
                        crate::refresh_windows();
                    }
                }
            }
            LRESULT(0)
        }
        WM_MOUSEWHEEL => {
            let d = ((wp.0 >> 16) & 0xFFFF) as i16 as i32;
            let pages = l.pages();
            if d < 0 && l.page_goal + 1 < pages {
                l.page_goal += 1;
            } else if d > 0 && l.page_goal > 0 {
                l.page_goal -= 1;
            }
            l.need = true;
            l.kick();
            LRESULT(0)
        }
        WM_KEYDOWN => {
            let vk = wp.0 as u16;
            if vk == VK_ESCAPE.0 {
                l.close();
            } else if vk == VK_RIGHT.0 && l.page_goal + 1 < l.pages() {
                l.page_goal += 1;
            } else if vk == VK_LEFT.0 && l.page_goal > 0 {
                l.page_goal -= 1;
            } else if vk == VK_RETURN.0 {
                l.launch(0);
            } else if vk == VK_BACK.0 {
                l.query.pop();
                l.page_goal = 0;
                l.refilter();
            }
            l.need = true;
            l.kick();
            LRESULT(0)
        }
        WM_CHAR => {
            let c = char::from_u32(wp.0 as u32).unwrap_or(' ');
            if !c.is_control() {
                l.query.push(c);
                l.page_goal = 0;
                l.refilter();
                l.need = true;
                l.kick();
            }
            LRESULT(0)
        }
        WM_KILLFOCUS | WM_ACTIVATE if m == WM_KILLFOCUS || (wp.0 & 0xFFFF) == 0 => {
            // clicking away (another app got focus) closes it
            if l.visible && l.open.v > 0.9 {
                let fgw = GetForegroundWindow();
                if fgw != h && !fgw.is_invalid() {
                    l.close();
                }
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

impl AppIndex {
    /// Display name of the app with this AppUserModelID (notification sender), e.g. WhatsApp.
    pub fn name_for(&self, aumid: &str) -> Option<String> {
        let want = aumid.to_lowercase();
        let g = self.apps.lock().ok()?;
        g.iter().find(|e| e.target.to_lowercase().ends_with(&want)).map(|e| e.name.clone())
    }
}

impl AppIndex {
    /// Installed app whose name equals / contains / is contained in `lab` (lower case) -> its AppsFolder target.
    pub fn find_by_name(&self, lab: &str) -> Option<String> {
        let g = self.apps.lock().ok()?;
        g.iter().find(|e| e.lower == lab).or_else(|| g.iter().find(|e| e.lower.starts_with(lab) || lab.starts_with(&e.lower))).map(|e| e.target.clone())
    }
}
