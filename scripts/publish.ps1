param(
    [string]$Output = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "publish"
}

$publishArgs = @(
    "publish",
    ".\src\PalworldServerManager.App\PalworldServerManager.App.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-o", $Output
)

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Version '$Version' is not supported. Expected a SemVer-like value such as 0.2.5 or 0.2.5-beta.1."
    }

    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:InformationalVersion=$Version"
}

Push-Location $root
try {
    Write-Host "dotnet $($publishArgs -join ' ')"
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE."
    }

    Write-Host "Published to $Output" -ForegroundColor Green
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        Write-Host "Published version $Version" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
