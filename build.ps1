<#
    Builds ScamWYF.Launcher.exe.

    Roslyn directly, like the other repos in this project: no NuGet, no .csproj, no restore step. The
    reasons are the same ones that shaped them. It compiles against real reference assemblies rather
    than whatever the developer happens to have, it needs no network, and the whole build is one
    command someone can read.

    This is a plain .NET Framework WinForms app, so unlike the mods it compiles against the reference
    assemblies under "Reference Assemblies" and runs on any Windows with .NET Framework 4.x - no game
    install, no BepInEx, no Mono. That matters for a tool whose job is to fix a broken game install:
    it must not share a dependency with the thing it repairs.

        .\build.ps1
        .\build.ps1 -CscDll C:\path\to\csc.dll
        .\build.ps1 -Cecil "C:\...\BepInEx\core\Mono.Cecil.dll"

    Mono.Cecil is the one external dependency, and it is the same copy the game already loads, so
    there is nothing extra to distribute: it is found in BepInEx\core and copied next to the exe.
    Point -Cecil at it if your game lives somewhere unusual.
#>
[CmdletBinding()]
param(
    [string]$CscDll,
    [string]$Cecil,
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot 'bin'
$exe = Join-Path $outDir 'ScamWYF.Launcher.exe'

# ---------------------------------------------------------------- Roslyn

function Resolve-Csc {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return $Explicit }
        throw "No compiler at $Explicit"
    }

    # Inside the .NET SDK: the copy everyone has, and the one that runs identically everywhere.
    $sdkRoots = @("${env:ProgramFiles}\dotnet\sdk", "$env:USERPROFILE\.dotnet\sdk", '/usr/share/dotnet/sdk', '/usr/lib/dotnet/sdk')
    foreach ($root in $sdkRoots) {
        if (-not $root -or -not (Test-Path $root)) { continue }
        $dll = Get-ChildItem -Path (Join-Path $root '*\Roslyn\bincore\csc.dll') -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($dll) { return $dll.FullName }
    }

    throw @"
Could not find a C# compiler.

Install the .NET SDK, or pass -CscDll pointing at one:
  dotnet /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll
"@
}

$csc = Resolve-Csc $CscDll

# ---------------------------------------------------------------- Mono.Cecil

# The launcher reads plugin metadata with Cecil. Taking it from the game's own BepInEx\core keeps the
# tool dependency-free: it is already installed by the thing the tool installs, and the same version
# the mods were compiled against.
function Resolve-Cecil {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return $Explicit }
        throw "No Mono.Cecil at $Explicit"
    }

    $game = $env:SWYG_GAME_DIR
    $candidates = @()
    if ($game) { $candidates += (Join-Path $game 'BepInEx\core\Mono.Cecil.dll') }

    foreach ($root in @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common",
        "${env:ProgramFiles}\Steam\steamapps\common")) {
        foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
            $candidates += (Join-Path $root "$name\BepInEx\core\Mono.Cecil.dll")
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    throw @"
Could not find Mono.Cecil.dll.

It is in the game's BepInEx\core. Either install the game and run the setup script, or point at a
copy:
  .\build.ps1 -Cecil C:\path\to\Mono.Cecil.dll
"@
}

$cecil = Resolve-Cecil $Cecil

# ---------------------------------------------------------------- references

# The .NET Framework reference assemblies, so the compiler sees exactly what any Windows with
# .NET Framework 4.x will see. Compiling against the GAC instead would work on this machine and then
# fail on a player's, which is the failure mode worth spending two lines to avoid.
$refRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path $refRoot)) {
    $found = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework') `
        -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($found) { $refRoot = $found.FullName }
}

if (-not (Test-Path $refRoot)) {
    throw "No .NET Framework reference assemblies found. On Windows they ship with Visual Studio or the .NET SDK."
}

$references = @(
    (Join-Path $refRoot 'mscorlib.dll')
    (Join-Path $refRoot 'System.dll')
    (Join-Path $refRoot 'System.Core.dll')
    (Join-Path $refRoot 'System.Drawing.dll')
    (Join-Path $refRoot 'System.Windows.Forms.dll')
    $cecil
)

foreach ($r in $references) {
    if (-not (Test-Path $r)) { throw "Missing reference: $r" }
}

# ---------------------------------------------------------------- compile

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
if (-not $sources) { throw "No sources in src\" }

$args = @(
    '-target:winexe'
    '-platform:anycpu'
    '-nostdlib+'
    '-langversion:7.3'
    '-optimize+'
    '-warnaserror-'
    '-warn:4'
    '-define:RELEASE'
    "-out:$exe"
    ($references | ForEach-Object { "-reference:$_" })
    '-utf8output'
) + $sources

Write-Host "=== building ScamWYF.Launcher"
Write-Host "  compiler:  $csc"
Write-Host "  cecil:     $cecil"
Write-Host "  framework: $refRoot"

if ($csc -like '*.dll') {
    & dotnet $csc @args
} else {
    & $csc @args
}

if ($LASTEXITCODE -ne 0) { throw "ScamWYF.Launcher failed to compile (exit $LASTEXITCODE)" }
Write-Host "  Built $exe" -ForegroundColor Green

# Cecil next to the exe, so the tool is two files and finds its dependency without a search.
Copy-Item $cecil (Join-Path $outDir 'Mono.Cecil.dll') -Force

if (Test-Path (Join-Path $outDir 'ScamWYF.Launcher.pdb')) {
    Write-Host "  (pdb written)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Run it:  $exe" -ForegroundColor Green
Write-Host "It finds the game itself; pass a path as the first argument if it cannot." -ForegroundColor DarkGray
