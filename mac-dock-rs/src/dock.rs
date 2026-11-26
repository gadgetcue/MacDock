//! The dock: pill (acrylic window) + icon layer (per-pixel alpha). Magnification springs, running dots,
//! launch bounce, drag-reorder, drag-out to unpin, file drops, context menus, hover labels, auto-hide.
use crate::config::*;
use crate::gfx::*;
use crate::icons::*;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use std::rc::Rc;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::Shell::*;

const ICON_FILTER: &str = "Icons and images\0*.ico;*.png;*.jpg;*.jpeg;*.bmp\0All files\0*.*\0\0";

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Kind {
    App,
    Folder,
    File,
    Url,
    Special,
    Trash,
    Start,
    Launchpad,
    Running, // unpinned running app
    Minimized, // a minimized window in its own tile
    Split,     // windows snapped side by side, one combined tile
}

pub struct Item {
    pub key: String,
    pub target: String,
    pub label: String,
    pub kind: Kind,
    pub section: u8,
    pub sep_before: bool,
    pub pinned: bool,
    pub exe: String,    // lower-case exe path to match windows (may be empty)
    pub suffix: String, // lower-case "\folder\app.exe" suffix match
    pub pkg: String,    // lower-case package/name prefix match
    pub scale: Spring,
    pub appear: Spring,
    pub off: Spring, // reorder slide offset (px)
    pub leaving: bool,
    pub bounce: f32, // seconds since launch, <0 = idle
    pub launching: bool,
    pub launch_at: u64,
    pub wins: Vec<isize>,
    pub x: f32,
    pub sz: f32,
    pub img_key: String,
    pub icon: Option<Rc<Img>>,
    pub minimized_only: bool,
    pub ready: bool,
}

pub struct Drag {
    pub idx_key: String,
    pub x: f32,
    pub grab: f32,
    pub out: bool,
}

pub struct Dock {
    pub hwnd: HWND,
    pub glass: Option<win::Glass>,
    pub tip: HWND,
    pub items: Vec<Item>,
    gfx: Option<Gfx>,
    s: f32,
    base: f32,
    gap: f32,
    pad_x: f32,
    pad_y: f32,
    margin: f32,
    group_gap: f32,
    glass_style: u8,
    glass_mul: f32,
    round_k: f32,
    show_ind: bool,
    show_div: bool,
    max_scale: f32,
    win_w: i32,
    win_h: i32,
    scr: RECT,
    pub mouse_x: f32,
    anchor_x: f32,
    smooth_x: f32,
    mouse_y: f32,
    inside: bool,
    hover: i32,
    glass_cache: Option<((i32, i32, i32, bool, u8), Img)>,
    glass_t: std::time::Instant,
    glass_ok: bool,
    glass_pending: bool,
    draw_ms: (f64, u32),
    seg_ms: (f64, f64, f64),
    hide: Spring,
    fullscreen: bool,
    press: Option<(i32, f32, f32)>,
    drag: Option<Drag>,
    timer_on: bool,
    last: std::time::Instant,
    need_draw: bool,
    pill_rect: (f32, f32, f32, f32),
    lnk_cache: std::collections::HashMap<String, String>,
    tip_text: String,
    tip_size: (i32, i32),
    tip_pos: (i32, i32),
    pub shown: bool,
    pub lift: f32,
    disabled: bool,
    fan_pending: Option<String>,
    logged: bool,
    logged_n: i32,
    anchors: Vec<(String, f32, f32)>, // key, screen x center, screen y top (for genie targets)
}

pub fn classify(t: &str) -> Kind {
    let l = t.to_lowercase();
    if l == "[start]" {
        return Kind::Start;
    }
    if l == "[apps]" {
        return Kind::Launchpad;
    }
    if l.contains("recyclebin") || l == "trash" || l.starts_with("shell:::{645ff040") {
        return Kind::Trash;
    }
    if l.starts_with("ms-settings:") || l.starts_with("ms-") {
        return Kind::Special;
    }
    if l.starts_with("http://") || l.starts_with("https://") {
        return Kind::Url;
    }
    if l.starts_with("shell:appsfolder") {
        return Kind::App;
    }
    if l.starts_with("shell:") {
        return Kind::Folder;
    }
    let p = std::path::Path::new(t);
    if p.is_dir() {
        return Kind::Folder;
    }
    if l.ends_with(".exe") || l.ends_with(".lnk") || l.ends_with(".bat") || l.ends_with(".cmd") || l.ends_with(".url") || l.ends_with(".msc") {
        return Kind::App;
    }
    if p.exists() {
        return Kind::File;
    }
    Kind::App
}

fn known_folder(guid: &str) -> Option<&'static str> {
    match guid.to_uppercase().as_str() {
        "{6D809377-6AF0-444B-8957-A3773F02200E}" | "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}" => Some("program files"),
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" => Some("system32"),
        _ => None,
    }
}

fn label_of(e: &Entry, cfg: &Config) -> String {
    if let Some(n) = cfg.names.get(&e.target.to_lowercase()) {
        return n.clone();
    }
    if let Some(l) = &e.label {
        return l.clone();
    }
    nice_name(&e.target)
}
pub fn nice_name(t: &str) -> String {
    let l = t.to_lowercase();
    if l == "[start]" {
        return "Start".into();
    }
    if l == "[apps]" {
        return "Launchpad".into();
    }
    if l.contains("recyclebin") {
        return "Trash".into();
    }
    if l.starts_with("ms-settings") {
        return "Settings".into();
    }
    if l.starts_with("http") {
        let h = t.split("//").nth(1).unwrap_or(t).split('/').next().unwrap_or(t);
        return h.trim_start_matches("www.").to_string();
    }
    let base = t.rsplit(|c| c == '\\' || c == '/' || c == '!').next().unwrap_or(t);
    let stem = base.rsplit_once('.').map(|(a, b)| if b.len() <= 4 { a } else { base }).unwrap_or(base);
    let mut s: Vec<char> = stem.chars().collect();
    if let Some(c) = s.first_mut() {
        *c = c.to_uppercase().next().unwrap_or(*c);
    }
    s.into_iter().collect()
}

impl Dock {
    pub fn new() -> Dock {
        Dock {
            hwnd: HWND::default(),
            glass: None,
            tip: HWND::default(),
            items: vec![],
            gfx: None,
            s: 1.0,
            base: 48.0,
            gap: 6.0,
            pad_x: 12.0,
            pad_y: 8.0,
            margin: 8.0,
            group_gap: 14.0,
            glass_style: 0,
            glass_mul: 1.0,
            round_k: 0.30,
            show_ind: true,
            show_div: true,
            max_scale: 1.75,
            win_w: 0,
            win_h: 0,
            scr: RECT::default(),
            mouse_x: -1.0,
            anchor_x: -1.0,
            smooth_x: f32::NAN,
            mouse_y: -1.0,
            inside: false,
            hover: -1,
            glass_cache: None,
            glass_t: std::time::Instant::now(),
            glass_ok: false,
            glass_pending: false,
            draw_ms: (0.0, 0),
            seg_ms: (0.0, 0.0, 0.0),
            hide: Spring::new(0.0),
            fullscreen: false,
            press: None,
            drag: None,
            timer_on: false,
            last: std::time::Instant::now(),
            need_draw: true,
            pill_rect: (0.0, 0.0, 0.0, 0.0),
            lnk_cache: Default::default(),
            tip_text: String::new(),
            tip_size: (0, 0),
            tip_pos: (0, 0),
            shown: true,
            logged: false,
            disabled: false,
            fan_pending: None,
            logged_n: 0,
            lift: std::env::args().skip_while(|a| a != "--lift").nth(1).and_then(|v| v.parse().ok()).unwrap_or(0.0),
            anchors: vec![],
        }
    }

    pub fn create(&mut self, scale: f32, cfg: &Config) {
        self.glass = Some(win::Glass::new(0x5A1E1E1E));
        self.hwnd = win::create(ID_DOCK, true, true, true, false, 0, 0, 10, 10);
        self.tip = win::create(ID_TIP, true, true, true, true, 0, 0, 10, 10);
        unsafe {
            DragAcceptFiles(self.hwnd, true);
        }
        self.relayout_all(scale, cfg);
        self.apply_theme();
        win::show_noactivate(self.hwnd);
    }

    pub fn dirty(&mut self) {
        self.need_draw = true;
        self.start_timer();
    }
    fn start_timer(&mut self) {
        if !self.timer_on && !self.hwnd.is_invalid() {
            self.timer_on = true;
            self.last = std::time::Instant::now();
            unsafe {
                crate::clock::start(crate::clock::bit(T_DOCK));
            }
        }
    }

    pub fn relayout_all(&mut self, scale: f32, cfg: &Config) {
        self.s = scale;
        self.base = cfg.s.size as f32 * scale;
        self.max_scale = cfg.s.magnify;
        crate::gfx::set_accent(crate::gfx::accent_for(&cfg.os("accent", "blue")));
        crate::gfx::set_icon_style(&cfg.os("icon-style", "default"));
        self.glass_style = match cfg.os("glass-style", "liquid").as_str() { "classic" => 1, "clear" => 2, _ => 0 };
        self.glass_mul = match cfg.os("glass-opacity", "normal").as_str() { "low" => 0.55, "high" => 1.6, _ => 1.0 };
        self.round_k = match cfg.os("dock-round", "normal").as_str() { "square" => 0.10, "soft" => 0.20, "round" => 0.42, _ => 0.30 };
        self.show_ind = cfg.ob("dock-indicator", true);
        self.show_div = cfg.ob("dock-divider", true);
        let sp = match cfg.os("dock-spacing", "normal").as_str() { "compact" => 0.4, "roomy" => 1.8, _ => 1.0 };
        self.gap = 6.0 * scale * sp;
        self.pad_x = 12.0 * scale;
        self.pad_y = 8.0 * scale;
        self.margin = 8.0 * scale;
        self.group_gap = 14.0 * scale;
        self.scr = screen_rect();
        let a = app();
        a.icons.px = 256;
        crate::gfx::SMALL_PX.store(((self.base * self.max_scale).ceil() as i32 + 6).clamp(48, 220), std::sync::atomic::Ordering::Relaxed);
        self.resize_window();
        self.dirty();
    }

    fn resize_window(&mut self) {
        let n = self.items.iter().filter(|i| !i.leaving).count().max(1) as f32;
        let sw = (self.scr.right - self.scr.left) as f32;
        let w0 = n * (self.base + self.gap) + 2.0 * self.pad_x + 40.0 * self.s;
        let w = (w0 + 6.0 * (self.max_scale - 1.0) * self.base + 80.0 * self.s).min(sw);
        let pill_h = self.base + 2.0 * self.pad_y;
        let h = self.margin + pill_h + (self.max_scale - 1.0) * self.base + self.base * 0.62 + 10.0 * self.s;
        let (wi, hi) = (w.ceil() as i32, h.ceil() as i32);
        if wi != self.win_w || hi != self.win_h || self.gfx.is_none() {
            self.win_w = wi;
            self.win_h = hi;
            self.gfx = Gfx::new(wi, hi);
        }
    }

    // ---------- model ----------
    fn exe_of_entry(&mut self, t: &str) -> (String, String, String) {
        let l = t.to_lowercase();
        if l.starts_with("shell:appsfolder\\") {
            let rest = &t[17..];
            if rest.to_lowercase().contains("_crx_") {
                return (String::new(), String::new(), format!("aumid:{}", rest.to_lowercase()));
            }
            if rest.starts_with('{') {
                if let Some(i) = rest.find('}') {
                    let tail = rest[i + 1..].trim_start_matches('\\').to_lowercase();
                    let _ = known_folder(&rest[..=i]);
                    if !tail.is_empty() {
                        return (String::new(), format!("\\{}", tail), String::new());
                    }
                }
            }
            if rest.contains(":\\") {
                return (rest.to_lowercase(), String::new(), String::new());
            }
            let (pkg, app_) = rest.split_once('!').map(|(a, b)| (a, b)).unwrap_or((rest, ""));
            let pfx = pkg.split('_').next().unwrap_or(pkg).to_lowercase();
            let _ = app_;
            if pkg.contains('_') {
                return (String::new(), String::new(), format!("\\{}_", pfx));
            }
            // plain names: Brave, FreeDownloadManager, Microsoft.Windows.Explorer ...
            let n = pkg.rsplit('.').next().unwrap_or(pkg).to_lowercase();
            return (String::new(), format!("\\{}.exe", n), String::new());
        }
        if l.ends_with(".lnk") {
            let r = self.lnk_cache.entry(l.clone()).or_insert_with(|| resolve_lnk_target(t).unwrap_or_default()).clone();
            if !r.is_empty() {
                return (r.to_lowercase(), String::new(), String::new());
            }
        }
        if l.ends_with(".exe") {
            return (l, String::new(), String::new());
        }
        (String::new(), String::new(), String::new())
    }

    fn matches(it: &Item, w: &Win) -> bool {
        pin_matches(&it.exe, &it.suffix, &it.pkg, w)
    }

    pub fn on_windows(&mut self, wins: &[Win], cfg: &mut Config, icons: &mut Icons, trash_full: bool, _fg: isize) {
        icons.tints = cfg.folder_colors.clone();
        // 1. desired item list: pins then unpinned running apps (before the right section) then trash
        struct Want {
            key: String,
            e: Entry,
            kind: Kind,
            pinned: bool,
            running_exe: String,
        }
        let mut want: Vec<Want> = vec![];
        for e in cfg.entries.iter() {
            let kind = classify(&e.target);
            if kind == Kind::Start && !cfg.ob("show-start-icon", true) {
                continue;
            }
            want.push(Want { key: e.target.to_lowercase(), e: e.clone(), kind, pinned: true, running_exe: String::new() });
        }
        // running-but-unpinned apps
        let mut seen_extra: Vec<String> = vec![];
        let pinned_items: Vec<(String, String, String)> = want.iter().filter(|w| matches!(w.kind, Kind::App)).map(|w| self.exe_of_entry(&w.e.target)).collect();
        let mut seen_pwa: Vec<(String, String, String)> = vec![];
        for w in wins {
            let hit = pinned_items.iter().any(|(e, s, p)| pin_matches(e, s, p, w));
            let pwa = w.aumid.contains("_crx_");
            if !hit {
                if pwa {
                    if !seen_pwa.iter().any(|x| x.1 == w.aumid) {
                        seen_pwa.push((w.exe.clone(), w.aumid.clone(), w.title.clone()));
                    }
                } else if !seen_extra.contains(&w.exe) {
                    seen_extra.push(w.exe.clone());
                }
            }
        }
        let first_right = want.iter().position(|w| w.e.section != 0).unwrap_or(want.len());
        let mut extras: Vec<Want> = vec![];
        for exe in &seen_extra {
            let e = Entry { section: 1, label: None, target: exe.clone(), sep_before: false };
            extras.push(Want { key: format!("run:{}", exe), e, kind: Kind::Running, pinned: false, running_exe: exe.clone() });
        }
        for (exe, aumid, title) in &seen_pwa {
            let e = Entry { section: 1, label: Some(title.clone()), target: exe.clone(), sep_before: false };
            extras.push(Want { key: format!("run:{}|{}", exe, aumid), e, kind: Kind::Running, pinned: false, running_exe: exe.clone() });
        }
        {
            let vis: Vec<isize> = wins.iter().filter(|w| !w.minimized).map(|w| w.hwnd).collect();
            for grp in crate::snap::find(&vis) {
                let ids: Vec<String> = grp.iter().map(|h| h.to_string()).collect();
                let key = format!("split:{}", ids.join(","));
                let e = Entry { section: 1, label: None, target: key.clone(), sep_before: false };
                extras.push(Want { key, e, kind: Kind::Split, pinned: false, running_exe: String::new() });
            }
        }
        let mut ordered: Vec<Want> = vec![];
        for (i, w) in want.into_iter().enumerate() {
            if i == first_right {
                for x in extras.drain(..) {
                    ordered.push(x);
                }
            }
            ordered.push(w);
        }
        ordered.extend(extras);
        if cfg.ob("minimize-to-dock", true) {
            for w in wins.iter().filter(|w| w.minimized) {
                let e = Entry { section: 4, label: Some(w.title.clone()), target: w.exe.clone(), sep_before: false };
                ordered.push(Want { key: format!("min:{}", w.hwnd), e, kind: Kind::Minimized, pinned: false, running_exe: w.exe.clone() });
            }
        }
        ordered.push(Want { key: "trash".into(), e: Entry { section: 3, label: Some("Trash".into()), target: "shell:RecycleBinFolder".into(), sep_before: false }, kind: Kind::Trash, pinned: false, running_exe: String::new() });
        // drop a duplicate trash if user pinned one
        let mut trash_seen = false;
        ordered.retain(|w| {
            if w.kind == Kind::Trash {
                if trash_seen {
                    return false;
                }
                trash_seen = true;
            }
            true
        });

        // 2. merge into self.items keeping animation state
        let mut old: Vec<Item> = std::mem::take(&mut self.items);
        let mut out: Vec<Item> = vec![];
        let mut order_changed = false;
        for (idx, w) in ordered.iter().enumerate() {
            let pos = old.iter().position(|o| o.key == w.key && !o.leaving);
            let mut it = match pos {
                Some(p) => old.remove(p),
                None => {
                    let pkgv = if w.kind == Kind::Running { w.key.split_once('|').map(|(_, a)| format!("aumid:{}", a)).unwrap_or_default() } else { String::new() };
                    let (exe, suffix, pkg) = if w.kind == Kind::Running || w.kind == Kind::Minimized { (w.running_exe.clone(), String::new(), pkgv) } else { self.exe_of_entry(&w.e.target) };
                    order_changed = true;
                    let mut sp = Spring::new(0.0);
                    sp.goal = 1.0;
                    Item {
                        key: w.key.clone(),
                        target: w.e.target.clone(),
                        label: String::new(),
                        kind: w.kind,
                        section: w.e.section,
                        sep_before: false,
                        pinned: w.pinned,
                        exe,
                        suffix,
                        pkg,
                        scale: Spring::new(1.0),
                        appear: sp,
                        off: Spring::new(0.0),
                        leaving: false,
                        bounce: -1.0,
                        launching: false,
                        launch_at: 0,
                        wins: vec![],
                        x: 0.0,
                        sz: 0.0,
                        img_key: String::new(),
                        icon: None,
                        minimized_only: false,
                        ready: false,
                    }
                }
            };
            let _ = idx;
            it.label = if w.kind == Kind::Split {
                let names: Vec<String> = w.key["split:".len()..].split(',').filter_map(|h| h.parse::<isize>().ok()).filter_map(|h| wins.iter().find(|x| x.hwnd == h)).map(|x| cfg.names.get(&x.exe.to_lowercase()).cloned().unwrap_or_else(|| nice_name(&x.exe))).collect();
                names.join(" + ")
            } else if w.kind == Kind::Minimized {
                w.e.label.clone().unwrap_or_default()
            } else if w.kind == Kind::Running {
                // window title of the first window is a poor app name; use the exe stem
                w.e.label.clone().or_else(|| cfg.names.get(&w.e.target.to_lowercase()).cloned()).unwrap_or_else(|| nice_name(&w.e.target))
            } else {
                label_of(&w.e, cfg)
            };
            it.section = w.e.section;
            it.sep_before = w.e.sep_before;
            it.pinned = w.pinned;
            it.kind = w.kind;
            out.push(it);
        }
        // items that vanished: shrink away (only unpinned running / removed pins)
        for mut o in old {
            if !o.leaving {
                o.leaving = true;
                o.appear.goal = 0.0;
                order_changed = true;
            }
            if o.appear.v > 0.02 {
                // keep at its former spot (insert before the trash item)
                let at = out.len().saturating_sub(1);
                out.insert(at, o);
            }
        }
        self.items = out;

        // 3. running state, windows, icons
        for it in self.items.iter_mut() {
            it.wins.clear();
            it.minimized_only = false;
            if it.leaving {
                continue;
            }
            if it.kind == Kind::Split {
                it.wins = it.key["split:".len()..].split(',').filter_map(|h| h.parse::<isize>().ok()).collect();
                continue;
            }
            if it.kind == Kind::Minimized {
                it.wins = it.key.strip_prefix("min:").and_then(|h| h.parse::<isize>().ok()).into_iter().collect();
            }
            if matches!(it.kind, Kind::App | Kind::Running) {
                let mut all_min = true;
                for w in wins.iter() {
                    if Self::matches(it, w) {
                        it.wins.push(w.hwnd);
                        if !w.minimized {
                            all_min = false;
                        }
                    }
                }
                it.minimized_only = !it.wins.is_empty() && all_min;
            }
            if it.kind == Kind::Folder && it.target.to_lowercase().contains("explorer") {
                it.wins.clear();
            }
            if !it.wins.is_empty() && it.launching {
                it.launching = false;
            }
            let key = match it.kind {
                Kind::Trash => format!("trash:{}", trash_full),
                Kind::Start => "start".into(),
                Kind::Launchpad => "pack:apps".into(),
                Kind::Running | Kind::Minimized => format!("exe:{}{}", it.exe, it.pkg),
                _ => format!("t:{}", it.target.to_lowercase()),
            };
            if it.img_key != key || it.icon.is_none() || !it.ready {
                if it.img_key != key { it.ready = false; }
                it.img_key = key.clone();
                it.icon = match it.kind {
                    Kind::Start => { it.ready = true; icons.get_or_logo("start", "", None) }
                    _ => {
                        let pack = if it.kind == Kind::Launchpad {
                            icons.pack.get("apps").or(icons.pack.get("launchpad")).cloned()
                        } else if it.kind == Kind::Trash {
                            None
                        } else {
                            let tg = if it.kind == Kind::Running { if let Some(a) = it.pkg.strip_prefix("aumid:") { format!("shell:AppsFolder{}{}", char::from(92u8), a) } else { it.exe.clone() } } else { it.target.clone() };
                            let p = pack_for(icons, cfg, &tg, &it.label);
                            if it.kind == Kind::Url && cfg.ob("site-icons", true) {
                                if let Some(h) = crate::icons::site_host(&it.target) {
                                    if !icons.pack.contains_key(&format!("site-{}", h)) && icons.mac_tried.insert(format!("site:{}", h)) {
                                        crate::icons::fetch_site_icon(it.target.clone(), crate::app().msg.0 as isize);
                                    }
                                }
                            }
                            if p.is_none() && cfg.s.icons_on && matches!(it.kind, Kind::App) && !it.label.is_empty() {
                                let key = if cfg.s.macosicons_key.is_empty() { std::env::var("MACOSICONS_API_KEY").unwrap_or_default() } else { cfg.s.macosicons_key.clone() };
                                if !key.is_empty() && icons.mac_tried.insert(it.label.to_lowercase()) {
                                    crate::icons::fetch_macos_icon(it.label.clone(), key, crate::app().msg.0 as isize);
                                }
                            }
                            p
                        };
                        let tg = if it.kind == Kind::Running { if let Some(a) = it.pkg.strip_prefix("aumid:") { format!("shell:AppsFolder{}{}", char::from(92u8), a) } else { it.exe.clone() } } else { it.target.clone() };
                        // placeholder = previously cached or logo until the worker answers
                        let first = icons.get(&key, &tg, pack.clone());
                        if first.is_some() && icons.failed.contains(&key) && it.kind == Kind::App {
                            // the pinned path no longer exists (app moved/updated): use the installed app of the same name
                            let lab = it.label.to_lowercase();
                            match crate::app().lp.index.find_by_name(&lab) {
                                Some(target) => {
                                    let k2 = format!("t:{}", target.to_lowercase());
                                    match icons.get(&k2, &target, None) { Some(i) => { it.ready = true; Some(i) } None => { it.ready = false; icons.logo.clone() } }
                                }
                                None => { it.ready = false; first } // index not loaded yet: try again on the next refresh
                            }
                        } else {
                            match first { Some(i) => { it.ready = true; Some(i) } None => { it.ready = false; icons.logo.clone() } }
                        }
                    }
                };
            }
        }
        // snap-group tiles: compose the members' icons once they are all ready
        for si in 0..self.items.len() {
            if self.items[si].kind != Kind::Split || self.items[si].leaving || (self.items[si].ready && self.items[si].icon.is_some()) {
                continue;
            }
            let mut imgs: Vec<Rc<Img>> = vec![];
            for h in self.items[si].wins.clone() {
                if let Some(m) = self.items.iter().find(|m| matches!(m.kind, Kind::App | Kind::Running) && m.ready && m.wins.contains(&h)) {
                    if let Some(i) = &m.icon {
                        imgs.push(i.clone());
                    }
                }
            }
            if imgs.len() >= 2 {
                let refs: Vec<&Img> = imgs.iter().map(|i| &**i).collect();
                if let Some(c) = crate::snap::compose(&refs) {
                    self.items[si].icon = Some(Rc::new(c));
                    self.items[si].ready = true;
                }
            } else if self.items[si].icon.is_none() {
                self.items[si].icon = icons.logo.clone();
            }
        }
        self.resize_window();
        let _ = order_changed;
        self.dirty();
    }

    pub fn check_trash(&mut self, full: &mut bool) {
        unsafe {
            let mut info = SHQUERYRBINFO { cbSize: std::mem::size_of::<SHQUERYRBINFO>() as u32, ..Default::default() };
            if SHQueryRecycleBinW(windows::core::PCWSTR::null(), &mut info).is_ok() {
                let f = info.i64NumItems > 0;
                if f != *full {
                    *full = f;
                    let a = app();
                    refresh_windows_soft(a);
                }
            }
        }
    }

    pub fn set_fullscreen(&mut self, fs: bool) {
        self.fullscreen = fs;
        if fs {
            win::hide(self.hwnd);
            if let Some(g) = self.glass.as_mut() { g.hide(); }
            self.shown = false;
        } else if !self.disabled {
            win::show_noactivate(self.hwnd);
            self.shown = true;
            self.dirty();
        }
    }

    pub fn anchor_for(&self, exe_or_hwnd: &dyn Fn(&Item) -> bool) -> Option<(f32, f32, f32)> {
        // screen-space (center x, top y, size) of an item for genie targets
        let it = self.items.iter().find(|i| exe_or_hwnd(i))?;
        Some((self.win_x() as f32 + it.x + it.sz / 2.0, self.win_y() as f32 + self.icon_top(it), it.sz))
    }

    // ---------- geometry ----------
    fn win_x(&self) -> i32 {
        self.scr.left + ((self.scr.right - self.scr.left) - self.win_w) / 2
    }
    fn win_y(&self) -> i32 {
        let hide_off = self.hide.v * (self.pill_h() + self.margin + 6.0 * self.s);
        self.scr.bottom - self.win_h + hide_off as i32 - self.lift as i32
    }
    /// Height of the strip the dock reserves at the bottom of its monitor.
    pub fn strip_h(&self) -> i32 {
        (self.pill_h() + self.margin) as i32 + 2
    }
    pub fn monitor(&self) -> RECT {
        self.scr
    }
    fn pill_h(&self) -> f32 {
        self.base + 2.0 * self.pad_y
    }
    fn icon_bottom(&self) -> f32 {
        self.win_h as f32 - self.margin - self.pad_y
    }
    fn icon_top(&self, it: &Item) -> f32 {
        self.icon_bottom() - it.sz - self.bounce_h(it)
    }
    fn bounce_h(&self, it: &Item) -> f32 {
        if it.bounce < 0.0 {
            return 0.0;
        }
        // three decaying hops while launching, one when activated
        let t = it.bounce;
        let period = 0.62;
        let phase = (t % period) / period;
        let decay = if it.launching { 1.0 } else { (1.0 - t / 1.4).max(0.0) };
        (phase * std::f32::consts::PI).sin().abs() * self.base * 0.55 * decay
    }

    fn layout(&mut self) {
        let mut total = 0.0;
        let mut prev_sec: Option<u8> = None;
        let mut first = true;
        let (base, gap, gg) = (self.base, self.gap, self.group_gap);
        let mut sizes: Vec<f32> = vec![];
        for it in self.items.iter() {
            let sz = base * it.scale.v.max(0.2) * it.appear.v.clamp(0.0, 1.0);
            sizes.push(sz);
            if !first {
                total += gap * it.appear.v.clamp(0.0, 1.0);
                if prev_sec.map(|p| p != it.section).unwrap_or(false) || it.sep_before {
                    total += gg * it.appear.v.clamp(0.0, 1.0);
                }
            }
            total += sz;
            prev_sec = Some(it.section);
            first = false;
        }
        let pill_w = (total + 2.0 * self.pad_x).max(base);
        let left = (self.win_w as f32 - pill_w) / 2.0;
        let mut x = left + self.pad_x;
        let mut prev: Option<u8> = None;
        let mut first = true;
        for (i, it) in self.items.iter_mut().enumerate() {
            if !first {
                x += gap * it.appear.v.clamp(0.0, 1.0);
                if prev.map(|p| p != it.section).unwrap_or(false) || it.sep_before {
                    x += gg * it.appear.v.clamp(0.0, 1.0);
                }
            }
            it.x = x + it.off.v;
            it.sz = sizes[i];
            x += sizes[i];
            prev = Some(it.section);
            first = false;
        }
        // Anchor the magnified row so the point under the cursor stays under the cursor (as in macOS): without this the
        // row slides as its total width changes, the icon under the pointer changes, and the scales feed back (jiggle).
        let mut shift = 0.0f32;
        if self.max_scale > 1.001 && self.anchor_x >= 0.0 && !self.items.is_empty() {
            let k = self.items.iter().map(|i| i.scale.v - 1.0).fold(0.0f32, f32::max) / (self.max_scale - 1.0);
            let k = k.clamp(0.0, 1.0);
            if k > 0.001 {
                let bc = self.base_centers();
                let cur: Vec<f32> = self.items.iter().map(|i| i.x + i.sz / 2.0).collect();
                let m = self.anchor_x;
                let mapped = if m <= bc[0] {
                    cur[0] - (bc[0] - m)
                } else if m >= bc[bc.len() - 1] {
                    cur[cur.len() - 1] + (m - bc[bc.len() - 1])
                } else {
                    let j = bc.windows(2).position(|w| m >= w[0] && m <= w[1]).unwrap_or(0);
                    let t = (m - bc[j]) / (bc[j + 1] - bc[j]).max(0.001);
                    cur[j] + (cur[j + 1] - cur[j]) * t
                };
                shift = (m - mapped) * k;
                for it in self.items.iter_mut() {
                    it.x += shift;
                }
            }
        }
        let left = left + shift;
        self.pill_rect = (left, self.win_h as f32 - self.margin - self.pill_h(), pill_w, self.pill_h());
    }

    /// Base (unmagnified) centers used for magnification distance, so the effect does not feed back on itself.
    fn base_centers(&self) -> Vec<f32> {
        let mut v = vec![];
        let mut total = 0.0;
        let mut prev: Option<u8> = None;
        let mut first = true;
        let step: Vec<f32> = self
            .items
            .iter()
            .map(|it| {
                let a = it.appear.v.clamp(0.0, 1.0);
                let mut s = 0.0;
                if !first {
                    s += self.gap * a;
                    if prev.map(|p| p != it.section).unwrap_or(false) || it.sep_before {
                        s += self.group_gap * a;
                    }
                }
                prev = Some(it.section);
                first = false;
                s + self.base * a
            })
            .collect();
        for s in &step {
            total += s;
        }
        let left = (self.win_w as f32 - (total + 2.0 * self.pad_x)) / 2.0 + self.pad_x;
        let mut x = left;
        for (i, s) in step.iter().enumerate() {
            let a = self.items[i].appear.v.clamp(0.0, 1.0);
            let w = self.base * a;
            v.push(x + (s - w) + w / 2.0);
            x += s;
        }
        v
    }

    fn hit(&self, x: f32, y: f32) -> i32 {
        let top_limit = self.pill_rect.1 - (self.max_scale - 1.0) * self.base - self.base * 0.6;
        if y < top_limit || y > self.win_h as f32 {
            return -1;
        }
        for (i, it) in self.items.iter().enumerate() {
            if it.leaving {
                continue;
            }
            if x >= it.x - self.gap / 2.0 && x <= it.x + it.sz + self.gap / 2.0 {
                // vertically within the icon column (generous: from icon top to pill bottom)
                if y >= self.icon_top(it) - 4.0 || y >= self.pill_rect.1 {
                    return i as i32;
                }
            }
        }
        -1
    }

    // ---------- animation ----------
    pub fn tick(&mut self) {
        let now = std::time::Instant::now();
        let dt = now.duration_since(self.last).as_secs_f32().min(0.05);
        self.last = now;
        let mut moving = false;
        let centers = self.base_centers();
        let dragging = self.drag.is_some();
        let near = self.inside && !dragging;
        // smoothed cursor x (C#: exponential, tau 14 ms): removes event-rate steps from the magnify target
        if !near || self.smooth_x.is_nan() || (self.smooth_x - self.mouse_x).abs() > self.base * 4.0 {
            self.smooth_x = self.mouse_x;
        } else {
            self.smooth_x += (self.mouse_x - self.smooth_x) * (1.0 - (-dt / 0.014).exp());
            if (self.smooth_x - self.mouse_x).abs() < 0.05 {
                self.smooth_x = self.mouse_x;
            }
        }
        let sx = self.smooth_x;
        if near {
            self.anchor_x = sx;
        }
        if near && (sx - self.mouse_x).abs() > 0.05 {
            moving = true;
        }
        let fixed = self.max_scale <= 1.001;
        for (i, it) in self.items.iter_mut().enumerate() {
            let mut goal = 1.0;
            if near && !fixed && !it.leaving {
                let d = (sx - centers[i]).abs();
                let t = d / (self.base * 2.2);
                if t < 1.0 {
                    goal = 1.0 + (self.max_scale - 1.0) * (0.5 + 0.5 * (std::f32::consts::PI * t).cos());
                }
            }
            if let Some(dr) = &self.drag {
                if dr.idx_key == it.key {
                    goal = 1.08;
                }
            }
            it.scale.goal = goal;
            moving |= it.scale.step_zeta(dt, 26.0, 0.9);
            moving |= it.appear.step(dt, 300.0, 30.0);
            moving |= it.off.step_zeta(dt, 30.0, 0.85);
            if it.bounce >= 0.0 {
                it.bounce += dt;
                let limit = if it.launching { 14.0 } else { 1.4 };
                if it.bounce > limit {
                    it.bounce = -1.0;
                    it.launching = false;
                } else {
                    moving = true;
                }
            }
        }
        let before = self.items.len();
        self.items.retain(|it| !(it.leaving && it.appear.v <= 0.01));
        if self.items.len() != before {
            self.resize_window();
        }
        let auto = app().cfg.s.auto_hide;
        let want_hide = if auto && !self.inside && !dragging { 1.0 } else { 0.0 };
        self.hide.goal = want_hide;
        moving |= self.hide.step(dt, 220.0, 26.0);
        // keep running while the cursor is in the dock (magnify follows it)
        if !moving && !self.need_draw && self.glass_pending {
            self.need_draw = true;
        }
        if !moving && !self.need_draw {
            self.timer_on = false;
            unsafe {
                let _ = crate::clock::stop(crate::clock::bit(T_DOCK));
            }
            return;
        }
        self.need_draw = false;
        self.draw();
    }

    // ---------- drawing ----------
    fn draw(&mut self) {
        if self.fullscreen || self.disabled {
            return;
        }
        self.layout();
        self.update_hover();
        let dark = app().cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let (pl, pt, pw_, ph) = self.pill_rect;
        let rr = (ph * self.round_k).max(6.0 * self.s);
        let t0 = std::time::Instant::now();
        let (px, py) = (pl.round(), pt.round());
        let (iw, ih, irr) = (pw_.round() as i32, ph.round() as i32, rr.round() as i32);
        let key = (iw, ih, irr, dark, self.glass_style);
        if self.glass_cache.as_ref().map(|c| c.0) != Some(key) {
            let gs = self.glass_style;
            self.glass_cache = Gfx::new(iw, ih).and_then(|mut cg| {
                cg.clear(0);
                let (w, h, r) = (iw as f32, ih as f32, irr as f32);
                match gs {
                    1 => cg.stroke_round(0.5, 0.5, w - 1.0, h - 1.0, r, 1.0, if dark { argb(46, 255, 255, 255) } else { argb(90, 255, 255, 255) }),
                    2 => cg.liquid_glass(0.0, 0.0, w, h, r, dark, 0.7),
                    _ => cg.liquid_glass(0.0, 0.0, w, h, r, dark, 1.0),
                }
                Img::from_gfx(&cg).map(|i| (key, i))
            });
        }
        if let Some((_, img)) = &self.glass_cache {
            g.blit_exact(img, px as i32, py as i32);
        }
        // near-invisible fill so the whole pill (and the magnified zone while hovering) keeps receiving the mouse
        g.fill_round(pl, pt, pw_, ph, rr, argb(1, 0, 0, 0));
        if self.inside {
            let up = (self.max_scale - 1.0) * self.base + 6.0 * self.s;
            g.fill_rect(pl, pt - up, pw_, up, argb(1, 0, 0, 0));
        }
        // separators
        let mut prev: Option<(u8, f32)> = None;
        for it in self.items.iter() {
            if let Some((ps, _)) = prev {
                if self.show_div && (ps != it.section || it.sep_before) && it.appear.v > 0.5 {
                    let x = it.x - (self.gap + self.group_gap) / 2.0;
                    g.line(x, pt + ph * 0.2, x, pt + ph * 0.8, 1.0, if dark { argb(60, 255, 255, 255) } else { argb(70, 0, 0, 0) });
                }
            }
            prev = Some((it.section, it.x));
        }
        // icons
        let dragging = self.drag.as_ref().map(|d| d.idx_key.clone());
        let mut dragged: Option<usize> = None;
        for (i, it) in self.items.iter().enumerate() {
            if Some(&it.key) == dragging.as_ref() {
                dragged = Some(i);
                continue;
            }
            self.draw_item(&mut g, i, it.x, dark);
        }
        if let Some(i) = dragged {
            let dr = self.drag.as_ref().unwrap();
            let it = &self.items[i];
            let x = dr.x - it.sz / 2.0;
            let top = if dr.out { self.mouse_y - it.sz / 2.0 } else { self.icon_top(it) };
            self.draw_item_at(&mut g, i, x, top, dark, if dr.out { 0.8 } else { 1.0 });
        }
        let (wx, wy) = (self.win_x(), self.win_y());
        // pill window follows
        let pr = (wx as f32 + pl, wy as f32 + pt, pw_, ph);
        let t1 = std::time::Instant::now();
        self.position_pill(pr, rr);
        let t2 = std::time::Instant::now();
        if !self.logged { self.logged = true; log(&format!("dock win {},{} {}x{} scr {:?} lift {}", wx, wy, self.win_w, self.win_h, (self.scr.left, self.scr.top, self.scr.right, self.scr.bottom), self.lift)); }
        g.present_layered(self.hwnd, wx, wy, 255);
        self.draw_ms.0 += t0.elapsed().as_secs_f64() * 1000.0;
        self.draw_ms.1 += 1;
        self.seg_ms.0 += (t1 - t0).as_secs_f64() * 1000.0;
        self.seg_ms.1 += (t2 - t1).as_secs_f64() * 1000.0;
        self.seg_ms.2 += t2.elapsed().as_secs_f64() * 1000.0;
        if self.draw_ms.1 >= 120 {
            if std::env::var_os("MACDOCK_PERF").is_some() {
                let n = self.draw_ms.1 as f64; log(&format!("dock draw avg {:.2} ms (paint {:.2} place {:.2} present {:.2})", self.draw_ms.0 / n, self.seg_ms.0 / n, self.seg_ms.1 / n, self.seg_ms.2 / n)); self.seg_ms = (0.0, 0.0, 0.0);
            }
            self.draw_ms = (0.0, 0);
        }
        self.gfx = Some(g);
        self.update_tip();
    }

    fn draw_item(&self, g: &mut Gfx, i: usize, x: f32, dark: bool) {
        let top = self.icon_top(&self.items[i]);
        self.draw_item_at(g, i, x, top, dark, 1.0);
    }
    fn draw_item_at(&self, g: &mut Gfx, i: usize, x: f32, top: f32, dark: bool, alpha: f32) {
        let it = &self.items[i];
        if it.sz < 1.0 {
            return;
        }
        if let Some(img) = &it.icon {
            g.image(img, x, top, it.sz, it.sz, alpha * it.appear.v.clamp(0.0, 1.0));
        }
        // running indicator
        if self.show_ind && !it.wins.is_empty() && it.appear.v > 0.6 && !matches!(it.kind, Kind::Minimized | Kind::Split) {
            let cx = x + it.sz / 2.0;
            let cy = self.pill_rect.1 + self.pill_rect.3 - self.pad_y * 0.5 + 1.0 * self.s;
            let c = if dark { argb(215, 255, 255, 255) } else { argb(200, 30, 30, 30) };
            g.fill_circle(cx, cy, 2.0 * self.s, if it.minimized_only { with_alpha(c, 0.5) } else { c });
        }
        if it.launching && it.bounce < 0.0 {
            let _ = i;
        }
    }

    fn position_pill(&mut self, pr: (f32, f32, f32, f32), rr: f32) {
        let (x, y, w_, h_) = (pr.0.round() as i32, pr.1.round() as i32, pr.2.round() as i32, pr.3.round() as i32);
        let above = self.hwnd;
        // Moving the acrylic pieces costs ~12 ms: while animating, refresh them at most every ~16 ms and
        // make sure the final rect is applied once the animation settles (tick re-draws while pending).
        let now = std::time::Instant::now();
        if now.duration_since(self.glass_t).as_millis() < 16 && self.glass_ok {
            self.glass_pending = true;
            return;
        }
        self.glass_t = now;
        self.glass_ok = true;
        self.glass_pending = false;
        if let Some(g) = self.glass.as_mut() {
            g.place(x, y, w_, h_, rr as i32, above);
        }
    }

    pub fn apply_theme(&mut self) {
        let dark = app().cfg.s.dark;
        let (base_a, rgb) = if dark { (0x5Au32, 0x1E1E1Eu32) } else { (0x66u32, 0xF5F5F5u32) };
        let clear = if self.glass_style == 2 { 0.55 } else { 1.0 };
        let a = ((base_a as f32 * self.glass_mul * clear).round() as u32).clamp(8, 0xE0);
        let tint = (a << 24) | rgb;
        if let Some(g) = self.glass.as_mut() {
            g.set_tint(tint);
        }
    }

    // ---------- tip (hover label) ----------
    fn update_tip(&mut self) {
        let want = if self.inside && self.hover >= 0 && self.drag.is_none() && (self.hover as usize) < self.items.len() { Some(self.hover as usize) } else { None };
        match want {
            None => {
                if !self.tip_text.is_empty() {
                    self.tip_text.clear();
                    self.tip_size = (0, 0);
                    win::hide(self.tip);
                }
            }
            Some(i) => {
                let it = &self.items[i];
                let text = it.label.clone();
                let cx = self.win_x() as f32 + it.x + it.sz / 2.0;
                let top = self.win_y() as f32 + self.icon_top(it);
                if text == self.tip_text && self.tip_size.0 > 0 {
                    // same label: just follow the icon (rebuilding the bitmap every frame made the label jitter)
                    let (w_, h_) = self.tip_size;
                    let x = ((cx - w_ as f32 / 2.0).round() as i32).clamp(self.scr.left + 4, self.scr.right - w_ - 4);
                    let y = (top - 8.0 * self.s - h_ as f32).round() as i32;
                    if (x, y) != self.tip_pos {
                        self.tip_pos = (x, y);
                        win::set_pos(self.tip, x, y, 0, 0, None, SWP_NOSIZE | SWP_NOZORDER);
                    }
                    return;
                }
                self.show_tip(&text, cx, top - 8.0 * self.s);
            }
        }
    }
    fn show_tip(&mut self, text: &str, cx: f32, bottom: f32) {
        let s = self.s;
        let dark = app().cfg.s.dark;
        let Some(mut m) = Gfx::new(400, 60) else { return };
        let tw = m.measure(text, 13.0 * s, false);
        let (w_, h_) = ((tw + 24.0 * s).ceil() as i32, (28.0 * s).ceil() as i32);
        let Some(mut g) = Gfx::new(w_, h_) else { return };
        g.clear(0);
        let bg = if dark { argb(235, 38, 38, 40) } else { argb(240, 246, 246, 248) };
        g.fill_round(0.5, 0.5, w_ as f32 - 1.0, h_ as f32 - 1.0, 8.0 * s, bg);
        g.stroke_round(0.5, 0.5, w_ as f32 - 1.0, h_ as f32 - 1.0, 8.0 * s, 1.0, if dark { argb(40, 255, 255, 255) } else { argb(40, 0, 0, 0) });
        g.text(text, 0.0, 0.0, w_ as f32, h_ as f32, 13.0 * s, false, 1, true, false, if dark { argb(255, 240, 240, 240) } else { argb(255, 30, 30, 30) });
        let x = (cx - w_ as f32 / 2.0).round() as i32;
        let y = (bottom - h_ as f32).round() as i32;
        let x = x.clamp(self.scr.left + 4, self.scr.right - w_ - 4);
        win::set_pos(self.tip, x, y, w_, h_, Some(HWND_TOPMOST), SWP_NOSIZE | SWP_NOMOVE);
        g.present_layered(self.tip, x, y, 255);
        self.tip_text = text.to_string();
        self.tip_size = (w_, h_);
        self.tip_pos = (x, y);
        win::show_noactivate(self.tip);
    }

    // ---------- input ----------
    fn on_move(&mut self, x: f32, y: f32) {
        self.mouse_x = x;

        self.mouse_y = y;
        let was = self.inside;
        let (pl, pt, pw_, ph) = self.pill_rect;
        let top = if was { pt - (self.max_scale - 1.0) * self.base - 6.0 * self.s } else { pt };
        self.inside = (x >= pl && x <= pl + pw_ && y >= top && y <= pt + ph) || self.drag.is_some();
        if !was {
            // track leave
            unsafe {
                let mut t = TRACKMOUSEEVENT { cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32, dwFlags: TME_LEAVE, hwndTrack: self.hwnd, dwHoverTime: 0 };
                let _ = TrackMouseEvent(&mut t);
            }
        }
        // drag handling
        if let Some((idx, px, py)) = self.press {
            if self.drag.is_none() && ((x - px).abs() > 6.0 * self.s || (y - py).abs() > 6.0 * self.s) {
                if let Some(it) = self.items.get(idx as usize) {
                    if it.pinned && !matches!(it.kind, Kind::Trash) {
                        self.drag = Some(Drag { idx_key: it.key.clone(), x, grab: 0.0, out: false });
                        unsafe {
                            SetCapture(self.hwnd);
                        }
                    }
                }
            }
        }
        if self.drag.is_some() {
            self.update_drag(x, y);
        }
        self.update_hover();
        self.dirty();
    }

    fn update_hover(&mut self) {
        let h = if self.inside && self.drag.is_none() {
            self.hit(self.mouse_x, self.mouse_y)
        } else {
            -1
        };
        let prev_h = self.hover;
        self.hover = h;
        if h != prev_h {
            let key = if h >= 0 && self.drag.is_none() { self.items.get(h as usize).filter(|i| !i.wins.is_empty()).map(|i| i.key.clone()).unwrap_or_default() } else { String::new() };
            app().preview.hover_item(&key);
            let a = app();
            let fk = if h >= 0 && self.drag.is_none() { self.items.get(h as usize).filter(|i| i.kind == Kind::Folder && !a.cfg.ob("fan-click", false)).map(|i| i.key.clone()) } else { None };
            self.fan_pending = fk.clone();
            unsafe {
                if fk.is_some() {
                    SetTimer(Some(a.msg), T_FANHOVER, 380, None);
                } else {
                    let _ = KillTimer(Some(a.msg), T_FANHOVER);
                }
            }
        }
    }

    fn update_drag(&mut self, x: f32, y: f32) {
        let Some(dr) = self.drag.as_mut() else { return };
        dr.x = x;
        dr.out = y < self.pill_rect.1 - self.base * 1.3;
        let key = dr.idx_key.clone();
        let out = dr.out;
        let Some(cur) = self.items.iter().position(|i| i.key == key) else { return };
        if out {
            return;
        }
        // find insertion slot among pinned items by x
        let mut target = cur;
        for (i, it) in self.items.iter().enumerate() {
            if i == cur || !it.pinned || it.leaving {
                continue;
            }
            let c = it.x + it.sz / 2.0;
            if i < cur && x < c {
                target = target.min(i);
            }
            if i > cur && x > c {
                target = target.max(i);
            }
        }
        // stay inside the same section group
        if target != cur && self.items[target].pinned && self.items[target].kind != Kind::Trash && self.items[target].section == self.items[cur].section {
            let w_ = self.items[cur].sz + self.gap;
            let dirn = if target > cur { 1.0 } else { -1.0 };
            let step_range: Vec<usize> = if target > cur { (cur + 1..=target).collect() } else { (target..cur).collect() };
            for i in step_range {
                self.items[i].off.v += dirn * w_;
            }
            let it = self.items.remove(cur);
            self.items.insert(target, it);
        }
    }

    fn on_up(&mut self, x: f32, y: f32, shift_or_mid: bool) {
        let _ = shift_or_mid;
        let press = self.press.take();
        unsafe {
            if GetCapture() == self.hwnd {
                let _ = ReleaseCapture();
            }
        }
        if let Some(dr) = self.drag.take() {
            if dr.out {
                // unpin
                if let Some(it) = self.items.iter().find(|i| i.key == dr.idx_key) {
                    let t = it.target.clone();
                    let a = app();
                    a.cfg.remove_pin(&t);
                    a.cfg.reload();
                    refresh_windows();
                }
            } else {
                // persist order (pinned only)
                let a = app();
                let order: Vec<Entry> = self.items.iter().filter(|i| i.pinned && i.kind != Kind::Trash).map(|i| Entry { section: i.section, label: a.cfg.entries.iter().find(|e| e.target.to_lowercase() == i.key).and_then(|e| e.label.clone()), target: a.cfg.entries.iter().find(|e| e.target.to_lowercase() == i.key).map(|e| e.target.clone()).unwrap_or(i.target.clone()), sep_before: i.sep_before }).collect();
                a.cfg.save_order(&order);
                a.cfg.reload();
            }
            self.dirty();
            return;
        }
        if let Some((idx, _, _)) = press {
            let h = self.hit(x, y);
            if h == idx && h >= 0 {
                activate(h as usize);
            }
        }
    }

    fn on_leave(&mut self) {
        if self.drag.is_some() {
            return;
        }
        self.inside = false;
        self.update_hover();
        self.mouse_x = -1.0;
        self.dirty();
    }

    fn context(&mut self, sx: i32, sy: i32) {
        let (lx, ly) = (sx as f32 - self.win_x() as f32, sy as f32 - self.win_y() as f32);
        let i = self.hit(lx, ly);
        let a = app();
        if i < 0 {
            let id = win::popup_menu(a.msg, sx, sy, &[(1, "Dock Settings...".into(), true, false), (2, if a.cfg.s.auto_hide { "Turn Hiding Off".into() } else { "Turn Hiding On".into() }, true, false), (3, "Add Application...".into(), true, false)]);
            match id {
                1 => a.set.show(1),
                2 => {
                    a.cfg.s.auto_hide = !a.cfg.s.auto_hide;
                    crate::reserve();
                    a.cfg.set_option("auto-hide-dock", if a.cfg.s.auto_hide { "on" } else { "off" });
                    self.dirty();
                }
                3 => pick_and_pin(),
                _ => {}
            }
            return;
        }
        let it = &self.items[i as usize];
        let (kind, target, key, pinned, label) = (it.kind, it.target.clone(), it.key.clone(), it.pinned, it.label.clone());
        let wins = it.wins.clone();
        let mut m: Vec<(u32, String, bool, bool)> = vec![];
        let mut win_ids: Vec<isize> = vec![];
        if !wins.is_empty() {
            for (n, h) in wins.iter().enumerate().take(12) {
                let t = a.wins.iter().find(|w| w.hwnd == *h).map(|w| w.title.clone()).unwrap_or_default();
                let t = if t.chars().count() > 46 { format!("{}...", t.chars().take(45).collect::<String>()) } else { t };
                m.push((200 + n as u32, t, true, *h == a.fg));
                win_ids.push(*h);
            }
            m.push((0, String::new(), true, false));
        }
        match kind {
            Kind::Trash => {
                m.push((1, "Open".into(), true, false));
                m.push((2, "Empty Trash".into(), a.trash_full, false));
            }
            Kind::Minimized => {
                m.push((1, "Restore".into(), true, false));
                m.push((9, "Close Window".into(), true, false));
            }
            Kind::Start | Kind::Launchpad => {
                m.push((1, "Open".into(), true, false));
            }
            Kind::Split => {
                m.push((8, "Close All Windows".into(), true, false));
            }
            _ => {
                m.push((1, if wins.is_empty() { "Open".into() } else { "New Window".into() }, true, false));
                if pinned {
                    m.push((4, "Remove from Dock".into(), true, false));
                } else if kind == Kind::Running {
                    m.push((3, "Keep in Dock".into(), true, false));
                }
                m.push((5, "Show in Explorer".into(), true, false));
                m.push((6, "Rename...".into(), pinned, false));
                if kind == Kind::Folder {
                    let cur = a.cfg.folder_colors.get(&target.to_lowercase()).cloned();
                    m.push((0, String::new(), true, false));
                    m.push((39, "Folder Color".into(), false, false));
                    m.push((40, "    Default".into(), true, cur.is_none()));
                    for (n, (name, _)) in crate::icons::FOLDER_COLORS.iter().enumerate() {
                        m.push((41 + n as u32, format!("    {}", name), true, cur.as_deref() == Some(*name)));
                    }
                }
                if matches!(kind, Kind::App | Kind::Running) {
                    let mode = a.cfg.choice_for(&target, &label).map(|c| c.0.clone());
                    m.push((0, String::new(), true, false));
                    m.push((19, "Icon".into(), false, false));
                    m.push((20, "    Default".into(), true, mode.is_none()));
                    m.push((21, "    MacDock Icon".into(), true, mode.as_deref() == Some("pack")));
                    m.push((22, "    Original Icon".into(), true, mode.as_deref() == Some("orig")));
                    m.push((23, "    Choose Custom Icon...".into(), true, mode.as_deref() == Some("custom")));
                    if mode.is_some() {
                        m.push((24, "    Reset Icon".into(), true, false));
                    }
                }
                if !wins.is_empty() {
                    m.push((0, String::new(), true, false));
                    m.push((7, "Hide".into(), true, false));
                    m.push((8, "Close All Windows".into(), true, false));
                    m.push((9, "Quit".into(), true, false));
                    m.push((10, "Force Quit".into(), true, false));
                }
            }
        }
        m.push((0, String::new(), true, false));
        m.push((11, "Dock Settings...".into(), true, false));
        let id = win::popup_menu(a.msg, sx, sy, &m);
        let a = app();
        match id {
            0 => {}
            200..=230 => {
                if let Some(h) = win_ids.get(id as usize - 200) {
                    activate_hwnd(*h);
                }
            }
            1 => {
                if kind == Kind::Trash {
                    shell_open("shell:RecycleBinFolder");
                } else {
                    launch_item(i as usize, true);
                }
            }
            2 => unsafe {
                let _ = SHEmptyRecycleBinW(None, windows::core::PCWSTR::null(), 0);
            },
            3 => {
                let exe = self.items.iter().find(|x| x.key == key).map(|x| x.exe.clone()).unwrap_or_default();
                a.cfg.add_pin(&exe, 0);
                a.cfg.reload();
                refresh_windows();
            }
            4 => {
                a.cfg.remove_pin(&target);
                a.cfg.reload();
                refresh_windows();
            }
            5 => {
                let p = if self.items.iter().find(|x| x.key == key).map(|x| !x.exe.is_empty()).unwrap_or(false) { self.items.iter().find(|x| x.key == key).unwrap().exe.clone() } else { target.clone() };
                run_cmd("explorer.exe", &format!("/select,\"{}\"", p));
            }
            6 => {
                if let Some(n) = crate::settings::prompt_text("Rename", "Name in the dock", &label) {
                    a.cfg.rename(&target, &n);
                    refresh_windows();
                }
            }
            7 => {
                for h in &wins {
                    minimize(*h);
                }
            }
            8 => {
                for h in &wins {
                    close_window(*h);
                }
            }
            9 => {
                for h in &wins {
                    close_window(*h);
                }
            }
            10 => {
                // kills only the target app's pids, never MacDock itself
                let pids: Vec<u32> = a.wins.iter().filter(|w| wins.contains(&w.hwnd)).map(|w| w.pid).collect();
                for p in pids {
                    kill_pid(p);
                }
            }
            11 => a.set.show(1),
            40 => {
                a.cfg.set_folder_color(&target, None);
                icons_changed();
            }
            41..=48 => {
                a.cfg.set_folder_color(&target, Some(crate::icons::FOLDER_COLORS[id as usize - 41].0));
                icons_changed();
            }
            20 | 24 => {
                a.cfg.put_choice(&target, &label, None);
                icons_changed();
            }
            21 => {
                a.cfg.put_choice(&target, &label, Some(("pack", "")));
                icons_changed();
            }
            22 => {
                a.cfg.put_choice(&target, &label, Some(("orig", "")));
                icons_changed();
            }
            23 => {
                if let Some(p) = crate::settings::pick_file(ICON_FILTER) {
                    a.cfg.put_choice(&target, &label, Some(("custom", &p)));
                    icons_changed();
                }
            }
            _ => {}
        }
    }

    fn on_drop(&mut self, hdrop: HDROP) {
        unsafe {
            let mut pt = POINT::default();
            let _ = DragQueryPoint(hdrop, &mut pt);
            let n = DragQueryFileW(hdrop, 0xFFFF_FFFF, None);
            let mut files: Vec<String> = vec![];
            for i in 0..n {
                let mut buf = [0u16; 1024];
                let l = DragQueryFileW(hdrop, i, Some(&mut buf));
                files.push(String::from_utf16_lossy(&buf[..l as usize]));
            }
            DragFinish(hdrop);
            let i = self.hit(pt.x as f32, pt.y as f32);
            let a = app();
            if i >= 0 {
                let it = &self.items[i as usize];
                match it.kind {
                    Kind::Trash => {
                        for f in &files {
                            recycle(f);
                        }
                        refresh_windows();
                    }
                    Kind::App | Kind::Running => {
                        let exe = if it.kind == Kind::Running { it.exe.clone() } else { it.target.clone() };
                        let args = files.iter().map(|f| format!("\"{}\"", f)).collect::<Vec<_>>().join(" ");
                        shell_verb("open", &exe, Some(&args));
                    }
                    Kind::Folder => {
                        for f in &files {
                            let dst = std::path::Path::new(&expand_env(&it.target)).join(std::path::Path::new(f).file_name().unwrap_or_default());
                            let _ = std::fs::copy(f, dst);
                        }
                    }
                    _ => {}
                }
            } else {
                for f in files {
                    let l = f.to_lowercase();
                    let sec = if std::path::Path::new(&f).is_dir() || !(l.ends_with(".exe") || l.ends_with(".lnk")) { 2 } else { 0 };
                    a.cfg.add_pin(&f, sec);
                }
                a.cfg.reload();
                refresh_windows();
            }
        }
    }
}

fn refresh_windows_soft(_a: &mut App) {
    crate::refresh_windows();
}

pub fn recycle(path: &str) {
    unsafe {
        let mut w: Vec<u16> = path.encode_utf16().collect();
        w.push(0);
        w.push(0);
        let mut op = SHFILEOPSTRUCTW { wFunc: FO_DELETE, pFrom: windows::core::PCWSTR(w.as_ptr()), fFlags: (FOF_ALLOWUNDO.0 | FOF_NOCONFIRMATION.0 | FOF_SILENT.0) as u16, ..Default::default() };
        SHFileOperationW(&mut op);
    }
}

fn resolve_lnk_target(path: &str) -> Option<String> {
    use windows::Win32::System::Com::*;
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        let pf: IPersistFile = windows::core::Interface::cast(&link).ok()?;
        let wp = wide(path);
        pf.Load(pw(&wp), STGM_READ).ok()?;
        let mut buf = [0u16; 1024];
        link.GetPath(&mut buf, std::ptr::null_mut(), 0).ok()?;
        let s = from_wide(&buf);
        if s.is_empty() { None } else { Some(s) }
    }
}

fn pick_and_pin() {
    if let Some(p) = crate::settings::pick_file("Applications\0*.exe;*.lnk\0All files\0*.*\0\0") {
        let a = app();
        a.cfg.add_pin(&p, 0);
        a.cfg.reload();
        refresh_windows();
    }
}

pub fn activate_hwnd(h: isize) {
    let a = app();
    let (from_rect, was_min) = unsafe {
        let mut r = RECT::default();
        let _ = GetWindowRect(hw(h), &mut r);
        (r, IsIconic(hw(h)).as_bool())
    };
    let _ = from_rect;
    crate::sys::activate(h);
}

pub fn launch_item(i: usize, force_new: bool) {
    let a = app();
    let Some(it) = a.dock.items.get_mut(i) else { return };
    it.bounce = 0.0;
    let _ = force_new;
    if it.kind == Kind::Split {
        return;
    }
    if it.kind == Kind::Minimized {
        if let Some(h) = it.wins.first().copied() {
            it.bounce = -1.0;
            activate_hwnd(h);
        }
        return;
    }
    match it.kind {
        Kind::Start => unsafe {
            let inputs = [
                INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VK_LWIN, ..Default::default() } } },
                INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VK_LWIN, dwFlags: KEYEVENTF_KEYUP, ..Default::default() } } },
            ];
            let _ = SendInput(&inputs, std::mem::size_of::<INPUT>() as i32);
        },
        Kind::Launchpad => {
            it.bounce = -1.0;
            a.lp.toggle();
        }
        Kind::Trash => shell_open("shell:RecycleBinFolder"),
        Kind::Running => {
            let exe = it.exe.clone();
            it.launching = true;
            shell_open(&exe);
        }
        Kind::Folder => {
            let t = expand_env(&it.target);
            let key = it.key.clone();
            it.bounce = 0.0;
            let anchor = a.dock.anchor_for(&|i: &Item| i.key == key);
            match anchor {
                Some((cx, top, _)) => a.fp.open(&t, cx, top),
                None => shell_open(&t),
            }
        }
        _ => {
            it.launching = it.kind == Kind::App;
            it.launch_at = unsafe { GetTickCount64_() };
            let t = it.target.clone();
            shell_open(&t);
        }
    }
    a.dock.dirty();
}
unsafe fn GetTickCount64_() -> u64 {
    windows::Win32::System::SystemInformation::GetTickCount64()
}

/// Click on a dock item: foreground+single window minimizes (genie), otherwise focus/launch.
pub fn activate(i: usize) {
    let a = app();
    let Some(it) = a.dock.items.get(i) else { return };
    let wins = it.wins.clone();
    let kind = it.kind;
    if kind == Kind::Minimized {
        if let Some(h) = wins.first() {
            activate_hwnd(*h);
        }
        return;
    }
    if kind == Kind::Split {
        for h in wins.iter().rev() {
            activate_hwnd(*h);
        }
        return;
    }
    if wins.is_empty() || !matches!(kind, Kind::App | Kind::Running) {
        launch_item(i, false);
        return;
    }
    let fg = a.fg;
    // z-ordered top-first list: pick the topmost window; if it is already foreground, cycle or minimize.
    if wins.contains(&fg) && unsafe { !IsIconic(hw(fg)).as_bool() } {
        if wins.len() == 1 {
            let h = fg;
            minimize(h);
        } else {
            let next = wins.iter().position(|w| *w == fg).map(|p| wins[(p + 1) % wins.len()]).unwrap_or(wins[0]);
            activate_hwnd(next);
        }
    } else {
        let target = wins.iter().copied().find(|h| unsafe { !IsIconic(hw(*h)).as_bool() }).unwrap_or(wins[0]);
        a.dock.items[i].bounce = 0.0;
        activate_hwnd(target);
    }
    app().dock.dirty();
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM, id: isize) -> LRESULT {
    let a = app();
    if id != ID_DOCK {
        if id == ID_PILL && (m == WM_NCHITTEST) {
            return LRESULT(-1); // HTTRANSPARENT
        }
        if id == ID_PILL && m == WM_ERASEBKGND {
            return LRESULT(1);
        }
        if id == ID_TIP && m == WM_NCHITTEST {
            return LRESULT(-1);
        }
        return DefWindowProcW(h, m, wp, lp);
    }
    let d = &mut a.dock;
    match m {
        WM_TIMER if wp.0 == T_DOCK => {
            d.tick();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            d.on_move(x as f32, y as f32);
            LRESULT(0)
        }
        x if x == WM_MOUSELEAVE => {
            d.on_leave();
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            let (x, y) = win::lparam_xy(lp);
            let i = d.hit(x as f32, y as f32);
            d.press = Some((i, x as f32, y as f32));
            if i >= 0 {
                SetCapture(h);
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            d.on_up(x as f32, y as f32, false);
            LRESULT(0)
        }
        WM_MBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let i = d.hit(x as f32, y as f32);
            if i >= 0 {
                let it = &d.items[i as usize];
                if it.kind == Kind::Trash {
                    let _ = SHEmptyRecycleBinW(None, windows::core::PCWSTR::null(), SHERB_NOCONFIRMATION | SHERB_NOSOUND);
                } else if matches!(it.kind, Kind::App | Kind::Running) {
                    launch_item(i as usize, true);
                }
            }
            LRESULT(0)
        }
        WM_RBUTTONUP => {
            let (x, y) = win::lparam_xy(lp);
            let mut p = POINT { x, y };
            let _ = ClientToScreen(h, &mut p);
            d.context(p.x, p.y);
            LRESULT(0)
        }
        WM_DROPFILES => {
            d.on_drop(HDROP(wp.0 as *mut _));
            LRESULT(0)
        }
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        WM_DISPLAYCHANGE => LRESULT(0),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

pub fn apply_theme() {
    app().dock.apply_theme();
}

impl Dock {
    pub fn dragging(&self) -> bool {
        self.drag.is_some()
    }
}

impl Dock {
    /// Settings > Show Dock: hide or show the whole dock.
    pub fn set_disabled(&mut self, d: bool) {
        self.disabled = d;
        if d {
            win::hide(self.hwnd);
            if let Some(g) = self.glass.as_mut() {
                g.hide();
            }
            self.shown = false;
        } else if !self.fullscreen {
            win::show_noactivate(self.hwnd);
            self.shown = true;
            self.dirty();
        }
    }
}

impl Dock {
    /// Hover timer for folder fans: open when the pointer still rests on the same folder.
    pub fn fan_hover_fire(&mut self) {
        if let Some(key) = self.fan_pending.take() {
            if self.hover >= 0 {
                if let Some(it) = self.items.get(self.hover as usize) {
                    if it.key == key {
                        launch_item(self.hover as usize, false);
                    }
                }
            }
        }
    }
}

/// Does a window belong to a pin / running item described by (exe, path suffix, package prefix or `aumid:`)?
/// Chromium web apps (Brave/Chrome/Edge "Install as app") carry their own AppUserModelID and are matched only by it,
/// so they never merge into the browser's tile.
pub fn pin_matches(exe: &str, suffix: &str, pkg: &str, w: &Win) -> bool {
    let pwa = w.aumid.contains("_crx_");
    if let Some(a) = pkg.strip_prefix("aumid:") {
        return pwa && w.aumid == a;
    }
    if pwa {
        return false;
    }
    (!exe.is_empty() && w.exe == exe) || (!suffix.is_empty() && w.exe.ends_with(suffix)) || (!pkg.is_empty() && w.exe.contains(pkg))
}

/// Drop every cached icon and rebuild the dock (after an icon choice changed).
pub fn icons_changed() {
    let a = app();
    a.icons.clear();
    a.icons.failed.clear();
    refresh_windows();
    a.lp.dirty(false);
}
