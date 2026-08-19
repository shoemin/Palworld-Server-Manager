$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $root "build-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir ("build-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

function Invoke-DotNetStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host ("dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Push-Location $root
$transcriptStarted = $false
try {
    Start-Transcript -Path $logFile -Force | Out-Null
    $transcriptStarted = $true
    Write-Host "Palworld Server Manager build/test transcript"
    Write-Host "Started: $(Get-Date -Format o)"
    Write-Host "Repository: $root"

    Invoke-DotNetStep -Name ".NET environment" -Arguments @("--info")
    Invoke-DotNetStep -Name "Restore" -Arguments @("restore", ".\PalworldServerManager.sln")
    Invoke-DotNetStep -Name "Release build" -Arguments @("build", ".\PalworldServerManager.sln", "-c", "Release", "--no-restore")
    Invoke-DotNetStep -Name "Self-tests" -Arguments @("run", "--project", ".\tests\PalworldServerManager.SelfTest\PalworldServerManager.SelfTest.csproj", "-c", "Release", "--no-build")

    Write-Host ""
    Write-Host "Build and self-tests completed successfully." -ForegroundColor Green
    Write-Host "Build log: $logFile" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "BUILD/SELF-TEST FAILURE" -ForegroundColor Red
    Write-Host $_.Exception.ToString() -ForegroundColor Red
    Write-Host "Build log: $logFile" -ForegroundColor Yellow
    throw
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    Pop-Location
}
