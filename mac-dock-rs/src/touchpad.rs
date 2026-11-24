//! Four- and five-finger Precision Touchpad swipes, decoded from raw HID reports.
use crate::*;
use std::collections::HashMap;
use std::time::Instant;
use windows::Win32::Devices::HumanInterfaceDevice::*;
use windows::Win32::UI::Input::*;

const PAGE_DIGITIZER: u16 = 0x0D;
const PAGE_DESKTOP: u16 = 0x01;
const USAGE_TOUCHPAD: u16 = 0x05;
const USAGE_CONTACT_COUNT: u16 = 0x54;
const USAGE_CONTACT_ID: u16 = 0x51;
const USAGE_TIP_SWITCH: u16 = 0x42;
const USAGE_X: u16 = 0x30;
const USAGE_Y: u16 = 0x31;
const MIN_FINGERS: usize = 4;
const SWIPE_DISTANCE: f64 = 0.22;
const VERTICAL_BIAS: f64 = 1.4;

struct Pad {
    preparsed: Vec<u8>,
    input_report_len: usize,
    fingers: Vec<u16>,
    x_min: i32,
    x_max: i32,
    y_min: i32,
    y_max: i32,
    valid: bool,
}

impl Pad {
    fn invalid(preparsed: Vec<u8>) -> Self {
        Self {
            preparsed,
            input_report_len: 0,
            fingers: Vec::new(),
            x_min: 0,
            x_max: 1,
            y_min: 0,
            y_max: 1,
            valid: false,
        }
    }
}

pub struct Touchpad {
    pads: HashMap<isize, Pad>,
    contacts: HashMap<u32, (f64, f64)>,
    active: bool,
    fired: bool,
    start: (f64, f64),
    last_report: Option<Instant>,
    pub registered: bool,
}

impl Touchpad {
    pub fn new() -> Self {
        Self {
            pads: HashMap::new(),
            contacts: HashMap::new(),
            active: false,
            fired: false,
            start: (0.0, 0.0),
            last_report: None,
            registered: false,
        }
    }

    pub fn register(&mut self, hwnd: HWND) {
        if self.registered {
            return;
        }
        let devices = [RAWINPUTDEVICE {
            usUsagePage: PAGE_DIGITIZER,
            usUsage: USAGE_TOUCHPAD,
            dwFlags: RIDEV_INPUTSINK,
            hwndTarget: hwnd,
        }];
        // SAFETY: `devices` is a live one-element RAWINPUTDEVICE slice for the duration of the call.
        unsafe {
            if RegisterRawInputDevices(&devices, std::mem::size_of::<RAWINPUTDEVICE>() as u32)
                .is_ok()
            {
                self.registered = true;
                log("touchpad: raw HID input registered");
            }
        }
    }

    unsafe fn load_pad(&mut self, device: HANDLE) {
        let key = device.0 as isize;
        if self.pads.contains_key(&key) {
            return;
        }

        let mut size = 0u32;
        let _ = GetRawInputDeviceInfoW(Some(device), RIDI_PREPARSEDDATA, None, &mut size);
        if size == 0 || size > 1024 * 1024 {
            self.pads.insert(key, Pad::invalid(Vec::new()));
            return;
        }
        let mut preparsed = vec![0u8; size as usize];
        if GetRawInputDeviceInfoW(
            Some(device),
            RIDI_PREPARSEDDATA,
            Some(preparsed.as_mut_ptr().cast()),
            &mut size,
        ) == u32::MAX
        {
            self.pads.insert(key, Pad::invalid(Vec::new()));
            return;
        }

        let data = PHIDP_PREPARSED_DATA(preparsed.as_ptr() as isize);
        let mut caps = HIDP_CAPS::default();
        if HidP_GetCaps(data, &mut caps).is_err()
            || caps.UsagePage != PAGE_DIGITIZER
            || caps.Usage != USAGE_TOUCHPAD
        {
            self.pads.insert(key, Pad::invalid(preparsed));
            return;
        }

        let mut count = caps.NumberInputValueCaps;
        let mut value_caps = vec![HIDP_VALUE_CAPS::default(); count as usize];
        if HidP_GetValueCaps(HidP_Input, value_caps.as_mut_ptr(), &mut count, data).is_err() {
            self.pads.insert(key, Pad::invalid(preparsed));
            return;
        }
        let mut pad = Pad::invalid(preparsed);
        pad.input_report_len = caps.InputReportByteLength as usize;
        for cap in value_caps.iter().take(count as usize) {
            if cap.UsagePage != PAGE_DESKTOP || cap.IsRange {
                continue;
            }
            // SAFETY: NotRange is the active union member because IsRange is false.
            let usage = unsafe { cap.Anonymous.NotRange.Usage };
            match usage {
                USAGE_X => {
                    if !pad.fingers.contains(&cap.LinkCollection) {
                        pad.fingers.push(cap.LinkCollection);
                    }
                    pad.x_min = cap.LogicalMin;
                    pad.x_max = cap.LogicalMax.max(cap.LogicalMin.saturating_add(1));
                }
                USAGE_Y => {
                    pad.y_min = cap.LogicalMin;
                    pad.y_max = cap.LogicalMax.max(cap.LogicalMin.saturating_add(1));
                }
                _ => {}
            }
        }
        pad.valid = !pad.fingers.is_empty();
        if pad.valid {
            log(&format!(
                "touchpad: detected {} finger slots",
                pad.fingers.len()
            ));
        }
        self.pads.insert(key, pad);
    }

    /// Called from the owning window's WM_INPUT handler.
    pub unsafe fn on_input(&mut self, raw_input: HRAWINPUT) {
        let header_size = std::mem::size_of::<RAWINPUTHEADER>() as u32;
        let mut size = 0u32;
        let _ = GetRawInputData(raw_input, RID_INPUT, None, &mut size, header_size);
        if size < header_size || size > 64 * 1024 {
            return;
        }
        let mut buffer = vec![0u64; (size as usize + 7) / 8];
        if GetRawInputData(
            raw_input,
            RID_INPUT,
            Some(buffer.as_mut_ptr().cast()),
            &mut size,
            header_size,
        ) == u32::MAX
        {
            return;
        }
        if size as usize > buffer.len() * 8 {
            return;
        }
        let bytes = std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>(), size as usize);
        if bytes.len() < header_size as usize {
            return;
        }
        let input = &*(bytes.as_ptr().cast::<RAWINPUT>());
        if input.header.dwType != RIM_TYPEHID.0 {
            return;
        }
        let device = input.header.hDevice;
        self.load_pad(device);
        let Some(pad) = self.pads.get(&(device.0 as isize)) else {
            return;
        };
        if !pad.valid || pad.preparsed.is_empty() || pad.input_report_len == 0 {
            return;
        }

        let hid = &input.data.hid;
        let report_len = hid.dwSizeHid as usize;
        let report_count = hid.dwCount as usize;
        if report_len == 0
            || report_count == 0
            || report_len
                .checked_mul(report_count)
                .is_none_or(|n| n > 16 * 1024)
        {
            return;
        }
        let report_bytes = std::slice::from_raw_parts(
            hid.bRawData.as_ptr().cast::<u8>(),
            report_len * report_count,
        );
        let mut owned_report = vec![0u8; report_len];
        let key = device.0 as isize;
        for report in report_bytes.chunks_exact(report_len) {
            owned_report.copy_from_slice(report);
            self.process_report(key, &mut owned_report);
        }
    }

    unsafe fn process_report(&mut self, device: isize, report: &mut [u8]) {
        if self
            .last_report
            .is_some_and(|last| last.elapsed().as_millis() > 800)
        {
            self.reset_gesture();
        }
        self.last_report = Some(Instant::now());

        let Some(pad) = self.pads.get(&device) else {
            return;
        };
        let preparsed = PHIDP_PREPARSED_DATA(pad.preparsed.as_ptr() as isize);
        let mut declared = 0u32;
        let count_ok = HidP_GetUsageValue(
            HidP_Input,
            PAGE_DIGITIZER,
            Some(0),
            USAGE_CONTACT_COUNT,
            &mut declared,
            preparsed,
            report,
        )
        .is_ok();
        let declared = if count_ok { declared as usize } else { 0 };

        // Some devices send each contact as its own report; keep their IDs until a tip switch releases them.
        for link in &pad.fingers {
            let mut x = 0u32;
            let mut y = 0u32;
            if HidP_GetUsageValue(
                HidP_Input,
                PAGE_DESKTOP,
                Some(*link),
                USAGE_X,
                &mut x,
                preparsed,
                report,
            )
            .is_err()
                || HidP_GetUsageValue(
                    HidP_Input,
                    PAGE_DESKTOP,
                    Some(*link),
                    USAGE_Y,
                    &mut y,
                    preparsed,
                    report,
                )
                .is_err()
            {
                continue;
            }
            let mut id = 0u32;
            if HidP_GetUsageValue(
                HidP_Input,
                PAGE_DIGITIZER,
                Some(*link),
                USAGE_CONTACT_ID,
                &mut id,
                preparsed,
                report,
            )
            .is_err()
            {
                id = *link as u32;
            }
            let mut usages = [0u16; 8];
            let mut usage_count = usages.len() as u32;
            let touching = HidP_GetUsages(
                HidP_Input,
                PAGE_DIGITIZER,
                Some(*link),
                usages.as_mut_ptr(),
                &mut usage_count,
                preparsed,
                report,
            )
            .is_ok()
                && usages[..(usage_count as usize).min(usages.len())].contains(&USAGE_TIP_SWITCH);
            if touching {
                self.contacts.insert(
                    id,
                    (
                        (x as f64 - pad.x_min as f64) / (pad.x_max - pad.x_min) as f64,
                        (y as f64 - pad.y_min as f64) / (pad.y_max - pad.y_min) as f64,
                    ),
                );
            } else {
                self.contacts.remove(&id);
            }
        }
        self.evaluate(self.contacts.len().max(declared));
    }

    fn reset_gesture(&mut self) {
        self.contacts.clear();
        self.active = false;
        self.fired = false;
    }

    fn evaluate(&mut self, fingers: usize) {
        if fingers < 3 {
            self.active = false;
            self.fired = false;
            return;
        }
        if fingers < MIN_FINGERS || self.contacts.is_empty() {
            return;
        }

        let n = self.contacts.len() as f64;
        let (x, y) = self
            .contacts
            .values()
            .fold((0.0, 0.0), |(sx, sy), (cx, cy)| (sx + cx, sy + cy));
        let center = (x / n, y / n);
        if !self.active {
            self.active = true;
            self.fired = false;
            self.start = center;
            return;
        }
        if self.fired {
            return;
        }
        let (dx, dy) = (center.0 - self.start.0, center.1 - self.start.1);
        if dy.abs() >= SWIPE_DISTANCE && dy.abs() > VERTICAL_BIAS * dx.abs() {
            self.fired = true;
            let a = app();
            if !a.cfg.s.gestures {
                return;
            }
            // Y grows down the pad. Match the C# dock's one wired action: down dismisses Mission Control.
            if dy > 0.0 && a.mc.visible {
                a.mc.close();
            }
        }
    }
}
