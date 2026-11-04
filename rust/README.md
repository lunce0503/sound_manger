# Sound Manager Rust migration

This workspace implements the incremental Rust migration. The working Windows UI
now launches a Rust sidecar; the public v0.4.1 release still has the C# server.

Crates: **sound-core**, **sound-protocol**, **sound-server**, **sound-output**
(Windows WASAPI) and **sound-app** (`sound-manager-server.exe`). v1 wire contracts,
pairing keys, persistent device settings and client identities are preserved.
The runtime does not load the C# mixer or ASP.NET server. The old implementation
remains available for comparison tests and rollback. The pinned toolchain is Rust 1.99.1.

```powershell
# From the repository root, with .NET 10 and Rust stable installed:
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

The C# clock-correction expression currently uses integer division, so fractional
drift correction is effectively inactive. This implementation preserves that
behavior for the initial numerical comparison. Correcting it requires a separate
Phase 4 change, clock-skew tests and reviewed baseline changes.

Internal source handles are not protocol UUIDs or persistent device IDs.
The sidecar accepts startup configuration and control over its private stdin pipe,
including the connection key (never process arguments). stdout carries bounded JSON
responses; the parent closing the pipe stops the server. Normal Windows startup,
device selection, source controls and close-to-tray continue through WPF.
AGC and look-ahead limiting remain to be implemented. See [migration status](../docs/rust-migration.md)
and [golden fixture specification](tests/compatibility/README.md).
