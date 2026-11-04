//! Sound Manager's v1-compatible DSP, with no network or platform dependencies.
//!
//! A single render owner holds `Mixer`. Push, configure and render need exclusive
//! access; a future network adapter must hand packets and commands to that owner.
//! This crate does not put mutexes or an unbounded queue on the output callback.

mod biquad;
mod contracts;
mod mixer;
mod processing;
mod resampler;
mod ring_buffer;
mod source;
mod spatial;

pub use contracts::{
    AudioError, AudioFormat, Encoding, MixSettings, SourceId, SourceSettings, SourceState,
};
pub use mixer::Mixer;
pub use processing::{LOOKAHEAD_FRAMES, ProcessingSettings};

pub const SAMPLE_RATE: u32 = 48_000;
pub const MAX_SOURCES: usize = 16;
pub const START_BUFFER_MS: u32 = 40;
pub const TARGET_BUFFER_MS: u32 = 60;
pub const TRIM_THRESHOLD_MS: u32 = 120;
