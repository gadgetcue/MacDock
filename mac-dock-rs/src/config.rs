//! dock.txt pins + options, docksize.txt, itemnames.txt, iconmap.txt: same files and formats as the C# dock.
use crate::util::*;
use std::collections::HashMap;
use std::path::PathBuf;

#[derive(Clone, Debug)]
pub struct Entry {
    pub section: u8, // 0 apps, 2 right-hand (folders/files/links)
    pub label: Option<String>,
    pub target: String,
    pub sep_before: bool,
}

#[derive(Clone)]
pub struct Settings {
    pub size: i32,
    pub magnify: f32,
    pub hide_taskbar: bool,
    pub auto_hide: bool,
    pub gestures: bool,
    pub icons_on: bool,
    pub dark: bool,
    pub bar_show: bool,
    pub island_on: bool,
    pub macosicons_key: String,
    pub reduce_motion: bool,
    pub genie: bool,
    pub genie_style: String,
}
impl Default for Settings {
    fn default() -> Self {
        Settings { size: 48, magnify: 1.75, hide_taskbar: true, auto_hide: false, gestures: true, icons_on: true, dark: true, bar_show: true, island_on: true, macosicons_key: String::new(), reduce_motion: false, genie: true, genie_style: "genie".into() }
    }
}

pub struct Config {
    pub dir: PathBuf,
    pub entries: Vec<Entry>,
    pub s: Settings,
    pub names: HashMap<String, String>,
    pub iconmap: HashMap<String, String>,
    /// iconchoice.txt: key (target or "n:name") -> (mode orig/pack/custom, path)
    pub choice: HashMap<String, (String, String)>,
    /// foldercolors.txt: folder target (lower case) -> colour name
    pub folder_colors: HashMap<String, String>,
    opts: std::cell::RefCell<HashMap<String, String>>,
}

fn is_off(v: &str) -> bool {
    let v = v.trim();
    v.ends_with("off") || v.ends_with("false") || v.ends_with("no")
}

impl Config {
    pub fn load() -> Config {
        let dir = data_dir();
        let mut c = Config { dir, entries: vec![], s: Settings::default(), names: HashMap::new(), iconmap: HashMap::new(), choice: HashMap::new(), folder_colors: HashMap::new(), opts: Default::default() };
        c.reload();
        c
    }
    pub fn path(&self, f: &str) -> PathBuf {
        self.dir.join(f)
    }
    pub fn reload(&mut self) {
        self.s = Settings::default();
        self.entries.clear();
        let text = std::fs::read(self.path("dock.txt")).map(|b| String::from_utf8_lossy(&b).trim_start_matches('\u{feff}').to_string()).unwrap_or_default();
        {
            let mut o = self.opts.borrow_mut();
            o.clear();
            for l in text.lines() {
                if let Some(r) = l.trim().strip_prefix('@') {
                    let (k, v) = r.split_once(|c: char| c == ' ' || c == '=').unwrap_or((r, ""));
                    o.insert(k.trim().to_lowercase(), v.trim().trim_start_matches('=').trim().to_string());
                }
            }
        }
        let (mut section, mut pending) = (0u8, false);
        for raw in text.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            if line == "---" {
                section = 2;
                pending = false;
                continue;
            }
            if line == "===" {
                pending = true;
                continue;
            }
            if let Some(opt) = line.strip_prefix('@') {
                let o = opt.trim().to_lowercase();
                let rest = |k: &str| o.strip_prefix(k).map(|s| s.trim().trim_start_matches('=').trim().to_string());
                if let Some(v) = rest("hide-taskbar") {
                    self.s.hide_taskbar = !is_off(&v);
                } else if let Some(v) = rest("auto-hide-dock") {
                    self.s.auto_hide = !is_off(&v);
                } else if let Some(v) = rest("gestures") {
                    self.s.gestures = !is_off(&v);
                } else if let Some(v) = rest("icons") {
                    self.s.icons_on = !is_off(&v);
                } else if let Some(v) = rest("theme") {
                    self.s.dark = v != "light";
                } else if let Some(v) = rest("menubar") {
                    self.s.bar_show = !is_off(&v);
                } else if let Some(v) = rest("island") {
                    self.s.island_on = !is_off(&v);
                } else if let Some(v) = rest("genie-style") {
                    self.s.genie_style = v;
                } else if let Some(v) = rest("genie") {
                    self.s.genie = !is_off(&v);
                } else if let Some(v) = rest("reduce-motion") {
                    self.s.reduce_motion = !is_off(&v);
                } else if o.starts_with("macosicons-key") {
                    self.s.macosicons_key = opt.split('=').nth(1).unwrap_or("").trim().to_string();
                }
                continue;
            }
            let (mut label, mut target) = (None, line.to_string());
            if let Some(i) = line.find('|') {
                if i > 0 {
                    label = Some(line[..i].trim().to_string());
                    target = line[i + 1..].trim().to_string();
                }
            }
            let target = expand_env(target.trim_matches('"'));
            self.entries.push(Entry { section, label, target, sep_before: pending });
            pending = false;
        }
        if let Ok(t) = std::fs::read_to_string(self.path("docksize.txt")) {
            for l in t.lines() {
                let mut kv = l.splitn(2, '=');
                let (k, v) = (kv.next().unwrap_or("").trim(), kv.next().unwrap_or("").trim());
                if let Ok(n) = v.parse::<f32>() {
                    match k {
                        "size" => self.s.size = (n as i32).clamp(28, 88),
                        "magnify" => self.s.magnify = n.clamp(1.0, 2.2),
                        _ => {}
                    }
                }
            }
        }
        self.names = Self::kv_file(&self.path("itemnames.txt"));
        self.iconmap = Self::kv_file(&self.path("iconmap.txt"));
        self.choice = Self::load_choice(&self.path("iconchoice.txt"));
        self.folder_colors = Self::kv_file(&self.path("foldercolors.txt")).into_iter().map(|(k, v)| (k.to_lowercase(), v)).collect();
    }
    fn kv_file(p: &PathBuf) -> HashMap<String, String> {
        let mut m = HashMap::new();
        if let Ok(t) = std::fs::read_to_string(p) {
            for l in t.lines() {
                let l = l.trim();
                if l.is_empty() || l.starts_with('#') {
                    continue;
                }
                if let Some(i) = l.rfind('=') {
                    m.insert(l[..i].trim().to_lowercase(), l[i + 1..].trim().to_string());
                }
            }
        }
        m
    }
    pub fn save_size(&self) {
        let _ = std::fs::write(self.path("docksize.txt"), format!("size={}\nmagnify={}\n", self.s.size, self.s.magnify));
    }
    /// Write or replace an `@key value` line in dock.txt.
    pub fn set_option(&self, key: &str, val: &str) {
        let p = self.path("dock.txt");
        let text = std::fs::read_to_string(&p).unwrap_or_default();
        let mut out: Vec<String> = Vec::new();
        let mut done = false;
        let pre = format!("@{}", key);
        for l in text.lines() {
            let t = l.trim().to_lowercase();
            let hit = t.starts_with(&pre) && !t[pre.len()..].starts_with(|c: char| c.is_alphanumeric() || c == '-');
            if hit {
                if !done {
                    out.push(format!("@{} {}", key, val));
                    done = true;
                }
            } else {
                out.push(l.to_string());
            }
        }
        if !done {
            out.insert(0, format!("@{} {}", key, val));
        }
        let _ = std::fs::write(&p, out.join("\r\n") + "\r\n");
    }
    fn rewrite_pins(&self, f: impl FnOnce(&mut Vec<String>)) {
        let p = self.path("dock.txt");
        let text = std::fs::read_to_string(&p).unwrap_or_default();
        let mut lines: Vec<String> = text.lines().map(|s| s.to_string()).collect();
        f(&mut lines);
        let _ = std::fs::write(&p, lines.join("\r\n") + "\r\n");
    }
    pub fn add_pin(&self, target: &str, section: u8) {
        if self.entries.iter().any(|e| e.target.eq_ignore_ascii_case(target)) {
            return;
        }
        let t = target.to_string();
        self.rewrite_pins(|lines| {
            let sep = lines.iter().position(|l| l.trim() == "---");
            if section == 0 {
                match sep {
                    Some(i) => lines.insert(i, t),
                    None => lines.push(t),
                }
            } else {
                if sep.is_none() {
                    lines.push("---".into());
                }
                lines.push(t);
            }
        });
    }
    pub fn remove_pin(&self, target: &str) {
        let tg = target.to_lowercase();
        self.rewrite_pins(|lines| {
            lines.retain(|l| {
                let t = l.trim();
                if t.is_empty() || t.starts_with('#') || t.starts_with('@') || t == "---" || t == "===" {
                    return true;
                }
                let raw = t.find('|').filter(|&i| i > 0).map(|i| t[i + 1..].trim()).unwrap_or(t);
                expand_env(raw.trim_matches('"')).to_lowercase() != tg
            })
        });
    }
    /// Persist a new order (apps, `---`, right section). Keeps header/@ lines.
    pub fn save_order(&self, order: &[Entry]) {
        self.rewrite_pins(|lines| {
            let mut head: Vec<String> = lines.iter().filter(|l| { let t = l.trim(); t.starts_with('#') || t.starts_with('@') }).cloned().collect();
            head.push(String::new());
            let fmt = |e: &Entry| match &e.label { Some(l) => format!("{} | {}", l, e.target), None => e.target.clone() };
            for e in order.iter().filter(|e| e.section == 0) {
                if e.sep_before { head.push("===".into()); }
                head.push(fmt(e));
            }
            if order.iter().any(|e| e.section != 0) {
                head.push("---".into());
                for e in order.iter().filter(|e| e.section != 0) {
                    if e.sep_before { head.push("===".into()); }
                    head.push(fmt(e));
                }
            }
            *lines = head;
        });
    }
    pub fn rename(&mut self, target: &str, name: &str) {
        let key = target.to_lowercase();
        if name.trim().is_empty() {
            self.names.remove(&key);
        } else {
            self.names.insert(key, name.trim().to_string());
        }
        let mut s = String::new();
        for (k, v) in &self.names {
            s.push_str(&format!("{}={}\n", k, v));
        }
        let _ = std::fs::write(self.path("itemnames.txt"), s);
    }
}

impl Config {
    /// Raw value of an `@key value` line in dock.txt (island timers etc.).
    pub fn get_opt(&self, key: &str) -> Option<String> {
        self.opts.borrow().get(&key.to_lowercase()).cloned()
    }
    pub fn ob(&self, key: &str, def: bool) -> bool {
        self.get_opt(key).map(|v| !is_off(&v.to_lowercase())).unwrap_or(def)
    }
    pub fn os(&self, key: &str, def: &str) -> String {
        self.get_opt(key).filter(|v| !v.is_empty()).unwrap_or_else(|| def.to_string())
    }
    pub fn set_ob(&self, key: &str, v: bool) {
        self.set_option(key, if v { "on" } else { "off" });
    }
}

impl Config {
    fn load_choice(p: &std::path::Path) -> HashMap<String, (String, String)> {
        let text = std::fs::read(p).map(|b| String::from_utf8_lossy(&b).trim_start_matches('\u{feff}').to_string()).unwrap_or_default();
        let mut m = HashMap::new();
        for l in text.lines() {
            let p: Vec<&str> = l.trim().split('|').collect();
            if p.len() >= 2 && !p[0].is_empty() {
                m.insert(p[0].to_lowercase(), (p[1].to_string(), p.get(2).unwrap_or(&"").to_string()));
            }
        }
        m
    }
    /// Per-app icon choice: target first, then "n:<label>".
    pub fn choice_for(&self, target: &str, label: &str) -> Option<&(String, String)> {
        if !target.is_empty() {
            if let Some(v) = self.choice.get(&target.to_lowercase()) {
                return Some(v);
            }
        }
        if !label.trim().is_empty() {
            return self.choice.get(&format!("n:{}", label.trim().to_lowercase()));
        }
        None
    }
    /// Set (mode Some) or clear (None) the icon choice for an app; saved to iconchoice.txt.
    pub fn put_choice(&mut self, target: &str, label: &str, mode: Option<(&str, &str)>) {
        for k in [target.to_lowercase(), if label.trim().is_empty() { String::new() } else { format!("n:{}", label.trim().to_lowercase()) }] {
            if k.is_empty() {
                continue;
            }
            match mode {
                Some((m, p)) => {
                    self.choice.insert(k, (m.to_string(), p.to_string()));
                }
                None => {
                    self.choice.remove(&k);
                }
            }
        }
        let body: Vec<String> = self.choice.iter().map(|(k, v)| format!("{}|{}|{}", k, v.0, v.1)).collect();
        let _ = std::fs::write(self.path("iconchoice.txt"), body.join("\r\n"));
    }
}

impl Config {
    pub fn set_folder_color(&mut self, target: &str, name: Option<&str>) {
        let k = target.to_lowercase();
        match name {
            Some(n) => {
                self.folder_colors.insert(k, n.to_string());
            }
            None => {
                self.folder_colors.remove(&k);
            }
        }
        let body: Vec<String> = self.folder_colors.iter().map(|(k, v)| format!("{}={}", k, v)).collect();
        let _ = std::fs::write(self.path("foldercolors.txt"), body.join("\r\n"));
    }
}
