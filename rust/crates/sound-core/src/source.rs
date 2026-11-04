use crate::{
    AudioError, AudioFormat, Encoding, SAMPLE_RATE, START_BUFFER_MS, SourceId, SourceSettings,
    SourceState, TARGET_BUFFER_MS, TRIM_THRESHOLD_MS,
    resampler::Resampler,
    ring_buffer::RingBuffer,
    spatial::{Direction, Spatial},
};

pub(crate) struct AudioSource {
    pub id: SourceId,
    pub settings: SourceSettings,
    name: String,
    format: AudioFormat,
    ring: RingBuffer,
    resampler: Resampler,
    spatial: Spatial,
    started: bool,
    dropped: u64,
    underruns: u64,
    received_packets: u64,
    latency_trims: u64,
    latency_trimmed_frames: u64,
}

impl AudioSource {
    pub fn new(id: SourceId, name: String, format: AudioFormat, settings: SourceSettings) -> Self {
        Self {
            id,
            name,
            format,
            settings,
            ring: RingBuffer::new((format.sample_rate / 2 + 2) as usize),
            resampler: Resampler::default(),
            spatial: Spatial::default(),
            started: false,
            dropped: 0,
            underruns: 0,
            received_packets: 0,
            latency_trims: 0,
            latency_trimmed_frames: 0,
        }
    }

    fn decode(&self, bytes: &[u8]) -> f32 {
        let value = match self.format.encoding {
            Encoding::Pcm16 => i16::from_le_bytes([bytes[0], bytes[1]]) as f32 / 32768.0,
            Encoding::Float32 => f32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]),
        };
        if value.is_finite() {
            value.clamp(-1.0, 1.0)
        } else {
            0.0
        }
    }

    pub fn push(&mut self, pcm: &[u8]) -> Result<(), AudioError> {
        if !pcm.len().is_multiple_of(self.format.frame_bytes()) {
            return Err(AudioError::PartialFrame);
        }
        if pcm.is_empty() {
            return Ok(());
        }
        self.received_packets += 1;
        for frame in pcm.chunks_exact(self.format.frame_bytes()) {
            let left = self.decode(frame);
            let right = if self.format.channels == 1 {
                left
            } else {
                self.decode(&frame[self.format.bytes_per_sample()..])
            };
            if self.ring.push([left, right]) {
                self.dropped += 1;
                self.resampler.reset();
            }
        }
        Ok(())
    }

    pub fn snapshot(&self) -> SourceState {
        SourceState {
            id: self.id,
            name: self
                .settings
                .display_name
                .as_ref()
                .unwrap_or(&self.name)
                .clone(),
            format: self.format,
            settings: self.settings.clone(),
            buffered_ms: self.ring.len() as f64 * 1000.0 / self.format.sample_rate as f64,
            dropped_frames: self.dropped,
            underruns: self.underruns,
            received_packets: self.received_packets,
            latency_trims: self.latency_trims,
            latency_trimmed_frames: self.latency_trimmed_frames,
        }
    }

    pub fn mix(&mut self, output: &mut [f32]) {
        let rate = self.format.sample_rate as usize;
        let reserve = rate * TARGET_BUFFER_MS as usize / 1000;
        let request = (output.len() as f64 / 2.0 * rate as f64 / SAMPLE_RATE as f64 * 1.002).ceil()
            as usize
            + 2;
        let keep = reserve + request;
        let threshold = (rate * TRIM_THRESHOLD_MS as usize / 1000).max(keep);
        if self.ring.len() > threshold {
            let discard = self.ring.len() - keep;
            self.ring.discard(discard);
            self.dropped += discard as u64;
            self.latency_trimmed_frames += discard as u64;
            self.latency_trims += 1;
            self.resampler.reset();
            self.spatial.reset();
        }
        if !self.started && self.ring.len() < rate * START_BUFFER_MS as usize / 1000 {
            return;
        }
        self.started = true;
        let step = Resampler::step(self.format.sample_rate, self.ring.len(), reserve);
        let direction = Direction::new(self.settings.azimuth);
        for frame in output.as_chunks_mut::<2>().0 {
            let Some(mut input) = self.resampler.next(&mut self.ring, step) else {
                self.started = false;
                self.underruns += 1;
                self.resampler.reset();
                self.spatial.reset();
                break;
            };
            if self.settings.muted {
                continue;
            }
            if self.settings.spatial {
                input = self.spatial.process(input, &direction);
            }
            frame[0] += input[0] * self.settings.volume;
            frame[1] += input[1] * self.settings.volume;
        }
    }
}
