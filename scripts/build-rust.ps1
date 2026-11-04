param([switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rust-env.ps1"
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$SkipChecks) { & "$PSScriptRoot/test-rust.ps1" }
Push-Location (Join-Path $repoRoot 'rust')
try {
    cargo build --release --workspace --locked
    if ($LASTEXITCODE -ne 0) { throw 'Rust server release build failed' }
} finally { Pop-Location }
