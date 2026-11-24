use std::io::Write;
use std::path::PathBuf;
use windows::core::PCWSTR;

pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}
pub fn pw(v: &[u16]) -> PCWSTR {
    PCWSTR(v.as_ptr())
}
pub fn from_wide(v: &[u16]) -> String {
    let n = v.iter().position(|&c| c == 0).unwrap_or(v.len());
    String::from_utf16_lossy(&v[..n])
}

/// Drop characters the bundled font lacks and control chars (missing glyphs trigger slow fallback searches).
pub fn safe_text(s: &str) -> String {
    s.chars().map(|c| if c.is_control() { ' ' } else { c }).collect()
}

/// Folder holding the exe (settings, icons, dock.txt live next to it, like the C# build).
pub fn base_dir() -> PathBuf {
    std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_path_buf())).unwrap_or_else(|| PathBuf::from("."))
}
/// Config folder: the C# dock's folder when it sits next to ours (shared dock.txt / icons), else our own folder.
pub fn data_dir() -> PathBuf {
    if let Ok(p) = std::env::var("MACDOCK_DATA") {
        return PathBuf::from(p);
    }
    let b = base_dir();
    if b.join("dock.txt").exists() {
        return b;
    }
    // mac-dock-rs\ sits beside mac-dock\ : reuse its pins and icon pack
    let sib = b.parent().map(|p| p.join("mac-dock")).unwrap_or_default();
    if sib.join("dock.txt").exists() {
        return sib;
    }
    let _ = std::fs::create_dir_all(&b);
    b
}

pub fn expand_env(s: &str) -> String {
    let mut out = String::new();
    let mut rest = s;
    while let Some(i) = rest.find('%') {
        out.push_str(&rest[..i]);
        let after = &rest[i + 1..];
        if let Some(j) = after.find('%') {
            let name = &after[..j];
            match std::env::var(name) {
                Ok(v) if !name.is_empty() => out.push_str(&v),
                _ => {
                    out.push('%');
                    out.push_str(name);
                    out.push('%');
                }
            }
            rest = &after[j + 1..];
        } else {
            out.push('%');
            rest = after;
        }
    }
    out.push_str(rest);
    out
}

static LOG: std::sync::Mutex<()> = std::sync::Mutex::new(());
pub fn log(msg: &str) {
    let _g = LOG.lock();
    let p = base_dir().join("dock-rs.log");
    if let Ok(m) = std::fs::metadata(&p) {
        if m.len() > 512 * 1024 {
            let _ = std::fs::remove_file(&p);
        }
    }
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(p) {
        let t = unsafe { windows::Win32::System::SystemInformation::GetTickCount64() };
        let _ = writeln!(f, "[{}] {}", t / 1000, msg);
    }
}

pub fn lerp(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

/// Critically damped-ish spring.
#[derive(Clone, Copy)]
pub struct Spring {
    pub v: f32,
    pub vel: f32,
    pub goal: f32,
}
impl Spring {
    pub fn new(v: f32) -> Spring {
        Spring { v, vel: 0.0, goal: v }
    }
    /// Returns true while still moving.
    pub fn step(&mut self, dt: f32, stiff: f32, damp: f32) -> bool {
        let dt = dt.min(0.05);
        let f = (self.goal - self.v) * stiff - self.vel * damp;
        self.vel += f * dt;
        self.v += self.vel * dt;
        if (self.goal - self.v).abs() < 0.0008 && self.vel.abs() < 0.01 {
            self.v = self.goal;
            self.vel = 0.0;
            false
        } else {
            true
        }
    }
}

impl Spring {
    /// Damped spring in the C# form (natural frequency `omega`, damping ratio `zeta`), sub-stepped at 240 Hz so the
    /// motion is identical at 60, 120 or 240 Hz. Returns true while still moving.
    pub fn step_zeta(&mut self, dt: f32, omega: f32, zeta: f32) -> bool {
        let dt = dt.min(0.05);
        let steps = ((dt / (1.0 / 240.0)).ceil() as i32).max(1);
        let h = dt / steps as f32;
        let (k, c) = (omega * omega, 2.0 * zeta * omega);
        for _ in 0..steps {
            self.vel += (k * (self.goal - self.v) - c * self.vel) * h;
            self.v += self.vel * h;
        }
        if (self.goal - self.v).abs() < 0.002 && self.vel.abs() < 0.008 {
            self.v = self.goal;
            self.vel = 0.0;
            false
        } else {
            true
        }
    }
}
