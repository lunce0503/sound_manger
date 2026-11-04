use crate::{SAMPLE_RATE, ring_buffer::RingBuffer};

#[derive(Default)]
pub(crate) struct Resampler {
    phase: f64,
}

impl Resampler {
    pub fn reset(&mut self) {
        self.phase = 0.0;
    }

    pub fn step(
        sample_rate: u32,
        buffered: usize,
        reserve: usize,
        correction_enabled: bool,
    ) -> f64 {
        // v1 C# divides two integers before converting to double. Preserve that
        // truncation for parity; enabling fractional drift is a Phase 4 change.
        let error = buffered as f64 - reserve as f64;
        let correction = if correction_enabled {
            (error / (f64::from(sample_rate) * 20.0)).clamp(-0.002, 0.002)
        } else {
            (((buffered as i64 - reserve as i64) / (sample_rate as i64 * 20)) as f64)
                .clamp(-0.002, 0.002)
        };
        sample_rate as f64 / SAMPLE_RATE as f64 * (1.0 + correction)
    }

    pub fn next(&mut self, ring: &mut RingBuffer, step: f64) -> Option<[f32; 2]> {
        if ring.len() < 2 {
            return None;
        }
        let a = ring.get(0);
        let b = ring.get(1);
        let frame = [
            a[0] + (b[0] - a[0]) * self.phase as f32,
            a[1] + (b[1] - a[1]) * self.phase as f32,
        ];
        self.phase += step;
        let advance = self.phase as usize;
        ring.discard(advance);
        self.phase -= advance as f64;
        Some(frame)
    }
}
