//! Windows notifications read straight from the system database (wpndatabase.db), polled on a worker thread
//! with a read-only connection; new rows are pushed to the UI as WM_NOTIFY_NEW.
use rusqlite::{Connection, OpenFlags};
use std::sync::{Arc, Mutex};
use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::*;

#[derive(Clone, Debug)]
pub struct Note {
    pub id: i64,
    pub app_id: String,
    pub app: String,
    pub title: String,
    pub body: String,
    /// protocol link of the toast (activationType="protocol"), opened when the notification is clicked
    pub launch: String,
}

pub struct Notes {
    pub fresh: Arc<Mutex<Vec<Note>>>,
    pub history: Arc<Mutex<Vec<Note>>>,
}

fn db_path() -> Option<std::path::PathBuf> {
    let mut cands = vec![];
    if let Ok(u) = std::env::var("USERPROFILE") {
        cands.push(std::path::PathBuf::from(u).join("AppData\\Local\\Microsoft\\Windows\\Notifications\\wpndatabase.db"));
    }
    if let Ok(l) = std::env::var("LOCALAPPDATA") {
        cands.push(std::path::PathBuf::from(l).join("Microsoft\\Windows\\Notifications\\wpndatabase.db"));
    }
    cands.into_iter().find(|p| p.exists())
}

fn xml_text(payload: &str) -> Vec<String> {
    // <text id="1">Title</text><text id="2">Body</text>
    let mut out = vec![];
    let mut rest = payload;
    while let Some(i) = rest.find("<text") {
        let after = &rest[i..];
        let Some(gt) = after.find('>') else { break };
        if after[..gt].ends_with('/') {
            rest = &after[gt..];
            continue;
        }
        let Some(end) = after.find("</text>") else { break };
        let t = &after[gt + 1..end];
        out.push(unescape(t.trim()));
        rest = &after[end + 7..];
    }
    out
}
fn unescape(s: &str) -> String {
    s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'").replace("&#xD;", " ").replace("&#xA;", " ")
}

pub fn app_name(id: &str) -> String {
    let base = id.split('!').next().unwrap_or(id);
    let base = base.rsplit('\\').next().unwrap_or(base);
    let base = base.trim_end_matches(".exe");
    let n = base.split('_').next().unwrap_or(base);
    let n = n.rsplit('.').next().unwrap_or(n);
    let mut c: Vec<char> = n.chars().collect();
    if let Some(f) = c.first_mut() {
        *f = f.to_uppercase().next().unwrap_or(*f);
    }
    c.into_iter().collect()
}

impl Notes {
    pub fn new() -> Notes {
        Notes { fresh: Arc::new(Mutex::new(vec![])), history: Arc::new(Mutex::new(vec![])) }
    }
    pub fn take_fresh(&self) -> Vec<Note> {
        std::mem::take(&mut *self.fresh.lock().unwrap())
    }
    pub fn recent(&self) -> Vec<Note> {
        self.history.lock().map(|h| h.clone()).unwrap_or_default()
    }
    pub fn start(&mut self, notify: isize) {
        let fresh = self.fresh.clone();
        let hist = self.history.clone();
        std::thread::spawn(move || {
            let Some(path) = db_path() else {
                crate::util::log("notifications: wpndatabase.db not found");
                return;
            };
            let mut last: i64 = -1;
            loop {
                let open = Connection::open_with_flags(&path, OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX);
                if let Ok(c) = open {
                    let _ = c.busy_timeout(std::time::Duration::from_millis(300));
                    let q = "SELECT n.Id, n.Payload, h.PrimaryId FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' AND n.Id > ?1 ORDER BY n.Id";
                    if last < 0 {
                        // first pass: load history, remember the newest id, announce nothing
                        if let Ok(mut st) = c.prepare("SELECT n.Id, n.Payload, h.PrimaryId FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' ORDER BY n.Id DESC LIMIT 30") {
                            if let Ok(rows) = st.query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, Vec<u8>>(1).unwrap_or_default(), r.get::<_, String>(2).unwrap_or_default()))) {
                                let mut v = vec![];
                                for r in rows.flatten() {
                                    let t = xml_text(&String::from_utf8_lossy(&r.1));
                                    last = last.max(r.0);
                                    if t.is_empty() {
                                        continue;
                                    }
                                    v.push(Note { id: r.0, app: app_name(&r.2), app_id: r.2, title: t[0].clone(), body: t.get(1).cloned().unwrap_or_default(), launch: toast_link(&String::from_utf8_lossy(&r.1)) });
                                }
                                *hist.lock().unwrap() = v;
                            }
                        }
                        if last < 0 {
                            last = 0;
                        }
                    } else if let Ok(mut st) = c.prepare(q) {
                        if let Ok(rows) = st.query_map([last], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, Vec<u8>>(1).unwrap_or_default(), r.get::<_, String>(2).unwrap_or_default()))) {
                            let mut any = false;
                            for r in rows.flatten() {
                                last = last.max(r.0);
                                let t = xml_text(&String::from_utf8_lossy(&r.1));
                                if t.is_empty() {
                                    continue;
                                }
                                let n = Note { id: r.0, app: app_name(&r.2), app_id: r.2, title: t[0].clone(), body: t.get(1).cloned().unwrap_or_default(), launch: toast_link(&String::from_utf8_lossy(&r.1)) };
                                hist.lock().unwrap().insert(0, n.clone());
                                fresh.lock().unwrap().push(n);
                                any = true;
                            }
                            if any {
                                hist.lock().unwrap().truncate(30);
                                unsafe {
                                    let _ = PostMessageW(Some(HWND(notify as *mut _)), crate::WM_NOTIFY_NEW, WPARAM(0), LPARAM(0));
                                }
                            }
                        }
                    }
                }
                std::thread::sleep(std::time::Duration::from_millis(1200));
            }
        });
    }
}

/// The toast's launch string when it is a link (activationType="protocol"), else empty.
fn toast_link(payload: &str) -> String {
    let Some(i) = payload.find("<toast") else { return String::new() };
    let tag = &payload[i..payload[i..].find('>').map(|e| i + e).unwrap_or(payload.len())];
    let attr = |name: &str| -> Option<String> {
        let p = tag.find(&format!("{}=\"", name))?;
        let rest = &tag[p + name.len() + 2..];
        Some(unescape(rest.split('"').next()?))
    };
    match (attr("activationType"), attr("launch")) {
        (Some(t), Some(l)) if t.eq_ignore_ascii_case("protocol") && !l.is_empty() => l,
        _ => String::new(),
    }
}

fn dismissed_file() -> std::path::PathBuf {
    crate::util::data_dir().join("dismissed.txt")
}
/// Notifications the user cleared, remembered across restarts (Windows may not let us clear its own list).
pub fn load_dismissed() -> std::collections::HashSet<i64> {
    std::fs::read_to_string(dismissed_file()).unwrap_or_default().lines().filter_map(|l| l.trim().parse::<i64>().ok()).collect()
}
pub fn save_dismissed(set: &std::collections::HashSet<i64>) {
    // keep the file small: only the newest few hundred ids matter
    let mut v: Vec<i64> = set.iter().copied().filter(|i| *i >= 0).collect();
    v.sort();
    let v: Vec<String> = v.into_iter().rev().take(400).map(|i| i.to_string()).collect();
    let _ = std::fs::write(dismissed_file(), v.join("\n"));
}
