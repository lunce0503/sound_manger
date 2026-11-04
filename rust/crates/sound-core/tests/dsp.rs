use sound_core::{
    AudioError, AudioFormat, Encoding, MAX_SOURCES, MixSettings, Mixer, SourceSettings,
};

fn format(rate: u32, channels: u16) -> AudioFormat {
    AudioFormat {
        sample_rate: rate,
        channels,
        encoding: Encoding::Pcm16,
    }
}

fn pcm(rate: u32, ms: usize, left: i16, right: i16) -> Vec<u8> {
    let mut data = Vec::new();
    for _ in 0..rate as usize * ms / 1000 {
        data.extend(left.to_le_bytes());
        data.extend(right.to_le_bytes());
    }
    data
}

#[test]
fn invalid_inputs_leave_state_unchanged() {
    let mut mixer = Mixer::default();
    for rate in [0, 7999, 192001, u32::MAX] {
        assert_eq!(
            mixer.add_source("invalid", format(rate, 2), SourceSettings::default()),
            Err(AudioError::InvalidFormat)
        );
    }
    for channels in [0, 3, u16::MAX] {
        assert_eq!(
            mixer.add_source(
                "invalid",
                format(48000, channels),
                SourceSettings::default()
            ),
            Err(AudioError::InvalidFormat)
        );
    }
    for volume in [f32::NAN, f32::INFINITY, -0.1, 1.1] {
        assert_eq!(
            mixer.set_settings(MixSettings {
                master_volume: volume,
                ..MixSettings::default()
            }),
            Err(AudioError::InvalidSettings)
        );
    }
    assert_eq!(mixer.settings(), MixSettings::default());
    let id = mixer
        .add_source("valid", format(48000, 2), SourceSettings::default())
        .unwrap();
    assert_eq!(mixer.push(id, &[0; 3]), Err(AudioError::PartialFrame));
    assert_eq!(mixer.snapshot()[0].received_packets, 0);
    let mut partial = [0.25; 3];
    assert_eq!(mixer.render(&mut partial), Err(AudioError::PartialFrame));
    assert_eq!(partial, [0.25; 3]);
    assert_eq!(
        mixer.configure_source(
            id,
            SourceSettings {
                azimuth: 360.0,
                ..SourceSettings::default()
            }
        ),
        Err(AudioError::InvalidSettings)
    );
}

#[test]
fn names_follow_utf16_length_and_display_name_rules() {
    let mut mixer = Mixer::default();
    for name in [
        "".to_owned(),
        " \t".to_owned(),
        "x".repeat(65),
        "😀".repeat(33),
    ] {
        assert_eq!(
            mixer.add_source(&name, format(48000, 2), SourceSettings::default()),
            Err(AudioError::InvalidName)
        );
    }
    let id = mixer
        .add_source(
            &"😀".repeat(32),
            format(48000, 2),
            SourceSettings::default(),
        )
        .unwrap();
    assert_eq!(
        mixer.configure_source(
            id,
            SourceSettings {
                display_name: Some("line\nbreak".into()),
                ..SourceSettings::default()
            }
        ),
        Err(AudioError::InvalidSettings)
    );
    mixer
        .configure_source(
            id,
            SourceSettings {
                display_name: Some("휴대전화".into()),
                ..SourceSettings::default()
            },
        )
        .unwrap();
    assert_eq!(mixer.snapshot()[0].name, "휴대전화");
    mixer
        .configure_source(id, SourceSettings::default())
        .unwrap();
    assert_eq!(mixer.snapshot()[0].name, "😀".repeat(32));
}

#[test]
fn disconnected_handles_never_address_replacement_source() {
    let mut mixer = Mixer::default();
    let mut ids = Vec::new();
    for _ in 0..MAX_SOURCES {
        ids.push(
            mixer
                .add_source("source", format(48000, 2), SourceSettings::default())
                .unwrap(),
        );
    }
    assert_eq!(
        mixer.add_source("overflow", format(48000, 2), SourceSettings::default()),
        Err(AudioError::SourceLimit)
    );
    assert!(mixer.remove_source(ids[0]));
    assert!(!mixer.remove_source(ids[0]));
    let replacement = mixer
        .add_source("replacement", format(48000, 2), SourceSettings::default())
        .unwrap();
    assert_ne!(replacement, ids[0]);
    assert_eq!(mixer.push(ids[0], &[]), Err(AudioError::SourceNotFound));
    assert_eq!(mixer.snapshot().len(), MAX_SOURCES);
}

#[test]
fn stereo_and_mute_consume_audio_in_real_time() {
    let mut mixer = Mixer::default();
    mixer
        .set_settings(MixSettings::preset("flat", 1.0).unwrap())
        .unwrap();
    let id = mixer
        .add_source("stereo", format(48000, 2), SourceSettings::default())
        .unwrap();
    mixer.push(id, &pcm(48000, 100, 8192, -16384)).unwrap();
    let mut output = [0.0; 960];
    mixer.render(&mut output).unwrap();
    assert!((output[100] - 0.25).abs() < 1e-6);
    assert!((output[101] + 0.5).abs() < 1e-6);
    let buffered = mixer.snapshot()[0].buffered_ms;
    mixer
        .configure_source(
            id,
            SourceSettings {
                muted: true,
                ..SourceSettings::default()
            },
        )
        .unwrap();
    mixer.render(&mut output).unwrap();
    assert!(output.iter().all(|v| *v == 0.0));
    assert!(mixer.snapshot()[0].buffered_ms < buffered - 9.0);
}

#[test]
fn startup_and_underrun_wait_for_buffer_without_blocking_other_sources() {
    let mut mixer = Mixer::default();
    let hungry = mixer
        .add_source("hungry", format(48000, 2), SourceSettings::default())
        .unwrap();
    let healthy = mixer
        .add_source("healthy", format(44100, 2), SourceSettings::default())
        .unwrap();
    mixer.push(hungry, &pcm(48000, 39, 8192, 8192)).unwrap();
    mixer.push(healthy, &pcm(44100, 100, 8192, 8192)).unwrap();
    let mut output = [0.0; 960];
    mixer.render(&mut output).unwrap();
    assert!(output.iter().all(|v| (*v - 0.175).abs() < 1e-6));
    assert_eq!(mixer.snapshot()[0].buffered_ms, 39.0);
    mixer.push(hungry, &pcm(48000, 11, 8192, 8192)).unwrap();
    mixer.render(&mut [0.0; 9600]).unwrap();
    assert_eq!(mixer.snapshot()[0].underruns, 1);
    mixer.push(hungry, &pcm(48000, 10, 8192, 8192)).unwrap();
    mixer.render(&mut output).unwrap();
    assert!(output.iter().all(|v| *v == 0.0));
}

#[test]
fn latency_trim_keeps_complete_request_and_target_reserve_at_all_rates() {
    for rate in [8000, 44100, 48000, 192000] {
        for request_ms in [10, 60, 200] {
            let mut mixer = Mixer::default();
            let id = mixer
                .add_source("backlog", format(rate, 2), SourceSettings::default())
                .unwrap();
            mixer.push(id, &pcm(rate, 400, 16000, 16000)).unwrap();
            let mut output = vec![0.0; 48000 * request_ms / 1000 * 2];
            mixer.render(&mut output).unwrap();
            let state = &mixer.snapshot()[0];
            assert!(
                (59.0..=61.0).contains(&state.buffered_ms),
                "rate={rate}, request={request_ms}, buffer={}",
                state.buffered_ms
            );
            assert_eq!(state.underruns, 0);
            assert_eq!(state.latency_trims, 1);
            assert!(output.iter().all(|v| *v > 0.3));
        }
    }
}

#[test]
fn ten_sources_stream_without_recurring_underruns_or_latency_trims() {
    let mut mixer = Mixer::default();
    let mut sources = Vec::new();
    for i in 0..10 {
        let rate = if i % 2 == 0 { 44100 } else { 48000 };
        let id = mixer
            .add_source("stream", format(rate, 2), SourceSettings::default())
            .unwrap();
        mixer.push(id, &pcm(rate, 60, 800, -800)).unwrap();
        sources.push((id, pcm(rate, 20, 800, -800)));
    }
    let mut output = [0.0; 1920];
    for _ in 0..500 {
        for (id, packet) in &sources {
            mixer.push(*id, packet).unwrap();
        }
        mixer.render(&mut output).unwrap();
        assert!(output.iter().all(|v| v.is_finite() && v.abs() < 1.0));
    }
    for state in mixer.snapshot() {
        assert_eq!(state.dropped_frames, 0);
        assert_eq!(state.underruns, 0);
        assert_eq!(state.latency_trims, 0);
        assert!((40.0..=65.0).contains(&state.buffered_ms));
    }
}
