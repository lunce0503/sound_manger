use sound_core::{AudioFormat, Encoding, MixSettings, Mixer, SourceSettings};
use std::{
    fs,
    io::{Cursor, Read},
    path::PathBuf,
};

struct Fixture(Cursor<Vec<u8>>);

impl Fixture {
    fn bytes<const N: usize>(&mut self) -> [u8; N] {
        let mut value = [0; N];
        self.0.read_exact(&mut value).unwrap();
        value
    }
    fn byte(&mut self) -> u8 {
        self.bytes::<1>()[0]
    }
    fn u32(&mut self) -> u32 {
        u32::from_le_bytes(self.bytes())
    }
    fn u64(&mut self) -> u64 {
        u64::from_le_bytes(self.bytes())
    }
    fn f32(&mut self) -> f32 {
        f32::from_le_bytes(self.bytes())
    }
    fn f64(&mut self) -> f64 {
        f64::from_le_bytes(self.bytes())
    }
}

#[test]
fn matches_current_csharp_golden_outputs_and_buffer_state() {
    let directory =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/compatibility/fixtures");
    let mut paths: Vec<_> = fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "smg"))
        .collect();
    paths.sort();
    assert_eq!(paths.len(), 40, "missing C# golden fixtures");
    for path in paths {
        let label = path.file_name().unwrap().to_str().unwrap();
        let mut file = Fixture(Cursor::new(fs::read(&path).unwrap()));
        assert_eq!(&file.bytes::<4>(), b"SMG1", "{label}");
        let mut mixer = Mixer::default();
        let source_count = file.u32();
        let mut ids = Vec::new();
        for _ in 0..source_count {
            let format = AudioFormat {
                sample_rate: file.u32(),
                channels: file.u32() as u16,
                encoding: match file.u32() {
                    0 => Encoding::Pcm16,
                    1 => Encoding::Float32,
                    _ => panic!("invalid encoding"),
                },
            };
            ids.push(
                mixer
                    .add_source("fixture", format, SourceSettings::default())
                    .unwrap(),
            );
        }
        let events = file.u32();
        for event in 0..events {
            match file.byte() {
                0 => {
                    let id = ids[file.u32() as usize];
                    let mut pcm = vec![0; file.u32() as usize];
                    file.0.read_exact(&mut pcm).unwrap();
                    mixer.push(id, &pcm).unwrap();
                }
                1 => {
                    let mut output = vec![0.0; file.u32() as usize];
                    mixer.render(&mut output).unwrap();
                    for (sample, actual) in output.iter().enumerate() {
                        let expected = file.f32();
                        assert!(
                            actual.is_finite() && (actual - expected).abs() <= 2e-5,
                            "{label}, event {event}, sample {sample}: Rust={actual}, C#={expected}"
                        );
                    }
                    assert_eq!(
                        mixer.clipped_samples(),
                        file.u64(),
                        "{label} clipped samples"
                    );
                    for state in mixer.snapshot() {
                        let buffered = file.f64();
                        assert!(
                            (state.buffered_ms - buffered).abs() <= 1e-9,
                            "{label} buffer state"
                        );
                        assert_eq!(state.dropped_frames, file.u64(), "{label} dropped frames");
                        assert_eq!(state.underruns, file.u64(), "{label} underruns");
                        assert_eq!(
                            state.received_packets,
                            file.u64(),
                            "{label} received packets"
                        );
                        assert_eq!(state.latency_trims, file.u64(), "{label} latency trims");
                        assert_eq!(
                            state.latency_trimmed_frames,
                            file.u64(),
                            "{label} latency trimmed frames"
                        );
                    }
                }
                2 => {
                    let id = ids[file.u32() as usize];
                    let settings = SourceSettings {
                        volume: file.f32(),
                        azimuth: file.f32(),
                        spatial: file.byte() != 0,
                        muted: file.byte() != 0,
                        ..SourceSettings::default()
                    };
                    mixer.configure_source(id, settings).unwrap();
                }
                3 => {
                    let settings = MixSettings {
                        master_volume: file.f32(),
                        bass_db: file.f32(),
                        treble_db: file.f32(),
                        presence_db: file.f32(),
                    };
                    mixer.set_settings(settings).unwrap();
                }
                opcode => panic!("{label}: unknown opcode {opcode}"),
            }
        }
        assert_eq!(
            file.0.position(),
            file.0.get_ref().len() as u64,
            "{label}: trailing data"
        );
    }
}
