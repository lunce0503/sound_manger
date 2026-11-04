use sound_core::{
    AudioFormat, Encoding, LOOKAHEAD_FRAMES, MixSettings, Mixer, ProcessingSettings, SourceId,
    SourceSettings,
};
use std::f32::consts::TAU;

fn tone(frames: usize, offset: usize, level: f32) -> Vec<u8> {
    (0..frames)
        .flat_map(|i| {
            let sample = (TAU * 440.0 * (i + offset) as f32 / 48000.0).sin() * level;
            [sample, sample * 0.5]
                .into_iter()
                .flat_map(f32::to_le_bytes)
        })
        .collect()
}
fn source(mixer: &mut Mixer, auto_gain: bool) -> SourceId {
    mixer
        .add_source(
            "test",
            AudioFormat {
                sample_rate: 48000,
                channels: 2,
                encoding: Encoding::Float32,
            },
            SourceSettings {
                auto_gain,
                ..Default::default()
            },
        )
        .unwrap()
}
#[test]
fn agc_normalizes_different_input_levels_without_destroying_stereo_balance() {
    let mut results = Vec::new();
    for level in [0.2, 0.8] {
        let mut mixer = Mixer::default();
        mixer
            .set_settings(MixSettings {
                master_volume: 1.0,
                ..Default::default()
            })
            .unwrap();
        let id = source(&mut mixer, true);
        mixer.push(id, &tone(3840, 0, level)).unwrap();
        let mut energy = 0.0;
        let mut count = 0;
        for block in 0..400 {
            mixer
                .push(id, &tone(480, 3840 + block * 480, level))
                .unwrap();
            let mut output = [0.0f32; 960];
            mixer.render(&mut output).unwrap();
            if block > 300 {
                for frame in output.as_chunks::<2>().0 {
                    assert!((frame[0] - frame[1] * 2.0).abs() < 1e-5);
                    energy += frame.iter().map(|&v| f64::from(v).powi(2)).sum::<f64>();
                    count += 2;
                }
            }
        }
        results.push((energy / count as f64).sqrt());
    }
    let target = 10f64.powf(-18.0 / 20.0);
    for actual in &results {
        assert!(
            (actual - target).abs() < 0.01,
            "AGC RMS {actual}, target {target}"
        );
    }
    assert!((results[0] - results[1]).abs() < 0.01);
}
#[test]
fn agc_silence_gate_and_maximum_boost_bound_background_noise() {
    let mut mixer = Mixer::default();
    mixer
        .set_settings(MixSettings {
            master_volume: 1.0,
            ..Default::default()
        })
        .unwrap();
    let id = source(&mut mixer, true);
    let tiny = 1e-5f32;
    let bytes: Vec<_> = (0..3840)
        .flat_map(|_| [tiny, tiny].into_iter().flat_map(f32::to_le_bytes))
        .collect();
    mixer.push(id, &bytes).unwrap();
    for _ in 0..100 {
        let packet: Vec<_> = (0..480)
            .flat_map(|_| [tiny, tiny].into_iter().flat_map(f32::to_le_bytes))
            .collect();
        mixer.push(id, &packet).unwrap();
        let mut output = [0.0; 960];
        mixer.render(&mut output).unwrap();
        assert!(output.iter().all(|v| v.abs() <= tiny * 1.01));
    }
}
#[test]
fn lookahead_limiter_caps_three_sources_and_keeps_channels_linked() {
    let mut mixer = Mixer::default();
    mixer
        .set_settings(MixSettings {
            master_volume: 1.0,
            ..Default::default()
        })
        .unwrap();
    mixer
        .set_processing(ProcessingSettings {
            limiter_enabled: true,
            peak_ceiling: 0.7,
            ..Default::default()
        })
        .unwrap();
    let ids: Vec<_> = (0..3).map(|_| source(&mut mixer, false)).collect();
    for &id in &ids {
        mixer.push(id, &tone(3840, 0, 0.8)).unwrap();
    }
    let mut heard = false;
    let mut previous = 0.0f32;
    let mut max_delta = 0.0f32;
    for block in 0..100 {
        for &id in &ids {
            mixer.push(id, &tone(480, 3840 + block * 480, 0.8)).unwrap();
        }
        let mut output = [0.0; 960];
        mixer.render(&mut output).unwrap();
        for (i, frame) in output.as_chunks::<2>().0.iter().enumerate() {
            assert!(frame.iter().all(|v| v.is_finite() && v.abs() <= 0.7));
            assert!((frame[0] - frame[1] * 2.0).abs() < 1e-5);
            if block == 0 && i < LOOKAHEAD_FRAMES {
                assert_eq!(frame, &[0.0, 0.0]);
            }
            heard |= frame[0].abs() > 0.1;
            max_delta = max_delta.max((frame[0] - previous).abs());
            previous = frame[0];
        }
    }
    assert!(heard);
    assert!(
        max_delta < 0.15,
        "limiter introduced excessive sample discontinuity {max_delta}"
    );
    assert_eq!(mixer.clipped_samples(), 0);
}
#[test]
fn processing_options_validate_before_changes_and_legacy_mode_is_available() {
    let mut mixer = Mixer::default();
    let original = mixer.processing();
    for invalid in [
        ProcessingSettings {
            peak_ceiling: f32::NAN,
            ..original
        },
        ProcessingSettings {
            max_gain_db: 25.0,
            ..original
        },
        ProcessingSettings {
            gate_dbfs: -30.0,
            target_dbfs: -36.0,
            ..original
        },
    ] {
        assert!(mixer.set_processing(invalid).is_err());
        assert_eq!(mixer.processing(), original);
    }
    mixer
        .set_processing(ProcessingSettings::recommended())
        .unwrap();
    assert!(mixer.processing().limiter_enabled && mixer.processing().clock_correction);
    mixer.set_processing(original).unwrap();
    assert!(!mixer.processing().limiter_enabled);
}
#[test]
fn fractional_clock_correction_handles_positive_and_negative_1500ppm_streams() {
    for skew in [-0.0015, 0.0015] {
        let mut mixer = Mixer::default();
        mixer
            .set_processing(ProcessingSettings {
                clock_correction: true,
                ..Default::default()
            })
            .unwrap();
        let id = source(&mut mixer, false);
        mixer.push(id, &vec![0u8; 3840 * 8]).unwrap();
        let mut fraction = 0.0f64;
        for _ in 0..9000 {
            fraction += 480.0 * (1.0 + skew);
            let frames = fraction.floor() as usize;
            fraction -= frames as f64;
            mixer.push(id, &vec![0; frames * 8]).unwrap();
            let mut output = [0.0; 960];
            mixer.render(&mut output).unwrap();
        }
        let state = mixer.source_state(id).unwrap();
        assert_eq!(state.underruns, 0, "clock skew {skew}");
        assert_eq!(state.latency_trims, 0, "clock skew {skew}");
        assert!(
            (10.0..=110.0).contains(&state.buffered_ms),
            "clock skew {skew}: {} ms",
            state.buffered_ms
        );
    }
}
