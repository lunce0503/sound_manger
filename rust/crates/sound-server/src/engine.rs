use crossbeam_queue::ArrayQueue;
use sound_core::{AudioFormat, MixSettings, Mixer, ProcessingSettings, SourceId, SourceSettings};
use sound_protocol::{MixerState, RenderDiagnostics, SourceDiagnostics, SourceState};
use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};
use tokio::sync::oneshot;
use uuid::Uuid;

pub const BLOCK_FRAMES: usize = 480;
pub type AudioBlock = [f32; BLOCK_FRAMES * 2];
pub type AudioQueue = ArrayQueue<AudioBlock>;

pub struct Packet {
    bytes: Vec<u8>,
    arrival: Instant,
}
pub struct Ingress {
    packets: ArrayQueue<Packet>,
    frame_bytes: usize,
    dropped: AtomicU64,
}
impl Ingress {
    pub fn push(&self, bytes: Vec<u8>) {
        let packet = Packet {
            bytes,
            arrival: Instant::now(),
        };
        if let Some(old) = self.packets.force_push(packet) {
            self.dropped.fetch_add(
                (old.bytes.len() / self.frame_bytes) as u64,
                Ordering::Relaxed,
            );
        }
    }
}
struct Slot {
    id: Uuid,
    handle: SourceId,
    input: Arc<Ingress>,
    rate: u32,
    timing: SourceDiagnostics,
    last_arrival: Option<Instant>,
}
type Reply<T> = oneshot::Sender<Result<T, String>>;
enum Command {
    Processing {
        settings: ProcessingSettings,
        reply: Reply<()>,
    },
    Add {
        id: Uuid,
        name: String,
        format: AudioFormat,
        settings: SourceSettings,
        reply: Reply<Arc<Ingress>>,
    },
    Remove {
        id: Uuid,
        reply: Reply<()>,
    },
    Configure {
        id: Uuid,
        settings: SourceSettings,
        reply: Reply<()>,
    },
    Mix {
        settings: MixSettings,
        reply: Reply<()>,
    },
    Snapshot {
        reply: Reply<MixerState>,
    },
}
#[derive(Clone)]
pub struct Engine {
    commands: SyncSender<Command>,
    stop: Arc<AtomicBool>,
    output_underruns: Arc<AtomicU64>,
}
pub struct EngineWorker {
    engine: Engine,
    thread: Option<thread::JoinHandle<()>>,
}
impl Drop for EngineWorker {
    fn drop(&mut self) {
        self.engine.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}
impl EngineWorker {
    pub fn engine(&self) -> Engine {
        self.engine.clone()
    }
}
impl Engine {
    pub fn start(output: Option<Arc<AudioQueue>>) -> std::io::Result<EngineWorker> {
        let (commands, receiver) = mpsc::sync_channel(64);
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let thread = thread::Builder::new()
            .name("sound-manager-dsp".into())
            .spawn(move || {
                let mut mixer = Mixer::default();
                let _ = mixer.set_processing(ProcessingSettings::recommended());
                let mut slots: Vec<Slot> = Vec::with_capacity(16);
                let mut timing = RenderDiagnostics::default();
                let mut last_render: Option<Instant> = None;
                let mut block = [0.0; BLOCK_FRAMES * 2];
                let mut rms = 0.0;
                let mut peak = 0.0;
                let mut next = Instant::now();
                while !stopped.load(Ordering::Acquire) {
                    // Control allocation, snapshots and packet destruction happen outside Mixer::render.
                    for command in receiver.try_iter().take(32) {
                        match command {
                            Command::Processing { settings, reply } => {
                                let _ = reply.send(
                                    mixer.set_processing(settings).map_err(|e| e.to_string()),
                                );
                            }
                            Command::Add {
                                id,
                                name,
                                format,
                                settings,
                                reply,
                            } => {
                                let result = mixer
                                    .add_source(&name, format, settings)
                                    .map(|handle| {
                                        let input = Arc::new(Ingress {
                                            packets: ArrayQueue::new(4),
                                            frame_bytes: format.frame_bytes(),
                                            dropped: AtomicU64::new(0),
                                        });
                                        slots.push(Slot {
                                            id,
                                            handle,
                                            input: input.clone(),
                                            rate: format.sample_rate,
                                            timing: SourceDiagnostics::default(),
                                            last_arrival: None,
                                        });
                                        input
                                    })
                                    .map_err(|e| e.to_string());
                                let _ = reply.send(result);
                            }
                            Command::Remove { id, reply } => {
                                if let Some(index) = slots.iter().position(|s| s.id == id) {
                                    let slot = slots.remove(index);
                                    mixer.remove_source(slot.handle);
                                }
                                let _ = reply.send(Ok(()));
                            }
                            Command::Configure {
                                id,
                                settings,
                                reply,
                            } => {
                                let result = slots
                                    .iter()
                                    .find(|s| s.id == id)
                                    .ok_or_else(|| "Source not found".into())
                                    .and_then(|s| {
                                        mixer
                                            .configure_source(s.handle, settings)
                                            .map_err(|e| e.to_string())
                                    });
                                let _ = reply.send(result);
                            }
                            Command::Mix { settings, reply } => {
                                let _ = reply
                                    .send(mixer.set_settings(settings).map_err(|e| e.to_string()));
                            }
                            Command::Snapshot { reply } => {
                                let sources = slots
                                    .iter()
                                    .filter_map(|slot| {
                                        mixer.source_state(slot.handle).map(|state| {
                                            let mut diagnostics = slot.timing.clone();
                                            diagnostics.received_packets = state.received_packets;
                                            diagnostics.latency_trims = state.latency_trims;
                                            diagnostics.latency_trimmed_frames =
                                                state.latency_trimmed_frames;
                                            diagnostics.target_buffer_ms = 60.0;
                                            diagnostics.trim_threshold_ms = 120.0;
                                            diagnostics.last_receive_ago_ms = slot
                                                .last_arrival
                                                .map(|last| last.elapsed().as_secs_f64() * 1000.0);
                                            diagnostics.ingress_dropped_frames =
                                                slot.input.dropped.load(Ordering::Relaxed);
                                            SourceState {
                                                id: slot.id,
                                                name: state.name,
                                                format: state.format,
                                                settings: state.settings,
                                                buffered_ms: state.buffered_ms,
                                                dropped_frames: state.dropped_frames
                                                    + diagnostics.ingress_dropped_frames,
                                                underruns: state.underruns,
                                                diagnostics,
                                            }
                                        })
                                    })
                                    .collect();
                                let mut diagnostics = timing.clone();
                                diagnostics.last_render_ago_ms =
                                    last_render.map(|last| last.elapsed().as_secs_f64() * 1000.0);
                                let _ = reply.send(Ok(MixerState {
                                    settings: mixer.settings(),
                                    sources,
                                    clipped_samples: mixer.clipped_samples(),
                                    diagnostics,
                                    output_rms: rms,
                                    output_peak: peak,
                                    processing: mixer.processing(),
                                }));
                            }
                        }
                    }
                    for slot in &mut slots {
                        if let Some(packet) = slot.input.packets.pop() {
                            let packet_ms = (packet.bytes.len() / slot.input.frame_bytes) as f64
                                * 1000.0
                                / slot.rate as f64;
                            if let Some(last) = slot.last_arrival {
                                let interval =
                                    packet.arrival.duration_since(last).as_secs_f64() * 1000.0;
                                slot.timing.last_receive_interval_ms = Some(interval);
                                slot.timing.max_receive_interval_ms =
                                    slot.timing.max_receive_interval_ms.max(interval);
                                slot.timing.max_receive_excess_ms =
                                    slot.timing.max_receive_excess_ms.max(interval - packet_ms);
                            }
                            slot.last_arrival = Some(packet.arrival);
                            slot.timing.last_packet_audio_ms = packet_ms;
                            let _ = mixer.push(slot.handle, &packet.bytes);
                            slot.timing.last_ingest_ms =
                                packet.arrival.elapsed().as_secs_f64() * 1000.0;
                            slot.timing.max_ingest_ms =
                                slot.timing.max_ingest_ms.max(slot.timing.last_ingest_ms);
                            if let Some(state) = mixer.source_state(slot.handle) {
                                slot.timing.peak_buffered_ms =
                                    slot.timing.peak_buffered_ms.max(state.buffered_ms);
                            }
                        }
                    }
                    let now = Instant::now();
                    let due = output.as_ref().map_or(now >= next, |queue| queue.len() < 2);
                    if due {
                        if let Some(last) = last_render {
                            let interval = now.duration_since(last).as_secs_f64() * 1000.0;
                            timing.last_render_interval_ms = Some(interval);
                            timing.max_render_interval_ms =
                                timing.max_render_interval_ms.max(interval);
                            let delay = (interval - 10.0).max(0.0);
                            timing.max_render_delay_ms = timing.max_render_delay_ms.max(delay);
                            if delay > 20.0 {
                                timing.late_render_calls += 1;
                            }
                        }
                        last_render = Some(now);
                        timing.render_calls += 1;
                        timing.last_requested_audio_ms = 10.0;
                        let begin = Instant::now();
                        let _ = mixer.render(&mut block);
                        timing.last_render_work_ms = begin.elapsed().as_secs_f64() * 1000.0;
                        timing.max_render_work_ms =
                            timing.max_render_work_ms.max(timing.last_render_work_ms);
                        rms = (block.iter().map(|&v| f64::from(v).powi(2)).sum::<f64>()
                            / block.len() as f64)
                            .sqrt();
                        peak = block.iter().fold(0.0f32, |p, &v| p.max(v.abs()));
                        if let Some(queue) = &output {
                            let _ = queue.push(block);
                        }
                        // Retain the hardware-free test clock's phase instead of adding
                        // scheduling lateness to every block and falling behind real time.
                        next += Duration::from_millis(10);
                        if now.saturating_duration_since(next) > Duration::from_secs(1) {
                            next = now + Duration::from_millis(10);
                        }
                    }
                    thread::sleep(Duration::from_millis(1));
                }
            })?;
        let engine = Self {
            commands,
            stop,
            output_underruns: Arc::new(AtomicU64::new(0)),
        };
        Ok(EngineWorker {
            engine,
            thread: Some(thread),
        })
    }
    async fn request<T>(&self, command: impl FnOnce(Reply<T>) -> Command) -> Result<T, String> {
        let (send, receive) = oneshot::channel();
        self.commands
            .try_send(command(send))
            .map_err(|_| "Audio control queue unavailable".to_owned())?;
        tokio::time::timeout(Duration::from_secs(2), receive)
            .await
            .map_err(|_| "Audio control timeout".to_owned())?
            .map_err(|_| "Audio engine stopped".to_owned())?
    }
    pub async fn add(
        &self,
        id: Uuid,
        name: String,
        format: AudioFormat,
        settings: SourceSettings,
    ) -> Result<Arc<Ingress>, String> {
        self.request(|reply| Command::Add {
            id,
            name,
            format,
            settings,
            reply,
        })
        .await
    }
    pub async fn remove(&self, id: Uuid) -> Result<(), String> {
        self.request(|reply| Command::Remove { id, reply }).await
    }
    pub async fn configure(&self, id: Uuid, settings: SourceSettings) -> Result<(), String> {
        self.request(|reply| Command::Configure {
            id,
            settings,
            reply,
        })
        .await
    }
    pub async fn set_mix(&self, settings: MixSettings) -> Result<(), String> {
        self.request(|reply| Command::Mix { settings, reply }).await
    }
    pub async fn snapshot(&self) -> Result<MixerState, String> {
        let mut state = self.request(|reply| Command::Snapshot { reply }).await?;
        state.diagnostics.output_underruns = self.output_underruns.load(Ordering::Relaxed);
        Ok(state)
    }
    pub fn output_underruns_counter(&self) -> Arc<AtomicU64> {
        self.output_underruns.clone()
    }
    pub async fn set_processing(&self, settings: ProcessingSettings) -> Result<(), String> {
        self.request(|reply| Command::Processing { settings, reply })
            .await
    }
}
