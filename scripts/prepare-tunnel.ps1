param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/windows'))
$ErrorActionPreference = 'Stop'
$version = '2026.10.3'
$expectedHash = '76b2805253d0d6ffacb092a7acfbee99e7af6917835c0c5410e23f3ef5052da2'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$executable = Join-Path $OutputDirectory 'cloudflared.exe'
if (!(Test-Path -LiteralPath $executable) -or (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $expectedHash) {
    $download = Join-Path $OutputDirectory 'cloudflared.download'
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/download/$version/cloudflared-windows-amd64.exe" -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) { throw 'cloudflared SHA256 mismatch' }
    Move-Item -LiteralPath $download -Destination $executable -Force
}
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/cloudflare/cloudflared/$version/LICENSE" -OutFile (Join-Path $OutputDirectory 'cloudflared-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'LICENSE') -Destination (Join-Path $OutputDirectory 'SoundManager-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'licenses') -Destination $OutputDirectory -Recurse -Force
Write-Output "cloudflared $version verified and bundled."
