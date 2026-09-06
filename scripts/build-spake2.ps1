param([string]$CargoPath = 'cargo', [string]$OutputDirectory, [switch]$EntropyFailureFixture)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'build-logs/spake2-native' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$priorTarget = $env:CARGO_TARGET_DIR; $priorFlags = $env:RUSTFLAGS
try {
    $env:CARGO_TARGET_DIR = Join-Path $OutputDirectory 'target'
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    $arguments = @('build', '--locked', '--release', '--target', 'x86_64-pc-windows-msvc', '--manifest-path', (Join-Path $root 'native/PalworldServerManager.Spake2/Cargo.toml'))
    if ($EntropyFailureFixture) { $arguments += @('--features', 'qualification-entropy-failure') }
    & $CargoPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Native pairing build failed.' }
    $dll = Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/palworld_spake2.dll'
    [ordered]@{
        abi = $(if ($EntropyFailureFixture) { 'qualification-entropy-failure' } else { '1' })
        source = '4fa353417ddddfcaaf29f990404e1f48127167e3'
        target = 'x86_64-pc-windows-msvc'
        sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'component.json')
    Copy-Item -LiteralPath (Join-Path $root 'native/PalworldServerManager.Spake2/THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt')
    Write-Output $dll
} finally { $env:CARGO_TARGET_DIR = $priorTarget; $env:RUSTFLAGS = $priorFlags }
