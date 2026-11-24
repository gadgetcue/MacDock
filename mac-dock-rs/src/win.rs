//! One window class for every MacDock window; per-window behaviour is selected by GWLP_USERDATA (see main.rs).
use crate::util::*;
use windows::core::{w, PCSTR};
use windows::Win32::Foundation::*;
use windows::Win32::System::LibraryLoader::*;
use windows::Win32::UI::WindowsAndMessaging::*;

const CLASS: windows::core::PCWSTR = w!("MacDockRsWnd");

pub fn register() {
    unsafe {
        let hi = GetModuleHandleW(None).unwrap_or_default();
        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(crate::wndproc),
            hInstance: hi.into(),
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            lpszClassName: CLASS,
            hIcon: LoadIconW(Some(hi.into()), windows::core::PCWSTR(1 as *const u16)).unwrap_or_default(),
            ..Default::default()
        };
        RegisterClassExW(&wc);
    }
}

pub fn create_msg() -> HWND {
    unsafe {
        let hi = GetModuleHandleW(None).unwrap_or_default();
        let h = CreateWindowExW(WS_EX_TOOLWINDOW, CLASS, w!("MacDock"), WS_POPUP, 0, 0, 1, 1, None, None, Some(hi.into()), None).unwrap_or_default();
        SetWindowLongPtrW(h, GWLP_USERDATA, crate::ID_MSG);
        h
    }
}

/// Create a window of kind `id`. `layered` = per-pixel alpha popup that never takes focus.
pub fn create(id: isize, layered: bool, topmost: bool, noactivate: bool, transparent: bool, x: i32, y: i32, w_: i32, h_: i32) -> HWND {
    unsafe {
        let hi = GetModuleHandleW(None).unwrap_or_default();
        let mut ex = WS_EX_TOOLWINDOW;
        if layered {
            ex |= WS_EX_LAYERED;
        }
        if topmost {
            ex |= WS_EX_TOPMOST;
        }
        if noactivate {
            ex |= WS_EX_NOACTIVATE;
        }
        if transparent {
            ex |= WS_EX_TRANSPARENT;
        }
        let h = CreateWindowExW(ex, CLASS, w!("MacDockWnd"), WS_POPUP, x, y, w_, h_, None, None, Some(hi.into()), None).unwrap_or_default();
        SetWindowLongPtrW(h, GWLP_USERDATA, id);
        h
    }
}

pub fn show_noactivate(h: HWND) {
    unsafe {
        let _ = ShowWindow(h, SW_SHOWNOACTIVATE);
    }
}
pub fn hide(h: HWND) {
    unsafe {
        let _ = ShowWindow(h, SW_HIDE);
    }
}
pub fn set_pos(h: HWND, x: i32, y: i32, w_: i32, hh: i32, after: Option<HWND>, flags: SET_WINDOW_POS_FLAGS) {
    unsafe {
        let _ = SetWindowPos(h, after, x, y, w_, hh, flags | SWP_NOACTIVATE);
    }
}
pub fn visible(h: HWND) -> bool {
    unsafe { !h.is_invalid() && IsWindowVisible(h).as_bool() }
}

/// Dark popup menus (TrackPopupMenu) via uxtheme ordinals 135 (SetPreferredAppMode) and 136 (FlushMenuThemes).
pub fn enable_dark_menus(dark: bool) {
    unsafe {
        if let Ok(lib) = LoadLibraryW(w!("uxtheme.dll")) {
            if let Some(f) = GetProcAddress(lib, PCSTR(135 as *const u8)) {
                let f: extern "system" fn(i32) -> i32 = std::mem::transmute(f);
                f(if dark { 2 } else { 3 });
            }
            if let Some(f) = GetProcAddress(lib, PCSTR(136 as *const u8)) {
                let f: extern "system" fn() = std::mem::transmute(f);
                f();
            }
        }
    }
}

pub fn lparam_xy(lp: LPARAM) -> (i32, i32) {
    ((lp.0 & 0xFFFF) as i16 as i32, ((lp.0 >> 16) & 0xFFFF) as i16 as i32)
}

/// Native popup menu helper. items: (id, text, enabled, checked); id 0 + empty text = separator.
pub fn popup_menu(owner: HWND, x: i32, y: i32, items: &[(u32, String, bool, bool)]) -> u32 {
    unsafe {
        let Ok(m) = CreatePopupMenu() else { return 0 };
        for (id, text, en, chk) in items {
            if *id == 0 && text.is_empty() {
                let _ = AppendMenuW(m, MF_SEPARATOR, 0, windows::core::PCWSTR::null());
            } else {
                let mut f = MF_STRING;
                if !*en {
                    f |= MF_GRAYED;
                }
                if *chk {
                    f |= MF_CHECKED;
                }
                let t = wide(text);
                let _ = AppendMenuW(m, f, *id as usize, pw(&t));
            }
        }
        let _ = SetForegroundWindow(owner);
        let r = TrackPopupMenu(m, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, Some(0), owner, None);
        let _ = PostMessageW(Some(owner), WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(m);
        r.0 as u32
    }
}

/// Rounded live-blur glass. Acrylic ignores window regions, so the shape is a stack of square acrylic windows:
/// one body plus thin bands whose insets follow the corner arcs (same technique as the C# dock).
pub struct Glass {
    pieces: Vec<HWND>,
    tint: u32,
    placed: (i32, i32, i32, i32, i32),
    shown: bool,
}
const BANDS: i32 = 5;
impl Glass {
    pub fn new(tint: u32) -> Glass {
        let mut pieces = vec![];
        for _ in 0..(1 + 2 * BANDS) {
            let h = create(crate::ID_PILL, false, true, true, true, 0, 0, 1, 1);
            unsafe {
                let none: i32 = 1; // DWMWCP_DONOTROUND
                let _ = windows::Win32::Graphics::Dwm::DwmSetWindowAttribute(h, windows::Win32::Graphics::Dwm::DWMWINDOWATTRIBUTE(33), &none as *const _ as *const _, 4);
            }
            crate::sys::set_acrylic(h, tint, false);
            pieces.push(h);
        }
        Glass { pieces, tint, placed: (0, 0, 0, 0, 0), shown: false }
    }
    pub fn set_tint(&mut self, tint: u32) {
        self.tint = tint;
        for p in &self.pieces {
            crate::sys::set_acrylic(*p, tint, false);
        }
    }
    pub fn hide(&mut self) {
        for p in &self.pieces {
            hide(*p);
        }
        self.shown = false;
        self.placed = (0, 0, 0, 0, 0);
    }
    /// Screen rect (x, y, w, h) with corner radius r, directly below window `above`.
    pub fn place(&mut self, x: i32, y: i32, w_: i32, h_: i32, r: i32, above: HWND) {
        if self.shown && self.placed == (x, y, w_, h_, r) {
            return;
        }
        let r = r.min(w_ / 2).min(h_ / 2).max(0);
        let mut rects: Vec<(i32, i32, i32, i32)> = vec![(x, y + r, w_, (h_ - 2 * r).max(1))];
        for k in 0..BANDS {
            let (y0, y1) = (k * r / BANDS, ((k + 1) * r / BANDS).max(k * r / BANDS + 1));
            let dy = r as f64 - (y0 as f64 + 0.5);
            let inset = (r as f64 - ((r * r) as f64 - dy * dy).max(0.0).sqrt()).ceil() as i32;
            rects.push((x + inset, y + y0, w_ - 2 * inset, y1 - y0));
            rects.push((x + inset, y + h_ - y1, w_ - 2 * inset, y1 - y0));
        }
        unsafe {
            let flags = SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOCOPYBITS;
            let mut hdwp = BeginDeferWindowPos(rects.len() as i32).unwrap_or_default();
            for (p, rc) in self.pieces.iter().zip(rects.iter()) {
                if let Ok(n) = DeferWindowPos(hdwp, *p, Some(above), rc.0, rc.1, rc.2.max(1), rc.3.max(1), flags) {
                    hdwp = n;
                }
            }
            let _ = EndDeferWindowPos(hdwp);
        }
        self.placed = (x, y, w_, h_, r);
        self.shown = true;
    }
}
