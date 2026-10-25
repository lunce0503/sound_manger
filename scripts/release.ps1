param([string]$Version = '0.3.3')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected semantic version, for example 0.2.0' }
# Reuse this key for all future updates. Never put it or its password in Git or release assets.
$signingRoot = Join-Path $env:LOCALAPPDATA 'SoundManager\release-signing'
$keystore = Join-Path $signingRoot 'release.jks'
$passwordFile = Join-Path $signingRoot 'keystore-password.dpapi'
New-Item -ItemType Directory -Force -Path $signingRoot | Out-Null
if ((Test-Path -LiteralPath $keystore) -and !(Test-Path -LiteralPath $passwordFile)) { throw 'Signing password missing. Restore the original password; do not replace the key.' }
if (!(Test-Path -LiteralPath $passwordFile)) {
    $randomPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
    ConvertTo-SecureString $randomPassword -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile
    $randomPassword = $null
}
$securePassword = Get-Content -LiteralPath $passwordFile | ConvertTo-SecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
$previous = @{}
foreach ($name in @('SM_KEYSTORE_PATH', 'SM_KEYSTORE_PASSWORD', 'SM_KEY_ALIAS', 'SM_KEY_PASSWORD')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $repoRoot
try {
    $env:SM_KEYSTORE_PATH = $keystore
    $env:SM_KEYSTORE_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $env:SM_KEY_ALIAS = 'soundmanager'
    $env:SM_KEY_PASSWORD = $env:SM_KEYSTORE_PASSWORD
    if (!(Test-Path -LiteralPath $keystore)) {
        & "$env:JAVA_HOME\bin\keytool.exe" -genkeypair -keystore $keystore -storetype JKS -alias soundmanager -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Sound Manager, O=Lunce' -storepass:env SM_KEYSTORE_PASSWORD -keypass:env SM_KEY_PASSWORD
        if ($LASTEXITCODE -ne 0) { throw 'Signing key creation failed' }
    }
    & "$PSScriptRoot\build.ps1" -SkipAndroid
    Push-Location android
    try {
        & .\gradlew.bat assembleRelease lintRelease testReleaseUnitTest --console=plain
        if ($LASTEXITCODE -ne 0) { throw 'Android release build failed' }
    } finally { Pop-Location }
    $releaseRoot = Join-Path $repoRoot "artifacts\release\v$Version"
    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
    $apk = Join-Path $releaseRoot "SoundManager-$Version-android.apk"
    Copy-Item -LiteralPath android/app/build/outputs/apk/release/app-release.apk -Destination $apk
    & "$PSScriptRoot\package-windows.ps1" -Version $Version
    Copy-Item -LiteralPath LICENSE, README.md, THIRD-PARTY-NOTICES.md -Destination $releaseRoot
    $sums = Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    $sums | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Output "Release packages: $releaseRoot"
    Write-Output "Keep the signing key and its DPAPI password file safe: $signingRoot"
} finally {
    Pop-Location
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
