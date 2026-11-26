// Embeds assets/app.ico as the exe icon. windres chokes on spaces and '#' in paths,
// so the resource is compiled in the temp dir.
use std::{env, fs, path::PathBuf, process::Command};

fn main() {
    println!("cargo:rerun-if-changed=assets/app.ico");
    println!("cargo:rerun-if-changed=assets/app.manifest");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let manifest = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let work = env::temp_dir().join("macdock-res");
    let _ = fs::create_dir_all(&work);
    // The compiled resource lives in the temp dir, which Windows may clean: rebuild it whenever it is gone.
    println!("cargo:rerun-if-changed={}", work.join("app_res.o").display());
    if fs::copy(manifest.join("assets").join("app.ico"), work.join("app.ico")).is_err() {
        return;
    }
    let _ = fs::copy(manifest.join("assets").join("app.manifest"), work.join("app.manifest"));
    // VERSIONINFO (company, description, product, version): an exe with no version resource is a classic
    // "unknown unsigned binary" signal for Defender's ML heuristics; real metadata makes it look like what it is.
    let ver = env::var("CARGO_PKG_VERSION").unwrap_or_else(|_| "0.0.0".into());
    let mut n: Vec<u32> = ver.split('.').map(|x| x.parse().unwrap_or(0)).collect();
    n.resize(4, 0);
    let (c, v) = (format!("{},{},{},{}", n[0], n[1], n[2], n[3]), format!("{}.{}.{}.{}", n[0], n[1], n[2], n[3]));
    let rc = format!(
        "1 ICON \"app.ico\"\n1 24 \"app.manifest\"\n\
         1 VERSIONINFO\nFILEVERSION {c}\nPRODUCTVERSION {c}\nFILEOS 0x4\nFILETYPE 0x1\n{{\n BLOCK \"StringFileInfo\"\n {{\n  BLOCK \"040904B0\"\n  {{\n\
         \x20  VALUE \"CompanyName\", \"MacDock Project\"\n   VALUE \"FileDescription\", \"MacDock - macOS-style dock for Windows\"\n\
         \x20  VALUE \"FileVersion\", \"{v}\"\n   VALUE \"InternalName\", \"MacDock\"\n   VALUE \"LegalCopyright\", \"Copyright (c) MacDock contributors\"\n\
         \x20  VALUE \"OriginalFilename\", \"MacDock.exe\"\n   VALUE \"ProductName\", \"MacDock\"\n   VALUE \"ProductVersion\", \"{v}\"\n  }}\n }}\n\
         \x20BLOCK \"VarFileInfo\"\n {{\n  VALUE \"Translation\", 0x409, 1200\n }}\n}}\n"
    );
    fs::write(work.join("app.rc"), rc).unwrap();
    println!("cargo:rerun-if-changed=Cargo.toml");
    let ok = Command::new("windres").current_dir(&work).args(["app.rc", "-O", "coff", "-o", "app_res.o"]).status().map(|s| s.success()).unwrap_or(false);
    if ok {
        println!("cargo:rustc-link-arg-bins={}", work.join("app_res.o").display());
    } else {
        println!("cargo:warning=windres failed; exe icon not embedded");
    }
}

