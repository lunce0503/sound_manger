use crate::{
    AudioError, AudioFormat, MAX_SOURCES, MixSettings, SourceId, SourceSettings, SourceState,
    biquad::Biquad,
    contracts::valid_name,
    processing::{Limiter, ProcessingSettings},
    source::AudioSource,
};

/// Render-owned mixer. Allocate sources and take snapshots outside the callback.
pub struct Mixer {
    sources: Vec<AudioSource>,
    next_id: u64,
    settings: MixSettings,
    applied: MixSettings,
    filters: [[Biquad; 3]; 2],
    clipped_samples: u64,
    processing: ProcessingSettings,
    limiter: Limiter,
}

impl Default for Mixer {
    fn default() -> Self {
        let settings = MixSettings::default();
        Self {
            sources: Vec::with_capacity(MAX_SOURCES),
            next_id: 1,
            settings,
            applied: settings,
            filters: Biquad::bank(settings),
            clipped_samples: 0,
            processing: ProcessingSettings::default(),
            limiter: Limiter::default(),
        }
    }
}

impl Mixer {
    pub fn processing(&self) -> ProcessingSettings {
        self.processing
    }
    pub fn set_processing(&mut self, settings: ProcessingSettings) -> Result<(), AudioError> {
        settings.validate()?;
        if settings.limiter_enabled != self.processing.limiter_enabled {
            self.limiter = Limiter::default();
        }
        self.processing = settings;
        for source in &mut self.sources {
            source.configure_processing(settings);
        }
        Ok(())
    }
    pub fn source_state(&self, id: SourceId) -> Option<SourceState> {
        self.sources
            .iter()
            .find(|s| s.id == id)
            .map(AudioSource::snapshot)
    }
    pub fn settings(&self) -> MixSettings {
        self.settings
    }

    pub fn set_settings(&mut self, settings: MixSettings) -> Result<(), AudioError> {
        settings.validate()?;
        self.settings = settings;
        Ok(())
    }

    pub fn add_source(
        &mut self,
        name: &str,
        format: AudioFormat,
        settings: SourceSettings,
    ) -> Result<SourceId, AudioError> {
        format.validate()?;
        settings.validate()?;
        if !valid_name(name) {
            return Err(AudioError::InvalidName);
        }
        if self.sources.len() >= MAX_SOURCES {
            return Err(AudioError::SourceLimit);
        }
        let id = SourceId(self.next_id);
        self.next_id = self.next_id.checked_add(1).ok_or(AudioError::SourceLimit)?;
        let mut source = AudioSource::new(id, name.to_owned(), format, settings);
        source.configure_processing(self.processing);
        self.sources.push(source);
        Ok(id)
    }

    pub fn remove_source(&mut self, id: SourceId) -> bool {
        let before = self.sources.len();
        self.sources.retain(|source| source.id != id);
        before != self.sources.len()
    }

    pub fn configure_source(
        &mut self,
        id: SourceId,
        settings: SourceSettings,
    ) -> Result<(), AudioError> {
        settings.validate()?;
        let source = self
            .sources
            .iter_mut()
            .find(|source| source.id == id)
            .ok_or(AudioError::SourceNotFound)?;
        source.settings = settings;
        Ok(())
    }

    pub fn push(&mut self, id: SourceId, pcm: &[u8]) -> Result<(), AudioError> {
        self.sources
            .iter_mut()
            .find(|source| source.id == id)
            .ok_or(AudioError::SourceNotFound)?
            .push(pcm)
    }

    pub fn snapshot(&self) -> Vec<SourceState> {
        self.sources.iter().map(AudioSource::snapshot).collect()
    }

    pub fn clipped_samples(&self) -> u64 {
        self.clipped_samples
    }

    /// Fill complete 48 kHz stereo frames. No allocation, locks or I/O, including
    /// when EQ changes. Underrunning sources contribute silence independently.
    pub fn render(&mut self, stereo: &mut [f32]) -> Result<(), AudioError> {
        if !stereo.len().is_multiple_of(2) {
            return Err(AudioError::PartialFrame);
        }
        if stereo.is_empty() {
            return Ok(());
        }
        stereo.fill(0.0);
        for source in &mut self.sources {
            source.mix(stereo);
        }
        if self.applied != self.settings {
            self.filters = Biquad::bank(self.settings);
            self.applied = self.settings;
        }
        for frame in stereo.as_chunks_mut::<2>().0 {
            let mut values = [0.0; 2];
            for channel in 0..2 {
                let mut value = frame[channel];
                for filter in &mut self.filters[channel] {
                    value = filter.process(value);
                }
                value *= self.settings.master_volume;
                values[channel] = if value.is_finite() { value } else { 0.0 };
            }
            if self.processing.limiter_enabled {
                *frame = self.limiter.process(values, self.processing.peak_ceiling);
            } else {
                for channel in 0..2 {
                    if values[channel].abs() > 1.0 {
                        self.clipped_samples += 1;
                    }
                    frame[channel] = values[channel].clamp(-1.0, 1.0);
                }
            }
        }
        Ok(())
    }
}
