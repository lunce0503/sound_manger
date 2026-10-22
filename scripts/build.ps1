param([switch]$SkipAndroid)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build SoundManager.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
    dotnet test tests/SoundManager.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet publish src/SoundManager.Windows -c Release -r win-x64 --self-contained true -o artifacts/windows
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    & "$PSScriptRoot\prepare-tunnel.ps1"
    if (!$SkipAndroid) {
        Push-Location android
        try {
            & .\gradlew.bat assembleDebug lintDebug --console=plain
            if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }
        } finally { Pop-Location }
        New-Item -ItemType Directory -Force artifacts/android | Out-Null
        Copy-Item -LiteralPath android/app/build/outputs/apk/debug/app-debug.apk -Destination artifacts/android/SoundManager-debug.apk
    }
} finally { Pop-Location }
