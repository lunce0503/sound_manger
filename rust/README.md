# Sound Manager Rust migration

This workspace implements the Rust server used by Sound Manager 1.0.0.
The Windows WPF UI launches and supervises the native sidecar. The previous
v0.4.1 release remains available as a C# rollback baseline.

Crates: **sound-core**, **sound-protocol**, **sound-server**, **sound-output**
(Windows WASAPI) and **sound-app** (`sound-manager-server.exe`). v1 wire contracts,
pairing keys, persistent device settings and client identities are preserved.
The runtime does not run the C# mixer or ASP.NET server. The old implementation
remains available for comparison tests and rollback. The pinned toolchain is Rust 1.99.1.

```powershell
# From the repository root, with .NET 10 and Rust 1.99.1 installed:
./scripts/test-rust.ps1

# Rust-only checks, using checked-in C# output fixtures:
cd rust
cargo fmt --all
cargo clippy --workspace --all-targets --locked --offline -- -D warnings
cargo test --workspace --locked --offline
```

`test-rust.ps1` formats Rust files, verifies that the C# fixture baseline has not
changed, runs Clippy and runs tests. Review formatting changes before committing.

`Mixer` uses exclusive ownership. It is not a thread-safe replacement for the
C# API: concurrent ingestion/control must use an adapter that transfers bounded
packets/commands to the render owner. Allocating sources, dropping sources and
taking snapshots belong outside the output callback. `render` performs no heap
allocation, blocking locks or I/O, including when EQ settings change.

Input frames stay in bounded per-source buffers at their input rate, matching C#;
resampling produces 48 kHz float stereo for mixing. Each source holds about
500 ms of input. Startup is 40 ms, the target reserve is 60 ms and the base trim
threshold is 120 ms, extended to retain large output requests.

The C# clock-correction expression uses integer division, so fractional correction
is effectively inactive. The core's default compatibility mode preserves this for
golden comparisons. The running server enables tested fractional clock correction
and a stereo-linked 5 ms look-ahead limiter. Both can be independently disabled via
`/v1/processing`. Per-source `autoGain` enables bounded RMS normalization before the
user's volume ratio; it defaults to false. Processing and device options persist.

Internal source handles are not protocol UUIDs or persistent device IDs.
The sidecar accepts startup configuration and control over its private stdin pipe,
including the connection key (never process arguments). stdout carries bounded JSON
responses; the parent closing the pipe stops the server. Normal Windows startup,
device selection, source controls and close-to-tray continue through WPF.
See [migration status](../docs/rust-migration.md), [release validation](../docs/validation-v1.0.0.md)
and [golden fixture specification](tests/compatibility/README.md). The final native
binary passed a 10-source 30-minute synthetic run with zero underruns or discarded
frames. Android physical capture/listening validation is deferred by user instruction.
