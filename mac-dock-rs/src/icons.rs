//! Icon loading off the UI thread. UI asks `get(key)`; a worker thread decodes pixels (pack PNG or shell icon),
//! the UI thread turns them into GDI+ bitmaps when it receives WM_ICONS_READY.
use crate::gfx::{Img, Pixels};
use crate::util::*;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::mpsc::{channel, Sender};
use std::sync::{Arc, Mutex};
use windows::core::PCWSTR;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::Com::*;
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

pub struct Req {
    pub key: String,
    pub target: String,
    pub pack: Option<PathBuf>,
    pub px: i32,
    pub tint: Option<String>,
}

type Done = Arc<Mutex<Vec<(String, Option<Pixels>)>>>;

pub struct Icons {
    cache: HashMap<String, Rc<Img>>,
    pending: HashSet<String>,
    tx: Sender<Req>,
    done: Done,
    pub pack: HashMap<String, PathBuf>,
    pub failed: HashSet<String>,
    pub mac_tried: HashSet<String>,
    pub tints: HashMap<String, String>, // folder target (lower case) -> colour name
    pub logo: Option<Rc<Img>>,
    notify: isize,
    pub px: i32,
}

pub fn from_hicon(h: HICON) -> Option<Pixels> {
    unsafe {
        let mut ii = ICONINFO::default();
        GetIconInfo(h, &mut ii).ok()?;
        let r = Pixels::from_hbitmap(ii.hbmColor);
        if !ii.hbmColor.is_invalid() {
            let _ = DeleteObject(ii.hbmColor.into());
        }
        if !ii.hbmMask.is_invalid() {
            let _ = DeleteObject(ii.hbmMask.into());
        }
        r
    }
}

/// The logo = our own exe icon (embedded at build). Used as the fallback for every empty icon.
pub fn logo_pixels() -> Option<Pixels> {
    unsafe {
        // the exe icon resource at 256 px (ExtractIcon only gives 32 px, which looked blurry when magnified)
        if let Ok(hi) = windows::Win32::System::LibraryLoader::GetModuleHandleW(None) {
            if let Ok(h) = LoadImageW(Some(hi.into()), PCWSTR(1 as *const u16), IMAGE_ICON, 256, 256, LR_DEFAULTCOLOR) {
                let r = from_hicon(HICON(h.0));
                let _ = DestroyIcon(HICON(h.0));
                if r.is_some() {
                    return r;
                }
            }
        }
        let exe = wide(&std::env::current_exe().ok()?.to_string_lossy());
        let mut large = [HICON::default(); 1];
        let n = ExtractIconExW(pw(&exe), 0, Some(large.as_mut_ptr()), None, 1);
        if n == 0 || large[0].is_invalid() {
            return None;
        }
        let r = from_hicon(large[0]);
        let _ = DestroyIcon(large[0]);
        r
    }
}

fn resolve_lnk(path: &str) -> Option<String> {
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        let pf: windows::Win32::System::Com::IPersistFile = windows::core::Interface::cast(&link).ok()?;
        let wp = wide(path);
        pf.Load(pw(&wp), STGM_READ).ok()?;
        let mut buf = [0u16; 1024];
        link.GetPath(&mut buf, std::ptr::null_mut(), 0).ok()?;
        let s = from_wide(&buf);
        if s.is_empty() { None } else { Some(s) }
    }
}

/// Package folders change version on every update (Claude_4.4212... -> Claude_4.4300...): find the live one.
fn resolve_versioned(path: &str) -> Option<String> {
    let low = path.to_lowercase();
    let marker = format!("{0}windowsapps{0}", '\u{5c}');
    let i = low.find(&marker)?;
    let root = &path[..i + marker.len()];
    let rest = &path[root.len()..];
    let (pkg, tail) = rest.split_once(char::from(92u8))?;
    let prefix = pkg.split('_').next()?.to_lowercase() + "_";
    let mut best: Option<String> = None;
    for e in std::fs::read_dir(root).ok()?.flatten() {
        let n = e.file_name().to_string_lossy().to_string();
        if n.to_lowercase().starts_with(&prefix) && n.to_lowercase().contains("_x64_") {
            let cand = format!("{}{}{}{}", root, n, char::from(92u8), tail);
            if std::path::Path::new(&cand).exists() && best.as_ref().map(|b| cand > *b).unwrap_or(true) {
                best = Some(cand);
            }
        }
    }
    best
}

/// 256 px "jumbo" shell icon (the largest image the shell has for the file).
unsafe fn jumbo_pixels(path: &str) -> Option<Pixels> {
    let wp = wide(path);
    let mut sfi = SHFILEINFOW::default();
    let flags = SHGFI_SYSICONINDEX | if std::path::Path::new(path).exists() { SHGFI_FLAGS(0) } else { SHGFI_USEFILEATTRIBUTES };
    if SHGetFileInfoW(pw(&wp), windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_NORMAL, Some(&mut sfi), std::mem::size_of::<SHFILEINFOW>() as u32, flags) == 0 {
        return None;
    }
    let il: windows::Win32::UI::Controls::IImageList = SHGetImageList(4).ok()?; // SHIL_JUMBO
    let h = il.GetIcon(sfi.iIcon, windows::Win32::UI::Controls::ILD_TRANSPARENT.0 as u32).ok()?;
    let r = from_hicon(h);
    let _ = DestroyIcon(h);
    r.map(|p| p.trim_square())
}

fn shell_pixels(target: &str) -> Option<Pixels> {
    let mut t = target.to_string();
    if t.to_lowercase().ends_with(".lnk") {
        if let Some(r) = resolve_lnk(&t) {
            if std::path::Path::new(&r).exists() {
                t = r;
            }
        }
    }
    // a pinned install path that moved (per-machine <-> per-user, x86 <-> x64): look for the same relative path
    if !t.to_lowercase().starts_with("shell:") && !std::path::Path::new(&t).exists() {
        let low = t.to_lowercase();
        let sep = char::from(92u8);
        let pf = std::env::var("ProgramFiles").unwrap_or_default().to_lowercase();
        let pf86 = std::env::var("ProgramFiles(x86)").unwrap_or_default().to_lowercase();
        let rel = if !pf.is_empty() && low.starts_with(&pf) { Some(t[pf.len()..].trim_start_matches(sep).to_string()) } else if !pf86.is_empty() && low.starts_with(&pf86) { Some(t[pf86.len()..].trim_start_matches(sep).to_string()) } else { None };
        if let Some(rel) = rel {
            let local = std::env::var("USERPROFILE").map(|u| format!("{0}{1}AppData{1}Local", u, sep)).unwrap_or_default();
            for base in [std::env::var("ProgramFiles").unwrap_or_default(), std::env::var("ProgramFiles(x86)").unwrap_or_default(), local] {
                if base.is_empty() {
                    continue;
                }
                let cand = format!("{}{}{}", base, sep, rel);
                if std::path::Path::new(&cand).exists() {
                    t = cand;
                    break;
                }
            }
        }
    }
    if !t.to_lowercase().starts_with("shell:") && !std::path::Path::new(&t).exists() {
        if let Some(v) = resolve_versioned(&t) {
            t = v;
        }
    }
    unsafe {
        let wp = wide(&t);
        let mut best: Option<Pixels> = None;
        if let Ok(f) = SHCreateItemFromParsingName::<_, _, IShellItemImageFactory>(pw(&wp), None) {
            if let Ok(hbm) = f.GetImage(SIZE { cx: 256, cy: 256 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK) {
                best = Pixels::from_hbitmap(hbm).map(|p| p.trim_square());
                let _ = DeleteObject(hbm.into());
            }
        }
        // small result (an .exe with only 32/48 px icons)? ask for the jumbo list, keep the bigger one
        if best.as_ref().map(|p| p.w < 160).unwrap_or(true) && !t.to_lowercase().starts_with("shell:") {
            if let Some(j) = jumbo_pixels(&t) {
                if best.as_ref().map(|b| j.w > b.w).unwrap_or(true) {
                    best = Some(j);
                }
            }
        }
        best
    }
}

impl Icons {
    pub fn new(notify_hwnd: isize, dir: &std::path::Path) -> Icons {
        let (tx, rx) = channel::<Req>();
        let done: Done = Arc::new(Mutex::new(Vec::new()));
        let rx = Arc::new(Mutex::new(rx));
        for _ in 0..4 {
            let rx = rx.clone();
            let d2 = done.clone();
            std::thread::spawn(move || unsafe {
                let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
                loop {
                    let r = match rx.lock().unwrap().recv() { Ok(r) => r, Err(_) => break };
                    let px = r.pack.as_ref()
                        .and_then(|p| Pixels::from_file(&p.to_string_lossy()))
                        .map(|p| p.fit(r.px))
                        .or_else(|| {
                            let cp = cache_path(&r.target, r.px);
                            match cache_load(&cp) {
                                Some(p) => Some(p),
                                None => {
                                    let p = shell_pixels(&r.target).or_else(|| if std::path::Path::new(&r.target).exists() { extract_exe_icon(&r.target) } else { None }).map(|p| p.fit(r.px));
                                    if let Some(px) = &p {
                                        cache_save(&cp, px);
                                    }
                                    p
                                }
                            }
                        });
                    let px = match (px, &r.tint) {
                        (Some(p), Some(name)) => Some(tint_pixels(p, name)),
                        (p, _) => p,
                    };
                    if px.is_none() { crate::util::log(&format!("icon FAIL {} pack={:?}", r.target, r.pack)); }
                    let notify = {
                        let mut completed = d2.lock().unwrap();
                        let was_empty = completed.is_empty();
                        completed.push((r.key, px));
                        was_empty
                    };
                    if notify {
                        let _ = PostMessageW(Some(HWND(notify_hwnd as *mut _)), crate::WM_ICONS_READY, WPARAM(0), LPARAM(0));
                    }
                }
            });
        }
        let mut ic = Icons { cache: HashMap::new(), pending: HashSet::new(), tx, done, pack: HashMap::new(), failed: HashSet::new(), mac_tried: HashSet::new(), tints: HashMap::new(), logo: None, notify: notify_hwnd, px: 256 };
        ic.scan_pack(dir);
        ic.logo = logo_pixels().and_then(Img::from_pixels).map(Rc::new);
        ic
    }
    pub fn scan_pack(&mut self, dir: &std::path::Path) {
        self.pack.clear();
        let mut roots: Vec<std::path::PathBuf> = vec![];
        if let Ok(u) = std::env::var("USERPROFILE") {
            roots.push(std::path::PathBuf::from(u).join(r"AppData\Local\MacDock"));
        }
        roots.push(dir.to_path_buf());
        for (root, sub) in roots.iter().flat_map(|r| ["icons", "dark-icons"].into_iter().map(move |s| (r, s))) {
            if let Ok(rd) = std::fs::read_dir(root.join(sub)) {
                for e in rd.flatten() {
                    let p = e.path();
                    if p.extension().map(|x| x.eq_ignore_ascii_case("png")).unwrap_or(false) {
                        if let Some(stem) = p.file_stem() {
                            self.pack.entry(stem.to_string_lossy().to_lowercase()).or_insert(p.clone());
                        }
                    }
                }
            }
        }
    }
    pub fn clear(&mut self) {
        self.cache.clear();
        self.pending.clear();
    }
    /// Cached image, or None while loading (a request is queued once).
    pub fn get(&mut self, key: &str, target: &str, pack: Option<PathBuf>) -> Option<Rc<Img>> {
        self.get_with_pack(key, target, |_| pack)
    }
    pub fn get_with_pack(&mut self, key: &str, target: &str, pack: impl FnOnce(&Self) -> Option<PathBuf>) -> Option<Rc<Img>> {
        if let Some(i) = self.cache.get(key) {
            return Some(i.clone());
        }
        if self.pending.insert(key.to_string()) {
            let tint = self.tints.get(&target.to_lowercase()).cloned();
            let _ = self.tx.send(Req { key: key.to_string(), target: target.to_string(), pack: pack(self), px: self.px, tint });
        }
        None
    }
    pub fn get_or_logo(&mut self, key: &str, target: &str, pack: Option<PathBuf>) -> Option<Rc<Img>> {
        self.get(key, target, pack).or_else(|| self.logo.clone())
    }
    /// UI thread: move finished pixels into bitmaps. Returns true if anything arrived.
    pub fn drain(&mut self) -> bool {
        let items: Vec<_> = std::mem::take(&mut *self.done.lock().unwrap());
        let any = !items.is_empty();
        for (k, px) in items {
            self.pending.remove(&k);
            if px.is_none() {
                self.failed.insert(k.clone());
            }
            let img = px.map(|p| p.styled()).and_then(Img::from_pixels).map(Rc::new).or_else(|| self.logo.clone());
            if let Some(i) = img {
                self.cache.insert(k, i);
            }
        }
        any
    }
    #[allow(dead_code)]
    pub fn notify(&self) -> isize {
        self.notify
    }
}

/// Resolve which pack icon (if any) a dock target/label should use.
pub fn pack_for(ic: &Icons, cfg: &crate::config::Config, target: &str, label: &str) -> Option<PathBuf> {
    if !cfg.s.icons_on {
        return None;
    }
    // per-app choice (Icon > Default / MacDock Icon / Original Icon / Custom), then the global "original icons" switch
    match cfg.choice_for(target, label) {
        Some((m, p)) if m == "custom" && std::path::Path::new(p).exists() => return Some(PathBuf::from(p)),
        Some((m, _)) if m == "orig" => return None,
        Some(_) => {}
        None => {
            if cfg.ob("original-icons", false) {
                return None;
            }
        }
    }
    let stem = std::path::Path::new(target).file_stem().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default();
    let low = target.to_lowercase();
    let mut cands: Vec<String> = vec![];
    for k in [&low, &stem, &label.to_lowercase()] {
        if let Some(v) = cfg.iconmap.get(k) {
            cands.push(v.to_lowercase());
        }
    }
    if low.contains("systemtoast") || low.contains("autoplay") || label.eq_ignore_ascii_case("autoplay") {
        cands.push("system settings".into());
    }
    if low.starts_with("ms-settings") {
        cands.push("system settings".into());
    }
    if low.starts_with("http") {
        if let Some(h) = site_host(target) {
            cands.insert(0, format!("site-{}", h));
        }
        cands.push("safari".into());
    }
    cands.push(stem);
    cands.push(label.to_lowercase());
    for c in cands {
        if let Some(p) = ic.pack.get(&c) {
            return Some(p.clone());
        }
    }
    None
}

#[allow(dead_code)]
pub fn _u(_: PCWSTR) {}

pub const FOLDER_COLORS: [(&str, (u8, u8, u8)); 8] = [
    ("Blue", (10, 132, 255)),
    ("Red", (255, 69, 58)),
    ("Orange", (255, 149, 0)),
    ("Yellow", (255, 204, 0)),
    ("Green", (48, 209, 88)),
    ("Purple", (175, 82, 222)),
    ("Pink", (255, 105, 180)),
    ("Graphite", (142, 142, 147)),
];

fn rgb_to_hsl(r: f32, g: f32, b: f32) -> (f32, f32, f32) {
    let (mx, mn) = (r.max(g).max(b), r.min(g).min(b));
    let l = (mx + mn) / 2.0;
    let d = mx - mn;
    if d < 1e-6 {
        return (0.0, 0.0, l);
    }
    let s = d / (1.0 - (2.0 * l - 1.0).abs());
    let h = if mx == r { 60.0 * (((g - b) / d) % 6.0) } else if mx == g { 60.0 * ((b - r) / d + 2.0) } else { 60.0 * ((r - g) / d + 4.0) };
    (if h < 0.0 { h + 360.0 } else { h }, s, l)
}
fn hsl_to_rgb(h: f32, s: f32, l: f32) -> (f32, f32, f32) {
    let q = if l < 0.5 { l * (1.0 + s) } else { l + s - l * s };
    let p = 2.0 * l - q;
    let hk = h / 360.0;
    let f = |mut t: f32| {
        if t < 0.0 { t += 1.0; }
        if t > 1.0 { t -= 1.0; }
        if t < 1.0 / 6.0 { p + (q - p) * 6.0 * t } else if t < 0.5 { q } else if t < 2.0 / 3.0 { p + (q - p) * (2.0 / 3.0 - t) * 6.0 } else { p }
    };
    (f(hk + 1.0 / 3.0), f(hk), f(hk - 1.0 / 3.0))
}
/// Recolour a folder icon keeping its shading (port of C# FolderColor.Apply). `px` is premultiplied BGRA.
pub fn tint_pixels(mut px: Pixels, name: &str) -> Pixels {
    let Some((_, (cr, cg, cb))) = FOLDER_COLORS.iter().find(|(n, _)| *n == name) else { return px };
    let (th, ts, _) = rgb_to_hsl(*cr as f32 / 255.0, *cg as f32 / 255.0, *cb as f32 / 255.0);
    let gray = name == "Graphite";
    for c in px.bgra.chunks_exact_mut(4) {
        let a = c[3] as f32;
        if a == 0.0 {
            continue;
        }
        let un = |v: u8| ((v as f32) * 255.0 / a).min(255.0) / 255.0;
        let (r, g, b) = (un(c[2]), un(c[1]), un(c[0]));
        let (_, sat, l) = rgb_to_hsl(r, g, b);
        if sat < 0.12 && !gray {
            continue;
        }
        let ns = if gray { sat * 0.12 } else { (sat.max(0.2) * (0.5 + ts * 0.5)).min(1.0) };
        let (or, og, ob) = hsl_to_rgb(if gray { 220.0 } else { th }, ns, l);
        let pm = |v: f32| (v.clamp(0.0, 1.0) * a).round() as u8;
        c[2] = pm(or);
        c[1] = pm(og);
        c[0] = pm(ob);
    }
    px
}

impl Icons {
    pub fn insert(&mut self, key: &str, px: Pixels) {
        if let Some(i) = Img::from_pixels(px.styled()) {
            self.cache.insert(key.to_string(), Rc::new(i));
        }
    }
}

/// Windows-logo style Start icon (4 panes on a blue squircle), drawn once.
pub fn start_icon() -> Option<Pixels> {
    let mut g = crate::gfx::Gfx::new(256, 256)?;
    g.clear(0);
    g.fill_round_grad(12.0, 12.0, 232.0, 232.0, 54.0, 0xFF3D9BFF, 0xFF0A5BE0);
    let (x0, y0, s, gp) = (62.0, 62.0, 62.0, 8.0);
    for (dx, dy) in [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)] {
        g.fill_rect(x0 + dx * (s + gp), y0 + dy * (s + gp), s, s, 0xF2FFFFFF);
    }
    Some(g.to_pixels())
}

/// Fallback when the shell image factory fails: the exe's own first icon.
pub fn extract_exe_icon(path: &str) -> Option<Pixels> {
    unsafe {
        let wp = wide(path);
        let mut large = [HICON::default(); 1];
        let n = ExtractIconExW(pw(&wp), 0, Some(large.as_mut_ptr()), None, 1);
        if n == 0 || large[0].is_invalid() {
            return None;
        }
        let r = from_hicon(large[0]);
        let _ = DestroyIcon(large[0]);
        r
    }
}

/// macosicons.com lookup for apps that have no pack icon: search by name, save the PNG into the user's icon folder
/// (`%LOCALAPPDATA%\MacDock\icons`), then tell the UI to rescan. Runs on its own thread, once per app name.
pub fn fetch_macos_icon(name: String, key: String, notify: isize) {
    std::thread::spawn(move || {
        let body = serde_json::json!({"query": name, "searchOptions": {"hitsPerPage": 1}}).to_string();
        let Ok(mut r) = ureq::post("https://api.macosicons.com/api/search").header("Content-Type", "application/json").header("x-api-key", &key).send(body) else { return };
        let Ok(text) = r.body_mut().read_to_string() else { return };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) else { return };
        let Some(hit) = v["hits"].as_array().and_then(|h| h.first()) else { return };
        let Some(url) = hit["lowResPngUrl"].as_str().or_else(|| hit["highResPngUrl"].as_str()) else { return };
        let Ok(mut img) = ureq::get(url).call() else { return };
        let Ok(bytes) = img.body_mut().with_config().limit(8 * 1024 * 1024).read_to_vec() else { return };
        let sep = char::from(92u8);
        let dir = std::path::PathBuf::from(std::env::var("USERPROFILE").unwrap_or_default()).join(format!("AppData{0}Local{0}MacDock{0}icons", sep));
        let _ = std::fs::create_dir_all(&dir);
        let clean: String = name.chars().map(|c| if "<>:\"/\\|?*".contains(c) { '_' } else { c }).collect();
        if std::fs::write(dir.join(format!("{}.png", clean)), bytes).is_ok() {
            unsafe {
                let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_ICONS_READY, WPARAM(1), LPARAM(0));
            }
        }
    });
}

// ---- on-disk icon cache: decoded 256 px icons, so Launchpad and the dock open fully populated on the next run ----
fn cache_dir() -> std::path::PathBuf {
    let d = crate::util::data_dir().join("iconcache-v3");
    let _ = std::fs::create_dir_all(&d);
    d
}
fn cache_path(target: &str, px: i32) -> std::path::PathBuf {
    let mut h: u64 = 0xcbf29ce484222325;
    let mut feed = |b: u8| {
        h ^= b as u64;
        h = h.wrapping_mul(0x100000001b3);
    };
    for b in target.to_lowercase().bytes() {
        feed(b);
    }
    if let Ok(md) = std::fs::metadata(target) {
        // an updated exe gets a new entry
        let secs = md.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_secs()).unwrap_or(0);
        for b in secs.to_le_bytes().iter().chain(md.len().to_le_bytes().iter()) {
            feed(*b);
        }
    }
    for b in px.to_le_bytes() {
        feed(b);
    }
    cache_dir().join(format!("{:016x}.ico4", h))
}
fn cache_load(p: &std::path::Path) -> Option<Pixels> {
    let b = std::fs::read(p).ok()?;
    if b.len() < 12 || &b[0..4] != b"MDI1" {
        return None;
    }
    let (w, h) = (i32::from_le_bytes(b[4..8].try_into().ok()?), i32::from_le_bytes(b[8..12].try_into().ok()?));
    if w <= 0 || h <= 0 || b.len() != 12 + (w * h * 4) as usize {
        return None;
    }
    Some(Pixels { w, h, bgra: b[12..].to_vec() })
}
fn cache_save(p: &std::path::Path, px: &Pixels) {
    let mut v = Vec::with_capacity(12 + px.bgra.len());
    v.extend_from_slice(b"MDI1");
    v.extend_from_slice(&px.w.to_le_bytes());
    v.extend_from_slice(&px.h.to_le_bytes());
    v.extend_from_slice(&px.bgra);
    let tmp = p.with_extension("tmp");
    if std::fs::write(&tmp, v).is_ok() {
        let _ = std::fs::rename(&tmp, p);
    }
}

/// Lower-case host of an http(s) target.
pub fn site_host(target: &str) -> Option<String> {
    let l = target.trim();
    let rest = l.strip_prefix("https://").or_else(|| l.strip_prefix("http://")).or_else(|| l.strip_prefix("HTTPS://")).or_else(|| l.strip_prefix("HTTP://"))?;
    let host = rest.split(|c| c == '/' || c == '?' || c == '#').next()?.split('@').last()?.split(':').next()?;
    if host.is_empty() { None } else { Some(host.to_lowercase()) }
}

/// Website icon for a URL shortcut (port of C# SiteIcons): the page's apple-touch-icon / icon link, else /favicon.ico.
/// Saved into the icon pack folder as `site-<host>.png`, then the dock rescans.
pub fn fetch_site_icon(target: String, notify: isize) {
    let Some(host) = site_host(&target) else { return };
    std::thread::spawn(move || {
        let scheme = if target.to_lowercase().starts_with("http://") { "http" } else { "https" };
        let base = format!("{}://{}", scheme, host);
        let mut cands: Vec<String> = vec![];
        if let Ok(mut r) = ureq::get(&target).call() {
            if let Ok(html) = r.body_mut().with_config().limit(600 * 1024).read_to_string() {
                let low = html.to_lowercase();
                let mut best_touch: Option<String> = None;
                let mut best_icon: Option<String> = None;
                let mut from = 0;
                while let Some(i) = low[from..].find("<link") {
                    let s = from + i;
                    let e = low[s..].find('>').map(|x| s + x).unwrap_or(low.len());
                    let tag = &html[s..e];
                    let tl = &low[s..e];
                    if let Some(hp) = tl.find("href=") {
                        let q = tag.as_bytes().get(hp + 5).copied().unwrap_or(b'"');
                        let rest = &tag[hp + 6..];
                        let href = rest.split(q as char).next().unwrap_or("").to_string();
                        if !href.is_empty() {
                            if tl.contains("apple-touch-icon") && best_touch.is_none() {
                                best_touch = Some(href);
                            } else if tl.contains("rel=\"icon\"") || tl.contains("rel=\"shortcut icon\"") || tl.contains("rel='icon'") {
                                if best_icon.is_none() { best_icon = Some(href); }
                            }
                        }
                    }
                    from = e;
                }
                for h in [best_touch, best_icon].into_iter().flatten() {
                    let url = if h.starts_with("http") { h } else if h.starts_with("//") { format!("{}:{}", scheme, h) } else if h.starts_with('/') { format!("{}{}", base, h) } else { format!("{}/{}", base, h) };
                    cands.push(url);
                }
            }
        }
        cands.push(format!("{}/apple-touch-icon.png", base));
        cands.push(format!("{}/favicon.ico", base));
        let sep = char::from(92u8);
        let dir = std::path::PathBuf::from(std::env::var("USERPROFILE").unwrap_or_default()).join(format!("AppData{0}Local{0}MacDock{0}icons", sep));
        let _ = std::fs::create_dir_all(&dir);
        let out = dir.join(format!("site-{}.png", host));
        for u in cands {
            let Ok(mut r) = ureq::get(&u).call() else { continue };
            let Ok(bytes) = r.body_mut().with_config().limit(2 * 1024 * 1024).read_to_vec() else { continue };
            if bytes.len() < 100 {
                continue;
            }
            if std::fs::write(&out, &bytes).is_ok() && crate::gfx::Pixels::from_file(&out.to_string_lossy()).is_some() {
                unsafe {
                    let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_ICONS_READY, WPARAM(1), LPARAM(0));
                }
                return;
            }
            let _ = std::fs::remove_file(&out);
        }
    });
}
