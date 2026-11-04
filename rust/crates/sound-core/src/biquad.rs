use crate::{MixSettings, SAMPLE_RATE};
use std::f64::consts::PI;

#[derive(Clone, Copy)]
pub(crate) struct Biquad {
    b0: f64,
    b1: f64,
    b2: f64,
    a1: f64,
    a2: f64,
    z1: f64,
    z2: f64,
}

impl Biquad {
    fn new(frequency: f64, db: f64, kind: u8) -> Self {
        let a = 10.0_f64.powf(db / 40.0);
        let w = 2.0 * PI * frequency / SAMPLE_RATE as f64;
        let c = w.cos();
        let alpha = w.sin() / 2.0_f64.sqrt();
        let t = 2.0 * a.sqrt() * alpha;
        let (b0, b1, b2, a0, a1, a2) = match kind {
            0 => (
                a * ((a + 1.0) - (a - 1.0) * c + t),
                2.0 * a * ((a - 1.0) - (a + 1.0) * c),
                a * ((a + 1.0) - (a - 1.0) * c - t),
                (a + 1.0) + (a - 1.0) * c + t,
                -2.0 * ((a - 1.0) + (a + 1.0) * c),
                (a + 1.0) + (a - 1.0) * c - t,
            ),
            1 => (
                a * ((a + 1.0) + (a - 1.0) * c + t),
                -2.0 * a * ((a - 1.0) + (a + 1.0) * c),
                a * ((a + 1.0) + (a - 1.0) * c - t),
                (a + 1.0) - (a - 1.0) * c + t,
                2.0 * ((a - 1.0) - (a + 1.0) * c),
                (a + 1.0) - (a - 1.0) * c - t,
            ),
            _ => (
                1.0 + alpha * a,
                -2.0 * c,
                1.0 - alpha * a,
                1.0 + alpha / a,
                -2.0 * c,
                1.0 - alpha / a,
            ),
        };
        Self {
            b0: b0 / a0,
            b1: b1 / a0,
            b2: b2 / a0,
            a1: a1 / a0,
            a2: a2 / a0,
            z1: 0.0,
            z2: 0.0,
        }
    }

    pub fn bank(settings: MixSettings) -> [[Self; 3]; 2] {
        let channel = [
            Self::new(120.0, settings.bass_db as f64, 0),
            Self::new(4000.0, settings.treble_db as f64, 1),
            Self::new(2500.0, settings.presence_db as f64, 2),
        ];
        [channel; 2]
    }

    pub fn process(&mut self, input: f32) -> f32 {
        let value = input as f64;
        let result = value * self.b0 + self.z1;
        self.z1 = value * self.b1 - self.a1 * result + self.z2;
        self.z2 = value * self.b2 - self.a2 * result;
        result as f32
    }
}
