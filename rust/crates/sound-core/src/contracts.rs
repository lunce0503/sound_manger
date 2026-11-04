use serde::{Deserialize, Serialize};
use std::fmt;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AudioError {
    InvalidFormat,
    InvalidSettings,
    InvalidName,
    PartialFrame,
    SourceLimit,
    SourceNotFound,
}

impl fmt::Display for AudioError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::InvalidFormat => "expected 8-192 kHz, mono/stereo PCM16 or float32 LE",
            Self::InvalidSettings => "invalid volume, EQ, direction or display name",
            Self::InvalidName => "source name must contain 1-64 UTF-16 code units",
            Self::PartialFrame => "PCM and render buffers must contain complete frames",
            Self::SourceLimit => "at most 16 sources can be connected",
            Self::SourceNotFound => "source not found",
        })
    }
}

impl std::error::Error for AudioError {}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Encoding {
    Pcm16,
    Float32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioFormat {
    pub sample_rate: u32,
    pub channels: u16,
    pub encoding: Encoding,
}

impl AudioFormat {
    pub fn validate(self) -> Result<(), AudioError> {
        if (8_000..=192_000).contains(&self.sample_rate) && (1..=2).contains(&self.channels) {
            Ok(())
        } else {
            Err(AudioError::InvalidFormat)
        }
    }

    pub fn bytes_per_sample(self) -> usize {
        match self.encoding {
            Encoding::Pcm16 => 2,
            Encoding::Float32 => 4,
        }
    }

    pub fn frame_bytes(self) -> usize {
        self.channels as usize * self.bytes_per_sample()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MixSettings {
    pub master_volume: f32,
    pub bass_db: f32,
    pub treble_db: f32,
    pub presence_db: f32,
}

impl Default for MixSettings {
    fn default() -> Self {
        Self {
            master_volume: 0.7,
            bass_db: 0.0,
            treble_db: 0.0,
            presence_db: 0.0,
        }
    }
}

impl MixSettings {
    pub fn validate(self) -> Result<(), AudioError> {
        if (0.0..=1.0).contains(&self.master_volume)
            && [self.bass_db, self.treble_db, self.presence_db]
                .iter()
                .all(|v| (-12.0..=12.0).contains(v))
        {
            Ok(())
        } else {
            Err(AudioError::InvalidSettings)
        }
    }

    pub fn preset(name: &str, master_volume: f32) -> Result<Self, AudioError> {
        let (bass_db, treble_db, presence_db) = match name {
            "flat" => (0.0, 0.0, 0.0),
            "bass" => (6.0, 0.0, 0.0),
            "treble" => (0.0, 6.0, 0.0),
            "soft" => (2.0, -4.0, -2.0),
            "clear" => (-2.0, 3.0, 4.0),
            _ => return Err(AudioError::InvalidSettings),
        };
        let settings = Self {
            master_volume,
            bass_db,
            treble_db,
            presence_db,
        };
        settings.validate()?;
        Ok(settings)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SourceSettings {
    pub volume: f32,
    pub azimuth: f32,
    pub spatial: bool,
    pub muted: bool,
    pub display_name: Option<String>,
    pub auto_gain: bool,
}

impl Default for SourceSettings {
    fn default() -> Self {
        Self {
            volume: 1.0,
            azimuth: 0.0,
            spatial: false,
            muted: false,
            display_name: None,
            auto_gain: false,
        }
    }
}

impl SourceSettings {
    pub fn validate(&self) -> Result<(), AudioError> {
        if (0.0..=2.0).contains(&self.volume)
            && (0.0..360.0).contains(&self.azimuth)
            && self
                .display_name
                .as_ref()
                .is_none_or(|name| valid_name(name) && !name.chars().any(char::is_control))
        {
            Ok(())
        } else {
            Err(AudioError::InvalidSettings)
        }
    }
}

pub(crate) fn valid_name(name: &str) -> bool {
    !name.trim().is_empty() && name.encode_utf16().count() <= 64
}

/// Internal DSP handle, not a v1 wire UUID or a persisted device identity.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SourceId(pub(crate) u64);

#[derive(Clone, Debug)]
pub struct SourceState {
    pub id: SourceId,
    pub name: String,
    pub format: AudioFormat,
    pub settings: SourceSettings,
    pub buffered_ms: f64,
    pub dropped_frames: u64,
    pub underruns: u64,
    pub received_packets: u64,
    pub latency_trims: u64,
    pub latency_trimmed_frames: u64,
}
