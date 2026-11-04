//! Wire contracts retain the v1 Android, Windows and browser clients' camelCase JSON.
use serde::{Deserialize, Serialize};
use sound_core::{AudioError, AudioFormat, MixSettings, ProcessingSettings, SourceSettings};
use uuid::Uuid;

pub const MAX_HELLO: usize = 4096;
pub const MAX_MESSAGE: usize = 65536;
pub const KEEPALIVE: &[u8] = b"{\"type\":\"keepalive\"}";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioHello {
    pub version: u32,
    pub name: String,
    pub format: AudioFormat,
    pub device_id: Option<Uuid>,
    pub key: Option<String>,
}
impl AudioHello {
    pub fn validate(&self) -> Result<(), AudioError> {
        self.format.validate()?;
        if self.version != 1 || self.device_id.is_some_and(|id| id.is_nil()) {
            return Err(AudioError::InvalidFormat);
        }
        if self.name.trim().is_empty() || self.name.encode_utf16().count() > 64 {
            return Err(AudioError::InvalidName);
        }
        Ok(())
    }
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SourceSettingsPatch {
    pub volume: Option<f32>,
    pub azimuth: Option<f32>,
    pub spatial: Option<bool>,
    pub muted: Option<bool>,
    pub display_name: Option<String>,
    pub reset_name: bool,
    pub auto_gain: Option<bool>,
}
impl SourceSettingsPatch {
    pub fn apply(&self, current: &SourceSettings) -> Result<SourceSettings, AudioError> {
        if self.reset_name && self.display_name.is_some() {
            return Err(AudioError::InvalidSettings);
        }
        let next = SourceSettings {
            volume: self.volume.unwrap_or(current.volume),
            azimuth: self.azimuth.unwrap_or(current.azimuth),
            spatial: self.spatial.unwrap_or(current.spatial),
            muted: self.muted.unwrap_or(current.muted),
            auto_gain: self.auto_gain.unwrap_or(current.auto_gain),
            display_name: if self.reset_name {
                None
            } else {
                self.display_name
                    .clone()
                    .or_else(|| current.display_name.clone())
            },
        };
        next.validate()?;
        Ok(next)
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SourceDiagnostics {
    pub received_packets: u64,
    pub last_packet_audio_ms: f64,
    pub last_receive_interval_ms: Option<f64>,
    pub max_receive_interval_ms: f64,
    pub max_receive_excess_ms: f64,
    pub last_receive_ago_ms: Option<f64>,
    pub last_ingest_ms: f64,
    pub max_ingest_ms: f64,
    pub peak_buffered_ms: f64,
    pub latency_trims: u64,
    pub latency_trimmed_frames: u64,
    pub target_buffer_ms: f64,
    pub trim_threshold_ms: f64,
    pub ingress_dropped_frames: u64,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RenderDiagnostics {
    pub render_calls: u64,
    pub last_requested_audio_ms: f64,
    pub last_render_interval_ms: Option<f64>,
    pub max_render_interval_ms: f64,
    pub max_render_delay_ms: f64,
    pub last_render_ago_ms: Option<f64>,
    pub last_render_work_ms: f64,
    pub max_render_work_ms: f64,
    pub late_render_calls: u64,
    pub output_underruns: u64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceState {
    pub id: Uuid,
    pub name: String,
    pub format: AudioFormat,
    pub settings: SourceSettings,
    pub buffered_ms: f64,
    pub dropped_frames: u64,
    pub underruns: u64,
    pub diagnostics: SourceDiagnostics,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MixerState {
    pub settings: MixSettings,
    pub sources: Vec<SourceState>,
    pub clipped_samples: u64,
    pub diagnostics: RenderDiagnostics,
    #[serde(default)]
    pub output_rms: f64,
    #[serde(default)]
    pub output_peak: f32,
    pub processing: ProcessingSettings,
}
