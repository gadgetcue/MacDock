//! Window/process plumbing: enumerate app windows, map to exe, launch, activate, taskbar hide, DPI, work area.
use crate::util::*;
use std::collections::HashMap;
use windows::core::{w, PCWSTR, PWSTR};
use windows::Win32::Foundation::*;
use windows::core::BOOL;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Clone, Debug)]
pub struct Win {
    pub hwnd: isize,
    pub pid: u32,
    pub title: String,
    pub exe: String, // lower-case full path
    pub minimized: bool,
    pub aumid: String, // only filled for Chromium browsers (web apps)
}

pub fn hw(h: isize) -> HWND {
    HWND(h as *mut _)
}
pub fn hv(h: HWND) -> isize {
    h.0 as isize
}

thread_local! {
    static PATHS: std::cell::RefCell<HashMap<u32, String>> = std::cell::RefCell::new(HashMap::new());
}

pub fn exe_of_pid(pid: u32) -> String {
    if let Some(p) = PATHS.with(|m| m.borrow().get(&pid).cloned()) {
        return p;
    }
    let mut out = String::new();
    unsafe {
        if let Ok(h) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            let mut buf = [0u16; 1024];
            let mut n = buf.len() as u32;
            if QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut n).is_ok() {
                out = String::from_utf16_lossy(&buf[..n as usize]).to_lowercase();
            }
            let _ = CloseHandle(h);
        }
    }
    if !out.is_empty() {
        PATHS.with(|m| m.borrow_mut().insert(pid, out.clone()));
    }
    out
}
pub fn forget_pids(alive: &[u32]) {
    PATHS.with(|m| m.borrow_mut().retain(|k, _| alive.contains(k)));
}

struct EnumCtx {
    out: Vec<Win>,
    me: u32,
}

unsafe extern "system" fn enum_cb(hwnd: HWND, lp: LPARAM) -> BOOL {
    let ctx = &mut *(lp.0 as *mut EnumCtx);
    if !IsWindowVisible(hwnd).as_bool() && !IsIconic(hwnd).as_bool() {
        return TRUE;
    }
    let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
    if ex & WS_EX_TOOLWINDOW.0 != 0 && ex & WS_EX_APPWINDOW.0 == 0 {
        return TRUE;
    }
    if ex & WS_EX_NOACTIVATE.0 != 0 && ex & WS_EX_APPWINDOW.0 == 0 {
        return TRUE;
    }
    if let Ok(o) = GetWindow(hwnd, GW_OWNER) {
        if !o.is_invalid() && ex & WS_EX_APPWINDOW.0 == 0 {
            return TRUE;
        }
    }
    let mut cloaked = 0u32;
    let _ = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4);
    if cloaked != 0 {
        return TRUE;
    }
    let mut tb = [0u16; 256];
    if IsHungAppWindow(hwnd).as_bool() {
        return TRUE; // GetWindowText would block on a hung app and freeze the dock
    }
    let n = GetWindowTextW(hwnd, &mut tb);
    if n <= 0 {
        return TRUE;
    }
    let title = String::from_utf16_lossy(&tb[..n as usize]);
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == ctx.me || pid == 0 {
        return TRUE;
    }
    let mut cls = [0u16; 64];
    let cn = GetClassNameW(hwnd, &mut cls);
    let class = String::from_utf16_lossy(&cls[..cn.max(0) as usize]);
    if class == "Progman" || class == "WorkerW" || class == "Shell_TrayWnd" || class == "Windows.UI.Core.CoreWindow" {
        return TRUE;
    }
    let mut rpid = pid;
    if class == "ApplicationFrameWindow" {
        // UWP: the real process owns a child CoreWindow
        let mut found = 0u32;
        let mut ctx2 = (pid, &mut found as *mut u32);
        let _ = EnumChildWindows(Some(hwnd), Some(child_cb), LPARAM(&mut ctx2 as *mut _ as isize));
        if found != 0 {
            rpid = found;
        } else if IsIconic(hwnd).as_bool() == false {
            // suspended UWP frames with no core window are ghosts
            return TRUE;
        }
    }
    let exe = exe_of_pid(rpid);
    if exe.is_empty() {
        return TRUE;
    }
    let fname = exe.rsplit(char::from(92u8)).next().unwrap_or("");
    let chromium = fname == "brave.exe" || fname == "chrome.exe" || fname == "msedge.exe";
    let aumid = if chromium { window_aumid(hwnd.0 as isize) } else { String::new() };
    ctx.out.push(Win { hwnd: hwnd.0 as isize, pid: rpid, title, exe, minimized: IsIconic(hwnd).as_bool(), aumid });
    TRUE
}

unsafe extern "system" fn child_cb(hwnd: HWND, lp: LPARAM) -> BOOL {
    let ctx = &mut *(lp.0 as *mut (u32, *mut u32));
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid != 0 && pid != ctx.0 {
        *ctx.1 = pid;
        return FALSE;
    }
    TRUE
}

pub fn enum_windows() -> Vec<Win> {
    let mut ctx = EnumCtx { out: Vec::new(), me: unsafe { GetCurrentProcessId() } };
    unsafe {
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut ctx as *mut _ as isize));
    }
    let pids: Vec<u32> = ctx.out.iter().map(|w| w.pid).collect();
    forget_pids(&pids);
    ctx.out
}

pub fn is_fullscreen_foreground() -> bool {
    unsafe {
        let fg = GetForegroundWindow();
        if fg.is_invalid() {
            return false;
        }
        let mut cls = [0u16; 64];
        let n = GetClassNameW(fg, &mut cls);
        let c = String::from_utf16_lossy(&cls[..n.max(0) as usize]);
        if c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" {
            return false;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(fg, Some(&mut pid));
        if pid == GetCurrentProcessId() {
            return false;
        }
        let mut r = RECT::default();
        if GetWindowRect(fg, &mut r).is_err() {
            return false;
        }
        let mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        if !GetMonitorInfoW(mon, &mut mi).as_bool() {
            return false;
        }
        let m = mi.rcMonitor;
        // exact cover of the monitor, no caption (maximized windows keep a frame offset)
        let style = GetWindowLongPtrW(fg, GWL_STYLE) as u32;
        r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom && style & WS_CAPTION.0 != WS_CAPTION.0
    }
}

pub fn foreground_info() -> Option<(isize, String)> {
    unsafe {
        let fg = GetForegroundWindow();
        if fg.is_invalid() {
            return None;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(fg, Some(&mut pid));
        Some((fg.0 as isize, exe_of_pid(pid)))
    }
}

pub fn activate(h: isize) {
    unsafe {
        let hwnd = hw(h);
        if !IsWindow(Some(hwnd)).as_bool() {
            return;
        }
        if IsIconic(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }
        // Alt tap lets SetForegroundWindow through the foreground lock.
        let inputs = [
            INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VK_MENU, dwFlags: KEYBD_EVENT_FLAGS(0), ..Default::default() } } },
            INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VK_MENU, dwFlags: KEYEVENTF_KEYUP, ..Default::default() } } },
        ];
        let _ = SendInput(&inputs, std::mem::size_of::<INPUT>() as i32);
        let _ = SetForegroundWindow(hwnd);
        let _ = BringWindowToTop(hwnd);
    }
}
pub fn minimize(h: isize) {
    unsafe {
        let _ = ShowWindow(hw(h), SW_MINIMIZE);
    }
}
pub fn close_window(h: isize) {
    unsafe {
        let _ = PostMessageW(Some(hw(h)), WM_CLOSE, WPARAM(0), LPARAM(0));
    }
}
/// Force quit: kills only the target app's pid, never our own.
pub fn kill_pid(pid: u32) {
    unsafe {
        if pid == GetCurrentProcessId() {
            return;
        }
        if let Ok(h) = OpenProcess(PROCESS_TERMINATE, false, pid) {
            let _ = TerminateProcess(h, 1);
            let _ = CloseHandle(h);
        }
    }
}

pub fn shell_open(target: &str) {
    shell_verb("open", target, None);
}
pub fn shell_verb(verb: &str, target: &str, params: Option<&str>) {
    let v = wide(verb);
    let t = wide(target);
    let p = params.map(wide);
    // run from the target's folder when it is a plain path
    let dir = std::path::Path::new(target).parent().filter(|d| d.is_dir()).map(|d| wide(&d.to_string_lossy()));
    unsafe {
        let _ = ShellExecuteW(None, pw(&v), pw(&t), p.as_ref().map(|p| pw(p)).unwrap_or(PCWSTR::null()), dir.as_ref().map(|d| pw(d)).unwrap_or(PCWSTR::null()), SW_SHOWNORMAL);
    }
}
pub fn run_cmd(exe: &str, args: &str) {
    shell_verb("open", exe, Some(args));
}

pub fn dpi_scale() -> f32 {
    unsafe {
        let d = GetDpiForSystem();
        if d == 0 { 1.0 } else { d as f32 / 96.0 }
    }
}
pub fn screen_rect() -> RECT {
    unsafe {
        let mon = MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let _ = GetMonitorInfoW(mon, &mut mi);
        mi.rcMonitor
    }
}

// ---- taskbar: same approach as the C# TaskbarControl ----
// The real taskbar state is switched to auto-hide (so Explorer itself keeps it out of the way, hover included),
// every Shell_TrayWnd / Shell_SecondaryTrayWnd (all monitors) is hidden, and the user's own state is saved to
// taskbar-state.txt so it is restored exactly on exit (or by `MacDock.exe /restore` after a kill).
static TB_HIDDEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static TB_PREV: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(2);

fn tray_windows() -> Vec<HWND> {
    let mut v = vec![];
    unsafe {
        if let Ok(h) = FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) {
            v.push(h);
        }
        let mut cur: Option<HWND> = None;
        while let Ok(h) = FindWindowExW(None, cur, w!("Shell_SecondaryTrayWnd"), PCWSTR::null()) {
            v.push(h);
            cur = Some(h);
        }
    }
    v
}
fn tb_state_path() -> std::path::PathBuf {
    crate::util::base_dir().join("taskbar-state.txt")
}
fn appbar(tray: HWND, msg: u32, state: usize) -> usize {
    unsafe {
        let mut d = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, hWnd: tray, lParam: LPARAM(state as isize), ..Default::default() };
        SHAppBarMessage(msg, &mut d)
    }
}
fn hide_tray_windows() {
    unsafe {
        for h in tray_windows() {
            if IsWindowVisible(h).as_bool() {
                let _ = ShowWindowAsync(h, SW_HIDE);
            }
        }
    }
}
pub fn taskbar_hide() {
    use std::sync::atomic::Ordering::SeqCst;
    let Ok(tray) = (unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) }) else { return };
    if !TB_HIDDEN.load(SeqCst) {
        let mut prev = appbar(tray, 4, 0) as u32; // ABM_GETSTATE
        if let Some(sv) = std::fs::read_to_string(tb_state_path()).ok().and_then(|t| t.trim().parse::<u32>().ok()) {
            prev = sv; // a previous instance saved the user's real state
        }
        let _ = std::fs::write(tb_state_path(), prev.to_string());
        TB_PREV.store(prev, SeqCst);
        appbar(tray, 10, (prev | 1) as usize); // ABM_SETSTATE: ABS_AUTOHIDE
        TB_HIDDEN.store(true, SeqCst);
    }
    hide_tray_windows();
}
/// Explorer can bring the taskbar back (restart, wake, display change): called on every refresh.
pub fn taskbar_ensure() {
    if TB_HIDDEN.load(std::sync::atomic::Ordering::SeqCst) {
        hide_tray_windows();
    }
}
/// After an Explorer restart the shell forgot our auto-hide state: apply it again.
pub fn taskbar_reapply() {
    use std::sync::atomic::Ordering::SeqCst;
    if TB_HIDDEN.load(SeqCst) {
        TB_HIDDEN.store(false, SeqCst);
        taskbar_hide();
    }
}
pub fn taskbar_restore() {
    use std::sync::atomic::Ordering::SeqCst;
    let prev = if TB_HIDDEN.load(SeqCst) { TB_PREV.load(SeqCst) } else { std::fs::read_to_string(tb_state_path()).ok().and_then(|t| t.trim().parse::<u32>().ok()).unwrap_or(2) };
    unsafe {
        for h in tray_windows() {
            let _ = ShowWindowAsync(h, SW_SHOWNA);
        }
        if let Ok(tray) = FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) {
            appbar(tray, 10, prev as usize);
        }
        let mut res = 0usize;
        let t = wide("TraySettings");
        let _ = SendMessageTimeoutW(HWND(0xFFFF as *mut _), 0x1A, WPARAM(0), LPARAM(t.as_ptr() as isize), SMTO_ABORTIFHUNG, 500, Some(&mut res));
    }
    TB_HIDDEN.store(false, SeqCst);
    let _ = std::fs::remove_file(tb_state_path());
}
pub fn set_taskbar_visible(show: bool) {
    if show {
        taskbar_restore();
    } else {
        taskbar_hide();
    }
}
/// Reserve the menu bar strip so maximized windows do not slide under it.
pub fn set_work_area(top: i32, bottom: i32) {
    unsafe {
        let s = screen_rect();
        let mut r = RECT { left: s.left, top: s.top + top, right: s.right, bottom: s.bottom - bottom };
        let _ = SystemParametersInfoW(SPI_SETWORKAREA, 0, Some(&mut r as *mut _ as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0));
    }
}
/// Register `hwnd` as an AppBar on the top (edge 1) or bottom (edge 3) of `mon`, `thick` px tall; 0 removes it.
/// Windows then keeps maximized/snapped windows out of the strip, like the C# build.
pub fn appbar_reserve(hwnd: HWND, edge: u32, thick: i32, mon: RECT) {
    use windows::Win32::UI::Shell::*;
    use std::sync::Mutex;
    static REG: Mutex<Vec<isize>> = Mutex::new(Vec::new());
    unsafe {
        let mut d = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, hWnd: hwnd, uCallbackMessage: 0, uEdge: edge, ..Default::default() };
        let key = hwnd.0 as isize;
        let mut reg = REG.lock().unwrap();
        let known = reg.contains(&key);
        if thick <= 0 {
            if known {
                SHAppBarMessage(ABM_REMOVE, &mut d);
                reg.retain(|k| *k != key);
            }
            return;
        }
        if !known {
            SHAppBarMessage(ABM_NEW, &mut d);
            reg.push(key);
        }
        let mut rc = mon;
        if edge == 1 {
            rc.bottom = rc.top + thick;
        } else {
            rc.top = rc.bottom - thick;
        }
        d.rc = rc;
        SHAppBarMessage(ABM_QUERYPOS, &mut d);
        if edge == 1 {
            d.rc.bottom = d.rc.top + thick;
        } else {
            d.rc.top = d.rc.bottom - thick;
        }
        SHAppBarMessage(ABM_SETPOS, &mut d);
    }
}
pub fn reset_work_area() {
    let s = screen_rect();
    unsafe {
        let mut r = s;
        // leave room for the real taskbar when it is visible
        if let Ok(h) = FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) {
            let mut tr = RECT::default();
            if IsWindowVisible(h).as_bool() && GetWindowRect(h, &mut tr).is_ok() && tr.top > s.top + 100 {
                r.bottom = tr.top;
            }
        }
        let _ = SystemParametersInfoW(SPI_SETWORKAREA, 0, Some(&mut r as *mut _ as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0));
    }
}

pub fn cursor_pos() -> POINT {
    let mut p = POINT::default();
    unsafe {
        let _ = GetCursorPos(&mut p);
    }
    p
}

pub fn set_acrylic(hwnd: HWND, tint_abgr: u32, flat: bool) {
    #[repr(C)]
    struct Accent {
        state: i32,
        flags: i32,
        color: u32,
        anim: i32,
    }
    #[repr(C)]
    struct Wca {
        attr: i32,
        data: *mut std::ffi::c_void,
        size: u32,
    }
    unsafe {
        let Ok(lib) = windows::Win32::System::LibraryLoader::GetModuleHandleW(w!("user32.dll")) else { return };
        let Some(f) = windows::Win32::System::LibraryLoader::GetProcAddress(lib, windows::core::s!("SetWindowCompositionAttribute")) else { return };
        let f: extern "system" fn(HWND, *mut Wca) -> i32 = std::mem::transmute(f);
        let mut a = Accent { state: if flat { 2 } else { 4 }, flags: 2, color: tint_abgr, anim: 0 };
        let mut d = Wca { attr: 19, data: &mut a as *mut _ as *mut _, size: std::mem::size_of::<Accent>() as u32 };
        f(hwnd, &mut d);
    }
}
pub fn round_corners(hwnd: HWND, w: i32, h: i32, r: i32) {
    unsafe {
        let rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
        SetWindowRgn(hwnd, Some(rgn), true);
    }
}

thread_local! {
    static THEMED: std::cell::RefCell<(bool, std::collections::HashSet<isize>)> = std::cell::RefCell::new((true, Default::default()));
}
/// Dark/light title bars on other apps' windows (DWMWA_USE_IMMERSIVE_DARK_MODE), once per window and theme.
pub fn apply_window_theme(wins: &[Win], dark: bool) {
    THEMED.with(|t| {
        let mut t = t.borrow_mut();
        if t.0 != dark {
            t.0 = dark;
            t.1.clear();
        }
        for w in wins {
            if t.1.insert(w.hwnd) {
                let v: i32 = if dark { 1 } else { 0 };
                unsafe {
                    let _ = DwmSetWindowAttribute(hw(w.hwnd), DWMWINDOWATTRIBUTE(20), &v as *const _ as *const _, 4);
                }
            }
        }
        if t.1.len() > 2000 {
            t.1.clear();
        }
    });
}
pub fn reset_window_theme() {
    THEMED.with(|t| t.borrow_mut().1.clear());
}

thread_local! {
    static AUMIDS: std::cell::RefCell<HashMap<isize, String>> = std::cell::RefCell::new(HashMap::new());
}
/// AppUserModelID of a window (Chromium web apps carry `Brave._crx_...`); cached per window, only asked for
/// Chromium-based browsers.
pub fn window_aumid(h: isize) -> String {
    if let Some(v) = AUMIDS.with(|m| m.borrow().get(&h).cloned()) {
        return v;
    }
    let v = unsafe {
        use windows::Win32::UI::Shell::PropertiesSystem::*;
        let mut out = String::new();
        if let Ok(store) = SHGetPropertyStoreForWindow::<IPropertyStore>(hw(h)) {
            let key = PROPERTYKEY { fmtid: windows::core::GUID::from_u128(0x9F4C2855_9F79_4B39_A8D0_E1D42DE1D5F3), pid: 5 };
            if let Ok(pv) = store.GetValue(&key) {
                if let Ok(p) = windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc(&pv) {
                    out = p.to_string().unwrap_or_default().to_lowercase();
                    windows::Win32::System::Com::CoTaskMemFree(Some(p.0 as *const _));
                }
            }
        }
        out
    };
    AUMIDS.with(|m| {
        let mut m = m.borrow_mut();
        if m.len() > 3000 {
            m.clear();
        }
        m.insert(h, v.clone());
    });
    v
}

/// Instant part of hiding the taskbar: hide the windows (async), nothing that waits for Explorer.
pub fn taskbar_hide_fast() {
    hide_tray_windows();
}
