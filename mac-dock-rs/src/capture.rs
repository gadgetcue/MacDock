//! One-click screenshot and screen recording (port of C# Capture.cs).
//! Screenshot: whole virtual screen -> clipboard + Pictures\MacDock Captures\Screenshot <time>.png.
//! Record: ffmpeg (gdigrab, if installed) -> mp4, otherwise Game Bar (Win+Alt+R).
use crate::sys::*;
use crate::util::*;
use std::result::Result::Ok;
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use windows::core::{GUID, PCWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::Graphics::GdiPlus::*;
use windows::Win32::System::DataExchange::*;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::WindowsAndMessaging::*;

static GAMEBAR_REC: AtomicBool = AtomicBool::new(false);
static FFMPEG_PID: AtomicIsize = AtomicIsize::new(0);
static FFMPEG_IN: std::sync::Mutex<Option<std::process::ChildStdin>> = std::sync::Mutex::new(None);

pub fn recording() -> bool {
    GAMEBAR_REC.load(Ordering::Relaxed) || FFMPEG_PID.load(Ordering::Relaxed) != 0
}

fn dir() -> std::path::PathBuf {
    let home = std::env::var("USERPROFILE").unwrap_or_default();
    let d = std::path::PathBuf::from(home).join("Pictures").join("MacDock Captures");
    let _ = std::fs::create_dir_all(&d);
    d
}
fn stamp() -> String {
    unsafe {
        let t = windows::Win32::System::SystemInformation::GetLocalTime();
        format!("{:04}-{:02}-{:02} {:02}.{:02}.{:02}", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond)
    }
}
fn toast(text: &str) {
    crate::app().island.custom("MacDock", text, "");
}

pub fn screenshot() {
    unsafe {
        let (x, y, w, h) = (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        let sdc = GetDC(None);
        let mdc = CreateCompatibleDC(Some(sdc));
        let bmp = CreateCompatibleBitmap(sdc, w, h);
        let old = SelectObject(mdc, bmp.into());
        let ok = BitBlt(mdc, 0, 0, w, h, Some(sdc), x, y, SRCCOPY | CAPTUREBLT).is_ok();
        SelectObject(mdc, old);
        let _ = DeleteDC(mdc);
        ReleaseDC(None, sdc);
        if !ok {
            let _ = DeleteObject(bmp.into());
            toast("Screenshot failed");
            return;
        }
        // clipboard gets its own copy (the clipboard owns it afterwards)
        let copy = CopyImage(HANDLE(bmp.0), IMAGE_BITMAP, 0, 0, LR_COPYRETURNORG);
        if let Ok(copy) = copy {
            if OpenClipboard(None).is_ok() {
                let _ = EmptyClipboard();
                if SetClipboardData(2, Some(HANDLE(copy.0))).is_err() {
                    let _ = DeleteObject(HGDIOBJ(copy.0));
                }
                let _ = CloseClipboard();
            } else {
                let _ = DeleteObject(HGDIOBJ(copy.0));
            }
        }
        let hb = bmp.0 as isize;
        std::thread::spawn(move || {
            crate::gfx::init();
            let path = dir().join(format!("Screenshot {}.png", stamp()));
            let wp = wide(&path.to_string_lossy());
            let mut img: *mut GpBitmap = std::ptr::null_mut();
            if GdipCreateBitmapFromHBITMAP(HBITMAP(hb as *mut _), HPALETTE::default(), &mut img) == Status(0) && !img.is_null() {
                // image/png encoder
                let png = GUID::from_u128(0x557cf406_1a04_11d3_9a73_0000f81ef32e);
                let _ = GdipSaveImageToFile(img as *mut GpImage, PCWSTR(wp.as_ptr()), &png, std::ptr::null());
                let _ = GdipDisposeImage(img as *mut GpImage);
            }
            let _ = DeleteObject(HGDIOBJ(hb as *mut _));
        });
        toast("Screenshot saved and copied");
    }
}

fn find_ffmpeg() -> Option<std::path::PathBuf> {
    for d in std::env::var("PATH").unwrap_or_default().split(';') {
        let p = std::path::Path::new(d.trim()).join("ffmpeg.exe");
        if p.exists() {
            return Some(p);
        }
    }
    None
}

pub fn toggle_record() {
    use std::io::Write;
    if GAMEBAR_REC.swap(false, Ordering::SeqCst) {
        key_combo_vk(&[VK_LWIN, VK_MENU], VK_R);
        return;
    }
    if FFMPEG_PID.load(Ordering::Relaxed) != 0 {
        FFMPEG_PID.store(0, Ordering::Relaxed);
        if let Some(mut stdin) = FFMPEG_IN.lock().unwrap().take() {
            let _ = stdin.write_all(b"q");
            let _ = stdin.flush();
        }
        toast("Recording saved");
        return;
    }
    match find_ffmpeg() {
        None => {
            GAMEBAR_REC.store(true, Ordering::SeqCst);
            key_combo_vk(&[VK_LWIN, VK_MENU], VK_R);
            toast("Recording with Game Bar");
        }
        Some(ff) => {
            let path = dir().join(format!("Recording {}.mp4", stamp()));
            use std::os::windows::process::CommandExt;
            let child = std::process::Command::new(ff)
                .args(["-y", "-f", "gdigrab", "-framerate", "30", "-i", "desktop", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"])
                .arg(&path)
                .stdin(std::process::Stdio::piped())
                .creation_flags(0x0800_0000)
                .spawn();
            match child {
                Ok(mut c) => {
                    FFMPEG_PID.store(c.id() as isize, Ordering::Relaxed);
                    *FFMPEG_IN.lock().unwrap() = c.stdin.take();
                    // reap when it exits so the tile flips back even if ffmpeg dies
                    std::thread::spawn(move || {
                        let _ = c.wait();
                        FFMPEG_PID.store(0, Ordering::Relaxed);
                    });
                    toast("Recording started");
                }
                Err(_) => toast("Recording failed"),
            }
        }
    }
}

fn key_combo_vk(mods: &[VIRTUAL_KEY], key: VIRTUAL_KEY) {
    let mut inputs: Vec<INPUT> = vec![];
    let mk = |vk: VIRTUAL_KEY, up: bool| INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) }, ..Default::default() } } };
    for m in mods {
        inputs.push(mk(*m, false));
    }
    inputs.push(mk(key, false));
    inputs.push(mk(key, true));
    for m in mods.iter().rev() {
        inputs.push(mk(*m, true));
    }
    unsafe {
        SendInput(&inputs, std::mem::size_of::<INPUT>() as i32);
    }
}
