<#
    Compiles and runs the Get mods tests.

        .\tools\test-getmods.ps1              # offline: no network, no game install
        .\tools\test-getmods.ps1 -Online      # also exercises the real GitHub listing and download

    The offline set covers what is worth testing without a network: the URL rules, the archive traversal
    defences, and the decisions InstallPlan makes about a file. -Online additionally checks the live
    listing, the download, the checksum comparison, and a plan built from the real published zip.

    The split is deliberate. The offline set runs on every commit and in CI, where a network call would be
    both slow and a source of false failures. The online set is run by hand before a release, because it
    depends on GitHub being up and on somebody having published a release to read.

    Like build.ps1: raw csc, no .csproj, no restore. The tests compile against the same sources as the app,
    so a test cannot pass against a stale copy of the logic it is testing.
#>
[CmdletBinding()]
param(
    [string]$CscDll,
    [string]$Cecil,

    # Compile and run the tests that need the network. Costs GitHub rate limit and needs a release to exist.
    [switch]$Online
)

$ErrorActionPreference = 'Stop'

# build.ps1 is not dot-sourced: it compiles and installs on load. The compiler, Cecil and the reference
# assemblies are resolved here the same way it resolves them, and both files must agree - which is why
# the framework lookup below is the same code, not a copy with a different sort.
$repo = Split-Path -Parent $PSScriptRoot

function Resolve-Csc {
    param([string]$Explicit)
    if ($Explicit) { return $Explicit }

    foreach ($root in @("${env:ProgramFiles}\dotnet\sdk", "$env:USERPROFILE\.dotnet\sdk")) {
        if (-not $root -or -not (Test-Path $root)) { continue }
        $dll = Get-ChildItem -Path (Join-Path $root '*\Roslyn\bincore\csc.dll') -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($dll) { return $dll.FullName }
    }

    throw "No C# compiler found. Install the .NET SDK, or pass -CscDll."
}

function Resolve-Cecil {
    param([string]$Explicit)
    if ($Explicit) { return $Explicit }

    $staged = Join-Path $repo 'vendor\Mono.Cecil.dll'
    if (Test-Path $staged) { return $staged }

    throw "No Mono.Cecil at vendor\Mono.Cecil.dll. Pass -Cecil to point at one."
}

$frameworkBase = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
$refRoot = Join-Path $frameworkBase 'v4.8'
if (-not (Test-Path $refRoot)) {
    $found = Get-ChildItem $frameworkBase -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v(\d+)\.(\d+)' } |
        Sort-Object { [version]$_.Name.TrimStart('v') } -Descending |
        Select-Object -First 1
    if ($found) { $refRoot = $found.FullName }
}

if (-not (Test-Path $refRoot)) { throw "No .NET Framework reference assemblies under $frameworkBase" }

$csc = Resolve-Csc $CscDll
$cecil = Resolve-Cecil $Cecil

$outDir = Join-Path $repo 'bin\tests'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir 'GetModsTests.exe'

# The app's sources plus one test program, so there is no second copy of the logic to drift. Both test
# programs have a Main, and so does the app, which is why the entry point is named below.
$testFile = if ($Online) { 'OnlineModTests.cs' } else { 'GetModsTests.cs' }
$entryPoint = if ($Online) { 'ScamWYF.Launcher.Tests.OnlineModTests' } else { 'ScamWYF.Launcher.Tests.GetModsTests' }

$sources = @(
    Get-ChildItem (Join-Path $repo 'src') -Filter *.cs | ForEach-Object { $_.FullName }
    (Join-Path $repo "tests\$testFile")
)

$references = @(
    (Join-Path $refRoot 'mscorlib.dll')
    (Join-Path $refRoot 'System.dll')
    (Join-Path $refRoot 'System.Core.dll')
    (Join-Path $refRoot 'System.Drawing.dll')
    (Join-Path $refRoot 'System.Windows.Forms.dll')
    (Join-Path $refRoot 'System.Web.Extensions.dll')
    (Join-Path $refRoot 'System.IO.Compression.dll')
    (Join-Path $refRoot 'System.IO.Compression.FileSystem.dll')
    $cecil
)

foreach ($r in $references) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$args = @(
    '-target:exe'
    '-platform:anycpu'
    '-nostdlib+'
    '-langversion:7.3'
    '-optimize+'
    '-warn:4'
    # App.cs has its own Main, and so does the test program. Naming the entry point is what lets the tests
    # compile against the real sources instead of a copy of them.
    "-main:$entryPoint"
    "-out:$exe"
    ($references | ForEach-Object { "-reference:$_" })
    '-utf8output'
) + $sources

Write-Host "=== building GetModsTests"
Write-Host "  compiler:  $csc"
Write-Host "  framework: $refRoot"

if ($csc -like '*.dll') { & dotnet $csc @args } else { & $csc @args }
if ($LASTEXITCODE -ne 0) { throw "GetModsTests failed to compile (exit $LASTEXITCODE)" }

Copy-Item $cecil (Join-Path $outDir 'Mono.Cecil.dll') -Force

Write-Host ""
& $exe
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) { Write-Host "tests passed" -ForegroundColor Green } else { Write-Host "tests FAILED" -ForegroundColor Red }
exit $code