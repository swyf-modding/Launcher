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
        .\build.ps1 -Version 1.2.3

    Mono.Cecil is the one external dependency, and it is the same copy the game already loads, so
    there is nothing extra to distribute: it is found in BepInEx\core and copied next to the exe.
    Point -Cecil at it if your game lives somewhere unusual.

    The version is taken from the nearest git tag, so a released build says which commit it came from
    without anyone having to remember to edit a number in a source file. -Version overrides that, which
    is what a build from a source archive with no .git needs.
#>
[CmdletBinding()]
param(
    [string]$CscDll,
    [string]$Cecil,
    [string]$Version,
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

    # A copy staged next to the sources, which is how a continuous build gets it without a game
    # install. Checked first so a build agent does not silently pick up a different version.
    $staged = Join-Path $PSScriptRoot 'vendor\Mono.Cecil.dll'
    if (Test-Path $staged) { return $staged }

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

Three ways to get it:

  - the game's BepInEx\core, which is where it comes from when you have the game installed
  - a copy at vendor\Mono.Cecil.dll, which is where a continuous build stages one
  - anywhere at all:
      .\build.ps1 -Cecil C:\path\to\Mono.Cecil.dll

Mono.Cecil is MIT licensed (github.com/jbevain/cecil), so any copy may be redistributed.
"@
}

$cecil = Resolve-Cecil $Cecil

# ---------------------------------------------------------------- version

# A released binary should be able to say where it came from. The version is read from the nearest git
# tag rather than typed into a source file, because a number in a source file drifts: the READMEs and
# the [BepInPlugin] attributes all showed 1.0.0 long after the dll changed, and the launcher reads
# those attributes back out to show the user, so a stale one is not merely untidy - it is visible.
#
# AssemblyVersion and AssemblyFileVersion must be numeric, because the CLR rejects "1.0.0-beta.1" and
# would fail the build over a prerelease tag. So a prerelease keeps its numeric part there and carries
# the rest in AssemblyInformationalVersion - which is the field Explorer and Programs and Features
# actually display, so this is what a player sees.
function ConvertFrom-Describe {
    param([string]$Describe, [string]$Sha)

    if ([string]::IsNullOrWhiteSpace($Describe)) { $Describe = '0.0.0' }

    $dirty = $Describe.EndsWith('-dirty')
    if ($dirty) { $Describe = $Describe.Substring(0, $Describe.Length - '-dirty'.Length) }

    # Strip "-<n>-g<sha>" from the right first, then split what is left on its first '-'. Doing it in
    # that order is the whole trick: "v1.1.0-beta.2-1-g9925471" has a dash inside the prerelease, so
    # splitting first would read the tag as "v1.1.0" and lose "beta.2".
    $commits = 0
    $distance = [regex]::Match($Describe, '-(?<n>\d+)-g(?<sha>[0-9a-fA-F]+)$')
    if ($distance.Success) {
        $commits = [int]$distance.Groups['n'].Value
        if (-not $Sha) { $Sha = $distance.Groups['sha'].Value }
        $Describe = $Describe.Substring(0, $distance.Index)
    }

    # Build metadata on the tag itself is dropped, because this function appends its own commit and
    # two "+" separators would make the result malformed. Git allows "+" in a tag name, so this is a
    # real if unusual input rather than a theoretical one.
    $plus = $Describe.IndexOf('+')
    if ($plus -ge 0) { $Describe = $Describe.Substring(0, $plus) }

    $tag = $Describe
    $prerelease = ''
    $dash = $tag.IndexOf('-')
    if ($dash -ge 0) {
        $prerelease = $tag.Substring($dash + 1)
        $tag = $tag.Substring(0, $dash)
    }
    if ($tag -match '^[vV]') { $tag = $tag.Substring(1) }

    # Anything not dot-separated integers is not a version we can stamp: an untagged tree, or a tag
    # like "nightly". Both fall back to 0.0.0 and are marked, rather than failing the build. Up to four
    # components are accepted because AssemblyVersion takes four.
    $numbers = @()
    if ($tag -match '^\d+(\.\d+){0,3}$') {
        foreach ($part in ($tag -split '\.')) { $numbers += [int]$part }
    }
    $untagged = $numbers.Count -eq 0
    if ($untagged) { $numbers = @(0, 0, 0) }
    while ($numbers.Count -lt 3) { $numbers += 0 }
    $numeric = $numbers -join '.'

    $informational = $numeric
    if ($prerelease) { $informational += "-$prerelease" }

    $meta = @()
    if ($untagged) { $meta += 'untagged' }
    if ($commits -gt 0) { $meta += "$commits" }
    if ($Sha) { $meta += "g$Sha" }
    if ($dirty) { $meta += 'dirty' }
    if ($meta.Count) { $informational += '+' + ($meta -join '.') }

    [pscustomobject]@{
        Numeric        = $numeric
        Informational = $informational
        Commit         = $Sha
        Dirty          = $dirty
        Untagged       = $untagged
    }
}

function Resolve-Version {
    param([string]$Explicit)

    if ($Explicit) {
        # An explicit -Version is taken at face value for the numeric part, so -Version 1.2.3-beta.1
        # stamps 1.2.3 into AssemblyVersion and keeps the prerelease where a person can see it.
        return ConvertFrom-Describe $Explicit ''
    }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warning "git was not found, so the version cannot be read from a tag. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    $root = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $root) {
        Write-Warning "Not a git checkout, so the version cannot be read from a tag. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    # --always so an untagged tree still describes rather than failing, and --dirty so a build from a
    # modified tree cannot be mistaken for a release.
    $describe = & git -C $PSScriptRoot describe --tags --dirty --always 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $describe) {
        Write-Warning "git describe failed, so the version could not be read. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    $sha = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
    return ConvertFrom-Describe $describe.Trim() "$($sha.Trim())"
}

$versionInfo = Resolve-Version $Version
if ($versionInfo.Dirty) {
    Write-Host "  note: the working tree has uncommitted changes, so this is not a clean release build." -ForegroundColor Yellow
}

# ---------------------------------------------------------------- references

# The .NET Framework reference assemblies, so the compiler sees exactly what any Windows with
# .NET Framework 4.x will see. Compiling against the GAC instead would work on this machine and then
# fail on a player's, which is the failure mode worth spending two lines to avoid.
$frameworkBase = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
$refRoot = Join-Path $frameworkBase 'v4.8'
if (-not (Test-Path $refRoot)) {
    # Sorted by parsed version rather than by name: a name sort puts v4.9 above v4.10, and would
    # happily pick an older reference assembly than the one already on the machine.
    $found = Get-ChildItem $frameworkBase -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v(\d+)\.(\d+)' } |
        Sort-Object { [version]$_.Name.TrimStart('v') } -Descending |
        Select-Object -First 1
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
    # For the Get mods tab. All part of .NET Framework itself, not extra downloads: the JSON one reads
    # GitHub's release listing, the other unpacks a downloaded zip. Newtonsoft is deliberately not used -
    # it lives in the game's Managed folder, and a tool that repairs a game install must not share a
    # dependency with the thing it repairs.
    (Join-Path $refRoot 'System.Web.Extensions.dll')
    # Both compression assemblies: FileSystem has ZipFile, Compression has the ZipArchive it returns.
    # Referencing the pair emits CS1701, a version-unification note between the reference assemblies and
    # what FileSystem was compiled against. It is benign on .NET Framework 4.8, where both versions are
    # present, and there is no way to reference one without it.
    (Join-Path $refRoot 'System.IO.Compression.dll')
    (Join-Path $refRoot 'System.IO.Compression.FileSystem.dll')
    $cecil
)

foreach ($r in $references) {
    if (-not (Test-Path $r)) { throw "Missing reference: $r" }
}

# ---------------------------------------------------------------- compile

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Generated rather than committed, because it is derived from the tag and a stale copy checked in next
# to the sources is exactly the drift this replaces. obj\ is gitignored.
$objDir = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
$assemblyInfo = Join-Path $objDir 'AssemblyInfo.g.cs'

@"
// Generated by build.ps1 from the git tag. Do not edit, and do not commit - this file is obj\.
[assembly: System.Reflection.AssemblyTitle("ScamWYF.Launcher")]
[assembly: System.Reflection.AssemblyProduct("ScamWYF.Launcher")]
[assembly: System.Reflection.AssemblyCompany("")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Ras_rap")]
[assembly: System.Reflection.AssemblyConfiguration("")]
[assembly: System.Reflection.AssemblyVersion("$($versionInfo.Numeric)")]
[assembly: System.Reflection.AssemblyFileVersion("$($versionInfo.Numeric)")]
[assembly: System.Reflection.AssemblyInformationalVersion("$($versionInfo.Informational)")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8

$sources = @(
    Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
    $assemblyInfo
)
if (-not $sources) { throw "No sources in src\" }

$args = @(
    '-target:winexe'
    '-platform:anycpu'
    '-nostdlib+'
    '-langversion:7.3'
    '-optimize+'
    # Deterministic so the same commit compiles to the same bytes, which is what makes it meaningful to
    # publish a build and say afterwards which commit it came from.
    '-deterministic+'
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
Write-Host "  version:   $($versionInfo.Informational)"

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
