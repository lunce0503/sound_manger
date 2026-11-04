use crate::AudioError;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProcessingSettings {
    pub limiter_enabled: bool,
    pub clock_correction: bool,
    pub peak_ceiling: f32,
    pub target_dbfs: f32,
    pub max_gain_db: f32,
    pub attack_ms: f32,
    pub release_ms: f32,
    pub gate_dbfs: f32,
}
impl Default for ProcessingSettings {
    fn default() -> Self {
        Self {
            limiter_enabled: false,
            clock_correction: false,
            peak_ceiling: 0.98,
            target_dbfs: -18.0,
            max_gain_db: 12.0,
            attack_ms: 10.0,
            release_ms: 500.0,
            gate_dbfs: -60.0,
        }
    }
}
impl ProcessingSettings {
    pub fn recommended() -> Self {
        Self {
            limiter_enabled: true,
            clock_correction: true,
            ..Self::default()
        }
    }
    pub fn validate(self) -> Result<(), AudioError> {
        if (0.1..=1.0).contains(&self.peak_ceiling)
            && (-36.0..=-6.0).contains(&self.target_dbfs)
            && (0.0..=24.0).contains(&self.max_gain_db)
            && (1.0..=100.0).contains(&self.attack_ms)
            && (50.0..=3000.0).contains(&self.release_ms)
            && (-90.0..=-30.0).contains(&self.gate_dbfs)
            && self.gate_dbfs <= self.target_dbfs - 6.0
        {
            Ok(())
        } else {
            Err(AudioError::InvalidSettings)
        }
    }
}

pub(crate) struct Agc {
    power: f64,
    gain: f64,
    target: f64,
    maximum: f64,
    gate: f64,
    attack: f64,
    release: f64,
}
impl Default for Agc {
    fn default() -> Self {
        let mut agc = Self {
            power: 0.0,
            gain: 1.0,
            target: 0.0,
            maximum: 1.0,
            gate: 0.0,
            attack: 0.0,
            release: 0.0,
        };
        agc.configure(ProcessingSettings::default());
        agc
    }
}
impl Agc {
    pub fn configure(&mut self, settings: ProcessingSettings) {
        self.target = 10f64.powf(f64::from(settings.target_dbfs) / 20.0);
        self.maximum = 10f64.powf(f64::from(settings.max_gain_db) / 20.0);
        self.gate = 10f64.powf(f64::from(settings.gate_dbfs) / 10.0);
        self.attack = 1.0 - (-1.0 / (f64::from(settings.attack_ms) * 48.0)).exp();
        self.release = 1.0 - (-1.0 / (f64::from(settings.release_ms) * 48.0)).exp();
    }
    pub fn process(&mut self, frame: [f32; 2], enabled: bool) -> [f32; 2] {
        if !enabled && (self.gain - 1.0).abs() < 1e-10 {
            self.gain = 1.0;
            return frame;
        }
        let power = (f64::from(frame[0]).powi(2) + f64::from(frame[1]).powi(2)) * 0.5;
        self.power += 0.000694203 * (power - self.power);
        let desired = if enabled && self.power > self.gate {
            (self.target / self.power.sqrt()).clamp(0.01, self.maximum)
        } else {
            1.0
        };
        let coefficient = if desired < self.gain {
            self.attack
        } else {
            self.release
        };
        self.gain += coefficient * (desired - self.gain);
        [frame[0] * self.gain as f32, frame[1] * self.gain as f32]
    }
}

pub const LOOKAHEAD_FRAMES: usize = 240;
const HISTORY: usize = LOOKAHEAD_FRAMES + 1;
/// Stereo-linked 5 ms look-ahead limiter. Fixed storage, no allocation in process().
pub(crate) struct Limiter {
    frames: [[f32; 2]; HISTORY],
    peaks: [f32; HISTORY],
    indices: [u64; HISTORY],
    head: usize,
    count: usize,
    position: u64,
    gain: f64,
}
impl Default for Limiter {
    fn default() -> Self {
        Self {
            frames: [[0.0; 2]; HISTORY],
            peaks: [0.0; HISTORY],
            indices: [0; HISTORY],
            head: 0,
            count: 0,
            position: 0,
            gain: 1.0,
        }
    }
}
impl Limiter {
    pub fn process(&mut self, input: [f32; 2], ceiling: f32) -> [f32; 2] {
        let peak = input[0].abs().max(input[1].abs());
        let index = self.position;
        while self.count > 0 && self.indices[self.head] + (LOOKAHEAD_FRAMES as u64) < index {
            self.head = (self.head + 1) % HISTORY;
            self.count -= 1;
        }
        while self.count > 0 && self.peaks[(self.head + self.count - 1) % HISTORY] <= peak {
            self.count -= 1;
        }
        let tail = (self.head + self.count) % HISTORY;
        self.peaks[tail] = peak;
        self.indices[tail] = index;
        self.count += 1;
        let position = index as usize % HISTORY;
        self.frames[position] = input;
        let output = if index >= LOOKAHEAD_FRAMES as u64 {
            self.frames[(position + 1) % HISTORY]
        } else {
            [0.0; 2]
        };
        let wanted = if self.peaks[self.head] > ceiling {
            f64::from(ceiling / self.peaks[self.head])
        } else {
            1.0
        };
        if wanted < self.gain {
            self.gain = wanted;
        } else {
            self.gain += 0.0002603828 * (wanted - self.gain);
        }
        self.position += 1;
        [
            (output[0] * self.gain as f32).clamp(-ceiling, ceiling),
            (output[1] * self.gain as f32).clamp(-ceiling, ceiling),
        ]
    }
}
