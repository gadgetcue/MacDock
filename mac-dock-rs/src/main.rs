#![windows_subsystem = "windows"]
//! MacDock (Rust): macOS-style dock, menu bar, Launchpad, Mission Control, Spotlight, Dynamic Island,
//! Control Center for Windows 11. Single UI thread, event-driven (WinEvent hooks), animation timers only
//! while something moves, icon decoding on a worker thread.
mod account;
mod capture;
mod clamp;
mod clock;
mod config;
mod control;
mod dock;
mod fan;
mod folderpop;
mod genie;
mod gfx;
mod icons;
mod island;
mod islandx;
mod localsend;
mod launchpad;
mod media;
mod mission;
mod netinfo;
mod notify;
mod panel;
mod preview;
mod settings;
mod snap;
mod spotlight;
mod status;
mod sys;
mod topbar;
mod tour;
mod tray;
mod touchpad;
mod util;
mod win;

use config::Config;
use icons::Icons;
use std::panic::{catch_unwind, AssertUnwindSafe};
use sys::*;
use util::*;
use windows::core::w;
use windows::Win32::Foundation::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::Accessibility::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::WindowsAndMessaging::*;

pub const WM_ICONS_READY: u32 = WM_APP + 1;
pub const WM_WINS_CHANGED: u32 = WM_APP + 2;
pub const WM_STATUS: u32 = WM_APP + 3;
pub const WM_NOTIFY_NEW: u32 = WM_APP + 4;
pub const WM_MEDIA: u32 = WM_APP + 5;
pub const WM_GENIE: u32 = WM_APP + 8;

pub const ID_MSG: isize = 1;
pub const ID_DOCK: isize = 2;
pub const ID_BAR: isize = 3;
pub const ID_PILL: isize = 4;
pub const ID_LAUNCHPAD: isize = 5;
pub const ID_MISSION: isize = 6;
pub const ID_SPOTLIGHT: isize = 7;
pub const ID_ISLAND: isize = 8;
pub const ID_CC: isize = 9;
pub const ID_SETTINGS: isize = 10;
pub const ID_TIP: isize = 11;
pub const ID_BARPILL: isize = 12;
pub const ID_GENIE: isize = 13;
pub const ID_MENU: isize = 14;
pub const ID_PREVIEW: isize = 15;
pub const ID_FOLDER: isize = 16;
pub const ID_LSPICK: isize = 17;
pub const ID_FAN: isize = 18;
pub const ID_TOUR: isize = 19;
pub const ID_PANEL: isize = 20;
pub const WM_PANEL: u32 = WM_APP + 10;
pub const WM_PANEL_RELOAD: u32 = WM_APP + 11;
pub const T_LSP: usize = 26;
pub const T_FP: usize = 25;
pub const T_PREVIEW: usize = 21;
pub const T_LATE: usize = 23;
pub const T_FANHOVER: usize = 28;
pub const T_RESUME: usize = 24;
static T0: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();
static RESUME_AT: std::sync::Mutex<Option<std::time::Instant>> = std::sync::Mutex::new(None);
pub const T_PREVIEW_HIDE: usize = 22;

pub const T_REFRESH: usize = 1;
pub const T_SLOW: usize = 2;
pub const T_CLAMP: usize = 29;
pub const T_TRIGGER: usize = 3;
pub const T_STATUS: usize = 4;
pub const T_DOCK: usize = 10;
pub const T_BAR: usize = 11;
pub const T_ISLAND: usize = 12;
pub const T_LP: usize = 13;
pub const T_MC: usize = 14;
pub const T_SPOT: usize = 15;
pub const T_CC: usize = 16;
pub const T_SET: usize = 17;
pub const T_GENIE: usize = 18;
pub const T_MENU: usize = 19;
pub const T_TIPHIDE: usize = 20;

pub struct App {
    pub cfg: Config,
    pub icons: Icons,
    pub msg: HWND,
    pub scale: f32,
    pub test: bool,
    pub wins: Vec<Win>,
    pub fg: isize,
    pub trash_full: bool,
    pub fullscreen: bool,
    pub dock: dock::Dock,
    pub bar: topbar::TopBar,
    pub lp: launchpad::Launchpad,
    pub mc: mission::Mission,
    pub spot: spotlight::Spotlight,
    pub island: island::Island,
    pub cc: control::ControlCenter,
    pub set: settings::SettingsWin,
    pub genie: genie::Genie,
    pub stat: status::Status,
    pub notes: notify::Notes,
    pub media: media::Media,
    pub preview: preview::Preview,
    pub fp: folderpop::FolderPop,
    pub fan: fan::Fan,
    pub tour: tour::Tour,
    pub panel: panel::Panel,
    pub tp: touchpad::Touchpad,
    pub ls: localsend::LocalSend,
    pub lspick: localsend::Picker,
}

static mut APP: *mut App = std::ptr::null_mut();
pub fn app() -> &'static mut App {
    unsafe { &mut *APP }
}

static DIRTY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static MSG_HWND: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);

unsafe extern "system" fn win_event(_h: HWINEVENTHOOK, ev: u32, hwnd: HWND, id_obj: i32, _c: i32, _t: u32, _ms: u32) {
    if id_obj != 0 || hwnd.is_invalid() {
        return;
    }
    if ev == 0x16 || ev == 0x17 {
        let m = MSG_HWND.load(std::sync::atomic::Ordering::SeqCst);
        let _ = PostMessageW(Some(HWND(m as *mut _)), WM_GENIE, WPARAM(hwnd.0 as usize), LPARAM(ev as isize));
    }
    if !DIRTY.swap(true, std::sync::atomic::Ordering::SeqCst) {
        let m = MSG_HWND.load(std::sync::atomic::Ordering::SeqCst);
        let _ = PostMessageW(Some(HWND(m as *mut _)), WM_WINS_CHANGED, WPARAM(0), LPARAM(0));
    }
}

pub unsafe extern "system" fn wndproc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let id = GetWindowLongPtrW(h, GWLP_USERDATA);
    if id == 0 || APP.is_null() {
        return DefWindowProcW(h, m, wp, lp);
    }
    let r = catch_unwind(AssertUnwindSafe(|| match id {
        ID_MSG => msg_proc(h, m, wp, lp),
        ID_DOCK | ID_PILL | ID_TIP => dock::proc(h, m, wp, lp, id),
        ID_BAR | ID_BARPILL => topbar::proc(h, m, wp, lp, id),
        ID_LAUNCHPAD => launchpad::proc(h, m, wp, lp),
        ID_MISSION => mission::proc(h, m, wp, lp),
        ID_SPOTLIGHT => spotlight::proc(h, m, wp, lp),
        ID_ISLAND => island::proc(h, m, wp, lp),
        ID_CC => control::proc(h, m, wp, lp),
        ID_SETTINGS => settings::proc(h, m, wp, lp),
        ID_GENIE => genie::proc(h, m, wp, lp),
        ID_PREVIEW => preview::proc(h, m, wp, lp),
        ID_FOLDER => folderpop::proc(h, m, wp, lp),
        ID_FAN => fan::proc(h, m, wp, lp),
        ID_TOUR => tour::proc(h, m, wp, lp),
        ID_PANEL => panel::proc(h, m, wp, lp),
        ID_LSPICK => localsend::proc(h, m, wp, lp),
        _ => DefWindowProcW(h, m, wp, lp),
    }));
    match r {
        Ok(v) => v,
        Err(_) => {
            log(&format!("PANIC in window {} msg {:#x}", id, m));
            DefWindowProcW(h, m, wp, lp)
        }
    }
}

static TB_MSG: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

pub fn refresh_windows() {
    let a = app();
    if !a.test && a.cfg.s.hide_taskbar {
        taskbar_ensure();
    }
    a.wins = enum_windows();
    if a.cfg.ob("windowtheme", false) {
        apply_window_theme(&a.wins, a.cfg.s.dark);
    }
    a.fg = foreground_info().map(|f| f.0).unwrap_or(0);
    a.dock.on_windows(&a.wins, &mut a.cfg, &mut a.icons, a.trash_full, a.fg);
    a.bar.on_foreground();
    a.island.on_windows();
}

unsafe fn msg_proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let a = app();
    if m != 0 && m == TB_MSG.load(std::sync::atomic::Ordering::Relaxed) {
        // "TaskbarCreated": Explorer restarted and forgot our auto-hide state
        if !a.test && a.cfg.s.hide_taskbar {
            taskbar_reapply();
        }
        a.dock.relayout_all(a.scale, &a.cfg);
        a.bar.relayout(a.scale);
        return LRESULT(0);
    }
    match m {
        WM_ICONS_READY => {
            if wp.0 == 1 {
                a.icons.scan_pack(&a.cfg.dir);
                a.icons.clear();
                refresh_windows();
            }
            if a.icons.drain() {
                a.dock.on_windows(&a.wins, &mut a.cfg, &mut a.icons, a.trash_full, a.fg);
                a.dock.dirty();
            }
            a.lp.dirty(wp.0 == 2);
            a.fp.dirty();
            a.fan.dirty();
            a.mc.dirty();
            a.spot.dirty();
            LRESULT(0)
        }
        WM_WINS_CHANGED => {
            if !MSG_HWND_TIMER.swap(true, std::sync::atomic::Ordering::SeqCst) {
                SetTimer(Some(h), T_REFRESH, 90, None);
            }
            LRESULT(0)
        }
        WM_STATUS => {
            let t = a.stat.get().trash_full;
            if t != a.trash_full {
                a.trash_full = t;
                a.dock.on_windows(&a.wins, &mut a.cfg, &mut a.icons, a.trash_full, a.fg);
            }
            a.bar.dirty();
            a.island.dirty();
            a.cc.dirty();
            LRESULT(0)
        }
        WM_NOTIFY_NEW => {
            a.island.on_notification();
            LRESULT(0)
        }
        localsend::WM_LS_NOTE => {
            let notes: Vec<(String, String)> = std::mem::take(&mut *localsend::NOTES.lock().unwrap());
            for (t, b) in notes {
                a.island.custom("LocalSend", &t, &b);
            }
            LRESULT(0)
        }
        0x004A => {
            // WM_COPYDATA from `MacDock.exe --send <file>` (Explorer verb)
            let cds = lp.0 as *const windows::Win32::System::DataExchange::COPYDATASTRUCT;
            if !cds.is_null() && (*cds).dwData == 1 {
                let s = std::slice::from_raw_parts((*cds).lpData as *const u16, (*cds).cbData as usize / 2);
                let p = from_wide(s);
                if !p.is_empty() {
                    a.lspick.open(vec![p]);
                }
            }
            LRESULT(1)
        }
        clock::WM_FRAME => {
            clock::frame_handled();
            let b = clock::active();
            if b & clock::DOCK != 0 { a.dock.tick(); }
            if b & clock::LP != 0 { a.lp.tick(); }
            if b & clock::MC != 0 { a.mc.tick(); }
            if b & clock::SPOT != 0 { a.spot.tick(); }
            if b & clock::CC != 0 { a.cc.tick(); }
            if b & clock::ISLAND != 0 { a.island.tick(); }
            if b & clock::FP != 0 { a.fp.tick(); }
            if b & clock::FAN != 0 { a.fan.tick(); }
            if b & clock::PANEL != 0 { a.panel.tick(); }
            if b & clock::GENIE != 0 { a.genie.tick(); }
            LRESULT(0)
        }
        0x00FF => {
            a.tp.on_input(windows::Win32::UI::Input::HRAWINPUT(lp.0 as *mut _));
            DefWindowProcW(h, m, wp, lp)
        }
        0x031D => {
            a.island.on_clipboard();
            LRESULT(0)
        }
        WM_PANEL => {
            a.panel.apply_pending();
            LRESULT(0)
        }
        WM_PANEL_RELOAD => {
            a.panel.reload();
            LRESULT(0)
        }
        WM_GENIE => {
            a.genie.on_event(wp.0 as isize, lp.0 == 0x16);
            LRESULT(0)
        }
        WM_MEDIA => {
            a.island.dirty();
            LRESULT(0)
        }
        WM_TIMER => {
            beat();
            match wp.0 {
                T_REFRESH => {
                    let _ = KillTimer(Some(h), T_REFRESH);
                    MSG_HWND_TIMER.store(false, std::sync::atomic::Ordering::SeqCst);
                    DIRTY.store(false, std::sync::atomic::Ordering::SeqCst);
                    refresh_windows();
                }
                T_CLAMP => {
                    if !a.test && !a.fullscreen {
                        clamp::tick();
                    }
                }
                T_SLOW => {
                    refresh_windows();
                    let fs = is_fullscreen_foreground();
                    if fs != a.fullscreen {
                        a.fullscreen = fs;
                        a.dock.set_fullscreen(fs);
                        a.bar.set_fullscreen(fs);
                        a.island.set_fullscreen(fs);
                    }
                    a.bar.dirty();
                }
                T_LATE => {
                    let _ = KillTimer(Some(h), T_LATE);
                    a.bar.create(a.scale, &a.cfg);
                    a.island.create(a.scale);
                    if !a.test {
                        reserve();
                    }
                    let _ = RegisterHotKey(Some(h), 1, MOD_ALT | MOD_NOREPEAT, VK_SPACE.0 as u32);
                    let _ = RegisterHotKey(Some(h), 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4C);
                    let _ = RegisterHotKey(Some(h), 3, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_UP.0 as u32);
                    a.mc.start_gestures();
                    a.tp.register(h);
                    if !a.test {
                        a.ls.start(h.0 as isize);
                    }
                    a.stat.start(h.0 as isize);
                    account::start(h.0 as isize, a.cfg.dir.clone());
                    tray::init(&a.cfg.dir);
                    if a.cfg.ob("tray-bar", false) && tray::cached().is_empty() && !a.test {
                        tray::scan(h.0 as isize, a.scale);
                    }
                    status::start_weather();
                    if !a.test {
                        a.tour.show_if_first_run();
                    }
                    a.notes.start(h.0 as isize);
                    a.media.start(h.0 as isize);
                    a.lp.index.ensure(h.0 as isize);
                    log(&format!("background services started {} ms after main()", T0.get().map(|t| t.elapsed().as_millis()).unwrap_or(0)));
                }
                T_RESUME => {
                    let _ = KillTimer(Some(h), T_RESUME);
                    resume_recover();
                }
                T_TRIGGER => test_trigger(),
                T_PREVIEW => a.preview.on_show_timer(),
                T_FANHOVER => {
                    let _ = KillTimer(Some(h), T_FANHOVER);
                    a.dock.fan_hover_fire();
                }
                T_PREVIEW_HIDE => a.preview.on_hide_timer(),
                T_STATUS => {
                    a.stat.poll_light();
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_DISPLAYCHANGE | WM_SETTINGCHANGE | WM_DPICHANGED => {
            if m == WM_SETTINGCHANGE && lp.0 != 0 {
                // only react to display/work-area style changes, not every "Environment" ping
                let mut buf = [0u16; 32];
                let mut n = 0;
                while n < 31 {
                    let c = *(lp.0 as *const u16).add(n);
                    if c == 0 { break; }
                    buf[n] = c;
                    n += 1;
                }
                let s = from_wide(&buf);
                if !(s.starts_with("ImmersiveColorSet") || s.is_empty()) && wp.0 as u32 != SPI_SETWORKAREA.0 {
                    return LRESULT(0);
                }
            }
            a.scale = dpi_scale();
            a.dock.relayout_all(a.scale, &a.cfg);
            a.bar.relayout(a.scale);
            a.island.relayout(a.scale);
            if !a.test {
                if a.cfg.s.hide_taskbar {
                    taskbar_ensure();
                }
                reserve();
            }
            LRESULT(0)
        }
        WM_HOTKEY => {
            match wp.0 {
                1 => a.spot.toggle(),
                2 => a.lp.toggle(),
                3 => a.mc.toggle(),
                _ => {}
            }
            LRESULT(0)
        }
        WM_POWERBROADCAST => {
            // PBT_APMRESUMEAUTOMATIC (0x12) / PBT_APMRESUMESUSPEND (0x7): quick recovery, no reinit
            if wp.0 == 0x12 || wp.0 == 0x7 {
                *RESUME_AT.lock().unwrap() = Some(std::time::Instant::now());
                SetTimer(Some(h), T_RESUME, 250, None);
            }
            LRESULT(1)
        }
        WM_ENDSESSION => LRESULT(1),
        WM_QUERYENDSESSION => LRESULT(1),
        WM_CLOSE => LRESULT(0), // MacDock never quits because an app closed; only the menu's Quit does
        WM_APP_QUIT => {
            do_quit();
            LRESULT(0)
        }
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
pub const WM_APP_QUIT: u32 = WM_APP + 20;
static MSG_HWND_TIMER: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub fn quit() {
    unsafe {
        let _ = PostMessageW(Some(app().msg), WM_APP_QUIT, WPARAM(0), LPARAM(0));
    }
}
/// AppBar reservations for the menu bar (top) and dock (bottom) + the clamp backstop's geometry.
fn reserve() {
    let a = app();
    if a.test {
        return;
    }
    let mon = a.dock.monitor();
    let bar_h = if a.cfg.s.bar_show { a.bar.height() } else { 0 };
    let dock_h = if a.cfg.s.auto_hide { 0 } else { a.dock.strip_h() };
    // SHAppBarMessage can block for hundreds of ms: do it off the UI thread, and skip when nothing changed
    static LAST: std::sync::Mutex<(i32, i32, i32, i32, i32)> = std::sync::Mutex::new((-1, 0, 0, 0, 0));
    let key = (bar_h, dock_h, mon.left, mon.top, mon.bottom);
    {
        let mut l = LAST.lock().unwrap();
        if *l != key {
            *l = key;
            let (bh, dh) = (a.bar.hwnd.0 as isize, a.dock.hwnd.0 as isize);
            std::thread::spawn(move || {
                if bh != 0 {
                    appbar_reserve(hw(bh), 1, bar_h, mon);
                }
                if dh != 0 {
                    appbar_reserve(hw(dh), 3, dock_h, mon);
                }
            });
        }
    }
    let bar_b = if bar_h > 0 { mon.top + bar_h } else { 0 };
    let dock_t = if dock_h > 0 { mon.bottom - dock_h } else { 0 };
    clamp::set(dock_t, bar_b, mon);
}
fn release_reservations() {
    let a = app();
    // the reservation thread may still be registering: wait for the registry lock inside appbar_reserve
    let mon = a.dock.monitor();
    appbar_reserve(a.bar.hwnd, 1, 0, mon);
    appbar_reserve(a.dock.hwnd, 3, 0, mon);
    clamp::set(0, 0, mon);
}

fn do_quit() {
    let a = app();
    if !a.test {
        release_reservations();
        set_taskbar_visible(true);
        reset_work_area();
    }
    unsafe {
        PostQuitMessage(0);
    }
}

/// Live test hook: write a name to scratch\click.trigger (same as the C# dock).
fn test_trigger() {
    let f = base_dir().join("scratch").join("click.trigger");
    let Ok(t) = std::fs::read_to_string(&f) else { return };
    let _ = std::fs::remove_file(&f);
    let want = t.trim().to_string();
    log(&format!("TRIGGER {}", want));
    let a = app();
    let t0 = std::time::Instant::now();
    match want.as_str() {
        "@settings" => a.set.show(0),
        "@launchpad" => a.lp.toggle(),
        "@mission" => a.mc.toggle(),
        "@spotlight" => a.spot.toggle(),
        "@cc" => a.cc.toggle(),
        "@wifi" => a.panel.toggle("wifi", 1500),
        "@bt" => a.panel.toggle("bluetooth", 1500),
        "@sound" => a.panel.toggle("sound", 1500),
        "@battery" => a.panel.toggle("battery", 1500),
        "@island" => a.island.demo(),
        "@panel" => a.island.toggle_center(),
        "@tab1" => { a.island.x.tab = 1; a.island.toggle_center() }
        "@tab2" => { a.island.x.tab = 2; a.island.toggle_center() }
        "@toast" => a.island.show_toast("Volume", "60%", 0xFFFFFFFF, 1, 0.6, 3.0, "", None),
        "@toast2" => a.island.show_toast("Time for water", "3 / 8", 0xFF0A84FF, 3, -1.0, 3.0, "", None),
        "@quit" => quit(),
        w if w.starts_with("@settings:") => a.set.show(w[10..].parse().unwrap_or(0)),
        w if w.starts_with("@shot:") => {
            let _ = w;
        }
        w => {
            let i = a.dock.items.iter().position(|it| it.target.to_lowercase().contains(&w.to_lowercase()));
            if let Some(i) = i {
                dock::activate(i);
                log(&format!("TRIGGER activate took {} ms", t0.elapsed().as_millis()));
            }
        }
    }
}

fn main() {
    unsafe {
        let args: Vec<String> = std::env::args().collect();
        if args.iter().any(|a| a == "/restore" || a == "--restore") {
            set_taskbar_visible(true);
            reset_work_area();
            return;
        }
        let _ = T0.set(std::time::Instant::now());
        start_watchdog(args.iter().any(|a| a == "--test"));
        if let Some(i) = args.iter().position(|a| a == "--send") {
            if let Some(path) = args.get(i + 1) {
                if let Ok(hw_) = FindWindowW(windows::core::w!("MacDockRsWnd"), windows::core::w!("MacDock")) {
                    let w16: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
                    let cds = windows::Win32::System::DataExchange::COPYDATASTRUCT { dwData: 1, cbData: (w16.len() * 2) as u32, lpData: w16.as_ptr() as *mut _ };
                    let _ = SendMessageW(hw_, 0x004A, None, Some(LPARAM(&cds as *const _ as isize)));
                }
            }
            return;
        }
        let test = args.iter().any(|a| a == "--test");
        let mx = CreateMutexW(None, true, w!("Local\\MacDockRs-single-instance")).ok();
        if GetLastError() == ERROR_ALREADY_EXISTS {
            return;
        }
        let _keep = mx;
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        gfx::init();
        win::enable_dark_menus(true);
        win::register();
        log("start");

        let cfg = Config::load();
        let msg = win::create_msg();
        MSG_HWND.store(msg.0 as isize, std::sync::atomic::Ordering::SeqCst);
        let scale = dpi_scale();
        TB_MSG.store(RegisterWindowMessageW(w!("TaskbarCreated")), std::sync::atomic::Ordering::Relaxed);
        clock::spawn(msg.0 as isize);
        log(&format!("display refresh {} Hz (animations paced by DwmFlush)", clock::refresh_hz()));
        lap("config");
        let icons = Icons::new(msg.0 as isize, &cfg.dir);
        lap("icons");
        let a = Box::new(App {
            dock: dock::Dock::new(),
            bar: topbar::TopBar::new(),
            lp: launchpad::Launchpad::new(),
            mc: mission::Mission::new(),
            spot: spotlight::Spotlight::new(),
            island: island::Island::new(),
            cc: control::ControlCenter::new(),
            set: settings::SettingsWin::new(),
            genie: genie::Genie::new(),
            stat: status::Status::new(),
            notes: notify::Notes::new(),
            media: media::Media::new(),
            preview: preview::Preview::new(),
            fp: folderpop::FolderPop::new(),
            fan: fan::Fan::new(),
            tour: tour::Tour::new(),
            panel: panel::Panel::new(),
            tp: touchpad::Touchpad::new(),
            ls: localsend::LocalSend::new(),
            lspick: localsend::Picker::new(),
            cfg,
            icons,
            msg,
            scale,
            test,
            wins: vec![],
            fg: 0,
            trash_full: false,
            fullscreen: false,
        });
        APP = Box::into_raw(a);
        let a = app();
        a.icons.px = 256;
        gfx::SMALL_PX.store(((a.cfg.s.size as f32 * a.cfg.s.magnify * scale).ceil() as i32 + 6).clamp(48, 220), std::sync::atomic::Ordering::Relaxed);
        if let Some(p) = icons::start_icon() { a.icons.insert("start", p); }

        if !test && a.cfg.s.hide_taskbar {
            taskbar_hide_fast();
            std::thread::spawn(taskbar_hide); // the slow AppBar state change must not hold up the first frame
        }
        lap("pre-dock");
        a.dock.create(a.scale, &a.cfg);
        lap("dock.create");
        refresh_windows();
        lap("refresh_windows");

        for (mn, mx_, _) in [(3u32, 3u32, 0), (0x16, 0x17, 0), (0x8001, 0x8003, 0)] {
            SetWinEventHook(mn, mx_, None, Some(win_event), 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
        SetTimer(Some(msg), T_SLOW, 2500, None);
        SetTimer(Some(msg), T_CLAMP, 350, None);
        SetTimer(Some(msg), T_TRIGGER, 500, None);
        SetTimer(Some(msg), T_STATUS, 4000, None);
        // Ctrl+Alt+D focus-dock key is not ported; Spotlight = Alt+Space, Launchpad = Ctrl+Alt+L, Mission Control = Ctrl+Alt+Up
        log(&format!("ready: dock shown {} ms after main()", T0.get().map(|t| t.elapsed().as_millis()).unwrap_or(0)));
        SetTimer(Some(msg), T_LATE, 40, None);

        let mut m = MSG::default();
        while GetMessageW(&mut m, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&m);
            DispatchMessageW(&m);
        }
        if !test {
            release_reservations();
            set_taskbar_visible(true);
            reset_work_area();
        }
        log("exit");
    }
}

/// After sleep/hibernate: re-assert what Windows may have disturbed (taskbar, topmost z-order, display size,
/// window list). Nothing is rebuilt: icons, caches and workers stay as they were.
fn resume_recover() {
    let a = app();
    if !a.test && a.cfg.s.hide_taskbar {
        taskbar_reapply();
        taskbar_hide();
    }
    a.scale = dpi_scale();
    a.dock.relayout_all(a.scale, &a.cfg);
    a.bar.relayout(a.scale);
    a.island.relayout(a.scale);
    if !a.test {
        reserve();
    }
    refresh_windows();
    a.bar.dirty();
    let ms = RESUME_AT.lock().unwrap().take().map(|t| t.elapsed().as_millis()).unwrap_or(0);
    log(&format!("resume recovered {} ms after wake event", ms));
}

fn lap(what: &str) {
    if std::env::var("MACDOCK_PROFILE").is_ok() {
        log(&format!("  [{} ms] {}", T0.get().map(|t| t.elapsed().as_millis()).unwrap_or(0), what));
    }
}

// ---- self-healing: heartbeat watchdog + crash filter ----
static BEAT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
fn beat() {
    BEAT.store(unsafe { windows::Win32::System::SystemInformation::GetTickCount64() }, std::sync::atomic::Ordering::Relaxed);
}
fn relaunch() {
    use std::os::windows::process::CommandExt;
    let _ = std::process::Command::new("schtasks").args(["/run", "/tn", "MacDockGuard"]).creation_flags(0x08000000).spawn();
}
unsafe extern "system" fn crash_filter(_p: *const windows::Win32::System::Diagnostics::Debug::EXCEPTION_POINTERS) -> i32 {
    log("CRASH: unhandled exception, relaunching through MacDockGuard");
    relaunch();
    1 // EXCEPTION_EXECUTE_HANDLER
}
fn start_watchdog(test: bool) {
    beat();
    unsafe {
        windows::Win32::System::Diagnostics::Debug::SetUnhandledExceptionFilter(Some(crash_filter));
    }
    std::panic::set_hook(Box::new(|i| log(&format!("PANIC {}", i))));
    if test {
        return;
    }
    std::thread::spawn(|| loop {
        std::thread::sleep(std::time::Duration::from_secs(3));
        let now = unsafe { windows::Win32::System::SystemInformation::GetTickCount64() };
        let last = BEAT.load(std::sync::atomic::Ordering::Relaxed);
        if now.saturating_sub(last) > 25_000 {
            log("WATCHDOG: UI thread unresponsive for 25 s, restarting");
            relaunch();
            std::process::exit(3);
        }
    });
}
