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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct OutputError {
    pub stage: &'static str,
    pub code: Option<i32>,
}
impl OutputError {
    fn at(stage: &'static str) -> Self {
        Self { stage, code: None }
    }
}

#[derive(Debug)]
pub enum OutputEvent {
    Ready,
    Retry { attempt: u64, error: OutputError },
}

pub struct Output {
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
    events: mpsc::Receiver<OutputEvent>,
}
impl Output {
    pub fn start<F: FnMut(&mut [f32])>(
        device_id: String,
        active: Arc<AtomicBool>,
        make_fill: impl Fn() -> F + Send + 'static,
    ) -> Result<Self, String> {
        if device_id.is_empty() || device_id.contains('\0') {
            return Err("Select an output device".into());
        }
        supervise(active, Duration::from_secs(5), move |stop, ready| {
            let mut fill = make_fill();
            #[cfg(windows)]
            return wasapi::run(&device_id, stop, &mut fill, ready);
            #[cfg(not(windows))]
            {
                let _ = (&device_id, stop, &mut fill, ready);
                Err(OutputError::at("unsupported-platform"))
            }
        })
        .map_err(|error| error.to_string())
    }

    pub fn next_event(&self) -> Option<OutputEvent> {
        self.events.try_recv().ok()
    }
}

fn supervise(
    active: Arc<AtomicBool>,
    retry_delay: Duration,
    mut run: impl FnMut(&AtomicBool, &mut dyn FnMut()) -> Result<(), OutputError> + Send + 'static,
) -> std::io::Result<Output> {
    let stop = Arc::new(AtomicBool::new(false));
    let stopped = stop.clone();
    let (sender, events) = mpsc::sync_channel(16);
    let thread = thread::Builder::new()
        .name("sound-manager-wasapi".into())
        .spawn(move || {
            let mut attempt = 0u64;
            let mut last_error = None;
            while !stopped.load(Ordering::Acquire) {
                let result = run(&stopped, &mut || {
                    attempt = 0;
                    last_error = None;
                    active.store(true, Ordering::Release);
                    let _ = sender.try_send(OutputEvent::Ready);
                });
                active.store(false, Ordering::Release);
                if stopped.load(Ordering::Acquire) {
                    break;
                }
                let error = result
                    .err()
                    .unwrap_or(OutputError::at("output-thread-ended"));
                attempt += 1;
                if last_error != Some(error) || attempt.is_power_of_two() {
                    let _ = sender.try_send(OutputEvent::Retry { attempt, error });
                }
                last_error = Some(error);
                thread::park_timeout(retry_delay);
            }
        })?;
    Ok(Output {
        stop,
        thread: Some(thread),
        events,
    })
}

impl Drop for Output {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            thread.thread().unpark();
            let _ = thread.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retries_the_same_output_after_initial_and_runtime_failures() {
        let active = Arc::new(AtomicBool::new(false));
        let mut attempt = 0;
        let output = supervise(
            active.clone(),
            Duration::from_millis(10),
            move |stop, ready| {
                attempt += 1;
                if attempt == 1 {
                    return Err(OutputError::at("device-lookup"));
                }
                ready();
                if attempt == 2 {
                    return Err(OutputError::at("render-buffer"));
                }
                while !stop.load(Ordering::Acquire) {
                    thread::park_timeout(Duration::from_millis(50));
                }
                Ok(())
            },
        )
        .unwrap();
        let next = || output.events.recv_timeout(Duration::from_secs(2)).unwrap();
        assert!(matches!(
            next(),
            OutputEvent::Retry {
                error: OutputError {
                    stage: "device-lookup",
                    ..
                },
                ..
            }
        ));
        assert!(matches!(next(), OutputEvent::Ready));
        assert!(matches!(
            next(),
            OutputEvent::Retry {
                error: OutputError {
                    stage: "render-buffer",
                    ..
                },
                ..
            }
        ));
        assert!(matches!(next(), OutputEvent::Ready));
        assert!(active.load(Ordering::Acquire));
        drop(output);
        assert!(!active.load(Ordering::Acquire));
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
        ready: &mut dyn FnMut(),
    ) -> Result<(), OutputError> {
        fn win(stage: &'static str) -> impl FnOnce(windows::core::Error) -> OutputError {
            move |error| OutputError {
                stage,
                code: Some(error.code().0),
            }
        }
        // SAFETY: fresh thread initializes its own COM apartment; no COM pointer leaves it.
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok() }
            .map_err(win("com-initialize"))?;
        let _com = Com;
        let device: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
                .map_err(win("device-enumerator"))?;
        let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
        // SAFETY: wide is NUL-terminated and alive for the entire GetDevice call.
        let device =
            unsafe { device.GetDevice(PCWSTR(wide.as_ptr())) }.map_err(win("device-lookup"))?;
        let client: IAudioClient =
            unsafe { device.Activate(CLSCTX_ALL, None) }.map_err(win("audio-client-activate"))?;
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
            .map_err(win("audio-client-initialize"))?;
        let event = Event(
            unsafe { CreateEventW(None, false, false, PCWSTR::null()) }
                .map_err(win("event-create"))?,
        );
        unsafe { client.SetEventHandle(event.0) }.map_err(win("event-register"))?;
        let render: IAudioRenderClient =
            unsafe { client.GetService() }.map_err(win("render-service"))?;
        let capacity = unsafe { client.GetBufferSize() }.map_err(win("buffer-size"))?;
        if capacity == 0 || capacity > 96000 {
            return Err(OutputError::at("invalid-buffer-size"));
        }
        // SAFETY: GetBuffer grants capacity complete 8-byte frames until ReleaseBuffer.
        let initial = unsafe { render.GetBuffer(capacity) }.map_err(win("initial-buffer"))?;
        if initial.is_null() {
            return Err(OutputError::at("null-initial-buffer"));
        }
        unsafe {
            std::ptr::write_bytes(initial, 0, capacity as usize * 8);
            render.ReleaseBuffer(capacity, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32)
        }
        .map_err(win("initial-release"))?;
        unsafe { client.Start() }.map_err(win("audio-client-start"))?;
        let _playing = Playing(client.clone());
        ready();
        let mut timeouts = 0;
        while !stop.load(Ordering::Acquire) {
            match unsafe { WaitForSingleObject(event.0, 100) } {
                WAIT_TIMEOUT => {
                    timeouts += 1;
                    if timeouts >= 50 {
                        return Err(OutputError::at("output-event-timeout"));
                    }
                    continue;
                }
                WAIT_OBJECT_0 => timeouts = 0,
                _ => return Err(OutputError::at("output-event-wait")),
            }
            let padding = unsafe { client.GetCurrentPadding() }.map_err(win("buffer-padding"))?;
            let available = capacity
                .checked_sub(padding)
                .ok_or_else(|| OutputError::at("invalid-padding"))?;
            if available == 0 {
                continue;
            }
            let buffer = unsafe { render.GetBuffer(available) }.map_err(win("render-buffer"))?;
            if buffer.is_null() || !(buffer as usize).is_multiple_of(std::mem::align_of::<f32>()) {
                unsafe {
                    let _ = render.ReleaseBuffer(available, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32);
                }
                return Err(OutputError::at("invalid-buffer-alignment"));
            }
            // SAFETY: negotiated format is f32 stereo, aligned pointer and exact granted frame count.
            // The slice is consumed synchronously and cannot escape this call.
            let samples = unsafe {
                std::slice::from_raw_parts_mut(buffer.cast::<f32>(), available as usize * 2)
            };
            fill(samples);
            unsafe { render.ReleaseBuffer(available, 0) }.map_err(win("render-release"))?;
        }
        Ok(())
    }
}
