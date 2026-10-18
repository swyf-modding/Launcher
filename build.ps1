<#
    Builds ScamWYF.Launcher: one self-contained single file per platform, into dist\.

    A thin wrapper around `dotnet publish`. The project is a normal SDK-style csproj, so the publish is
    not done by hand here:

        dotnet publish Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

    is a complete, equivalent command for one platform's file. What this script adds is the version
    stamped from the nearest git tag, both platforms' files in one run, and one predictable dist\ layout
    for a release to zip:

        dist\linux-x64\ScamWYF.Launcher
        dist\win-x64\ScamWYF.Launcher.exe

        .\build.ps1
        .\build.ps1 -Configuration Debug
        .\build.ps1 -Version 1.2.3
        .\build.ps1 -Runtime linux-x64      # just one platform

    There is no Setup folder beside the exe any more
    -------------------------------------------------
    The launcher used to ship three things: the exe, Mono.Cecil.dll, and a Setup\ folder holding
    Setup's PowerShell scripts and the ~12 MB of corlib and Doorstop binaries they seeded into the
    game. The Setup tab ran those scripts and streamed their output.

    It does not any more. The setup runs in-process, in-process in the launcher, through
    ScamWYF.Setup.Core - the same library the CLI and the tests use - so there are no PowerShell
    scripts to stage and no SETUP-VERSION.txt to record. The corlib and Doorstop those steps need are
    compiled into the executable as embedded resources, so the download really is one file, and this
    script no longer needs a Setup checkout at all. That is the whole point of the arrangement: no
    build-time dependency on a second repository, and nothing to keep beside the exe.

    Mono.Cecil is a NuGet package now, referenced by ScamWYF.Setup.Core and pulled in transitively, so
    it is inside the single file too rather than copied out of the game's BepInEx\core.

    Version
    -------
    Taken from the nearest git tag, so a released build says which commit it came from without anyone
    having to remember to edit a number in a source file. -Version overrides that, which is what a build
    from a source archive with no .git needs.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version,
    [string[]]$Runtime = @('linux-x64', 'win-x64')
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'Launcher.csproj'
$dist = Join-Path $repoRoot 'dist'

# ---------------------------------------------------------------- version

# A released binary should be able to say where it came from. The version is read from the nearest git
# tag rather than typed into a source file, because a number in a source file drifts: the READMEs and
# the [BepInPlugin] attributes all showed 1.0.0 long after the dll changed, and the launcher reads
# those attributes back out to show the user, so a stale one is not merely untidy - it is visible.
#
# AssemblyVersion and AssemblyFileVersion must be numeric, because the CLR rejects "1.0.0-beta.1" and
# would fail the build over a prerelease tag. So a prerelease keeps its numeric part there and carries
# the rest in AssemblyInformationalVersion - which is the field Explorer and Programs and Features
# actually display, so this is what a player sees. build.sh stamps exactly the same shape.
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
Write-Host "  config:   $Configuration"
Write-Host "  version:  $($versionInfo.Informational)"
Write-Host "  runtimes: $($Runtime -join ' ')"

# Checked before the build rather than after it. MSBuild retries a locked output file ten times over
# about ten seconds and then fails with MSB3021, which says the file is locked but not by what. The
# usual cause is a launcher still open from last time, which is worth naming.
$running = @(Get-Process -Name 'ScamWYF.Launcher' -ErrorAction SilentlyContinue)
if ($running.Count) {
    throw @"
A ScamWYF.Launcher process is still running (PID $($running.Id -join ', ')), and it has the published file open.

Close it and build again. The file cannot be replaced while it is running.
"@
}

foreach ($rid in $Runtime) {
    $out = Join-Path $dist $rid
    Write-Host ""
    Write-Host "--- publishing $rid -> $out"

    # Removed first rather than published over. Copying over leaves a file behind when a newer SDK
    # renames or drops one, and dist\ is exactly what gets zipped - a single-file publish is supposed
    # to leave one thing behind, so anything else is a mistake worth removing rather than shipping.
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    & dotnet publish $project `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        --nologo `
        --verbosity quiet `
        --output $out `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:AssemblyVersion="$($versionInfo.Numeric)" `
        -p:FileVersion="$($versionInfo.Numeric)" `
        -p:InformationalVersion="$($versionInfo.Informational)"

    if ($LASTEXITCODE -ne 0) { throw "ScamWYF.Launcher failed to publish for $rid (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------- report

Write-Host ""
Write-Host "  Built:" -ForegroundColor Green

foreach ($rid in $Runtime) {
    $out = Join-Path $dist $rid
    if (-not (Test-Path $out)) { throw "$out is missing - the publish reported success but wrote nothing." }

    $files = @(Get-ChildItem $out -File)
    if (-not $files.Count) { throw "$out is empty." }

    # A single self-contained file is tens of megabytes; anything tiny means the publish silently
    # produced a framework-dependent stub, which is the failure worth failing the script over.
    $files | Sort-Object Name | ForEach-Object {
        "    {0,-44} {1,12:N0} bytes" -f $_.FullName.Substring($repoRoot.Length + 1), $_.Length
    }
}

Write-Host ""
Write-Host "Done. The file is all there is: it carries the setup logic and payload embedded, so no Setup folder belongs beside it." -ForegroundColor Green
