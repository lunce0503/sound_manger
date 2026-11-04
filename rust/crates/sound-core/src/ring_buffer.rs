/// Bounded, render-owned stereo frames at the input sample rate.
/// Not an SPSC transport: all mutation requires exclusive ownership.
pub(crate) struct RingBuffer {
    frames: Box<[[f32; 2]]>,
    head: usize,
    len: usize,
}

impl RingBuffer {
    pub fn new(capacity: usize) -> Self {
        Self {
            frames: vec![[0.0; 2]; capacity].into_boxed_slice(),
            head: 0,
            len: 0,
        }
    }

    pub fn len(&self) -> usize {
        self.len
    }

    pub fn push(&mut self, frame: [f32; 2]) -> bool {
        let overflow = self.len == self.frames.len();
        if overflow {
            self.discard(1);
        }
        let tail = (self.head + self.len) % self.frames.len();
        self.frames[tail] = frame;
        self.len += 1;
        overflow
    }

    pub fn get(&self, offset: usize) -> [f32; 2] {
        debug_assert!(offset < self.len);
        self.frames[(self.head + offset) % self.frames.len()]
    }

    pub fn discard(&mut self, frames: usize) {
        let count = frames.min(self.len);
        self.head = (self.head + count) % self.frames.len();
        self.len -= count;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overflow_and_wrap_preserve_latest_frames() {
        let mut ring = RingBuffer::new(3);
        for i in 0..3 {
            assert!(!ring.push([i as f32; 2]));
        }
        assert!(ring.push([3.0; 2]));
        assert_eq!(ring.get(0), [1.0; 2]);
        ring.discard(2);
        ring.push([4.0; 2]);
        ring.push([5.0; 2]);
        assert_eq!(ring.len(), 3);
        assert_eq!(ring.get(0), [3.0; 2]);
        assert_eq!(ring.get(2), [5.0; 2]);
    }
}
