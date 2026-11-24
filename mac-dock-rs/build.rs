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
    fs::write(work.join("app.rc"), "1 ICON \"app.ico\"\n1 24 \"app.manifest\"\n").unwrap();
    let ok = Command::new("windres").current_dir(&work).args(["app.rc", "-O", "coff", "-o", "app_res.o"]).status().map(|s| s.success()).unwrap_or(false);
    if ok {
        println!("cargo:rustc-link-arg-bins={}", work.join("app_res.o").display());
    } else {
        println!("cargo:warning=windres failed; exe icon not embedded");
    }
}

