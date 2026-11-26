//! Settings window (own title strip, custom-drawn controls) plus the small modal helpers (text prompt, file picker).
use crate::gfx::*;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use windows::core::w;
use windows::Win32::UI::Controls::Dialogs::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;

#[derive(Clone, Copy, PartialEq)]
enum Act {
    Page(u32),
    Close,
    Theme(bool),
    Taskbar,
    Bar,
    Island,
    Gestures,
    Motion,
    AutoHide,
    Icons,
    Size,
    Magnify,
    AddApp,
    EditPins,
    OpenFolder,
    Startup,
    Link(u32),
    Tog(&'static str, bool),
    Pick(&'static str),
    MoveBar(usize, i32),
    Tour,
    TrayScan,
    TraySel(usize),
}

pub struct SettingsWin {
    hwnd: HWND,
    gfx: Option<Gfx>,
    w: i32,
    h: i32,
    pos: (i32, i32),
    page: u32,
    pub visible: bool,
    hits: Vec<(f32, f32, f32, f32, Act)>,
    drag: Option<Act>,
    hover: i32,
    scroll: f32,
    content_h: f32,
    content_range: (f32, f32),
    need: bool,
    glass: Option<win::Glass>,
    glass_tint: u32,
}

impl SettingsWin {
    pub fn new() -> SettingsWin {
        SettingsWin { hwnd: HWND::default(), gfx: None, w: 0, h: 0, pos: (0, 0), page: 0, visible: false, hits: vec![], drag: None, hover: -1, need: true, scroll: 0.0, content_h: 0.0, content_range: (0.0, 0.0), glass: None, glass_tint: 0 }
    }
    pub fn show(&mut self, page: u32) {
        let a = app();
        let s = a.scale;
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_SETTINGS, true, false, false, false, 0, 0, 10, 10);
        }
        let m = 28.0 * s;
        let (w, h) = ((760.0 * s + 2.0 * m) as i32, (520.0 * s + 2.0 * m) as i32);
        if self.gfx.is_none() || w != self.w || h != self.h {
            self.w = w;
            self.h = h;
            self.gfx = Gfx::new(w, h);
            let scr = screen_rect();
            self.pos = (scr.left + ((scr.right - scr.left) - w) / 2, scr.top + ((scr.bottom - scr.top) - h) / 2);
        }
        self.page = if page == 99 { 5 } else { page.min(5) };
        self.scroll = 0.0;
        self.visible = true;
        unsafe {
            win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
            let _ = ShowWindow(self.hwnd, SW_SHOW);
            let _ = SetWindowPos(self.hwnd, Some(HWND_NOTOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        crate::sys::activate(hv(self.hwnd));
        self.draw();
    }
    pub fn close(&mut self) {
        self.visible = false;
        if let Some(g) = self.glass.as_mut() { g.hide(); }
        win::hide(self.hwnd);
    }

    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let m = 28.0 * s;
        let (pw_, ph) = (self.w as f32 - 2.0 * m, self.h as f32 - 2.0 * m);
        g.shadow(m, m, pw_, ph, 16.0 * s, 26.0 * s, 200);
        let liquid = a.cfg.os("glass-style", "liquid") != "classic";
        let (ba, sa, ca) = if liquid { if dark { (150u8, 105u8, 195u8) } else { (165, 120, 215) } } else { (255, 255, 255) };
        let bg = if dark { argb(ba, 30, 30, 34) } else { argb(ba, 242, 242, 246) };
        let side = if dark { argb(sa, 40, 40, 45) } else { argb(sa, 230, 230, 236) };
        let fg = if dark { argb(255, 240, 240, 244) } else { argb(255, 28, 28, 32) };
        let dim = if dark { argb(255, 150, 150, 160) } else { argb(255, 110, 110, 120) };
        let card = if dark { argb(ca, 44, 44, 50) } else { argb(ca, 255, 255, 255) };
        let line = if dark { argb(255, 64, 64, 72) } else { argb(255, 224, 224, 230) };
        let accent = crate::gfx::accent();
        if !liquid { g.fill_round(m, m, pw_, ph, 16.0 * s, argb(255, 0, 0, 0)); }
        g.fill_round(m, m, pw_, ph, 16.0 * s, bg);
        // sidebar
        let sw = 190.0 * s;
        g.fill_round(m, m, sw, ph, 16.0 * s, side);
        g.fill_rect(m + sw - 20.0 * s, m, 20.0 * s, ph, side);
        if liquid { g.liquid_glass(m, m, pw_, ph, 16.0 * s, dark, 0.9); } else { g.stroke_round(m + 0.5, m + 0.5, pw_ - 1.0, ph - 1.0, 16.0 * s, 1.0, argb(60, 255, 255, 255)); }
        self.hits.clear();
        // window dots
        g.fill_circle(m + 22.0 * s, m + 22.0 * s, 6.0 * s, argb(255, 255, 95, 87));
        g.fill_circle(m + 42.0 * s, m + 22.0 * s, 6.0 * s, argb(255, 254, 188, 46));
        g.fill_circle(m + 62.0 * s, m + 22.0 * s, 6.0 * s, argb(255, 40, 200, 64));
        self.hits.push((m + 14.0 * s, m + 14.0 * s, 16.0 * s, 16.0 * s, Act::Close));
        let pages = ["General", "Dock", "Menu Bar", "Island", "Appearance", "About"];
        for (i, p) in pages.iter().enumerate() {
            let y = m + 60.0 * s + i as f32 * 38.0 * s;
            if self.page == i as u32 {
                g.fill_round(m + 10.0 * s, y, sw - 20.0 * s, 32.0 * s, 8.0 * s, accent);
            }
            g.text(p, m + 28.0 * s, y, sw - 40.0 * s, 32.0 * s, 13.5 * s, self.page == i as u32, 0, true, false, if self.page == i as u32 { argb(255, 255, 255, 255) } else { fg });
            self.hits.push((m + 10.0 * s, y, sw - 20.0 * s, 32.0 * s, Act::Page(i as u32)));
        }
        let cx = m + sw + 24.0 * s;
        let cw = pw_ - sw - 48.0 * s;
        let mut y = m + 22.0 * s;
        g.text(pages[self.page as usize], cx, y, cw, 30.0 * s, 20.0 * s, true, 0, true, false, fg);
        y += 46.0 * s;
        let content_top = y;
        let content_bottom = m + ph - 14.0 * s;
        self.content_range = (content_top, content_bottom);
        y -= self.scroll;
        g.clip(cx as i32 - 6, content_top as i32 - 2, cw as i32 + 12, (content_bottom - content_top) as i32 + 2);
        let s_ = a.cfg.s.clone();
        let row = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, y: &mut f32, label: &str, sub: &str, on: bool, act: Act| {
            let h_ = 50.0 * s;
            g.fill_round(cx, *y, cw, h_, 10.0 * s, card);
            g.text(label, cx + 16.0 * s, *y + 6.0 * s, cw - 100.0 * s, 20.0 * s, 13.5 * s, false, 0, true, false, fg);
            g.text(sub, cx + 16.0 * s, *y + 26.0 * s, cw - 100.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, dim);
            let (tx, ty, tw, th) = (cx + cw - 58.0 * s, *y + h_ / 2.0 - 11.0 * s, 42.0 * s, 22.0 * s);
            g.fill_round(tx, ty, tw, th, th / 2.0, if on { argb(255, 52, 199, 89) } else { line });
            g.fill_circle(if on { tx + tw - th / 2.0 } else { tx + th / 2.0 }, ty + th / 2.0, th / 2.0 - 2.0 * s, argb(255, 255, 255, 255));
            hits.push((cx, *y, cw, h_, act));
            *y += h_ + 8.0 * s;
        };
        let slider = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, y: &mut f32, label: &str, val: &str, v: f32, act: Act| {
            let h_ = 62.0 * s;
            g.fill_round(cx, *y, cw, h_, 10.0 * s, card);
            g.text(label, cx + 16.0 * s, *y + 6.0 * s, cw / 2.0, 20.0 * s, 13.5 * s, false, 0, true, false, fg);
            g.text(val, cx + cw - 116.0 * s, *y + 6.0 * s, 100.0 * s, 20.0 * s, 13.0 * s, false, 2, true, false, dim);
            let (tx, tw, ty) = (cx + 16.0 * s, cw - 32.0 * s, *y + 44.0 * s);
            g.fill_round(tx, ty - 3.0 * s, tw, 6.0 * s, 3.0 * s, line);
            g.fill_round(tx, ty - 3.0 * s, (tw * v).max(6.0 * s), 6.0 * s, 3.0 * s, accent);
            g.fill_circle(tx + tw * v, ty, 8.0 * s, argb(255, 255, 255, 255));
            g.stroke_circle(tx + tw * v, ty, 8.0 * s, 1.0, line);
            hits.push((tx - 8.0 * s, *y + 26.0 * s, tw + 16.0 * s, 36.0 * s, act));
            *y += h_ + 8.0 * s;
        };
        let button = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, y: &mut f32, label: &str, act: Act| {
            let h_ = 38.0 * s;
            g.fill_round(cx, *y, cw, h_, 10.0 * s, card);
            g.text(label, cx + 16.0 * s, *y, cw - 32.0 * s, h_, 13.5 * s, false, 0, true, false, accent);
            hits.push((cx, *y, cw, h_, act));
            *y += h_ + 8.0 * s;
        };
        let trow = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, y: &mut f32, label: &str, sub: &str, key: &'static str, def: bool| {
            row(g, hits, y, label, sub, a.cfg.ob(key, def), Act::Tog(key, def));
        };
        let prow = |g: &mut Gfx, hits: &mut Vec<(f32, f32, f32, f32, Act)>, y: &mut f32, label: &str, key: &'static str| {
            let h_ = 44.0 * s;
            g.fill_round(cx, *y, cw, h_, 10.0 * s, card);
            g.text(label, cx + 16.0 * s, *y, cw - 200.0 * s, h_, 13.5 * s, false, 0, true, false, fg);
            let cur = a.cfg.os(key, pick_default(key));
            let shown = pick_opts(key).iter().find(|o| o.0 == cur).map(|o| o.1).unwrap_or("Custom");
            g.text(shown, cx + cw - 200.0 * s, *y, 184.0 * s, h_, 13.0 * s, true, 2, true, false, accent);
            hits.push((cx, *y, cw, h_, Act::Pick(key)));
            *y += h_ + 8.0 * s;
        };
        match self.page {
            0 => {
                // theme segmented
                let h_ = 50.0 * s;
                g.fill_round(cx, y, cw, h_, 10.0 * s, card);
                g.text("Appearance", cx + 16.0 * s, y, 160.0 * s, h_, 13.5 * s, false, 0, true, false, fg);
                for (i, (lbl, d)) in [("Dark", true), ("Light", false)].iter().enumerate() {
                    let bx = cx + cw - 188.0 * s + i as f32 * 90.0 * s;
                    let on = s_.dark == *d;
                    g.fill_round(bx, y + 10.0 * s, 84.0 * s, 30.0 * s, 8.0 * s, if on { accent } else { line });
                    g.text(lbl, bx, y + 10.0 * s, 84.0 * s, 30.0 * s, 12.5 * s, false, 1, true, false, if on { argb(255, 255, 255, 255) } else { fg });
                    self.hits.push((bx, y + 10.0 * s, 84.0 * s, 30.0 * s, Act::Theme(*d)));
                }
                y += h_ + 8.0 * s;
                row(&mut g, &mut self.hits, &mut y, "Start at login", "Run MacDock when you sign in", startup_on(), Act::Startup);
                row(&mut g, &mut self.hits, &mut y, "Hide the Windows taskbar", "MacDock replaces it", s_.hide_taskbar, Act::Taskbar);
                row(&mut g, &mut self.hits, &mut y, "Show menu bar", "Top bar with status readouts", s_.bar_show, Act::Bar);
                row(&mut g, &mut self.hits, &mut y, "Dynamic Island", "Notifications, media and power events", s_.island_on, Act::Island);
                row(&mut g, &mut self.hits, &mut y, "Touchpad gestures", "Mission Control swipe (Ctrl+Alt+Up also works)", s_.gestures, Act::Gestures);
                row(&mut g, &mut self.hits, &mut y, "Reduce motion", "Skip the genie effect", s_.reduce_motion, Act::Motion);
                trow(&mut g, &mut self.hits, &mut y, "Match window title bars to the theme", "Dark or light title bars on every window", "windowtheme", false);
                button(&mut g, &mut self.hits, &mut y, "Show the welcome tour", Act::Tour);
            }
            1 => {
                slider(&mut g, &mut self.hits, &mut y, "Icon size", &format!("{} px", s_.size), (s_.size as f32 - 28.0) / 60.0, Act::Size);
                slider(&mut g, &mut self.hits, &mut y, "Magnification", &if s_.magnify <= 1.01 { "Off".to_string() } else { format!("{:.2}x", s_.magnify) }, (s_.magnify - 1.0) / 1.2, Act::Magnify);
                trow(&mut g, &mut self.hits, &mut y, "Show Dock", "Turn the Dock off completely", "show-dock", true);
                prow(&mut g, &mut self.hits, &mut y, "Icon spacing", "dock-spacing");
                prow(&mut g, &mut self.hits, &mut y, "Corner rounding", "dock-round");
                trow(&mut g, &mut self.hits, &mut y, "Running indicators", "Dot under open apps", "dock-indicator", true);
                trow(&mut g, &mut self.hits, &mut y, "Dividers", "Line between apps and folders", "dock-divider", true);
                row(&mut g, &mut self.hits, &mut y, "Auto-hide Dock", "Slides away until the pointer returns", s_.auto_hide, Act::AutoHide);
                trow(&mut g, &mut self.hits, &mut y, "Show Start icon in Dock", "The Windows Start button at the left", "show-start-icon", true);
                trow(&mut g, &mut self.hits, &mut y, "Genie effect", "Windows are sucked into their Dock icon", "genie", true);
                prow(&mut g, &mut self.hits, &mut y, "Minimize effect", "genie-style");
                trow(&mut g, &mut self.hits, &mut y, "Minimize windows into the Dock", "Minimized windows get their own thumbnail", "minimize-to-dock", true);
                trow(&mut g, &mut self.hits, &mut y, "Show window previews on hover", "Live thumbnails above a Dock icon", "window-previews", true);
                trow(&mut g, &mut self.hits, &mut y, "Open folder fans on click instead of hover", "Folders fan out when clicked", "fan-click", false);
                row(&mut g, &mut self.hits, &mut y, "Hide the Windows taskbar while MacDock runs", "MacDock replaces it", s_.hide_taskbar, Act::Taskbar);
                button(&mut g, &mut self.hits, &mut y, "Add an application to the dock...", Act::AddApp);
                button(&mut g, &mut self.hits, &mut y, "Edit pinned items (dock.txt)", Act::EditPins);
            }
            2 => {
                row(&mut g, &mut self.hits, &mut y, "Show menu bar", "Top bar with status readouts", s_.bar_show, Act::Bar);
                trow(&mut g, &mut self.hits, &mut y, "CPU usage", "Percentage in the menu bar", "stats-cpu", true);
                trow(&mut g, &mut self.hits, &mut y, "Memory (RAM) usage", "Percentage in the menu bar", "stats-ram", true);
                trow(&mut g, &mut self.hits, &mut y, "Disk usage", "System drive, percent used", "stats-disk", false);
                trow(&mut g, &mut self.hits, &mut y, "Network speed", "Download and upload", "stats-net", true);
                prow(&mut g, &mut self.hits, &mut y, "Network speed shows", "stats-net-mode");
                trow(&mut g, &mut self.hits, &mut y, "Arrows next to network speed", "Up and down arrows", "stats-net-arrows", true);
                trow(&mut g, &mut self.hits, &mut y, "Temperature", "CPU temperature when the PC reports it", "stats-temp", false);
                trow(&mut g, &mut self.hits, &mut y, "Weather", "Current conditions for your location", "stats-weather", false);
                prow(&mut g, &mut self.hits, &mut y, "Extra spacing between items", "bar-spacing");
                trow(&mut g, &mut self.hits, &mut y, "Profile picture", "Your account at the right", "bar-profile", true);
                trow(&mut g, &mut self.hits, &mut y, "Notification bell", "Opens the Dynamic Island list", "bar-bell", true);
                trow(&mut g, &mut self.hits, &mut y, "Bluetooth icon", "Opens Bluetooth settings", "bar-bluetooth", true);
                trow(&mut g, &mut self.hits, &mut y, "Screenshot button", "Win+Shift+S", "bar-screenshot", false);
                trow(&mut g, &mut self.hits, &mut y, "Screen record button", "Win+Alt+R", "bar-record", false);
                trow(&mut g, &mut self.hits, &mut y, "Show tray icons in the menu bar", "Notification-area icons", "tray-bar", false);
                trow(&mut g, &mut self.hits, &mut y, "Show the tray arrow", "Handle to fold the tray icons", "tray-arrow", true);
                trow(&mut g, &mut self.hits, &mut y, "Fold tray icons into a small handle", "Collapsible tray", "tray-collapsible", false);
                trow(&mut g, &mut self.hits, &mut y, "Keep them folded", "Start folded", "tray-collapsed", false);
                button(&mut g, &mut self.hits, &mut y, "Scan tray icons (briefly opens the hidden-icons flyout)", Act::TrayScan);
                {
                    let sel = crate::tray::selected(&a.cfg);
                    for (i, t) in crate::tray::cached().iter().enumerate() {
                        let title = if t.hidden { format!("{}  (hidden)", t.title) } else { t.title.clone() };
                        row(&mut g, &mut self.hits, &mut y, &title, "Show in the menu bar", sel.contains(&t.key), Act::TraySel(i));
                    }
                }
                g.text("Order of the right-hand items", cx, y + 4.0 * s, cw, 22.0 * s, 13.0 * s, true, 0, true, false, fg);
                y += 30.0 * s;
                let ord = crate::topbar::bar_order(&a.cfg);
                for (i, id) in ord.iter().enumerate() {
                    let h_ = 34.0 * s;
                    g.fill_round(cx, y, cw, h_, 10.0 * s, card);
                    g.text(id, cx + 16.0 * s, y, cw - 120.0 * s, h_, 13.0 * s, false, 0, true, false, fg);
                    g.text("Up", cx + cw - 96.0 * s, y, 40.0 * s, h_, 12.0 * s, true, 1, true, false, accent);
                    g.text("Down", cx + cw - 52.0 * s, y, 44.0 * s, h_, 12.0 * s, true, 1, true, false, accent);
                    self.hits.push((cx + cw - 96.0 * s, y, 40.0 * s, h_, Act::MoveBar(i, -1)));
                    self.hits.push((cx + cw - 52.0 * s, y, 44.0 * s, h_, Act::MoveBar(i, 1)));
                    y += h_ + 6.0 * s;
                }
            }
            3 => {
                row(&mut g, &mut self.hits, &mut y, "Show Dynamic Island", "Notifications, media and power events", s_.island_on, Act::Island);
                trow(&mut g, &mut self.hits, &mut y, "Volume pop-up", "Level bar when the volume changes", "island-volume", true);
                trow(&mut g, &mut self.hits, &mut y, "Brightness pop-up", "Level bar when the brightness changes", "island-brightness", true);
                trow(&mut g, &mut self.hits, &mut y, "Mic and camera in-use dots", "Orange and green dots", "island-privacy", true);
                trow(&mut g, &mut self.hits, &mut y, "Caps Lock and Num Lock alerts", "A small pop-up on each toggle", "island-locks", true);
                trow(&mut g, &mut self.hits, &mut y, "Water reminders", "Daily glass counter", "island-water", true);
                prow(&mut g, &mut self.hits, &mut y, "Remind about water every", "island-water-every");
                prow(&mut g, &mut self.hits, &mut y, "Daily water goal", "island-water-goal");
                trow(&mut g, &mut self.hits, &mut y, "Stand-up reminders", "Stretch break reminders", "island-stand", true);
                prow(&mut g, &mut self.hits, &mut y, "Stand up every", "island-stand-every");
                prow(&mut g, &mut self.hits, &mut y, "Pomodoro focus length", "island-pomo-focus");
                prow(&mut g, &mut self.hits, &mut y, "Pomodoro break length", "island-pomo-break");
            }
            4 => {
                prow(&mut g, &mut self.hits, &mut y, "Glass style", "glass-style");
                prow(&mut g, &mut self.hits, &mut y, "Glass opacity", "glass-opacity");
                prow(&mut g, &mut self.hits, &mut y, "Icon style", "icon-style");
                prow(&mut g, &mut self.hits, &mut y, "Accent color", "accent");
                row(&mut g, &mut self.hits, &mut y, "Use icon pack", "Off shows each app's original icon", s_.icons_on, Act::Icons);
                button(&mut g, &mut self.hits, &mut y, "Open the icons folder", Act::OpenFolder);
                g.text("Drop .png files named after the app (for example Safari.png) into the icons folder. Names map through iconmap.txt.", cx, y + 6.0 * s, cw, 50.0 * s, 12.0 * s, false, 0, false, true, dim);
            }
            _ => {
                g.text("MacDock", cx, y, cw, 36.0 * s, 26.0 * s, true, 0, true, false, fg);
                y += 40.0 * s;
                g.text("Version 3.0 (Rust)", cx, y, cw, 22.0 * s, 13.0 * s, false, 0, true, false, dim);
                y += 34.0 * s;
                g.text("A macOS-style dock, menu bar, Launchpad, Mission Control, Spotlight, Dynamic Island and Control Center for Windows 11. Native Win32, one process, event-driven.", cx, y, cw, 70.0 * s, 13.0 * s, false, 0, false, true, fg);
                y += 84.0 * s;
                button(&mut g, &mut self.hits, &mut y, "GitHub: gadgetcue", Act::Link(0));
                button(&mut g, &mut self.hits, &mut y, "YouTube: GadgetCue", Act::Link(1));
            }
        }
        g.unclip();
        self.content_h = (y + self.scroll - content_top).max(0.0) + 24.0 * s;
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
        // live-blur glass behind the panel (same stacked-acrylic technique as the Dock)
        if liquid && self.visible {
            let tint = if dark { 0x501C1C20u32 } else { 0x40F5F5F8 };
            let hw = self.hwnd;
            let gl = self.glass.get_or_insert_with(|| win::Glass::new(tint));
            if self.glass_tint != tint { gl.set_tint(tint); self.glass_tint = tint; }
            gl.invalidate();
            gl.place(self.pos.0 + m as i32, self.pos.1 + m as i32, pw_ as i32, ph as i32, (16.0 * s) as i32, hw);
        } else if let Some(gl) = self.glass.as_mut() {
            gl.hide();
        }
    }

    fn hit(&self, x: f32, y: f32) -> Option<(Act, f32, f32, f32, f32)> {
        let (ct, cb) = self.content_range;
        self.hits.iter().rev().find(|h| x >= h.0 && x <= h.0 + h.2 && y >= h.1 && y <= h.1 + h.3 && (matches!(h.4, Act::Page(_) | Act::Close) || (y >= ct && y <= cb))).map(|h| (h.4, h.0, h.1, h.2, h.3))
    }

    fn apply(&mut self, act: Act, x: f32, r: (f32, f32, f32, f32), finish: bool) {
        let a = app();
        let s = a.scale;
        match act {
            Act::Page(p) => self.page = p,
            Act::Close => self.close(),
            Act::Theme(d) => {
                a.cfg.s.dark = d;
                a.cfg.set_option("theme", if d { "dark" } else { "light" });
                win::enable_dark_menus(d);
                a.dock.apply_theme();
                a.bar.apply_theme(d);
            }
            Act::Taskbar => {
                a.cfg.s.hide_taskbar = !a.cfg.s.hide_taskbar;
                a.cfg.set_option("hide-taskbar", if a.cfg.s.hide_taskbar { "on" } else { "off" });
                if !a.test {
                    set_taskbar_visible(!a.cfg.s.hide_taskbar);
                }
            }
            Act::Bar => {
                a.cfg.s.bar_show = !a.cfg.s.bar_show;
                a.cfg.set_option("menubar", if a.cfg.s.bar_show { "on" } else { "off" });
                a.bar.set_visible(a.cfg.s.bar_show);
                if !a.test {
                    crate::reserve();
                }
            }
            Act::Island => {
                a.cfg.s.island_on = !a.cfg.s.island_on;
                a.cfg.set_option("island", if a.cfg.s.island_on { "on" } else { "off" });
                a.island.set_enabled(a.cfg.s.island_on);
            }
            Act::Gestures => {
                a.cfg.s.gestures = !a.cfg.s.gestures;
                a.cfg.set_option("gestures", if a.cfg.s.gestures { "on" } else { "off" });
            }
            Act::Motion => {
                a.cfg.s.reduce_motion = !a.cfg.s.reduce_motion;
                a.cfg.set_option("reduce-motion", if a.cfg.s.reduce_motion { "on" } else { "off" });
            }
            Act::AutoHide => {
                a.cfg.s.auto_hide = !a.cfg.s.auto_hide;
                crate::reserve();
                a.cfg.set_option("auto-hide-dock", if a.cfg.s.auto_hide { "on" } else { "off" });
                a.dock.dirty();
            }
            Act::Icons => {
                a.cfg.s.icons_on = !a.cfg.s.icons_on;
                a.cfg.set_option("icons", if a.cfg.s.icons_on { "on" } else { "off" });
                a.icons.clear();
                crate::refresh_windows();
            }
            Act::Size | Act::Magnify => {
                let (tx, tw) = (r.0 + 8.0 * s, r.2 - 16.0 * s);
                let v = ((x - tx) / tw).clamp(0.0, 1.0);
                if act == Act::Size {
                    a.cfg.s.size = (28.0 + v * 60.0).round() as i32;
                } else {
                    a.cfg.s.magnify = if v < 0.04 { 1.0 } else { ((1.0 + v * 1.2) * 20.0).round() / 20.0 };
                }
                a.dock.relayout_all(a.scale, &a.cfg);
                if finish {
                    a.cfg.save_size();
                    a.icons.clear();
                    crate::refresh_windows();
                }
            }
            Act::AddApp => {
                if let Some(p) = pick_file("Applications\0*.exe;*.lnk\0All files\0*.*\0\0") {
                    a.cfg.add_pin(&p, 0);
                    a.cfg.reload();
                    crate::refresh_windows();
                }
            }
            Act::EditPins => {
                let p = a.cfg.path("dock.txt");
                run_cmd("notepad.exe", &format!("\"{}\"", p.display()));
            }
            Act::OpenFolder => {
                let p = a.cfg.dir.join("icons");
                let _ = std::fs::create_dir_all(&p);
                shell_open(&p.to_string_lossy());
            }
            Act::Startup => set_startup(!startup_on()),
            Act::Tog(key, def) => {
                let cur = a.cfg.ob(key, def);
                a.cfg.set_ob(key, !cur);
                option_changed(key);
            }
            Act::Pick(key) => {
                let opts = pick_opts(key);
                let cur = a.cfg.os(key, pick_default(key));
                let i = opts.iter().position(|o| o.0 == cur).map(|i| (i + 1) % opts.len().max(1)).unwrap_or(0);
                if let Some(o) = opts.get(i) {
                    a.cfg.set_option(key, o.0);
                }
                option_changed(key);
            }
            Act::MoveBar(i, dir) => {
                let mut ord = crate::topbar::bar_order(&a.cfg);
                let j = (i as i32 + dir).clamp(0, ord.len() as i32 - 1) as usize;
                if j != i {
                    ord.swap(i, j);
                    a.cfg.set_option("bar-order", &ord.join(","));
                    option_changed("bar-order");
                }
            }
            Act::Tour => a.tour.show(),
            Act::TrayScan => crate::tray::scan(a.msg.0 as isize, a.scale),
            Act::TraySel(i) => {
                if let Some(t) = crate::tray::cached().get(i) {
                    let on = crate::tray::selected(&a.cfg).contains(&t.key);
                    crate::tray::set_selected(&mut a.cfg, &t.key, !on);
                    a.bar.dirty();
                }
            }
            Act::Link(0) => shell_open("https://github.com/gadgetcue"),
            Act::Link(_) => shell_open("https://www.youtube.com/@GadgetCue"),
        }
        self.draw();
    }
}

const RUN_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
fn startup_on() -> bool {
    use windows::Win32::System::Registry::*;
    unsafe {
        let mut h = HKEY::default();
        let k = wide(RUN_KEY);
        if RegOpenKeyExW(HKEY_CURRENT_USER, pw(&k), None, KEY_READ, &mut h).is_err() {
            return false;
        }
        let n = wide("MacDock");
        let r = RegQueryValueExW(h, pw(&n), None, None, None, None).is_ok();
        let _ = RegCloseKey(h);
        r
    }
}
fn set_startup(on: bool) {
    use windows::Win32::System::Registry::*;
    unsafe {
        let mut h = HKEY::default();
        let k = wide(RUN_KEY);
        if RegOpenKeyExW(HKEY_CURRENT_USER, pw(&k), None, KEY_WRITE, &mut h).is_err() {
            return;
        }
        let n = wide("MacDock");
        if on {
            let exe = std::env::current_exe().map(|p| format!("\"{}\"", p.display())).unwrap_or_default();
            let v: Vec<u8> = wide(&exe).iter().flat_map(|c| c.to_le_bytes()).collect();
            let _ = RegSetValueExW(h, pw(&n), None, REG_SZ, Some(&v));
        } else {
            let _ = RegDeleteValueW(h, pw(&n));
        }
        let _ = RegCloseKey(h);
    }
}

pub fn pick_file(filter: &str) -> Option<String> {
    unsafe {
        let mut buf = [0u16; 1024];
        let f: Vec<u16> = filter.encode_utf16().collect();
        let mut ofn = OPENFILENAMEW { lStructSize: std::mem::size_of::<OPENFILENAMEW>() as u32, lpstrFilter: windows::core::PCWSTR(f.as_ptr()), lpstrFile: windows::core::PWSTR(buf.as_mut_ptr()), nMaxFile: buf.len() as u32, Flags: OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR, ..Default::default() };
        if GetOpenFileNameW(&mut ofn).as_bool() {
            Some(from_wide(&buf))
        } else {
            None
        }
    }
}

// ---- tiny modal text prompt ----
static mut PROMPT_EDIT: HWND = HWND(std::ptr::null_mut());
static mut PROMPT_DONE: i32 = 0; // 1 ok, 2 cancel

unsafe extern "system" fn prompt_proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match m {
        WM_COMMAND => {
            let id = wp.0 & 0xFFFF;
            if id == 1 {
                PROMPT_DONE = 1;
            } else if id == 2 {
                PROMPT_DONE = 2;
            }
            LRESULT(0)
        }
        WM_CLOSE => {
            PROMPT_DONE = 2;
            LRESULT(0)
        }
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

pub fn prompt_text(title: &str, label: &str, initial: &str) -> Option<String> {
    use windows::Win32::Graphics::Gdi::*;
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    unsafe {
        let hi = GetModuleHandleW(None).ok()?;
        let cls = w!("MacDockPrompt");
        let wc = WNDCLASSW { lpfnWndProc: Some(prompt_proc), hInstance: hi.into(), lpszClassName: cls, hbrBackground: HBRUSH((COLOR_WINDOW.0 + 1) as isize as *mut _), hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(), ..Default::default() };
        RegisterClassW(&wc);
        let s = app().scale;
        let (w_, h_) = ((360.0 * s) as i32, (150.0 * s) as i32);
        let scr = screen_rect();
        let t = wide(title);
        let dlg = CreateWindowExW(WS_EX_TOPMOST | WS_EX_DLGMODALFRAME, cls, pw(&t), WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_VISIBLE, scr.left + ((scr.right - scr.left) - w_) / 2, scr.top + ((scr.bottom - scr.top) - h_) / 2, w_, h_, None, None, Some(hi.into()), None).ok()?;
        let font = GetStockObject(DEFAULT_GUI_FONT);
        let mk = |class: windows::core::PCWSTR, text: &str, style: u32, x: i32, y: i32, ww: i32, hh: i32, id: isize, ex: u32| {
            let t = wide(text);
            let c = CreateWindowExW(WINDOW_EX_STYLE(ex), class, pw(&t), WINDOW_STYLE(WS_CHILD.0 | WS_VISIBLE.0 | style), x, y, ww, hh, Some(dlg), Some(HMENU(id as *mut _)), Some(hi.into()), None).unwrap_or_default();
            SendMessageW(c, WM_SETFONT, Some(WPARAM(font.0 as usize)), Some(LPARAM(1)));
            c
        };
        let u = |v: f32| (v * s) as i32;
        mk(w!("STATIC"), label, 0, u(14.0), u(12.0), u(320.0), u(18.0), 0, 0);
        let edit = mk(w!("EDIT"), initial, ES_AUTOHSCROLL as u32 | WS_TABSTOP.0, u(14.0), u(34.0), u(318.0), u(24.0), 3, 0x200);
        mk(w!("BUTTON"), "OK", BS_DEFPUSHBUTTON as u32 | WS_TABSTOP.0, u(170.0), u(74.0), u(76.0), u(26.0), 1, 0);
        mk(w!("BUTTON"), "Cancel", BS_PUSHBUTTON as u32 | WS_TABSTOP.0, u(256.0), u(74.0), u(76.0), u(26.0), 2, 0);
        PROMPT_EDIT = edit;
        PROMPT_DONE = 0;
        let _ = SetForegroundWindow(dlg);
        let _ = SetFocus(Some(edit));
        let mut msg = MSG::default();
        while PROMPT_DONE == 0 && GetMessageW(&mut msg, None, 0, 0).as_bool() {
            if msg.message == WM_KEYDOWN && msg.wParam.0 as u16 == VK_RETURN.0 {
                PROMPT_DONE = 1;
                break;
            }
            if msg.message == WM_KEYDOWN && msg.wParam.0 as u16 == VK_ESCAPE.0 {
                PROMPT_DONE = 2;
                break;
            }
            if !IsDialogMessageW(dlg, &msg).as_bool() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        let mut buf = [0u16; 512];
        let n = GetWindowTextW(edit, &mut buf);
        let ok = PROMPT_DONE == 1;
        let _ = DestroyWindow(dlg);
        if ok { Some(String::from_utf16_lossy(&buf[..n.max(0) as usize])) } else { None }
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let st = &mut app().set;
    match m {
        WM_MOUSEWHEEL => {
            st.wheel(((wp.0 >> 16) & 0xFFFF) as i16 as i32);
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            let (x, y) = win::lparam_xy(lp);
            let s = app().scale;
            if let Some((act, rx, ry, rw, rh)) = st.hit(x as f32, y as f32) {
                if matches!(act, Act::Size | Act::Magnify) {
                    st.drag = Some(act);
                    SetCapture(h);
                    st.apply(act, x as f32, (rx, ry, rw, rh), false);
                } else {
                    st.apply(act, x as f32, (rx, ry, rw, rh), true);
                }
            } else if (y as f32) < 28.0 * s + 40.0 * s {
                // title strip: drag the window
                let _ = ReleaseCapture();
                SendMessageW(h, WM_NCLBUTTONDOWN, Some(WPARAM(2)), Some(LPARAM(0)));
                let mut r = RECT::default();
                let _ = GetWindowRect(h, &mut r);
                st.pos = (r.left, r.top);
            }
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            if let Some(act) = st.drag {
                let (x, _) = win::lparam_xy(lp);
                if let Some(r) = st.hits.iter().find(|h| h.4 == act).map(|h| (h.0, h.1, h.2, h.3)) {
                    st.apply(act, x as f32, r, false);
                }
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if let Some(act) = st.drag.take() {
                let _ = ReleaseCapture();
                let (x, _) = win::lparam_xy(lp);
                if let Some(r) = st.hits.iter().find(|h| h.4 == act).map(|h| (h.0, h.1, h.2, h.3)) {
                    st.apply(act, x as f32, r, true);
                }
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == VK_ESCAPE.0 {
                st.close();
            }
            LRESULT(0)
        }
        WM_CLOSE => {
            st.close();
            LRESULT(0)
        }
        WM_ACTIVATE => {
            if st.visible { st.redraw_look(); }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

fn pick_default(key: &str) -> &'static str {
    match key {
        "genie-style" => "genie",
        "stats-net-mode" => "both",
        "bar-spacing" => "normal",
        "glass-style" => "liquid",
        "glass-opacity" => "normal",
        "icon-style" => "default",
        "accent" => "blue",
        "dock-spacing" | "dock-round" => "normal",
        "island-water-every" => "60",
        "island-water-goal" => "8",
        "island-stand-every" => "45",
        "island-pomo-focus" => "25",
        "island-pomo-break" => "5",
        _ => "",
    }
}
fn pick_opts(key: &str) -> &'static [(&'static str, &'static str)] {
    match key {
        "genie-style" => &[("genie", "Genie"), ("scale", "Scale"), ("fade", "Fade")],
        "stats-net-mode" => &[("both", "Download and upload"), ("down", "Download only"), ("up", "Upload only")],
        "glass-style" => &[("liquid", "Liquid Glass"), ("clear", "Clear glass"), ("classic", "Classic")],
        "glass-opacity" => &[("low", "Clear"), ("normal", "Normal"), ("high", "Frosted")],
        "icon-style" => &[("default", "Default"), ("dark", "Dark"), ("clear", "Clear"), ("tinted", "Tinted")],
        "accent" => &[("blue", "Blue"), ("purple", "Purple"), ("pink", "Pink"), ("red", "Red"), ("orange", "Orange"), ("yellow", "Yellow"), ("green", "Green"), ("graphite", "Graphite")],
        "dock-spacing" => &[("compact", "Compact"), ("normal", "Normal"), ("roomy", "Roomy")],
        "dock-round" => &[("square", "Square"), ("soft", "Soft"), ("normal", "Normal"), ("round", "Round")],
        "bar-spacing" => &[("normal", "Normal"), ("compact", "Compact"), ("roomy", "Roomy"), ("wide", "Wide")],
        "island-water-every" => &[("30", "30 min"), ("45", "45 min"), ("60", "60 min"), ("90", "90 min"), ("120", "2 hours")],
        "island-water-goal" => &[("4", "4 glasses"), ("6", "6 glasses"), ("8", "8 glasses"), ("10", "10 glasses"), ("12", "12 glasses")],
        "island-stand-every" => &[("30", "30 min"), ("45", "45 min"), ("60", "60 min"), ("90", "90 min")],
        "island-pomo-focus" => &[("15", "15 min"), ("20", "20 min"), ("25", "25 min"), ("30", "30 min"), ("45", "45 min"), ("60", "60 min")],
        "island-pomo-break" => &[("3", "3 min"), ("5", "5 min"), ("10", "10 min"), ("15", "15 min")],
        _ => &[],
    }
}

/// Side effects after an option changed in Settings.
fn option_changed(key: &str) {
    let a = app();
    a.cfg.reload();
    if key.starts_with("bar-") || key.starts_with("stats-") || key.starts_with("tray-") {
        a.bar.relayout(a.scale);
    }
    if key.starts_with("dock-") || key.starts_with("glass-") || key == "icon-style" || key == "accent" {
        a.dock.relayout_all(a.scale, &a.cfg);
        a.dock.apply_theme();
        a.set.redraw_look();
        if key == "icon-style" || key == "accent" {
            a.icons.clear();
            crate::refresh_windows();
        }
    }
    match key {
        "island" => a.island.set_enabled(a.cfg.s.island_on),
        "show-dock" => a.dock.set_disabled(!a.cfg.ob("show-dock", true)),
        "show-start-icon" | "minimize-to-dock" => crate::refresh_windows(),
        "windowtheme" => {
            crate::sys::reset_window_theme();
            crate::refresh_windows();
        }
        _ => {}
    }
    a.island.dirty();
}

impl SettingsWin {
    pub fn redraw_look(&mut self) {
        if self.visible { self.draw(); }
    }
    pub fn wheel(&mut self, delta: i32) {
        let s = app().scale;
        let view = self.content_range.1 - self.content_range.0;
        let max = (self.content_h - view).max(0.0);
        self.scroll = (self.scroll - delta as f32 / 120.0 * 48.0 * s).clamp(0.0, max);
        self.draw();
    }
}
