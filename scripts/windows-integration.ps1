#requires -Version 5.1
<#
.SYNOPSIS
    Runs the #41 privileged Windows integration harness.

.DESCRIPTION
    Creates REAL machine-global resources (a Windows service, a local group, temporary local
    users), all uniquely named per run and removed in a finally block. Deliberately separate from
    ./scripts/build.ps1, which must stay safe for ordinary developer execution.

    Requires an elevated process. It fails rather than skipping silently when not elevated.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The Windows integration harness requires an elevated (Administrator) process. It is never silently skipped.'
}

Write-Host '=== Building solution (Release) ===' -ForegroundColor Cyan
dotnet build (Join-Path $root 'PalworldServerManager.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

Write-Host '=== Running privileged Windows integration harness ===' -ForegroundColor Cyan
dotnet run --project (Join-Path $root 'tests\PalworldServerManager.SelfTest\PalworldServerManager.SelfTest.csproj') `
    -c Release --no-build -- --windows-integration
if ($LASTEXITCODE -ne 0) { throw "Windows integration harness failed with exit code $LASTEXITCODE." }

Write-Host 'Windows integration harness completed successfully.' -ForegroundColor Green
