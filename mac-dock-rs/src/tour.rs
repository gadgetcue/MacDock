//! First-run welcome tour (flag `@onboarded` in dock.txt), re-openable from Settings > General.
use crate::gfx::*;
use crate::sys::*;
use crate::win;
use crate::*;

const PAGES: [(&str, &[&str]); 3] = [
    ("Welcome to MacDock", &["A macOS-style Dock, menu bar and Launchpad for Windows.", "Take a quick look around. This takes under a minute."]),
    (
        "What you get",
        &[
            "Dock|Magnifying icons, genie minimize, folders that fan open.",
            "Launchpad|Every app in one grid. Swipe with four fingers or use the shortcut.",
            "Spotlight|Search apps and files from anywhere.",
            "Menu bar & island|Control Center, notifications, tray icons and live status.",
            "Mission Control|See every window at once and switch spaces.",
        ],
    ),
    (
        "Quick setup tips",
        &[
            "Pin apps|Drag any app or folder onto the Dock. Right-click an icon for options.",
            "Make it yours|Open MacDock Settings from the menu bar for size, theme and icons.",
            "Start with Windows|Turn on Open MacDock at login in Settings > General.",
            "Closing apps|Closing or force-quitting any app never closes MacDock.",
        ],
    ),
];

pub struct Tour {
    hwnd: HWND,
    gfx: Option<Gfx>,
    page: usize,
    pos: (i32, i32),
    pub visible: bool,
    hits: Vec<(f32, f32, f32, f32, i32)>, // -1 back, 1 next/done, 0 close
}

impl Tour {
    pub fn new() -> Tour {
        Tour { hwnd: HWND::default(), gfx: None, page: 0, pos: (0, 0), visible: false, hits: vec![] }
    }
    pub fn show_if_first_run(&mut self) {
        let a = app();
        if !a.cfg.ob("onboarded", false) {
            a.cfg.set_option("onboarded", "on");
            self.show();
        }
    }
    pub fn show(&mut self) {
        let a = app();
        let s = a.scale;
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_TOUR, true, true, false, false, 0, 0, 10, 10);
        }
        let m = 30.0 * s;
        let (w, h) = ((560.0 * s + 2.0 * m) as i32, (400.0 * s + 2.0 * m) as i32);
        self.gfx = Gfx::new(w, h);
        let scr = screen_rect();
        self.pos = (scr.left + ((scr.right - scr.left) - w) / 2, scr.top + ((scr.bottom - scr.top) - h) / 2);
        self.page = 0;
        self.visible = true;
        win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        unsafe {
            let _ = ShowWindow(self.hwnd, SW_SHOW);
        }
        crate::sys::activate(hv(self.hwnd));
        self.draw();
    }
    pub fn close(&mut self) {
        self.visible = false;
        win::hide(self.hwnd);
    }
    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let m = 30.0 * s;
        let (w, h) = (g.w as f32 - 2.0 * m, g.h as f32 - 2.0 * m);
        g.shadow(m, m, w, h, 20.0 * s, 28.0 * s, 200);
        g.fill_round(m, m, w, h, 20.0 * s, if dark { argb(250, 32, 32, 36) } else { argb(250, 246, 246, 248) });
        g.stroke_round(m + 0.5, m + 0.5, w - 1.0, h - 1.0, 20.0 * s, 1.0, argb(50, 255, 255, 255));
        let fg = if dark { argb(255, 245, 245, 247) } else { argb(255, 28, 28, 32) };
        let dim = if dark { argb(255, 160, 160, 170) } else { argb(255, 110, 110, 120) };
        let (title, lines) = PAGES[self.page];
        self.hits.clear();
        if self.page == 0 {
            if let Some(logo) = a.icons.logo.clone() {
                g.image(&logo, m + w / 2.0 - 44.0 * s, m + 36.0 * s, 88.0 * s, 88.0 * s, 1.0);
            }
            g.text(title, m, m + 140.0 * s, w, 40.0 * s, 26.0 * s, true, 1, true, false, fg);
            for (i, l) in lines.iter().enumerate() {
                g.text(l, m + 30.0 * s, m + 190.0 * s + i as f32 * 28.0 * s, w - 60.0 * s, 26.0 * s, 14.0 * s, false, 1, true, false, dim);
            }
        } else {
            g.text(title, m + 30.0 * s, m + 26.0 * s, w - 60.0 * s, 40.0 * s, 24.0 * s, true, 0, true, false, fg);
            for (i, l) in lines.iter().enumerate() {
                let (head, body) = l.split_once('|').unwrap_or(("", l));
                let y = m + 84.0 * s + i as f32 * 54.0 * s;
                g.text(head, m + 30.0 * s, y, w - 60.0 * s, 22.0 * s, 14.5 * s, true, 0, true, false, fg);
                g.text(body, m + 30.0 * s, y + 22.0 * s, w - 60.0 * s, 22.0 * s, 12.5 * s, false, 0, true, false, dim);
            }
        }
        // dots + buttons
        for i in 0..PAGES.len() {
            g.fill_circle(m + w / 2.0 + (i as f32 - 1.0) * 16.0 * s, m + h - 32.0 * s, 3.6 * s, if i == self.page { fg } else { argb(90, 150, 150, 160) });
        }
        let bw = 90.0 * s;
        let by = m + h - 50.0 * s;
        if self.page > 0 {
            g.fill_round(m + 24.0 * s, by, bw, 32.0 * s, 16.0 * s, if dark { argb(255, 60, 60, 66) } else { argb(255, 226, 226, 230) });
            g.text("Back", m + 24.0 * s, by, bw, 32.0 * s, 13.0 * s, true, 1, true, false, fg);
            self.hits.push((m + 24.0 * s, by, bw, 32.0 * s, -1));
        }
        let last = self.page == PAGES.len() - 1;
        g.fill_round(m + w - 24.0 * s - bw, by, bw, 32.0 * s, 16.0 * s, argb(255, 10, 132, 255));
        g.text(if last { "Done" } else { "Next" }, m + w - 24.0 * s - bw, by, bw, 32.0 * s, 13.0 * s, true, 1, true, false, argb(255, 255, 255, 255));
        self.hits.push((m + w - 24.0 * s - bw, by, bw, 32.0 * s, 1));
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let t = &mut app().tour;
    match m {
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let hit = t.hits.iter().find(|r| x as f32 >= r.0 && x as f32 <= r.0 + r.2 && y as f32 >= r.1 && y as f32 <= r.1 + r.3).map(|r| r.4);
            match hit {
                Some(-1) => {
                    t.page = t.page.saturating_sub(1);
                    t.draw();
                }
                Some(1) => {
                    if t.page + 1 >= PAGES.len() {
                        t.close();
                    } else {
                        t.page += 1;
                        t.draw();
                    }
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == 0x1B {
                t.close();
            }
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
