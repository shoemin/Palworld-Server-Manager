param([string]$OutputDirectory = 'build-logs/windows-lan-abi')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'Windows C++ SDK toolchain is required.' }
$setup = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$source = Join-Path $repoRoot 'tests/PalworldServerManager.SelfTest/Fixtures/windows-lan-abi.cpp'
$binary = Join-Path $outputRoot 'windows-lan-abi.exe'
$objectFile = Join-Path $outputRoot 'windows-lan-abi.obj'
# These paths enter cmd for the SDK environment/compiler only; reject batch metacharacters.
foreach ($itemPath in @($setup, $source, $binary, $objectFile)) {
    if ($itemPath.IndexOfAny([char[]]'"&|<>^%!') -ge 0 -or $itemPath.Contains("`r") -or $itemPath.Contains("`n")) { throw 'Unsupported build path.' }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$compileLine = 'call "{0}" -no_logo -arch=x64 -host_arch=x64 && cl.exe /nologo /W4 /WX /EHsc /std:c++17 "{1}" /Fe:"{2}" /Fo:"{3}"' -f $setup, $source, $binary, $objectFile
& $env:ComSpec /d /s /c $compileLine
if ($LASTEXITCODE -ne 0) { throw 'Windows LAN SDK ABI compilation failed.' }
& $binary
if ($LASTEXITCODE -ne 0) { throw 'Windows LAN SDK ABI qualification failed.' }
