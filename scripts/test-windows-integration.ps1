$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet run --project tests/PalworldServerManager.SelfTest -c Release --no-build -- --windows-integration
    if ($LASTEXITCODE -ne 0) { throw "Windows integration failed with exit code $LASTEXITCODE. No privileged criterion may be treated as passed." }
}
finally { Pop-Location }
