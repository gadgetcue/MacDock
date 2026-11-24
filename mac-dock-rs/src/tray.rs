//! Notification-area (tray) icons in the menu bar (port of C# TrayIcons.cs).
//!
//! Windows 11's tray is XAML, so the tray buttons (SystemTray.NormalButton) are read through UI Automation: visible ones
//! from the taskbar, hidden ones from the overflow flyout, which a scan opens with Win+B, reads and closes again. Each
//! icon's name and a snapshot of its image are kept in <data>\tray. Clicking a bar icon focuses the app's window if it has
//! one; otherwise the button is invoked through UI Automation while the flyout is made fully transparent; right click
//! sends a real right click to the original button.
use crate::gfx::Pixels;
use std::collections::HashSet;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;
use windows::core::{Interface, BSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::UI::Accessibility::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Clone, Debug)]
pub struct TrayItem {
    pub key: String,
    pub title: String,
    pub hidden: bool,
}

/// Bumped whenever the stored icons change; the bar reloads its bitmaps when it differs from what it drew.
pub static VERSION: AtomicU32 = AtomicU32::new(1);
static BUSY: Mutex<()> = Mutex::new(());
static DIR: Mutex<Option<std::path::PathBuf>> = Mutex::new(None);

pub fn init(data_dir: &std::path::Path) {
    *DIR.lock().unwrap() = Some(data_dir.join("tray"));
}
fn dir() -> std::path::PathBuf {
    DIR.lock().unwrap().clone().unwrap_or_default()
}
fn file_for(key: &str) -> std::path::PathBuf {
    let n: String = key.chars().map(|c| if c.is_alphanumeric() { c } else { '_' }).collect();
    dir().join(format!("{}.px", n))
}

fn clean(name: &str) -> String {
    let first = name.replace('\r', "");
    let first = first.split('\n').next().unwrap_or("").trim().to_string();
    let first = first.split(':').next().unwrap_or("").to_string();
    let first = first.split(" - ").next().unwrap_or("").to_string();
    first.split_whitespace().filter(|w| !w.chars().any(|c| c.is_ascii_digit())).take(2).collect::<Vec<_>>().join(" ").trim().to_string()
}
fn key_of(clean: &str) -> String {
    clean.to_lowercase()
}

/// Saved icons (index.txt: key|title|hidden).
pub fn cached() -> Vec<TrayItem> {
    let text = std::fs::read_to_string(dir().join("index.txt")).unwrap_or_default();
    text.lines()
        .filter_map(|l| {
            let p: Vec<&str> = l.trim_start_matches('\u{feff}').split('|').collect();
            if p.len() >= 3 && !p[0].is_empty() { Some(TrayItem { key: p[0].to_string(), title: p[1].to_string(), hidden: p[2] == "1" }) } else { None }
        })
        .collect()
}

/// The snapshot of one icon (raw premultiplied BGRA: w, h as u32 LE, then pixels).
pub fn pixels_of(key: &str) -> Option<Pixels> {
    let b = std::fs::read(file_for(key)).ok()?;
    if b.len() < 8 {
        return None;
    }
    let w = u32::from_le_bytes(b[0..4].try_into().ok()?) as i32;
    let h = u32::from_le_bytes(b[4..8].try_into().ok()?) as i32;
    if w <= 0 || h <= 0 || b.len() < 8 + (w * h * 4) as usize {
        return None;
    }
    Some(Pixels { w, h, bgra: b[8..8 + (w * h * 4) as usize].to_vec() })
}

pub fn selected(cfg: &crate::config::Config) -> HashSet<String> {
    cfg.os("tray-icons", "").split('|').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect()
}
pub fn set_selected(cfg: &mut crate::config::Config, key: &str, on: bool) {
    let mut s = selected(cfg);
    if on { s.insert(key.to_string()); } else { s.remove(key); }
    let mut v: Vec<String> = s.into_iter().collect();
    v.sort();
    cfg.set_option("tray-icons", &v.join("|"));
    VERSION.fetch_add(1, Ordering::SeqCst);
}

// ---------- UI Automation helpers ----------

fn press(vk: VIRTUAL_KEY) {
    let mk = |up: bool| INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) }, ..Default::default() } } };
    unsafe {
        SendInput(&[mk(false), mk(true)], std::mem::size_of::<INPUT>() as i32);
    }
}
fn win_b() {
    let mk = |vk: VIRTUAL_KEY, up: bool| INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) }, ..Default::default() } } };
    unsafe {
        SendInput(&[mk(VK_LWIN, false), mk(VIRTUAL_KEY(0x42), false), mk(VIRTUAL_KEY(0x42), true), mk(VK_LWIN, true)], std::mem::size_of::<INPUT>() as i32);
    }
}

struct Ua {
    a: IUIAutomation,
}
impl Ua {
    fn new() -> Option<Ua> {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            let a: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER).ok()?;
            Some(Ua { a })
        }
    }
    fn cond(&self, cls: &str) -> Option<IUIAutomationCondition> {
        unsafe { self.a.CreatePropertyCondition(UIA_ClassNamePropertyId, &VARIANT::from(BSTR::from(cls))).ok() }
    }
    fn top(&self, cls: &str) -> Option<IUIAutomationElement> {
        unsafe {
            let root = self.a.GetRootElement().ok()?;
            root.FindFirst(TreeScope_Children, &self.cond(cls)?).ok()
        }
    }
    fn buttons(&self, top: &Option<IUIAutomationElement>) -> Vec<IUIAutomationElement> {
        let mut v = vec![];
        let (Some(t), Some(c)) = (top, self.cond("SystemTray.NormalButton")) else { return v };
        unsafe {
            if let Ok(arr) = t.FindAll(TreeScope_Descendants, &c) {
                for i in 0..arr.Length().unwrap_or(0) {
                    if let Ok(e) = arr.GetElement(i) {
                        v.push(e);
                    }
                }
            }
        }
        v
    }
    fn find(&self, top: &Option<IUIAutomationElement>, key: &str) -> Option<IUIAutomationElement> {
        let first = key.split(' ').next().unwrap_or("").to_string();
        let mut loose = None;
        for b in self.buttons(top) {
            let k = key_of(&clean(&unsafe { b.CurrentName() }.map(|s| s.to_string()).unwrap_or_default()));
            if k == key {
                return Some(b);
            }
            if loose.is_none() && !k.is_empty() && k.split(' ').next() == Some(first.as_str()) {
                loose = Some(b);
            }
        }
        loose
    }
    /// Centre of the button. Some buttons report an oversized rectangle overlapping neighbours; then only the
    /// top-left cell, sized like the sibling buttons, is the real icon.
    fn center(&self, b: &IUIAutomationElement, host: &Option<IUIAutomationElement>) -> (i32, i32) {
        let r = unsafe { b.CurrentBoundingRectangle() }.unwrap_or_default();
        if r.right <= r.left {
            return (0, 0);
        }
        let (mut w, mut h) = ((r.right - r.left) as f32, (r.bottom - r.top) as f32);
        let mut cell = 0f32;
        for o in self.buttons(host) {
            if let Ok(q) = unsafe { o.CurrentBoundingRectangle() } {
                let qw = (q.right - q.left) as f32;
                if qw >= 8.0 && qw <= 80.0 && (cell == 0.0 || qw < cell) {
                    cell = qw;
                }
            }
        }
        if cell == 0.0 {
            cell = 40.0;
        }
        if w > cell * 1.5 || h > cell * 1.5 {
            w = cell;
            h = cell;
        }
        ((r.left as f32 + w / 2.0) as i32, (r.top as f32 + h / 2.0) as i32)
    }
    /// Opens the overflow flyout (Win+B, Enter, like clicking the ^) and returns it.
    fn open_flyout(&self) -> Option<IUIAutomationElement> {
        let cls = "TopLevelWindowForOverflowXamlIsland";
        let ok = |s: &Ua, f: &Option<IUIAutomationElement>| !s.buttons(f).is_empty();
        let f = self.top(cls);
        if ok(self, &f) {
            return f;
        }
        win_b();
        std::thread::sleep(std::time::Duration::from_millis(250));
        press(VK_RETURN);
        for _ in 0..20 {
            std::thread::sleep(std::time::Duration::from_millis(100));
            let f = self.top(cls);
            if ok(self, &f) {
                return f;
            }
        }
        None
    }
}

fn snapshot(center: (i32, i32), scale: f32) -> Option<Pixels> {
    // capture exactly the icon's physical pixels (16 DIP), centred
    let size = ((16.0 * scale).round() as i32).max(16);
    let (x, y) = (center.0 - size / 2, center.1 - size / 2);
    unsafe {
        let sdc = GetDC(None);
        let mdc = CreateCompatibleDC(Some(sdc));
        let bmp = CreateCompatibleBitmap(sdc, size, size);
        let old = SelectObject(mdc, bmp.into());
        let ok = BitBlt(mdc, 0, 0, size, size, Some(sdc), x, y, SRCCOPY | CAPTUREBLT).is_ok();
        SelectObject(mdc, old);
        let _ = DeleteDC(mdc);
        ReleaseDC(None, sdc);
        let p = if ok { Pixels::from_hbitmap(bmp) } else { None };
        let _ = DeleteObject(bmp.into());
        // a screen grab has no alpha: make it opaque
        p.map(|mut p| {
            for c in p.bgra.chunks_exact_mut(4) {
                c[3] = 255;
            }
            p
        })
    }
}

/// Scans the taskbar tray and the overflow flyout, then stores names and image snapshots. Opens the flyout briefly,
/// so run it on request (when the option is turned on or Rescan is pressed). `notify` gets WM_STATUS when done.
pub fn scan(notify: isize, scale: f32) {
    std::thread::spawn(move || {
        let _g = BUSY.lock().unwrap();
        let Some(ua) = Ua::new() else { return };
        let _ = std::fs::create_dir_all(dir());
        let bar = ua.top("Shell_TrayWnd");
        let mut found: Vec<(IUIAutomationElement, bool)> = ua.buttons(&bar).into_iter().map(|b| (b, false)).collect();
        let fly = ua.open_flyout();
        let opened = fly.is_some();
        if opened {
            found.extend(ua.buttons(&fly).into_iter().map(|b| (b, true)));
        }
        let mut lines = vec![];
        let mut seen = HashSet::new();
        for (b, hidden) in &found {
            let title = clean(&unsafe { b.CurrentName() }.map(|s| s.to_string()).unwrap_or_default());
            if title.is_empty() {
                continue;
            }
            let key = key_of(&title);
            if !seen.insert(key.clone()) {
                continue;
            }
            let host = if *hidden { &fly } else { &bar };
            let c = ua.center(b, host);
            if c != (0, 0) {
                if let Some(p) = snapshot(c, scale) {
                    let mut out = Vec::with_capacity(8 + p.bgra.len());
                    out.extend_from_slice(&(p.w as u32).to_le_bytes());
                    out.extend_from_slice(&(p.h as u32).to_le_bytes());
                    out.extend_from_slice(&p.bgra);
                    let _ = std::fs::write(file_for(&key), out);
                }
            }
            lines.push(format!("{}|{}|{}", key, title, if *hidden { "1" } else { "0" }));
        }
        if opened {
            press(VK_ESCAPE);
        }
        if !lines.is_empty() {
            let _ = std::fs::write(dir().join("index.txt"), lines.join("\r\n"));
        }
        VERSION.fetch_add(1, Ordering::SeqCst);
        unsafe {
            let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_STATUS, WPARAM(0), LPARAM(0));
        }
    });
}

fn norm(s: &str) -> String {
    s.chars().filter(|c| c.is_alphanumeric()).collect::<String>().to_lowercase()
}

/// The app's main window (process name or title matching the tray name), from the dock's window list.
pub fn find_app_window(key: &str, wins: &[crate::sys::Win]) -> Option<isize> {
    let k = norm(key);
    let first = norm(key.split(' ').next().unwrap_or(""));
    if k.len() < 3 {
        return None;
    }
    let mut best: Option<(u8, isize)> = None;
    for w in wins {
        let stem = norm(&std::path::Path::new(&w.exe).file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default());
        if matches!(stem.as_str(), "explorer" | "applicationframehost" | "textinputhost") {
            continue;
        }
        let t = norm(&w.title);
        let sc = if stem == k || stem == first { 3 } else if stem.len() >= 4 && (k.starts_with(&stem) || stem.starts_with(&first)) { 2 } else if !t.is_empty() && t.contains(&k) { 1 } else { 0 };
        if sc > 0 && best.map(|b| sc > b.0).unwrap_or(true) {
            best = Some((sc, w.hwnd));
        }
    }
    best.map(|b| b.1)
}

/// Click on a bar icon. mode 0 left, 1 right, 2 double. `window` is the app's own window when it has one.
pub fn click(key: String, mode: u8, window: Option<isize>) {
    if mode != 1 {
        if let Some(h) = window {
            crate::sys::activate(h);
            return;
        }
    }
    std::thread::spawn(move || {
        let _g = BUSY.lock().unwrap();
        let Some(ua) = Ua::new() else { return };
        if mode == 0 {
            invoke_hidden(&ua, &key);
            return;
        }
        let mut flyout = false;
        for _ in 0..4 {
            let mut host = ua.top("Shell_TrayWnd");
            let mut button = ua.find(&host, &key);
            if button.is_none() {
                let f = ua.open_flyout();
                if f.is_some() {
                    flyout = true;
                }
                host = f;
                button = ua.find(&host, &key);
            }
            let Some(b) = button else {
                std::thread::sleep(std::time::Duration::from_millis(200));
                continue;
            };
            let mut c = ua.center(&b, &host);
            // wait until the layout is stable (the flyout animates while it opens)
            for _ in 0..8 {
                std::thread::sleep(std::time::Duration::from_millis(120));
                let Some(b2) = ua.find(&host, &key) else { break };
                let c2 = ua.center(&b2, &host);
                let same = (c2.0 - c.0).abs() < 2 && (c2.1 - c.1).abs() < 2;
                c = c2;
                if same {
                    break;
                }
            }
            if c.0 <= 0 && c.1 <= 0 {
                std::thread::sleep(std::time::Duration::from_millis(200));
                continue;
            }
            unsafe {
                let mut old = POINT::default();
                let _ = GetCursorPos(&mut old);
                let _ = SetCursorPos(c.0, c.1);
                std::thread::sleep(std::time::Duration::from_millis(80));
                let (dn, up) = if mode == 1 { (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP) } else { (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP) };
                for n in 0..(if mode == 2 { 2 } else { 1 }) {
                    mouse_event(dn, 0, 0, 0, 0);
                    mouse_event(up, 0, 0, 0, 0);
                    if n == 0 && mode == 2 {
                        std::thread::sleep(std::time::Duration::from_millis(60));
                    }
                }
                std::thread::sleep(std::time::Duration::from_millis(120));
                let _ = SetCursorPos(old.x, old.y);
            }
            return;
        }
        if flyout {
            press(VK_ESCAPE);
        }
    });
}

/// Left click for apps without a window: invoke the button while the flyout is fully transparent, so the app gets its
/// tray click and the Windows flyout never shows.
fn invoke_hidden(ua: &Ua, key: &str) {
    let mut flyout = false;
    for _ in 0..3 {
        let host = ua.top("Shell_TrayWnd");
        let mut button = ua.find(&host, key);
        if button.is_none() {
            let Some(f) = ua.open_flyout() else {
                std::thread::sleep(std::time::Duration::from_millis(200));
                continue;
            };
            flyout = true;
            unsafe {
                if let Ok(h) = f.CurrentNativeWindowHandle() {
                    let hw = HWND(h.0);
                    SetWindowLongPtrW(hw, GWL_EXSTYLE, GetWindowLongPtrW(hw, GWL_EXSTYLE) | 0x80000);
                    let _ = SetLayeredWindowAttributes(hw, COLORREF(0), 0, LWA_ALPHA);
                }
            }
            button = ua.find(&Some(f), key);
        }
        let Some(b) = button else {
            std::thread::sleep(std::time::Duration::from_millis(200));
            continue;
        };
        unsafe {
            if let Ok(p) = b.GetCurrentPattern(UIA_InvokePatternId) {
                if let Ok(ip) = p.cast::<IUIAutomationInvokePattern>() {
                    let _ = ip.Invoke();
                }
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(400));
        if flyout {
            // dismiss the (invisible) flyout so it does not keep focus and swallow the next click
            for _ in 0..4 {
                if !flyout_focused(ua) {
                    break;
                }
                press(VK_ESCAPE);
                std::thread::sleep(std::time::Duration::from_millis(150));
            }
        }
        return;
    }
    if flyout && flyout_focused(ua) {
        press(VK_ESCAPE);
    }
}

fn flyout_focused(ua: &Ua) -> bool {
    unsafe {
        let Some(f) = ua.top("TopLevelWindowForOverflowXamlIsland") else { return false };
        let fh = f.CurrentNativeWindowHandle().map(|h| h.0 as isize).unwrap_or(0);
        let fg = GetForegroundWindow().0 as isize;
        if fg == 0 || fg == fh {
            return fg == fh && fh != 0;
        }
        let (mut p1, mut p2) = (0u32, 0u32);
        GetWindowThreadProcessId(HWND(fg as *mut _), Some(&mut p1));
        GetWindowThreadProcessId(HWND(fh as *mut _), Some(&mut p2));
        p1 == p2
    }
}
