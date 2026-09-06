param(
    [string]$CargoPath = 'cargo',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'build-logs/spake2-qualification' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$previousTarget = $env:CARGO_TARGET_DIR
try {
    $env:CARGO_TARGET_DIR = Join-Path $OutputDirectory 'native'
    & $CargoPath build --locked --release --manifest-path (Join-Path $PSScriptRoot 'native/Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Qualification native build failed.' }
    $nativeDll = Join-Path $env:CARGO_TARGET_DIR 'release/astra_spake2_qualification.dll'
    & dotnet run --project (Join-Path $PSScriptRoot 'managed/Qualification.csproj') -c Release -- $nativeDll
    if ($LASTEXITCODE -ne 0) { throw 'Qualification fixture failed.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $nativeDll
} finally { $env:CARGO_TARGET_DIR = $previousTarget }
