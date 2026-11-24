//! Now-playing via Windows.Media.Control (GSMTC), polled on a worker thread. Never touches the UI thread.
use std::sync::{Arc, Mutex};
use windows::Media::Control::*;
use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Clone, Default, PartialEq)]
pub struct Now {
    pub title: String,
    pub artist: String,
    pub app: String,
    pub playing: bool,
    pub active: bool,
}

pub struct Media {
    pub now: Arc<Mutex<Now>>,
}

fn session() -> Option<GlobalSystemMediaTransportControlsSession> {
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_MULTITHREADED);
    }
    let mgr = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().ok()?.join().ok()?;
    mgr.GetCurrentSession().ok()
}

impl Media {
    pub fn new() -> Media {
        Media { now: Arc::new(Mutex::new(Now::default())) }
    }
    pub fn get(&self) -> Now {
        self.now.lock().map(|n| n.clone()).unwrap_or_default()
    }
    pub fn start(&self, notify: isize) {
        let now = self.now.clone();
        std::thread::spawn(move || {
            let mut last = Now::default();
            loop {
                let mut n = Now::default();
                if let Some(s) = session() {
                    if let Ok(p) = s.TryGetMediaPropertiesAsync().and_then(|o| o.join()) {
                        n.title = p.Title().map(|t| t.to_string()).unwrap_or_default();
                        n.artist = p.Artist().map(|t| t.to_string()).unwrap_or_default();
                    }
                    n.app = s.SourceAppUserModelId().map(|t| t.to_string()).unwrap_or_default();
                    if let Ok(info) = s.GetPlaybackInfo() {
                        n.playing = info.PlaybackStatus().map(|st| st == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing).unwrap_or(false);
                    }
                    n.active = !n.title.is_empty();
                }
                if n != last {
                    last = n.clone();
                    if let Ok(mut g) = now.lock() {
                        *g = n;
                    }
                    unsafe {
                        let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_MEDIA, WPARAM(0), LPARAM(0));
                    }
                }
                std::thread::sleep(std::time::Duration::from_millis(1500));
            }
        });
    }
}

pub fn play_pause() {
    std::thread::spawn(|| {
        if let Some(s) = session() {
            let _ = s.TryTogglePlayPauseAsync().and_then(|o| o.join());
        }
    });
}
pub fn next() {
    std::thread::spawn(|| {
        if let Some(s) = session() {
            let _ = s.TrySkipNextAsync().and_then(|o| o.join());
        }
    });
}
pub fn prev() {
    std::thread::spawn(|| {
        if let Some(s) = session() {
            let _ = s.TrySkipPreviousAsync().and_then(|o| o.join());
        }
    });
}
