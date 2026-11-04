use std::f64::consts::PI;

pub(crate) struct Spatial {
    delay: [f32; 64],
    index: usize,
    rear_low: f32,
}

pub(crate) struct Direction {
    side: f64,
    rear: f64,
    ear_delay: usize,
    left_gain: f32,
    right_gain: f32,
}

impl Direction {
    pub fn new(azimuth: f32) -> Self {
        let angle = azimuth as f64 * PI / 180.0;
        let side = angle.sin();
        Self {
            side,
            rear: (1.0 - angle.cos()) / 2.0,
            ear_delay: (side.abs() * 30.0) as usize,
            left_gain: (1.0 - side.max(0.0) * 0.65) as f32,
            right_gain: (1.0 + side.min(0.0) * 0.65) as f32,
        }
    }
}

impl Default for Spatial {
    fn default() -> Self {
        Self {
            delay: [0.0; 64],
            index: 0,
            rear_low: 0.0,
        }
    }
}

impl Spatial {
    pub fn reset(&mut self) {
        self.delay.fill(0.0);
        self.rear_low = 0.0;
    }

    pub fn process(&mut self, frame: [f32; 2], direction: &Direction) -> [f32; 2] {
        let mut mono = (frame[0] + frame[1]) / 2.0;
        self.rear_low += 0.18 * (mono - self.rear_low);
        mono = mono * (1.0 - direction.rear as f32 * 0.65)
            + self.rear_low * direction.rear as f32 * 0.65;
        self.delay[self.index] = mono;
        let far =
            self.delay[(self.index + self.delay.len() - direction.ear_delay) % self.delay.len()];
        self.index = (self.index + 1) % self.delay.len();
        let left = if direction.side > 0.0 { far } else { mono };
        let right = if direction.side < 0.0 { far } else { mono };
        [left * direction.left_gain, right * direction.right_gain]
    }
}
