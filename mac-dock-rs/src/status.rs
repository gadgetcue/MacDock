//! System readouts (CPU, RAM, battery, net speed, volume, Wi-Fi) polled on one background thread every 2 s.
//! The UI thread only reads a snapshot; nothing here ever blocks it.
use std::sync::{Arc, Mutex};
use windows::core::Interface;
use windows::Win32::Foundation::*;
use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
use windows::Win32::Media::Audio::*;
use windows::Win32::NetworkManagement::IpHelper::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Power::*;
use windows::Win32::System::SystemInformation::*;
use windows::Win32::System::Threading::GetSystemTimes;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Clone, Default)]
pub struct Snap {
    pub cpu: f32,
    pub ram: f32,
    pub battery: i32, // -1 = none
    pub charging: bool,
    pub ac: bool,
    pub down_kbps: f32,
    pub up_kbps: f32,
    pub volume: f32,
    pub muted: bool,
    pub wifi: bool,
    pub ethernet: bool,
    pub saver: bool,
    pub trash_full: bool,
    pub disk_pct: f32,
}

pub struct Status {
    pub snap: Arc<Mutex<Snap>>,
}

impl Status {
    pub fn new() -> Status {
        Status { snap: Arc::new(Mutex::new(Snap { battery: -1, ..Default::default() })) }
    }
    pub fn get(&self) -> Snap {
        self.snap.lock().map(|s| s.clone()).unwrap_or_default()
    }
    pub fn start(&mut self, notify: isize) {
        let snap = self.snap.clone();
        std::thread::spawn(move || unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            let mut last_idle = 0u64;
            let mut tick = 0u32;
            let mut trash = false;
            let mut disk = 0.0f32;
            let mut last_total = 0u64;
            let mut last_net: Option<(u64, u64, std::time::Instant)> = None;
            loop {
                let mut s = Snap { battery: -1, ..Default::default() };
                // cpu
                let (mut i, mut k, mut u) = (FILETIME::default(), FILETIME::default(), FILETIME::default());
                if GetSystemTimes(Some(&mut i), Some(&mut k), Some(&mut u)).is_ok() {
                    let f = |t: FILETIME| ((t.dwHighDateTime as u64) << 32) | t.dwLowDateTime as u64;
                    let (idle, total) = (f(i), f(k) + f(u));
                    if last_total != 0 && total > last_total {
                        let dt = (total - last_total) as f32;
                        s.cpu = (100.0 * (1.0 - (idle - last_idle) as f32 / dt)).clamp(0.0, 100.0);
                    }
                    last_idle = idle;
                    last_total = total;
                }
                let mut ms = MEMORYSTATUSEX { dwLength: std::mem::size_of::<MEMORYSTATUSEX>() as u32, ..Default::default() };
                if GlobalMemoryStatusEx(&mut ms).is_ok() {
                    s.ram = ms.dwMemoryLoad as f32;
                }
                let mut ps = SYSTEM_POWER_STATUS::default();
                if GetSystemPowerStatus(&mut ps).is_ok() {
                    if ps.BatteryFlag != 128 && ps.BatteryLifePercent != 255 {
                        s.battery = ps.BatteryLifePercent as i32;
                    }
                    s.ac = ps.ACLineStatus == 1;
                    s.charging = ps.BatteryFlag & 8 != 0;
                    s.saver = ps.SystemStatusFlag == 1;
                }
                // network
                let mut tbl: *mut MIB_IF_TABLE2 = std::ptr::null_mut();
                if GetIfTable2(&mut tbl).0 == 0 && !tbl.is_null() {
                    let t = &*tbl;
                    let rows = std::slice::from_raw_parts(t.Table.as_ptr(), t.NumEntries as usize);
                    let (mut rx, mut tx) = (0u64, 0u64);
                    for r in rows {
                        if r.OperStatus.0 == 1 && r.Type != 24 && r.Type != 131 {
                            rx += r.InOctets;
                            tx += r.OutOctets;
                            if r.Type == 71 {
                                s.wifi = true;
                            }
                            if r.Type == 6 {
                                s.ethernet = true;
                            }
                        }
                    }
                    FreeMibTable(tbl as *const _);
                    let now = std::time::Instant::now();
                    if let Some((prx, ptx, pt)) = last_net {
                        let dt = now.duration_since(pt).as_secs_f32().max(0.1);
                        s.down_kbps = rx.saturating_sub(prx) as f32 / 1024.0 / dt;
                        s.up_kbps = tx.saturating_sub(ptx) as f32 / 1024.0 / dt;
                    }
                    last_net = Some((rx, tx, now));
                }
                if tick % 5 == 0 {
                    use windows::Win32::UI::Shell::{SHQueryRecycleBinW, SHQUERYRBINFO};
                    let mut info = SHQUERYRBINFO { cbSize: std::mem::size_of::<SHQUERYRBINFO>() as u32, ..Default::default() };
                    if SHQueryRecycleBinW(windows::core::PCWSTR::null(), &mut info).is_ok() {
                        trash = info.i64NumItems > 0;
                    }
                }
                tick += 1;
                s.trash_full = trash;
                if tick % 5 == 1 { disk = disk_used_pct(); }
                s.disk_pct = disk;
                if let Some((v, m)) = get_volume() {
                    s.volume = v;
                    s.muted = m;
                }
                if let Ok(mut g) = snap.lock() {
                    *g = s;
                }
                let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_STATUS, WPARAM(0), LPARAM(0));
                std::thread::sleep(std::time::Duration::from_millis(2000));
            }
        });
    }
    pub fn poll_light(&mut self) {}
}

unsafe fn endpoint() -> Option<IAudioEndpointVolume> {
    let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
    let dev = en.GetDefaultAudioEndpoint(eRender, eMultimedia).ok()?;
    dev.Activate::<IAudioEndpointVolume>(CLSCTX_ALL, None).ok()
}
pub fn get_volume() -> Option<(f32, bool)> {
    unsafe {
        let ep = endpoint()?;
        let v = ep.GetMasterVolumeLevelScalar().ok()?;
        let m = ep.GetMute().ok()?.as_bool();
        Some((v, m))
    }
}
pub fn set_volume(v: f32) {
    unsafe {
        if let Some(ep) = endpoint() {
            let _ = ep.SetMasterVolumeLevelScalar(v.clamp(0.0, 1.0), std::ptr::null());
            if v > 0.0 {
                let _ = ep.SetMute(false, std::ptr::null());
            }
        }
    }
}
pub fn toggle_mute() {
    unsafe {
        if let Some(ep) = endpoint() {
            if let Ok(m) = ep.GetMute() {
                let _ = ep.SetMute(!m.as_bool(), std::ptr::null());
            }
        }
    }
}

/// Current display brightness (laptops) via WMI through a hidden PowerShell: only ever called off the UI thread.
pub fn get_brightness_blocking() -> Option<i32> {
    use std::os::windows::process::CommandExt;
    let o = std::process::Command::new("powershell").args(["-NoProfile", "-Command", "(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness"]).creation_flags(0x08000000).output().ok()?;
    String::from_utf8_lossy(&o.stdout).trim().parse().ok()
}
pub fn set_brightness_async(v: i32) {
    use std::os::windows::process::CommandExt;
    std::thread::spawn(move || {
        let _ = std::process::Command::new("powershell").args(["-NoProfile", "-Command", &format!("(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods).WmiSetBrightness(1,{})", v)]).creation_flags(0x08000000).status();
    });
}

/// Internal-panel brightness (0-100) through the video IOCTL: cheap, no WMI. None on desktops / external monitors.
pub fn brightness_ioctl() -> Option<i32> {
    use windows::Win32::Foundation::*;
    use windows::Win32::Storage::FileSystem::*;
    use windows::Win32::System::IO::DeviceIoControl;
    unsafe {
        let name = crate::util::wide(&format!("{0}{0}.{0}LCD", char::from(92u8)));
        let h = CreateFileW(crate::util::pw(&name), 0x8000_0000, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES(0), None).ok()?;
        let mut out = [0u8; 3];
        let mut ret = 0u32;
        // IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS: policy, AC level, DC level
        let ok = DeviceIoControl(h, 0x0023_0498, None, 0, Some(out.as_mut_ptr() as *mut _), 3, Some(&mut ret), None).is_ok();
        let _ = CloseHandle(h);
        if !ok || ret < 3 {
            return None;
        }
        let mut ps = windows::Win32::System::Power::SYSTEM_POWER_STATUS::default();
        let ac = windows::Win32::System::Power::GetSystemPowerStatus(&mut ps).is_ok() && ps.ACLineStatus == 1;
        Some(if ac { out[1] } else { out[2] } as i32)
    }
}

/// True while an app is using the microphone / webcam (CapabilityAccessManager consent store: a value with
/// LastUsedTimeStart set and LastUsedTimeStop == 0).
pub fn capability_in_use(which: &str) -> bool {
    use windows::Win32::System::Registry::*;
    unsafe fn scan(parent: HKEY, path: &str) -> bool {
        let p = crate::util::wide(path);
        let mut key = HKEY::default();
        if RegOpenKeyExW(parent, crate::util::pw(&p), None, KEY_READ, &mut key).is_err() {
            return false;
        }
        let mut found = false;
        let mut i = 0u32;
        loop {
            let mut name = [0u16; 512];
            let mut n = name.len() as u32;
            if RegEnumKeyExW(key, i, Some(windows::core::PWSTR(name.as_mut_ptr())), &mut n, None, None, None, None).is_err() {
                break;
            }
            i += 1;
            let sub = String::from_utf16_lossy(&name[..n as usize]);
            let mut sk = HKEY::default();
            let sp = crate::util::wide(&sub);
            if RegOpenKeyExW(key, crate::util::pw(&sp), None, KEY_READ, &mut sk).is_ok() {
                let (mut start, mut stop) = (0u64, 0u64);
                let mut sz = 8u32;
                let a = crate::util::wide("LastUsedTimeStart");
                let b = crate::util::wide("LastUsedTimeStop");
                let _ = RegQueryValueExW(sk, crate::util::pw(&a), None, None, Some(&mut start as *mut _ as *mut u8), Some(&mut sz));
                sz = 8;
                let _ = RegQueryValueExW(sk, crate::util::pw(&b), None, None, Some(&mut stop as *mut _ as *mut u8), Some(&mut sz));
                let _ = RegCloseKey(sk);
                if start != 0 && stop == 0 {
                    found = true;
                    break;
                }
            }
        }
        let _ = RegCloseKey(key);
        found
    }
    let base = format!("Software{0}Microsoft{0}Windows{0}CurrentVersion{0}CapabilityAccessManager{0}ConsentStore{0}{1}", char::from(92u8), which);
    unsafe { scan(HKEY_CURRENT_USER, &base) || scan(HKEY_CURRENT_USER, &format!("{}{}NonPackaged", base, char::from(92u8))) }
}

static WEATHER: Mutex<String> = Mutex::new(String::new());
pub static WEATHER_ON: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
pub fn weather() -> String {
    WEATHER_ON.store(true, std::sync::atomic::Ordering::Relaxed);
    WEATHER.lock().map(|w| w.clone()).unwrap_or_default()
}
/// Weather text refreshed every 20 minutes on a worker thread (only after the bar asked for it).
pub fn start_weather() {
    std::thread::spawn(|| loop {
        if WEATHER_ON.load(std::sync::atomic::Ordering::Relaxed) {
            if let Ok(mut r) = ureq::get("https://wttr.in/?format=%t+%C").call() {
                if let Ok(t) = r.body_mut().read_to_string() {
                    let t = t.trim().to_string();
                    if !t.is_empty() && t.len() < 60 && !t.contains('<') {
                        *WEATHER.lock().unwrap() = t;
                    }
                }
            }
            std::thread::sleep(std::time::Duration::from_secs(20 * 60));
        } else {
            std::thread::sleep(std::time::Duration::from_secs(5));
        }
    });
}
pub fn disk_used_pct() -> f32 {
    use windows::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
    let drive = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into());
    let root = crate::util::wide(&format!("{}{}", drive.trim_end_matches(char::from(92u8)), char::from(92u8)));
    let (mut free, mut total) = (0u64, 0u64);
    unsafe {
        if GetDiskFreeSpaceExW(crate::util::pw(&root), Some(&mut free), Some(&mut total), None).is_ok() && total > 0 {
            return 100.0 * (1.0 - free as f32 / total as f32);
        }
    }
    0.0
}
