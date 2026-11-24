//! The signed-in Windows user's account picture for the menu bar's profile icon (port of C# AccountPic.cs).
//! Looked up on a worker thread in C:\Users\Public\AccountPictures\<sid>\ (the largest image that decodes), cropped to a
//! centred circle and cached as avatar.png next to the settings for the next start.
use crate::gfx::Pixels;
use std::sync::Mutex;
use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::PostMessageW;

static PIC: Mutex<Option<Pixels>> = Mutex::new(None);
static STARTED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Circle-cropped 96 px picture, once it has loaded (taken once; the caller keeps the bitmap).
pub fn take() -> Option<Pixels> {
    PIC.lock().unwrap().take()
}

pub fn start(notify: isize, cache_dir: std::path::PathBuf) {
    if STARTED.swap(true, std::sync::atomic::Ordering::SeqCst) {
        return;
    }
    std::thread::spawn(move || {
        crate::gfx::init();
        let cache = cache_dir.join("avatar.png");
        if let Some(p) = load(&cache.to_string_lossy()) {
            *PIC.lock().unwrap() = Some(p);
            ping(notify);
        }
        if let Some(path) = find() {
            if let Some(p) = load(&path) {
                *PIC.lock().unwrap() = Some(p);
                let _ = std::fs::copy(&path, &cache);
                ping(notify);
            }
        }
    });
}

fn ping(notify: isize) {
    unsafe {
        let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_STATUS, WPARAM(0), LPARAM(0));
    }
}

fn find() -> Option<String> {
    let public = std::env::var("PUBLIC").unwrap_or_else(|_| r"C:\Users\Public".into());
    let root = std::path::Path::new(&public).join("AccountPictures");
    let mut files: Vec<(u64, std::path::PathBuf)> = vec![];
    for d in std::fs::read_dir(root).ok()?.flatten() {
        if let Ok(rd) = std::fs::read_dir(d.path()) {
            for f in rd.flatten() {
                if let Ok(m) = f.metadata() {
                    if m.is_file() {
                        files.push((m.len(), f.path()));
                    }
                }
            }
        }
    }
    files.sort_by(|a, b| b.0.cmp(&a.0));
    files.into_iter().map(|f| f.1.to_string_lossy().to_string()).find(|p| load(p).is_some())
}

fn load(path: &str) -> Option<Pixels> {
    let px = Pixels::from_file(path)?;
    let s = px.w.min(px.h);
    if s < 8 {
        return None;
    }
    // centred square crop, then down to 96 px
    let (ox, oy) = ((px.w - s) / 2, (px.h - s) / 2);
    let mut sq = vec![0u8; (s * s * 4) as usize];
    for y in 0..s {
        let src = (((oy + y) * px.w + ox) * 4) as usize;
        let dst = (y * s * 4) as usize;
        sq[dst..dst + (s * 4) as usize].copy_from_slice(&px.bgra[src..src + (s * 4) as usize]);
    }
    let mut p = Pixels { w: s, h: s, bgra: sq }.fit(96);
    // circular alpha mask with a soft edge
    let (w, r) = (p.w as f32, p.w as f32 / 2.0);
    for y in 0..p.h {
        for x in 0..p.w {
            let (dx, dy) = (x as f32 + 0.5 - r, y as f32 + 0.5 - r);
            let k = (r - (dx * dx + dy * dy).sqrt()).clamp(0.0, 1.0);
            let o = ((y * p.w + x) * 4) as usize;
            for c in 0..4 {
                p.bgra[o + c] = (p.bgra[o + c] as f32 * k) as u8;
            }
        }
    }
    let _ = w;
    Some(p)
}
