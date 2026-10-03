<#
    Runs the launcher's tests.

    A thin wrapper around `dotnet run`, in the same way build.ps1 is a thin wrapper around `dotnet build`:

        dotnet run --project tests\Launcher.Tests.csproj -c Release

    is an equivalent command. What this adds is the -Online switch and a readable summary.

        .\tools\test-getmods.ps1
        .\tools\test-getmods.ps1 -Online

    The two suites are separate programs rather than one with a filter, and both are compiled from the
    app's own sources. That is deliberate: these tests are about what happens to files on disk - zip-slip
    refused, a checksum mismatch caught, an enable/disable round trip - and the cheapest way to be sure
    they are testing the shipped code rather than a copy of it is to compile that code.

    -Online needs the network and spends GitHub's unauthenticated rate limit, so it is never the default.
#>
[CmdletBinding()]
param(
    [switch]$Online,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests\Launcher.Tests.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH. The tests are run with the .NET SDK: https://dotnet.microsoft.com/download"
}

$properties = @("-p:Online=$($Online.IsPresent.ToString().ToLowerInvariant())")
$output = Join-Path $repoRoot 'tests\bin'

Write-Host "=== $(if ($Online) { 'online' } else { 'offline' }) tests"
Write-Host "  project: $project"
Write-Host ""

& dotnet run --project $project --configuration $Configuration --nologo `
    -p:OutputPath="$output" `
    -p:AppendTargetFrameworkToOutputPath=false `
    @properties

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "All tests passed." -ForegroundColor Green