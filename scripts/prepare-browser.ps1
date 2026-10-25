param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/windows/browser-extension'))
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'browser-extension'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$files = @('manifest.json','protocol.js','auth-store.js','pcm-batcher.js','pcm-worklet.js','offscreen.html','offscreen.js','service-worker.js','popup.html','popup.js','popup.css')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $OutputDirectory $name) -Force }
Write-Output 'Chrome/Edge extension bundled.'
