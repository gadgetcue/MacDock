//! Keeps app windows between the menu bar and the dock, like the C# DockClamp. The AppBar reservations (sys::appbar_*)
//! already move the work area; this is the backstop for apps that ignore it (custom frames, windows restored to
//! full-screen sizes, windows opened before the reservation, windows dragged under the bar).
use crate::sys::*;
use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use windows::Win32::Foundation::*;
use windows::core::BOOL;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::Accessibility::*;
use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows::Win32::UI::WindowsAndMessaging::*;

/// Screen y of the top of the dock's reserved strip (0 = nothing reserved).
pub static DOCK_TOP: AtomicI32 = AtomicI32::new(0);
/// Screen y of the bottom of the menu bar (0 = off).
pub static BAR_BOTTOM: AtomicI32 = AtomicI32::new(0);
static MON: [AtomicI32; 4] = [AtomicI32::new(0), AtomicI32::new(0), AtomicI32::new(0), AtomicI32::new(0)];
static CLAMPING: AtomicBool = AtomicBool::new(false);

thread_local! {
    static TRIES: RefCell<HashMap<isize, i32>> = RefCell::new(HashMap::new());
    static HOOK: RefCell<Option<HWINEVENTHOOK>> = RefCell::new(None);
}

fn mon() -> RECT {
    RECT { left: MON[0].load(Ordering::Relaxed), top: MON[1].load(Ordering::Relaxed), right: MON[2].load(Ordering::Relaxed), bottom: MON[3].load(Ordering::Relaxed) }
}
pub fn set(dock_top: i32, bar_bottom: i32, monitor: RECT) {
    DOCK_TOP.store(dock_top, Ordering::Relaxed);
    BAR_BOTTOM.store(bar_bottom, Ordering::Relaxed);
    MON[0].store(monitor.left, Ordering::Relaxed);
    MON[1].store(monitor.top, Ordering::Relaxed);
    MON[2].store(monitor.right, Ordering::Relaxed);
    MON[3].store(monitor.bottom, Ordering::Relaxed);
    start_hook();
}

fn start_hook() {
    HOOK.with(|h| {
        if h.borrow().is_none() {
            // EVENT_OBJECT_LOCATIONCHANGE: a window cannot be dragged over the bar at all
            let hk = unsafe { SetWinEventHook(0x800B, 0x800B, None, Some(on_move), 0, 0, 0x0002) };
            *h.borrow_mut() = Some(hk);
        }
    });
}

fn class_of(h: HWND) -> String {
    let mut cls = [0u16; 64];
    let n = unsafe { GetClassNameW(h, &mut cls) };
    String::from_utf16_lossy(&cls[..n.max(0) as usize])
}
fn visible_frame(h: HWND) -> Option<(RECT, RECT)> {
    unsafe {
        let mut w = RECT::default();
        GetWindowRect(h, &mut w).ok()?;
        let mut vis = RECT::default();
        if DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, &mut vis as *mut _ as *mut _, 16).is_err() {
            vis = w;
        }
        Some((w, vis))
    }
}

unsafe extern "system" fn on_move(_h: HWINEVENTHOOK, _ev: u32, hwnd: HWND, id_obj: i32, _c: i32, _t: u32, _ms: u32) {
    let bar = BAR_BOTTOM.load(Ordering::Relaxed);
    if id_obj != 0 || bar == 0 || hwnd.is_invalid() || CLAMPING.swap(true, Ordering::SeqCst) {
        return;
    }
    let _ = (|| -> Option<()> {
        if GetAncestor(hwnd, GA_ROOT) != hwnd || !IsWindowVisible(hwnd).as_bool() || IsIconic(hwnd).as_bool() || IsZoomed(hwnd).as_bool() {
            return None;
        }
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        let st = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
        if ex & 0x80 != 0 && ex & 0x40000 == 0 {
            return None;
        }
        if ex & 0x8 != 0 {
            return None; // topmost popups / toasts
        }
        if st & 0x00C0_0000 == 0 && st & 0x0004_0000 == 0 {
            return None;
        }
        let (w, vis) = visible_frame(hwnd)?;
        let m = mon();
        if !(vis.left + vis.right >= 2 * m.left && (vis.left + vis.right) / 2 <= m.right) {
            return None;
        }
        if vis.top >= bar || vis.bottom <= bar + 40 {
            return None;
        }
        let c = class_of(hwnd);
        if matches!(c.as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd") {
            return None;
        }
        let _ = SetWindowPos(hwnd, None, w.left, w.top + (bar - vis.top), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Some(())
    })();
    CLAMPING.store(false, Ordering::SeqCst);
}

struct Ctx {
    fix: Vec<isize>,
    lower: Vec<isize>,
    me: u32,
    top: i32,
    bar: i32,
    mon: RECT,
}

unsafe extern "system" fn enum_cb(h: HWND, lp: LPARAM) -> BOOL {
    let c = &mut *(lp.0 as *mut Ctx);
    if !IsWindowVisible(h).as_bool() || IsIconic(h).as_bool() {
        return TRUE;
    }
    let ex = GetWindowLongPtrW(h, GWL_EXSTYLE) as u32;
    let st = GetWindowLongPtrW(h, GWL_STYLE) as u32;
    if ex & 0x80 != 0 && ex & 0x40000 == 0 {
        return TRUE; // tool window
    }
    if st & 0x00C0_0000 == 0 && st & 0x0004_0000 == 0 {
        return TRUE; // no caption and no sizing frame: fullscreen / borderless, leave it
    }
    let mut cloaked = 0u32;
    let _ = DwmGetWindowAttribute(h, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4);
    if cloaked != 0 {
        return TRUE;
    }
    let zoomed = IsZoomed(h).as_bool();
    if ex & 0x8 != 0 && !zoomed {
        return TRUE; // topmost popups / toasts: never move or resize
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(h, Some(&mut pid));
    if pid == c.me {
        return TRUE;
    }
    if matches!(class_of(h).as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd") {
        return TRUE;
    }
    let Some((wr, r)) = visible_frame(h) else { return TRUE };
    let (cx, cy) = ((r.left + r.right) / 2, (r.top + r.bottom) / 2);
    let m = c.mon;
    if cx < m.left || cx >= m.right || cy < m.top || cy >= m.bottom {
        return TRUE;
    }
    let tall = zoomed || (wr.top <= m.top + 2 && wr.bottom >= m.bottom - 2);
    if c.bar > 0 && !zoomed && r.top < c.bar && r.bottom > c.bar + 40 {
        c.lower.push(h.0 as isize);
    }
    if c.top > 0 && r.bottom > c.top + 1 && (tall || r.top < c.top - 60) {
        c.fix.push(h.0 as isize);
    } else {
        TRIES.with(|t| t.borrow_mut().remove(&(h.0 as isize)));
    }
    TRUE
}

/// Every ~350 ms: shorten windows that reach into the dock strip, slide windows that sit under the bar.
pub fn tick() {
    let top = DOCK_TOP.load(Ordering::Relaxed);
    let bar = BAR_BOTTOM.load(Ordering::Relaxed);
    if top == 0 && bar == 0 {
        return;
    }
    let m = mon();
    let mut ctx = Ctx { fix: vec![], lower: vec![], me: unsafe { GetCurrentProcessId() }, top, bar, mon: m };
    unsafe {
        let _ = EnumWindows(Some(enum_cb), LPARAM(&mut ctx as *mut _ as isize));
        let dragging = GetAsyncKeyState(0x01) as u16 & 0x8000 != 0;
        if !ctx.lower.is_empty() && !dragging {
            for h in &ctx.lower {
                let hwnd = hw(*h);
                if let Some((w, vis)) = visible_frame(hwnd) {
                    let down = bar - vis.top;
                    if down > 0 {
                        let _ = SetWindowPos(hwnd, None, w.left, w.top + down, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                }
            }
        }
        for h in &ctx.fix {
            let n = TRIES.with(|t| *t.borrow().get(h).unwrap_or(&0));
            if n >= 4 {
                continue; // the app keeps resizing itself back (or it is elevated): stop fighting
            }
            TRIES.with(|t| t.borrow_mut().insert(*h, n + 1));
            let hwnd = hw(*h);
            let Some((w, vis)) = visible_frame(hwnd) else { continue };
            let over = vis.bottom - top;
            if over <= 0 {
                continue;
            }
            let zoomed = IsZoomed(hwnd).as_bool();
            if !zoomed && !(w.top <= m.top + 2 && w.bottom >= m.bottom - 2) {
                // ordinary window: do not fight a drag; slide up first, shrink only if it still does not fit
                if dragging {
                    TRIES.with(|t| t.borrow_mut().insert(*h, n));
                    continue;
                }
                let mut wa = RECT::default();
                let _ = SystemParametersInfoW(SPI_GETWORKAREA, 0, Some(&mut wa as *mut _ as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0));
                let up = over.min((vis.top - wa.top).max(0));
                let mut rest = over - up;
                if rest > 0 && (w.bottom - w.top) < (m.bottom - m.top) * 2 / 3 {
                    rest = 0; // small windows / popups: slide only, never shrink
                }
                let _ = SetWindowPos(hwnd, None, w.left, w.top - up, w.right - w.left, (w.bottom - w.top) - rest, SWP_NOZORDER | SWP_NOACTIVATE);
                TRIES.with(|t| t.borrow_mut().remove(h));
                continue;
            }
            let _ = SetWindowPos(hwnd, None, 0, 0, w.right - w.left, (w.bottom - w.top) - over, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
