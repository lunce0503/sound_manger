param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/rust-env.ps1"
Get-Command dotnet,cargo -ErrorAction Stop | Out-Null
Push-Location $root
try {
    dotnet build tools/SoundManager.Golden -c Release
    if ($LASTEXITCODE -ne 0) { throw 'C# golden generator build failed' }
    dotnet tools/SoundManager.Golden/bin/Release/net10.0/SoundManager.Golden.dll --verify rust/tests/compatibility/fixtures
    if ($LASTEXITCODE -ne 0) { throw 'C# golden baseline verification failed; review changes before regeneration' }
    Set-Location (Join-Path $root 'rust')
    cargo fetch --locked
    if ($LASTEXITCODE -ne 0) { throw 'cargo dependency fetch failed' }
    cargo fmt --all
    if ($LASTEXITCODE -ne 0) { throw 'cargo fmt failed' }
    cargo clippy --workspace --all-targets --locked --offline -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'cargo clippy failed' }
    cargo test --workspace --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'cargo test failed' }
}
finally { Pop-Location }
