param([string]$PairingProviderPath)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $integrationArgs = @('--windows-integration')
    if ($PairingProviderPath) {
        $providerFullPath = [IO.Path]::GetFullPath($PairingProviderPath)
        $integrationArgs += @($providerFullPath, (Get-FileHash -LiteralPath $providerFullPath -Algorithm SHA256).Hash)
    }
    dotnet run --project tests/PalworldServerManager.SelfTest -c Release --no-build -- @integrationArgs
    if ($LASTEXITCODE -ne 0) { throw "Windows integration failed with exit code $LASTEXITCODE. No privileged criterion may be treated as passed." }
}
finally { Pop-Location }
