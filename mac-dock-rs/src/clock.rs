//! Animation clock: one thread paced by DwmFlush (the compositor's real vsync, so it follows the monitor's
//! native refresh rate, 60/120/144/240 Hz alike) posts WM_FRAME to the UI thread only while something animates.
//! Idle = thread parked on a condvar = 0% CPU. Springs use measured dt, so any refresh rate looks right.
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicIsize, Ordering};
use std::sync::{Condvar, Mutex};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Dwm::DwmFlush;
use windows::Win32::UI::WindowsAndMessaging::*;

pub const WM_FRAME: u32 = WM_APP + 7;
static ACTIVE: AtomicU32 = AtomicU32::new(0);
static PENDING: AtomicBool = AtomicBool::new(false);
static MSG: AtomicIsize = AtomicIsize::new(0);
static CV: (Mutex<()>, Condvar) = (Mutex::new(()), Condvar::new());

pub const DOCK: u32 = 1;
pub const LP: u32 = 2;
pub const MC: u32 = 4;
pub const SPOT: u32 = 8;
pub const CC: u32 = 16;
pub const ISLAND: u32 = 32;
pub const FP: u32 = 64;
pub const GENIE: u32 = 128;
pub const FAN: u32 = 256;
pub const PANEL: u32 = 512;

pub fn bit(t: usize) -> u32 {
    use crate::*;
    match t {
        T_DOCK => DOCK,
        T_LP => LP,
        T_MC => MC,
        T_SPOT => SPOT,
        T_CC => CC,
        T_ISLAND => ISLAND,
        T_FP => FP,
        T_GENIE => GENIE,
        _ => 0,
    }
}
pub fn start(b: u32) {
    if ACTIVE.fetch_or(b, Ordering::SeqCst) == 0 {
        let _g = CV.0.lock();
        CV.1.notify_all();
    }
}
pub fn stop(b: u32) {
    ACTIVE.fetch_and(!b, Ordering::SeqCst);
}
pub fn active() -> u32 {
    ACTIVE.load(Ordering::SeqCst)
}
pub fn frame_handled() {
    PENDING.store(false, Ordering::SeqCst);
}

/// Native refresh rate (Hz) of the monitor under the dock; informational (pacing comes from DwmFlush).
pub fn refresh_hz() -> u32 {
    use windows::Win32::Graphics::Gdi::*;
    unsafe {
        let mut dm = DEVMODEW { dmSize: std::mem::size_of::<DEVMODEW>() as u16, ..Default::default() };
        if EnumDisplaySettingsW(windows::core::PCWSTR::null(), ENUM_CURRENT_SETTINGS, &mut dm).as_bool() && dm.dmDisplayFrequency > 1 {
            dm.dmDisplayFrequency
        } else {
            60
        }
    }
}

pub fn spawn(msg: isize) {
    MSG.store(msg, Ordering::SeqCst);
    std::thread::spawn(|| loop {
        {
            let g = CV.0.lock().unwrap();
            let _ = CV.1.wait_timeout_while(g, std::time::Duration::from_secs(3600), |_| ACTIVE.load(Ordering::SeqCst) == 0);
        }
        while ACTIVE.load(Ordering::SeqCst) != 0 {
            unsafe {
                if DwmFlush().is_err() {
                    std::thread::sleep(std::time::Duration::from_millis(8));
                }
                if !PENDING.swap(true, Ordering::SeqCst) {
                    let _ = PostMessageW(Some(HWND(MSG.load(Ordering::SeqCst) as *mut _)), WM_FRAME, WPARAM(0), LPARAM(0));
                } else {
                    // UI thread is behind: do not flood the queue
                    std::thread::sleep(std::time::Duration::from_millis(2));
                }
            }
        }
    });
}
