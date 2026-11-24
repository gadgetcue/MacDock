//! Wi-Fi (WLAN API), radios and Bluetooth devices (WinRT), and audio output endpoints for the menu bar panels.
//! Everything here blocks, so it only runs on worker threads.
use windows::core::{Interface, GUID, PCWSTR};
use windows::Devices::Bluetooth::*;
use windows::Devices::Enumeration::DeviceInformation;
use windows::Devices::Radios::*;
use windows::Win32::Foundation::*;
use windows::Win32::Media::Audio::*;
use windows::Win32::NetworkManagement::WiFi::*;
use windows::Win32::System::Com::*;

#[derive(Clone, Debug)]
pub struct Net {
    pub name: String,
    pub signal: u32,
    pub secure: bool,
    pub connected: bool,
    pub profile: String, // saved profile name, empty when never connected
}

unsafe fn open_wlan() -> Option<(HANDLE, GUID)> {
    let mut ver = 0u32;
    let mut h = HANDLE::default();
    if WlanOpenHandle(2, None, &mut ver, &mut h) != 0 {
        return None;
    }
    let mut list: *mut WLAN_INTERFACE_INFO_LIST = std::ptr::null_mut();
    if WlanEnumInterfaces(h, None, &mut list) != 0 || list.is_null() {
        let _ = WlanCloseHandle(h, None);
        return None;
    }
    let ok = (*list).dwNumberOfItems > 0;
    let guid = if ok { (*list).InterfaceInfo[0].InterfaceGuid } else { GUID::zeroed() };
    WlanFreeMemory(list as *const _);
    if !ok {
        let _ = WlanCloseHandle(h, None);
        return None;
    }
    Some((h, guid))
}

/// Visible networks (strongest per name, connected first). None = this PC has no Wi-Fi adapter.
pub fn wifi_scan() -> Option<Vec<Net>> {
    unsafe {
        let (h, guid) = open_wlan()?;
        let _ = WlanScan(h, &guid, None, None, None);
        std::thread::sleep(std::time::Duration::from_millis(1200));
        let mut nl: *mut WLAN_AVAILABLE_NETWORK_LIST = std::ptr::null_mut();
        let mut out: Vec<Net> = vec![];
        if WlanGetAvailableNetworkList(h, &guid, 0, None, &mut nl) == 0 && !nl.is_null() {
            let n = (*nl).dwNumberOfItems as usize;
            let items = std::slice::from_raw_parts((*nl).Network.as_ptr(), n);
            for it in items {
                let len = (it.dot11Ssid.uSSIDLength as usize).min(32);
                let name = String::from_utf8_lossy(&it.dot11Ssid.ucSSID[..len]).to_string();
                if name.is_empty() {
                    continue;
                }
                let connected = it.dwFlags & 1 != 0;
                let profile = String::from_utf16_lossy(&it.strProfileName).trim_end_matches('\0').to_string();
                let net = Net { name: name.clone(), signal: it.wlanSignalQuality, secure: it.bSecurityEnabled.as_bool(), connected, profile };
                match out.iter_mut().find(|x| x.name == name) {
                    Some(x) => {
                        if net.connected || (!x.connected && net.signal > x.signal) {
                            *x = net;
                        }
                    }
                    None => out.push(net),
                }
            }
            WlanFreeMemory(nl as *const _);
        }
        let _ = WlanCloseHandle(h, None);
        out.sort_by(|a, b| b.connected.cmp(&a.connected).then(b.signal.cmp(&a.signal)));
        Some(out)
    }
}

pub fn wifi_connect(profile: &str) -> bool {
    unsafe {
        let Some((h, guid)) = open_wlan() else { return false };
        let p: Vec<u16> = profile.encode_utf16().chain(std::iter::once(0)).collect();
        let params = WLAN_CONNECTION_PARAMETERS { wlanConnectionMode: wlan_connection_mode_profile, strProfile: PCWSTR(p.as_ptr()), pDot11Ssid: std::ptr::null_mut(), pDesiredBssidList: std::ptr::null_mut(), dot11BssType: dot11_BSS_type_any, dwFlags: 0 };
        let r = WlanConnect(h, &guid, &params, None);
        let _ = WlanCloseHandle(h, None);
        r == 0
    }
}
pub fn wifi_disconnect() -> bool {
    unsafe {
        let Some((h, guid)) = open_wlan() else { return false };
        let r = WlanDisconnect(h, &guid, None);
        let _ = WlanCloseHandle(h, None);
        r == 0
    }
}

fn radios() -> Vec<Radio> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    let _ = Radio::RequestAccessAsync().and_then(|o| o.join());
    match Radio::GetRadiosAsync().and_then(|o| o.join()) {
        Ok(v) => v.into_iter().collect(),
        Err(_) => vec![],
    }
}
/// Some(on) when a radio of that kind exists.
pub fn radio_on(wifi: bool) -> Option<bool> {
    let want = if wifi { RadioKind::WiFi } else { RadioKind::Bluetooth };
    radios().into_iter().find(|r| r.Kind().map(|k| k == want).unwrap_or(false)).and_then(|r| r.State().ok()).map(|s| s == RadioState::On)
}
pub fn set_radio(wifi: bool, on: bool) {
    let want = if wifi { RadioKind::WiFi } else { RadioKind::Bluetooth };
    if let Some(r) = radios().into_iter().find(|r| r.Kind().map(|k| k == want).unwrap_or(false)) {
        let _ = r.SetStateAsync(if on { RadioState::On } else { RadioState::Off }).and_then(|o| o.join());
    }
}

/// Paired Bluetooth devices: (name, connected).
pub fn bt_devices() -> Vec<(String, bool)> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    let mut out = vec![];
    let Ok(sel) = BluetoothDevice::GetDeviceSelectorFromPairingState(true) else { return out };
    let Ok(infos) = DeviceInformation::FindAllAsyncAqsFilter(&sel).and_then(|o| o.join()) else { return out };
    for info in infos {
        let (Ok(id), Ok(name)) = (info.Id(), info.Name()) else { continue };
        if name.to_string().trim().is_empty() {
            continue;
        }
        let connected = BluetoothDevice::FromIdAsync(&id).and_then(|o| o.join()).and_then(|d| d.ConnectionStatus()).map(|s| s == BluetoothConnectionStatus::Connected).unwrap_or(false);
        out.push((name.to_string(), connected));
    }
    out.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.to_lowercase().cmp(&b.0.to_lowercase())));
    out
}

/// Active audio output devices: (friendly name, is default).
pub fn audio_outputs() -> Vec<(String, bool)> {
    use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let mut out = vec![];
        let Ok(en) = CoCreateInstance::<_, IMMDeviceEnumerator>(&MMDeviceEnumerator, None, CLSCTX_ALL) else { return out };
        let def_id = en.GetDefaultAudioEndpoint(eRender, eMultimedia).ok().and_then(|d| d.GetId().ok()).map(|p| {
            let s = p.to_string().unwrap_or_default();
            CoTaskMemFree(Some(p.0 as *const _));
            s
        });
        let Ok(col) = en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) else { return out };
        let n = col.GetCount().unwrap_or(0);
        for i in 0..n {
            let Ok(dev) = col.Item(i) else { continue };
            let id = dev.GetId().ok().map(|p| {
                let s = p.to_string().unwrap_or_default();
                CoTaskMemFree(Some(p.0 as *const _));
                s
            });
            let Ok(store) = dev.OpenPropertyStore(STGM_READ) else { continue };
            let Ok(pv) = store.GetValue(&PKEY_Device_FriendlyName) else { continue };
            let name = windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc(&pv).ok().map(|p| {
                let s = p.to_string().unwrap_or_default();
                CoTaskMemFree(Some(p.0 as *const _));
                s
            });
            if let Some(name) = name {
                out.push((name, id.is_some() && id == def_id));
            }
        }
        let _ = Interface::as_raw(&en);
        out
    }
}

/// `radio_on` with a deadline: the WinRT radio calls can stall for seconds (or forever) on some PCs.
pub fn radio_on_timeout(wifi: bool, ms: u64) -> Option<bool> {
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(radio_on(wifi));
    });
    rx.recv_timeout(std::time::Duration::from_millis(ms)).ok().flatten()
}
/// `bt_devices` with a deadline.
pub fn bt_devices_timeout(ms: u64) -> Vec<(String, bool)> {
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(bt_devices());
    });
    rx.recv_timeout(std::time::Duration::from_millis(ms)).unwrap_or_default()
}

/// `wifi_scan` with a deadline (None on timeout or no adapter).
pub fn wifi_scan_timeout(ms: u64) -> Option<Vec<Net>> {
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        crate::util::log("wifi: scan start");
        let r = wifi_scan();
        crate::util::log(&format!("wifi: scan done {:?}", r.as_ref().map(|v| v.len())));
        let _ = tx.send(r);
    });
    rx.recv_timeout(std::time::Duration::from_millis(ms)).ok().flatten()
}
