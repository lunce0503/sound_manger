# Dot-source before Cargo commands. Uses installed tools, or the local SDK fallback.
$cargoBin = Join-Path $env:USERPROFILE '.cargo/bin'
if (Test-Path -LiteralPath $cargoBin) { $env:Path = "$cargoBin;$env:Path" }
$env:Path = "$env:WINDIR\System32;$env:Path"
$localSdk = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/windows-sdk/microsoft.windows.sdk.cpp.x64/c'
if (Test-Path -LiteralPath (Join-Path $localSdk 'um/x64/kernel32.lib')) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vsPath) {
            $vc = Get-ChildItem -LiteralPath (Join-Path $vsPath 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
            $env:Path = "$(Join-Path $vc.FullName 'bin/Hostx64/x64');$env:Path"
            $env:LIB = "$(Join-Path $vc.FullName 'lib/x64');$(Join-Path $localSdk 'um/x64');$(Join-Path $localSdk 'ucrt/x64');$env:LIB"
        }
    }
}
