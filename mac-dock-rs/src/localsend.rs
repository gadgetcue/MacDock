//! LocalSend (protocol v2): multicast discovery, an HTTP receive server on 53317, sending to nearby devices
//! (HTTPS with self-signed certs accepted, like LocalSend itself), a device picker popup and the Explorer
//! "Send with LocalSend" verb. All networking runs on worker threads; the UI only reads the device list.
use crate::gfx::*;
use crate::sys::*;
use crate::util::*;
use crate::win;
use crate::*;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{Ipv4Addr, SocketAddr, TcpListener, TcpStream, UdpSocket};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const PORT: u16 = 53317;
const GROUP: Ipv4Addr = Ipv4Addr::new(224, 0, 0, 167);

#[derive(Clone)]
pub struct Dev {
    pub fp: String,
    pub alias: String,
    pub ip: String,
    pub port: u16,
    pub https: bool,
    pub seen: Instant,
}

pub static NOTES: Mutex<Vec<(String, String)>> = Mutex::new(Vec::new());
static MSG: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);
pub const WM_LS_NOTE: u32 = WM_APP + 6;

pub fn post_note(title: &str, body: &str) {
    NOTES.lock().unwrap().push((title.to_string(), body.to_string()));
    unsafe {
        let _ = PostMessageW(Some(HWND(MSG.load(std::sync::atomic::Ordering::SeqCst) as *mut _)), WM_LS_NOTE, WPARAM(0), LPARAM(0));
    }
}

fn agent() -> ureq::Agent {
    let tls = ureq::tls::TlsConfig::builder().disable_verification(true).build();
    let cfg = ureq::Agent::config_builder().tls_config(tls).timeout_global(Some(Duration::from_secs(3600))).http_status_as_error(false).build();
    ureq::Agent::new_with_config(cfg)
}

fn me(fp: &str) -> Value {
    json!({
        "alias": std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows PC".into()),
        "version": "2.0", "deviceModel": "Windows", "deviceType": "desktop",
        "fingerprint": fp, "port": PORT, "protocol": "http", "download": false
    })
}

pub struct LocalSend {
    pub devices: Arc<Mutex<Vec<Dev>>>,
    fp: String,
    started: bool,
}

impl LocalSend {
    pub fn new() -> LocalSend {
        let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(1);
        LocalSend { devices: Arc::new(Mutex::new(vec![])), fp: format!("{:016x}{:016x}", t as u64, (t >> 64) as u64 ^ (std::process::id() as u64) << 20), started: false }
    }
    pub fn start(&mut self, msg: isize) {
        if self.started {
            return;
        }
        self.started = true;
        MSG.store(msg, std::sync::atomic::Ordering::SeqCst);
        let (devs, fp) = (self.devices.clone(), self.fp.clone());
        std::thread::spawn({
            let (devs, fp) = (devs.clone(), fp.clone());
            move || discovery(devs, fp)
        });
        std::thread::spawn(move || server(fp));
        ensure_verb();
    }
    pub fn list(&self) -> Vec<Dev> {
        let mut v: Vec<Dev> = self.devices.lock().unwrap().iter().filter(|d| d.seen.elapsed().as_secs() < 90).cloned().collect();
        v.sort_by(|a, b| a.alias.to_lowercase().cmp(&b.alias.to_lowercase()));
        v
    }
    pub fn announce_now(&self) {
        let fp = self.fp.clone();
        std::thread::spawn(move || {
            if let Ok(s) = UdpSocket::bind("0.0.0.0:0") {
                let mut m = me(&fp);
                m["announce"] = json!(true);
                let _ = s.send_to(m.to_string().as_bytes(), (GROUP, PORT));
            }
        });
    }
    pub fn send(&self, dev: Dev, files: Vec<String>) {
        let fp = self.fp.clone();
        std::thread::spawn(move || {
            let n = files.len();
            match do_send(&fp, &dev, &files) {
                Ok(()) => post_note("Sent", &format!("{} file{} to {}", n, if n == 1 { "" } else { "s" }, dev.alias)),
                Err(e) => post_note("LocalSend failed", &e),
            }
        });
    }
}

fn learn(devs: &Arc<Mutex<Vec<Dev>>>, v: &Value, ip: String, my_fp: &str) -> bool {
    let fp = v["fingerprint"].as_str().unwrap_or("").to_string();
    if fp.is_empty() || fp == my_fp {
        return false;
    }
    let d = Dev { fp: fp.clone(), alias: v["alias"].as_str().unwrap_or("Device").to_string(), ip, port: v["port"].as_u64().unwrap_or(PORT as u64) as u16, https: v["protocol"].as_str().unwrap_or("https") == "https", seen: Instant::now() };
    let mut g = devs.lock().unwrap();
    g.retain(|x| x.fp != fp);
    g.push(d);
    true
}

fn discovery(devs: Arc<Mutex<Vec<Dev>>>, fp: String) {
    use socket2::{Domain, Protocol, Socket, Type};
    let Ok(sock) = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP)) else { return };
    let _ = sock.set_reuse_address(true);
    if sock.bind(&SocketAddr::from((Ipv4Addr::UNSPECIFIED, PORT)).into()).is_err() {
        log("localsend: udp bind failed (LocalSend app running?)");
        return;
    }
    let _ = sock.join_multicast_v4(&GROUP, &Ipv4Addr::UNSPECIFIED);
    let udp: UdpSocket = sock.into();
    let _ = udp.set_read_timeout(Some(Duration::from_secs(1)));
    let mut last_announce = Instant::now() - Duration::from_secs(60);
    let mut buf = [0u8; 4096];
    loop {
        if last_announce.elapsed().as_secs() >= 15 {
            let mut m = me(&fp);
            m["announce"] = json!(true);
            let _ = udp.send_to(m.to_string().as_bytes(), (GROUP, PORT));
            last_announce = Instant::now();
        }
        if let Ok((n, from)) = udp.recv_from(&mut buf) {
            if let Ok(v) = serde_json::from_slice::<Value>(&buf[..n]) {
                let ip = from.ip().to_string();
                if learn(&devs, &v, ip.clone(), &fp) && v["announce"].as_bool().unwrap_or(false) {
                    // reply so the other side learns about us
                    let (port, https) = (v["port"].as_u64().unwrap_or(PORT as u64), v["protocol"].as_str().unwrap_or("https") == "https");
                    let fp2 = fp.clone();
                    std::thread::spawn(move || {
                        let url = format!("{}://{}:{}/api/localsend/v2/register", if https { "https" } else { "http" }, ip, port);
                        let _ = agent().post(&url).header("Content-Type", "application/json").send(me(&fp2).to_string());
                    });
                }
            }
        }
    }
}

fn base_dir() -> std::path::PathBuf {
    let up = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".into());
    let d = std::path::PathBuf::from(up).join("Downloads").join("LocalSend");
    let _ = std::fs::create_dir_all(&d);
    d
}

fn unique(dir: &std::path::Path, name: &str) -> std::path::PathBuf {
    let clean: String = name.chars().map(|c| if r#"<>:"/\|?*"#.contains(c) { '_' } else { c }).collect();
    let p = dir.join(&clean);
    if !p.exists() {
        return p;
    }
    let (stem, ext) = match clean.rsplit_once('.') {
        Some((a, b)) => (a.to_string(), format!(".{}", b)),
        None => (clean.clone(), String::new()),
    };
    for i in 1..1000 {
        let c = dir.join(format!("{} ({}){}", stem, i, ext));
        if !c.exists() {
            return c;
        }
    }
    p
}

struct Session {
    files: HashMap<String, (String, String)>, // id -> (token, name)
}

fn server(fp: String) {
    let Ok(l) = TcpListener::bind(("0.0.0.0", PORT)) else {
        log("localsend: tcp bind failed");
        return;
    };
    let sessions: Arc<Mutex<HashMap<String, Session>>> = Arc::new(Mutex::new(HashMap::new()));
    for c in l.incoming().flatten() {
        let (fp, sessions) = (fp.clone(), sessions.clone());
        std::thread::spawn(move || {
            let _ = handle(c, &fp, &sessions);
        });
    }
}

fn respond(s: &mut TcpStream, code: u16, body: &str) {
    let text = match code {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        _ => "Error",
    };
    let _ = write!(s, "HTTP/1.1 {} {}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}", code, text, body.len(), body);
}

fn handle(stream: TcpStream, fp: &str, sessions: &Arc<Mutex<HashMap<String, Session>>>) -> std::io::Result<()> {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(60)));
    let mut out = stream.try_clone()?;
    let mut r = BufReader::new(stream);
    let mut line = String::new();
    r.read_line(&mut line)?;
    let mut parts = line.split_whitespace();
    let (method, target) = (parts.next().unwrap_or("").to_string(), parts.next().unwrap_or("").to_string());
    let mut len = 0usize;
    loop {
        let mut h = String::new();
        r.read_line(&mut h)?;
        if h.trim().is_empty() {
            break;
        }
        if let Some(v) = h.to_lowercase().strip_prefix("content-length:") {
            len = v.trim().parse().unwrap_or(0);
        }
    }
    let (path, query) = target.split_once('?').unwrap_or((&target, ""));
    let q: HashMap<String, String> = query.split('&').filter_map(|kv| kv.split_once('=')).map(|(k, v)| (k.to_string(), v.to_string())).collect();
    match (method.as_str(), path) {
        (_, "/api/localsend/v2/register") | (_, "/api/localsend/v2/info") => {
            let mut body = vec![0u8; len.min(65536)];
            let _ = r.read_exact(&mut body);
            respond(&mut out, 200, &me(fp).to_string());
        }
        ("POST", "/api/localsend/v2/prepare-upload") => {
            let mut body = vec![0u8; len.min(4 * 1024 * 1024)];
            r.read_exact(&mut body)?;
            let v: Value = serde_json::from_slice(&body).unwrap_or(Value::Null);
            let alias = v["info"]["alias"].as_str().unwrap_or("A device").to_string();
            let Some(files) = v["files"].as_object() else {
                respond(&mut out, 400, "{}");
                return Ok(());
            };
            let names: Vec<String> = files.values().filter_map(|f| f["fileName"].as_str().map(|s| s.to_string())).collect();
            let preview = names.iter().take(6).cloned().collect::<Vec<_>>().join("\n");
            let more = if names.len() > 6 { format!("\n... and {} more", names.len() - 6) } else { String::new() };
            let ok = ask(&format!("{} wants to send you {} file{}:\n\n{}{}\n\nAccept? (saved to Downloads\\LocalSend)", alias, names.len(), if names.len() == 1 { "" } else { "s" }, preview, more));
            if !ok {
                respond(&mut out, 403, "{}");
                return Ok(());
            }
            let sid = format!("{:x}", Instant::now().elapsed().as_nanos() ^ (std::process::id() as u128) << 40 ^ std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0));
            let mut map = serde_json::Map::new();
            let mut sess = Session { files: HashMap::new() };
            for (id, f) in files {
                let token = format!("{:x}", std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0) ^ (id.len() as u128 * 7919));
                sess.files.insert(id.clone(), (token.clone(), f["fileName"].as_str().unwrap_or("file").to_string()));
                map.insert(id.clone(), json!(token));
            }
            sessions.lock().unwrap().insert(sid.clone(), sess);
            respond(&mut out, 200, &json!({"sessionId": sid, "files": map}).to_string());
        }
        ("POST", "/api/localsend/v2/upload") => {
            let (sid, fid, tok) = (q.get("sessionId").cloned().unwrap_or_default(), q.get("fileId").cloned().unwrap_or_default(), q.get("token").cloned().unwrap_or_default());
            let name = sessions.lock().unwrap().get(&sid).and_then(|s| s.files.get(&fid)).filter(|f| f.0 == tok).map(|f| f.1.clone());
            let Some(name) = name else {
                respond(&mut out, 403, "{}");
                return Ok(());
            };
            let dest = unique(&base_dir(), std::path::Path::new(&name).file_name().and_then(|s| s.to_str()).unwrap_or("file"));
            let mut f = std::fs::File::create(&dest)?;
            let mut left = len;
            let mut buf = vec![0u8; 256 * 1024];
            while left > 0 {
                let cap = left.min(buf.len());
                let n = r.read(&mut buf[..cap])?;
                if n == 0 {
                    break;
                }
                f.write_all(&buf[..n])?;
                left -= n;
            }
            drop(f);
            respond(&mut out, 200, "{}");
            let done = {
                let mut g = sessions.lock().unwrap();
                if let Some(s) = g.get_mut(&sid) {
                    s.files.remove(&fid);
                    s.files.is_empty()
                } else {
                    false
                }
            };
            post_note("Received", &dest.file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default());
            if done {
                sessions.lock().unwrap().remove(&sid);
            }
        }
        (_, "/api/localsend/v2/cancel") => {
            if let Some(sid) = q.get("sessionId") {
                sessions.lock().unwrap().remove(sid);
            }
            respond(&mut out, 200, "{}");
        }
        _ => respond(&mut out, 404, "{}"),
    }
    Ok(())
}

fn ask(text: &str) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::*;
    let (t, c) = (wide(text), wide("LocalSend"));
    unsafe { MessageBoxW(None, pw(&t), pw(&c), MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND).0 == 6 }
}

fn do_send(fp: &str, dev: &Dev, files: &[String]) -> Result<(), String> {
    let proto = if dev.https { "https" } else { "http" };
    let base = format!("{}://{}:{}/api/localsend/v2", proto, dev.ip, dev.port);
    let ag = agent();
    let mut map = serde_json::Map::new();
    for (i, f) in files.iter().enumerate() {
        let md = std::fs::metadata(f).map_err(|e| e.to_string())?;
        if !md.is_file() {
            continue;
        }
        let name = std::path::Path::new(f).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
        map.insert(format!("f{}", i), json!({"id": format!("f{}", i), "fileName": name, "size": md.len(), "fileType": "application/octet-stream"}));
    }
    if map.is_empty() {
        return Err("Nothing to send".into());
    }
    post_note("LocalSend", &format!("Waiting for {} to accept...", dev.alias));
    let body = json!({"info": me(fp), "files": map}).to_string();
    let mut resp = ag.post(&format!("{}/prepare-upload", base)).header("Content-Type", "application/json").send(body).map_err(|e| e.to_string())?;
    let code = resp.status().as_u16();
    if code == 403 {
        return Err(format!("{} declined", dev.alias));
    }
    if code != 200 {
        return Err(format!("{} answered {}", dev.alias, code));
    }
    let text = resp.body_mut().read_to_string().map_err(|e| e.to_string())?;
    let v: Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
    let sid = v["sessionId"].as_str().unwrap_or("").to_string();
    for (i, f) in files.iter().enumerate() {
        let id = format!("f{}", i);
        let Some(tok) = v["files"][&id].as_str() else { continue };
        let mut file = std::fs::File::open(f).map_err(|e| e.to_string())?;
        let url = format!("{}/upload?sessionId={}&fileId={}&token={}", base, sid, id, tok);
        let r = ag.post(&url).header("Content-Type", "application/octet-stream").send(ureq::SendBody::from_reader(&mut file)).map_err(|e| e.to_string())?;
        if r.status().as_u16() != 200 {
            return Err(format!("Upload failed ({})", r.status().as_u16()));
        }
    }
    Ok(())
}

/// Explorer right-click "Send with LocalSend" for every file.
fn ensure_verb() {
    std::thread::spawn(|| {
        use std::os::windows::process::CommandExt;
        let Ok(exe) = std::env::current_exe() else { return };
        let cmd = format!("\"{}\" --send \"%1\"", exe.display());
        for key in ["HKCU\\Software\\Classes\\*\\shell\\MacDockLocalSend", "HKCU\\Software\\Classes\\Directory\\shell\\MacDockLocalSend"] {
            let _ = std::process::Command::new("reg").args(["add", key, "/ve", "/d", "Send with LocalSend", "/f"]).creation_flags(0x08000000).status();
            let _ = std::process::Command::new("reg").args(["add", &format!("{}\\command", key), "/ve", "/d", &cmd, "/f"]).creation_flags(0x08000000).status();
        }
    });
}

// ---------------- device picker popup ----------------
pub struct Picker {
    hwnd: HWND,
    gfx: Option<Gfx>,
    pub visible: bool,
    files: Vec<String>,
    pos: (i32, i32),
    hits: Vec<(f32, f32, f32, f32, usize)>,
    hover: i32,
    devs: Vec<Dev>,
}

impl Picker {
    pub fn new() -> Picker {
        Picker { hwnd: HWND::default(), gfx: None, visible: false, files: vec![], pos: (0, 0), hits: vec![], hover: -1, devs: vec![] }
    }
    pub fn open(&mut self, files: Vec<String>) {
        let a = app();
        if !app().test {
            a.ls.start(a.msg.0 as isize);
        }
        a.ls.announce_now();
        self.files = files;
        if self.hwnd.is_invalid() {
            self.hwnd = win::create(ID_LSPICK, true, true, false, false, 0, 0, 10, 10);
        }
        self.visible = true;
        self.refresh();
        unsafe {
            SetTimer(Some(self.hwnd), T_LSP, 1000, None);
            let _ = ShowWindow(self.hwnd, SW_SHOW);
        }
        crate::sys::activate(hv(self.hwnd));
    }
    pub fn close(&mut self) {
        if self.visible {
            self.visible = false;
            unsafe {
                let _ = KillTimer(Some(self.hwnd), T_LSP);
            }
            win::hide(self.hwnd);
        }
    }
    fn refresh(&mut self) {
        let a = app();
        let s = a.scale;
        self.devs = a.ls.list();
        let m = 30.0 * s;
        let rows = self.devs.len().max(1) as f32;
        let (w, h) = ((380.0 * s + 2.0 * m) as i32, ((70.0 + rows * 52.0 + 16.0) * s + 2.0 * m) as i32);
        if self.gfx.is_none() || self.gfx.as_ref().map(|g| (g.w, g.h)) != Some((w, h)) {
            self.gfx = Gfx::new(w, h);
            let scr = screen_rect();
            self.pos = (scr.left + ((scr.right - scr.left) - w) / 2, scr.top + ((scr.bottom - scr.top) - h) / 2);
            win::set_pos(self.hwnd, self.pos.0, self.pos.1, w, h, Some(HWND_TOPMOST), SWP_NOACTIVATE);
        }
        self.draw();
    }
    fn draw(&mut self) {
        let a = app();
        let s = a.scale;
        let dark = a.cfg.s.dark;
        let Some(mut g) = self.gfx.take() else { return };
        g.clear(0);
        let m = 30.0 * s;
        let (w, h) = (g.w as f32 - 2.0 * m, g.h as f32 - 2.0 * m);
        g.shadow(m, m, w, h, 18.0 * s, 26.0 * s, 190);
        g.fill_round(m, m, w, h, 18.0 * s, if dark { argb(244, 30, 30, 34) } else { argb(246, 246, 246, 248) });
        g.stroke_round(m + 0.5, m + 0.5, w - 1.0, h - 1.0, 18.0 * s, 1.0, argb(46, 255, 255, 255));
        let fg = if dark { argb(255, 245, 245, 247) } else { argb(255, 28, 28, 32) };
        let dim = if dark { argb(255, 160, 160, 170) } else { argb(255, 110, 110, 120) };
        let what = if self.files.len() == 1 { std::path::Path::new(&self.files[0]).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default() } else { format!("{} files", self.files.len()) };
        g.text("LocalSend", m + 20.0 * s, m + 12.0 * s, w - 40.0 * s, 24.0 * s, 16.0 * s, true, 0, true, false, fg);
        g.text(&format!("Send {} to a nearby device", what), m + 20.0 * s, m + 36.0 * s, w - 40.0 * s, 20.0 * s, 12.0 * s, false, 0, true, false, dim);
        self.hits.clear();
        if self.devs.is_empty() {
            g.text("Looking for devices on your network...", m, m + 80.0 * s, w, 30.0 * s, 13.0 * s, false, 1, true, false, dim);
        }
        for (i, d) in self.devs.iter().enumerate() {
            let y = m + 70.0 * s + i as f32 * 52.0 * s;
            if self.hover == i as i32 {
                g.fill_round(m + 10.0 * s, y, w - 20.0 * s, 48.0 * s, 10.0 * s, argb(255, 10, 132, 255));
            }
            g.fill_circle(m + 38.0 * s, y + 24.0 * s, 16.0 * s, argb(255, 90, 90, 100));
            g.text(&d.alias.chars().next().map(|c| c.to_uppercase().to_string()).unwrap_or_default(), m + 22.0 * s, y + 8.0 * s, 32.0 * s, 32.0 * s, 14.0 * s, true, 1, true, false, argb(255, 255, 255, 255));
            let on = if self.hover == i as i32 { argb(255, 255, 255, 255) } else { fg };
            g.text(&d.alias, m + 64.0 * s, y + 5.0 * s, w - 90.0 * s, 22.0 * s, 14.0 * s, true, 0, true, false, on);
            g.text(&d.ip, m + 64.0 * s, y + 25.0 * s, w - 90.0 * s, 18.0 * s, 11.0 * s, false, 0, true, false, if self.hover == i as i32 { argb(255, 220, 235, 255) } else { dim });
            self.hits.push((m + 10.0 * s, y, w - 20.0 * s, 48.0 * s, i));
        }
        g.present_layered(self.hwnd, self.pos.0, self.pos.1, 255);
        self.gfx = Some(g);
    }
}

pub unsafe fn proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let p = &mut app().lspick;
    match m {
        WM_TIMER if wp.0 == T_LSP => {
            p.refresh();
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let (x, y) = win::lparam_xy(lp);
            let i = p.hits.iter().find(|h| x as f32 >= h.0 && x as f32 <= h.0 + h.2 && y as f32 >= h.1 && y as f32 <= h.1 + h.3).map(|h| h.4 as i32).unwrap_or(-1);
            if i != p.hover {
                p.hover = i;
                p.draw();
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if p.hover >= 0 {
                if let Some(d) = p.devs.get(p.hover as usize).cloned() {
                    let files = p.files.clone();
                    p.close();
                    app().ls.send(d, files);
                }
            }
            LRESULT(0)
        }
        WM_KEYDOWN => {
            if wp.0 as u16 == 0x1B {
                p.close();
            }
            LRESULT(0)
        }
        WM_ACTIVATE if (wp.0 & 0xFFFF) == 0 => {
            if p.visible && GetForegroundWindow() != h {
                p.close();
            }
            DefWindowProcW(h, m, wp, lp)
        }
        WM_ERASEBKGND => LRESULT(1),
        _ => DefWindowProcW(h, m, wp, lp),
    }
}
