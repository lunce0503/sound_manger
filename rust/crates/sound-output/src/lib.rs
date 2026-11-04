//! A dedicated WASAPI thread. It consumes already mixed PCM through a bounded queue.
//! All COM objects and their native buffers stay on the thread that creates them.
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::Duration,
};

pub struct Output {
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
    failures: mpsc::Receiver<String>,
}
impl Output {
    pub fn start(
        device_id: String,
        mut fill: impl FnMut(&mut [f32]) + Send + 'static,
    ) -> Result<Self, String> {
        if device_id.is_empty() || device_id.contains('\0') {
            return Err("Select an output device".into());
        }
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let (ready_send, ready) = mpsc::sync_channel(1);
        let (failed, failures) = mpsc::sync_channel(1);
        let thread = thread::Builder::new()
            .name("sound-manager-wasapi".into())
            .spawn(move || {
                #[cfg(windows)]
                let result = wasapi::run(&device_id, &stopped, &mut fill, &ready_send);
                #[cfg(not(windows))]
                let result: Result<(), String> = {
                    let _ = (&device_id, &stopped, &mut fill);
                    Err("Windows WASAPI output is only available on Windows".into())
                };
                if let Err(error) = result {
                    let _ = ready_send.try_send(Err(error.clone()));
                    let _ = failed.try_send(error);
                }
            })
            .map_err(|e| e.to_string())?;
        match ready.recv_timeout(Duration::from_secs(15)) {
            Ok(Ok(())) => Ok(Self {
                stop,
                thread: Some(thread),
                failures,
            }),
            other => {
                stop.store(true, Ordering::Release);
                let _ = thread.join();
                Err(match other {
                    Ok(Err(error)) => error,
                    _ => "Output device initialization timed out".into(),
                })
            }
        }
    }
    pub fn failure(&self) -> Option<String> {
        match self.failures.try_recv() {
            Ok(error) => Some(error),
            Err(mpsc::TryRecvError::Disconnected) if !self.stop.load(Ordering::Acquire) => {
                Some("Output thread stopped".into())
            }
            _ => None,
        }
    }
}
impl Drop for Output {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[cfg(windows)]
#[allow(unsafe_code)]
mod wasapi {
    use super::*;
    use windows::{
        Win32::{
            Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT},
            Media::Audio::*,
            System::{
                Com::{
                    CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
                    CoUninitialize,
                },
                Threading::{CreateEventW, WaitForSingleObject},
            },
        },
        core::PCWSTR,
    };
    struct Com;
    impl Drop for Com {
        fn drop(&mut self) {
            unsafe {
                CoUninitialize();
            }
        }
    }
    struct Event(HANDLE);
    impl Drop for Event {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
    struct Playing(IAudioClient);
    impl Drop for Playing {
        fn drop(&mut self) {
            unsafe {
                let _ = self.0.Stop();
            }
        }
    }
    pub fn run(
        device_id: &str,
        stop: &AtomicBool,
        fill: &mut impl FnMut(&mut [f32]),
        ready: &mpsc::SyncSender<Result<(), String>>,
    ) -> Result<(), String> {
        // SAFETY: fresh thread initializes its own COM apartment; no COM pointer leaves it.
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }.map_err(|e| e.to_string())?;
        let _com = Com;
        let device: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
                .map_err(|e| e.to_string())?;
        let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
        // SAFETY: wide is NUL-terminated and alive for the entire GetDevice call.
        let device =
            unsafe { device.GetDevice(PCWSTR(wide.as_ptr())) }.map_err(|e| e.to_string())?;
        let client: IAudioClient =
            unsafe { device.Activate(CLSCTX_ALL, None) }.map_err(|e| e.to_string())?;
        let format = WAVEFORMATEX {
            wFormatTag: 3,
            nChannels: 2,
            nSamplesPerSec: 48000,
            nAvgBytesPerSec: 384000,
            nBlockAlign: 8,
            wBitsPerSample: 32,
            cbSize: 0,
        };
        let flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK
            | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        // The shared engine performs conversion to the endpoint format; our buffer stays f32 stereo.
        unsafe { client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 600_000, 0, &format, None) }
            .map_err(|e| e.to_string())?;
        let event = Event(
            unsafe { CreateEventW(None, false, false, PCWSTR::null()) }
                .map_err(|e| e.to_string())?,
        );
        unsafe { client.SetEventHandle(event.0) }.map_err(|e| e.to_string())?;
        let render: IAudioRenderClient =
            unsafe { client.GetService() }.map_err(|e| e.to_string())?;
        let capacity = unsafe { client.GetBufferSize() }.map_err(|e| e.to_string())?;
        if capacity == 0 || capacity > 96000 {
            return Err("Invalid endpoint buffer size".into());
        }
        // SAFETY: GetBuffer grants capacity complete 8-byte frames until ReleaseBuffer.
        let initial = unsafe { render.GetBuffer(capacity) }.map_err(|e| e.to_string())?;
        if initial.is_null() {
            return Err("Output buffer is null".into());
        }
        unsafe {
            std::ptr::write_bytes(initial, 0, capacity as usize * 8);
            render.ReleaseBuffer(capacity, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32)
        }
        .map_err(|e| e.to_string())?;
        unsafe { client.Start() }.map_err(|e| e.to_string())?;
        let _playing = Playing(client.clone());
        ready
            .send(Ok(()))
            .map_err(|_| "Output owner disappeared".to_owned())?;
        let mut timeouts = 0;
        while !stop.load(Ordering::Acquire) {
            match unsafe { WaitForSingleObject(event.0, 100) } {
                WAIT_TIMEOUT => {
                    timeouts += 1;
                    if timeouts >= 50 {
                        return Err("Output device stopped requesting audio".into());
                    }
                    continue;
                }
                WAIT_OBJECT_0 => timeouts = 0,
                _ => return Err("Output event failed".into()),
            }
            let padding = unsafe { client.GetCurrentPadding() }.map_err(|e| e.to_string())?;
            let available = capacity
                .checked_sub(padding)
                .ok_or_else(|| "Invalid endpoint padding".to_owned())?;
            if available == 0 {
                continue;
            }
            let buffer = unsafe { render.GetBuffer(available) }.map_err(|e| e.to_string())?;
            if buffer.is_null() || !(buffer as usize).is_multiple_of(std::mem::align_of::<f32>()) {
                unsafe {
                    let _ = render.ReleaseBuffer(available, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32);
                }
                return Err("Invalid output buffer alignment".into());
            }
            // SAFETY: negotiated format is f32 stereo, aligned pointer and exact granted frame count.
            // The slice is consumed synchronously and cannot escape this call.
            let samples = unsafe {
                std::slice::from_raw_parts_mut(buffer.cast::<f32>(), available as usize * 2)
            };
            fill(samples);
            unsafe { render.ReleaseBuffer(available, 0) }.map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}
