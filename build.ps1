<#
    Builds ScamWYF.Launcher.exe.

    A thin wrapper around `dotnet build`. The project is a normal SDK-style csproj, so this script is not
    where the build happens:

        dotnet build -c Release

    is a complete, equivalent command. What this script adds is the part a plain build cannot do:
    stamping the version from the nearest git tag, and reducing the output to the two files that get
    published.

        .\build.ps1
        .\build.ps1 -Configuration Release
        .\build.ps1 -Cecil "C:\...\BepInEx\core\Mono.Cecil.dll"
        .\build.ps1 -Version 1.2.3

    Why this used to compile Roslyn by hand, and why that changed
    ---------------------------------------------------------------
    The launcher was originally built with csc, no project file, no restore, for the same reasons the
    other repos in this project are: real reference assemblies rather than whatever the developer has,
    no network, one readable command. That was a reasonable trade while it was WinForms.

    It stopped being one when the UI became WPF. XAML has to be compiled by MSBuild's markup compiler,
    which means a project file, so the csproj arrived regardless - and once it had, the hand-rolled
    csc line was a second description of the same build that could disagree with the first. This is now
    the only one.

    WPF on net48 rather than a modern .NET
    --------------------------------------
    The target framework is deliberately .NET Framework 4.8. A net8/net10 build would be framework-
    dependent too, but on a runtime that is not installed by default, so a player would have to install
    something before the launcher would start. That is a bad thing to ask of someone whose game install
    is already broken. net48 ships with every Windows 10 and 11, so the download stays two files and
    runs on a clean machine.

    Mono.Cecil
    ----------
    The one external dependency, and it is the same copy the game already loads, so there is nothing
    extra to distribute: it is found in BepInEx\core and copied next to the exe. Point -Cecil at it if
    your game lives somewhere unusual.

    Version
    -------
    Taken from the nearest git tag, so a released build says which commit it came from without anyone
    having to remember to edit a number in a source file. -Version overrides that, which is what a build
    from a source archive with no .git needs.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Cecil,
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$outDir = Join-Path $repoRoot 'bin'
$exe = Join-Path $outDir 'ScamWYF.Launcher.exe'

# ---------------------------------------------------------------- Mono.Cecil

# The launcher reads plugin metadata with Cecil. Taking it from the game's own BepInEx\core keeps the
# tool dependency-free: it is already installed by the thing the tool installs, and it is the same
# version the mods were compiled against.
function Resolve-Cecil {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return $Explicit }
        throw "No Mono.Cecil at $Explicit"
    }

    # A copy staged next to the sources, which is how a continuous build gets it without a game
    # install. Checked first so a build agent does not silently pick up a different version.
    $staged = Join-Path $repoRoot 'vendor\Mono.Cecil.dll'
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

    $root = & git -C $repoRoot rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $root) {
        Write-Warning "Not a git checkout, so the version cannot be read from a tag. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    # --always so an untagged tree still describes rather than failing, and --dirty so a build from a
    # modified tree cannot be mistaken for a release.
    $describe = & git -C $repoRoot describe --tags --dirty --always 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $describe) {
        Write-Warning "git describe failed, so the version could not be read. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    $sha = & git -C $repoRoot rev-parse --short HEAD 2>$null
    return ConvertFrom-Describe $describe.Trim() "$($sha.Trim())"
}

$versionInfo = Resolve-Version $Version
if ($versionInfo.Dirty) {
    Write-Host "  note: the working tree has uncommitted changes, so this is not a clean release build." -ForegroundColor Yellow
}

# ---------------------------------------------------------------- build

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw @"
dotnet was not found on PATH.

The launcher is built with the .NET SDK, which is also what builds the mods in this project:

  https://dotnet.microsoft.com/download
"@
}

Write-Host "=== building ScamWYF.Launcher"
Write-Host "  sdk:      $((& dotnet --version).Trim())"
Write-Host "  cecil:    $cecil"
Write-Host "  version:  $($versionInfo.Informational)"

# Checked before the build rather than after it. MSBuild retries a locked output file ten times over
# about ten seconds and then fails with MSB3021, which says the file is locked but not by what. The
# usual cause is a launcher still open from last time, which is worth naming.
$running = @(Get-Process -Name 'ScamWYF.Launcher' -ErrorAction SilentlyContinue)
if ($running.Count) {
    throw @"
A ScamWYF.Launcher process is still running (PID $($running.Id -join ', ')), and it has bin\ScamWYF.Launcher.exe open.

Close it and build again. The exe cannot be replaced while it is running.
"@
}

# Output straight into bin\ rather than bin\Release\net48\. The project file's default is fine for
# `dotnet build` on its own; this keeps the published path, and everything that knows it, unchanged.
& dotnet build (Join-Path $repoRoot 'Launcher.csproj') `
    --configuration $Configuration `
    --nologo `
    --verbosity quiet `
    -p:OutputPath="$outDir" `
    -p:AppendTargetFrameworkToOutputPath=false `
    -p:AssemblyVersion="$($versionInfo.Numeric)" `
    -p:FileVersion="$($versionInfo.Numeric)" `
    -p:InformationalVersion="$($versionInfo.Informational)"

if ($LASTEXITCODE -ne 0) { throw "ScamWYF.Launcher failed to build (exit $LASTEXITCODE)" }

if (-not (Test-Path $exe)) { throw "Build reported success but $exe is missing." }

# Cecil next to the exe, so the tool is two files and finds its dependency without a search. The csproj
# copies it too, but doing it here means -Cecil is honoured: pointing at a different copy changes the
# one that ships.
Copy-Item $cecil (Join-Path $outDir 'Mono.Cecil.dll') -Force

# The SDK writes an app.config for any .NET Framework project, and there is no property that stops it:
# both AutoGenerateBindingRedirects and GenerateAppConfig are ignored for this. Its entire contents are a
# supportedRuntime line naming .NET Framework 4.8, which is what the executable already targets and what
# every Windows 10 and 11 has by default.
#
# Removed rather than shipped, because the download is two files and has been since 1.0.0 - the README,
# the release notes and the release workflow all say so, and quietly adding a third file to bin\ would
# make zipped-up bin\ folders disagree with the published ones for no benefit.
$generatedConfig = Join-Path $outDir 'ScamWYF.Launcher.exe.config'
if (Test-Path $generatedConfig) {
    Remove-Item $generatedConfig -Force
    Write-Host "  (removed the SDK-generated app.config, which net48 does not need)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- report

Write-Host ""
Write-Host "  Built $exe" -ForegroundColor Green

Get-ChildItem $outDir -File |
    Where-Object { $_.Name -match '^ScamWYF\.Launcher\.(exe|dll|exe\.config)$|^Mono\.Cecil\.dll$' } |
    Sort-Object Name |
    ForEach-Object { "    {0,-28} {1,9:N0} bytes" -f $_.Name, $_.Length }

if (Test-Path (Join-Path $outDir 'ScamWYF.Launcher.pdb')) {
    Write-Host "  (pdb written)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Run it:  $exe" -ForegroundColor Green
Write-Host "It finds the game itself; pass a path as the first argument if it cannot." -ForegroundColor DarkGray