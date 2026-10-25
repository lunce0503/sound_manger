param([string]$Version = '0.3.3')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$payloadRoot = Join-Path $repoRoot 'artifacts/launcher'
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
$payload = Join-Path $payloadRoot 'Payload.zip'
Compress-Archive -Path (Join-Path $repoRoot 'artifacts/windows/*') -DestinationPath $payload -Force
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$launcherOutput = Join-Path $payloadRoot 'published'
dotnet publish (Join-Path $repoRoot 'src/SoundManager.Launcher') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true "-p:PayloadPath=$payload" "-p:PayloadSha256=$payloadHash" -o $launcherOutput
if ($LASTEXITCODE -ne 0) { throw 'Windows EXE packaging failed' }
$releaseRoot = Join-Path $repoRoot "artifacts/release/v$Version"
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherOutput 'SoundManager.exe') -Destination (Join-Path $releaseRoot "SoundManager-$Version-windows-x64.exe")
Write-Output "Windows single EXE created: $releaseRoot"
