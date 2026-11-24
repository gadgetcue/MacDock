//! Windows 11 snap groups (port of C# Snap.cs): visible, non-maximized windows on one monitor that do not overlap and
//! together fill most of its work area are shown in the dock as one combined tile. There is no public API for snap
//! groups, so a group is recognised by geometry.
use crate::gfx::*;
use crate::sys::*;
use windows::Win32::Foundation::RECT;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::UI::WindowsAndMessaging::*;

fn bounds(h: isize) -> Option<RECT> {
    let mut r = RECT::default();
    unsafe { DwmGetWindowAttribute(hw(h), DWMWA_EXTENDED_FRAME_BOUNDS, &mut r as *mut _ as *mut _, 16).ok()? };
    Some(r)
}
fn area(r: &RECT) -> i64 {
    ((r.right - r.left).max(0) as i64) * ((r.bottom - r.top).max(0) as i64)
}
fn inter(a: &RECT, b: &RECT) -> RECT {
    RECT { left: a.left.max(b.left), top: a.top.max(b.top), right: a.right.min(b.right), bottom: a.bottom.min(b.bottom) }
}
fn work_of(b: &RECT) -> Option<(isize, RECT)> {
    unsafe {
        let m = MonitorFromRect(b, MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        if GetMonitorInfoW(m, &mut mi).as_bool() {
            Some((m.0 as isize, mi.rcWork))
        } else {
            None
        }
    }
}

/// `ordered`: visible windows, front-most first. Returns groups of 2 to 4 snapped windows.
pub fn find(ordered: &[isize]) -> Vec<Vec<isize>> {
    let mut by_mon: Vec<(isize, RECT, Vec<(isize, RECT)>)> = vec![];
    for &h in ordered {
        unsafe {
            if IsIconic(hw(h)).as_bool() || IsZoomed(hw(h)).as_bool() {
                continue;
            }
        }
        let Some(b) = bounds(h) else { continue };
        let Some((m, wa)) = work_of(&b) else { continue };
        if area(&b) * 100 < area(&wa) * 18 {
            continue;
        }
        let tol = 20;
        let edge = (b.left - wa.left).abs() <= tol || (b.right - wa.right).abs() <= tol || (b.top - wa.top).abs() <= tol || (b.bottom - wa.bottom).abs() <= tol;
        if !edge {
            continue;
        }
        match by_mon.iter_mut().find(|x| x.0 == m) {
            Some(x) => x.2.push((h, b)),
            None => by_mon.push((m, wa, vec![(h, b)])),
        }
    }
    let mut groups = vec![];
    for (_, wa, list) in by_mon {
        let mut chosen: Vec<(isize, RECT)> = vec![];
        for c in list {
            let clash = chosen.iter().any(|o| area(&inter(&c.1, &o.1)) * 100 > area(&wa) * 3);
            if !clash && chosen.len() < 4 {
                chosen.push(c);
            }
        }
        if chosen.len() < 2 {
            continue;
        }
        let sum: i64 = chosen.iter().map(|c| area(&c.1)).sum();
        if sum * 100 < area(&wa) * 80 {
            continue;
        }
        groups.push(chosen.into_iter().map(|c| c.0).collect());
    }
    groups
}

/// One 96 px tile showing each member's icon (two side by side, three or four in a grid).
pub fn compose(icons: &[&Img]) -> Option<Img> {
    const S: f32 = 96.0;
    let mut g = Gfx::new(96, 96)?;
    g.clear(0);
    g.fill_round_grad(0.0, 0.0, S, S, 22.0, argb(235, 70, 74, 84), argb(235, 38, 40, 48));
    g.stroke_round(0.75, 0.75, S - 1.5, S - 1.5, 22.0, 1.5, argb(90, 255, 255, 255));
    let n = icons.len();
    if n == 2 {
        let sz = 42.0;
        g.image(icons[0], 4.0, (S - sz) / 2.0, sz, sz, 1.0);
        g.image(icons[1], S - sz - 4.0, (S - sz) / 2.0, sz, sz, 1.0);
        g.line(S / 2.0, 20.0, S / 2.0, S - 20.0, 2.0, argb(110, 255, 255, 255));
    } else {
        let sz = 38.0;
        for (i, ic) in icons.iter().take(4).enumerate() {
            g.image(ic, 8.0 + (i % 2) as f32 * (sz + 4.0), 8.0 + (i / 2) as f32 * (sz + 4.0), sz, sz, 1.0);
        }
    }
    Img::from_pixels(g.to_pixels())
}
