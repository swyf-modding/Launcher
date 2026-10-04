<#
    Builds ScamWYF.Launcher.exe.

    A thin wrapper around `dotnet build`. The project is a normal SDK-style csproj, so this script is not
    where the build happens:

        dotnet build -c Release

    is a complete, equivalent command. What this script adds is the part a plain build cannot do:
    stamping the version from the nearest git tag, staging the Setup payload the Setup tab runs, and
    reducing the output to exactly what gets published.

        .\build.ps1
        .\build.ps1 -Configuration Release
        .\build.ps1 -Cecil "C:\...\BepInEx\core\Mono.Cecil.dll"
        .\build.ps1 -Version 1.2.3
        .\build.ps1 -SetupPath C:\src\Setup
        .\build.ps1 -NoSetup         # build the exe alone; the Setup tab will be disabled

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

    Setup
    -----
    The launcher's Setup tab is not an implementation of Setup; it runs Setup's scripts. Those scripts
    carry the vendored Doorstop and the unstripped corlib, which together are about 12 MB of binaries
    and cannot be reimplemented or regenerated - so the release has to contain them or the Setup tab
    does nothing.

    Rather than copy that payload into this repository, the build finds a Setup checkout beside it,
    stages the part Setup actually needs into bin\Setup\, and records which Setup that was in
    SETUP-VERSION.txt. Three reasons for that over vendoring:

      - the download is the thing that was wrong. A user who had to clone a second repository before
        the launcher would work was not getting a two-file tool, and Setup exists as its own repo
        because those scripts are useful on their own. One zip has to be enough now.
      - Setup stays the single source of truth. A vendored copy is a copy somebody has to remember to
        re-sync, and a re-sync that is forgotten is a launcher shipping a fix that never shipped.
      - 12 MB of redistributable binaries in this repository's history, to be carried by every clone
        forever, for files that are already versioned somewhere else.

    The trade is a build-time dependency on another checkout, which is why -SetupPath exists, why
    release.yml pins a Setup tag, and why the release zip names the Setup commit it shipped.

    A missing Setup is a warning locally and an error in CI. Someone with only this repository should
    still be able to build and run the tool to work on it; a release that quietly omits the payload
    ships a launcher whose Setup tab is dead, which is exactly what this is fixing. -NoSetup is the
    explicit opt-out for the first case.

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
    [string]$Version,
    [string]$SetupPath,
    [switch]$NoSetup
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

# ---------------------------------------------------------------- Setup payload

# The files Setup's setup.ps1 reaches for, named rather than globbed. A glob is how a stale file from
# an older Setup survives into a release, and how a tool nobody calls gets shipped because it was
# sitting in the same folder.
#
# The layout has to be Setup's own layout and not a tidied-up one: the scripts dot-source each other
# with paths relative to $PSScriptRoot and its parent (tools\ dot-sources ..\GameDir.ps1), and
# setup.ps1 itself reads vendor\corlib and vendor\doorstop by those names. A flattened copy would
# break the first thing the user runs.
$setupScripts = @(
    'setup.ps1'
    'GameDir.ps1'
    'install-bepinex.ps1'
)

# All five, not just the three setup.ps1 calls. patch-preloader.ps1 is the offline repair for a
# preloader that has already been patched, and the two diagnostics are what you want when the verify
# step reports vtable breakages and explains nothing. None of them run unless asked for.
$setupTools = @(
    'unstrip-awaiters.ps1'
    'find-vtable-breaks.ps1'
    'patch-preloader.ps1'
    'compare-corlib.ps1'
    'scan-missing-apis.ps1'
)

# The eight unstripped BCL assemblies by name, for the same reason Setup's own CI lists them: a
# missing one is a game that will not boot, and an extra one is either a mistake or something we are
# not entitled to redistribute. Mono's licence text travels with them because the scripts print
# "licence texts are in vendor\" and that has to stay true.
$setupCorlib = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'System.Configuration.dll'
    'System.Numerics.dll'
    'System.Security.dll'
    'System.Xml.dll'
    'Mono.Security.dll'
    'LICENSE-Mono'
)

$setupDoorstop = @(
    'winhttp.dll'
    '.doorstop_version'
    'doorstop_config.ini'
    'LICENSE'
)

# Documentation that has to be redistributed rather than rewritten: Setup's README explains what the
# scripts do to a game folder, and vendor\README.md is where the licence obligations are recorded.
$setupDocs = @(
    'LICENSE'
    'README.md'
    'vendor\README.md'
)

function Resolve-Setup {
    param([string]$Explicit)

    $candidates = @()
    if ($Explicit) {
        $candidates += $Explicit
    } else {
        # The sibling checkout, which is how all five repositories sit on one machine. Checked before
        # anything inside this repository, so a build agent with both cannot silently pick the wrong
        # one.
        $candidates += (Join-Path (Split-Path -Parent $repoRoot) 'Setup')
        $candidates += (Join-Path $repoRoot 'Setup')
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'setup.ps1'))) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw @"
Could not find a Setup checkout.

Looked for setup.ps1 in:
  $($candidates -join "`n  ")

The launcher's Setup tab runs Setup's scripts, and those scripts carry the vendored Doorstop and the
unstripped corlib - about 12 MB of binaries that have to be in the release, so this cannot be skipped
for a release build.

  git clone https://github.com/swyf-modding/Setup.git

next to this repository, or point at it:

  .\build.ps1 -SetupPath C:\path\to\Setup

For a local build where the Setup tab is not what you are working on, -NoSetup builds the exe alone.
"@
}

function Install-Setup {
    param([string]$Source)

    $dest = Join-Path $outDir 'Setup'

    # Removed first rather than copied over. Copying over is what leaves a file behind when Setup
    # deletes or renames one, and bin\Setup is then a folder assembled from two different Setup
    # versions - a combination no Setup checkout has ever been tested in.
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }

    $files = @()
    foreach ($name in $setupScripts)  { $files += , @($name, '') }
    foreach ($name in $setupTools)    { $files += , @("tools\$name", '') }
    foreach ($name in $setupCorlib)   { $files += , @("vendor\corlib\$name", '') }
    foreach ($name in $setupDoorstop) { $files += , @("vendor\doorstop\$name", '') }
    foreach ($name in $setupDocs)     { $files += , @($name, '') }

    # Every missing file named at once. Checking as it copies turns one absent assembly into as many
    # build runs as there are absent assemblies.
    $missing = @()
    $zeroLength = @()
    foreach ($pair in $files) {
        $path = Join-Path $Source $pair[0]
        if (-not (Test-Path -LiteralPath $path)) {
            $missing += $pair[0]
        } elseif ((Get-Item -LiteralPath $path).Length -eq 0) {
            $zeroLength += $pair[0]
        }
    }

    if ($missing)    { throw "Setup is missing: $($missing -join ', ')`n  looked in $Source" }
    if ($zeroLength) { throw "Setup has zero-length files: $($zeroLength -join ', ')`n  looked in $Source" }

    # One setting the entire corlib override depends on. If it is edited away upstream the game
    # stops preferring unstripped_corlib and fails to start in a way that looks like a BepInEx bug,
    # so it is worth failing here rather than shipping it.
    $ini = Get-Content -LiteralPath (Join-Path $Source 'vendor\doorstop\doorstop_config.ini') -Raw
    if ($ini -notmatch 'dll_search_path_override\s*=\s*unstripped_corlib') {
        throw 'Setup\doorstop_config.ini does not set dll_search_path_override=unstripped_corlib'
    }

    foreach ($pair in $files) {
        $target = Join-Path $dest $pair[0]
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $pair[0]) -Destination $target -Force
    }

    # Which Setup this is, inside the thing that was built. The zip has to be able to answer "which
    # scripts did this launcher ship with" without a network round trip or a guess, because that is the
    # first question asked when a setup run misbehaves.
    #
    # Guarded on .git existing rather than on git's exit code. A Setup checkout is not guaranteed to be a
    # repository - GitHub's source zip is the obvious case, and -SetupPath is how someone points at one -
    # and asking git about a directory that is not a checkout writes to stderr, which under this script's
    # $ErrorActionPreference arrives as a terminating NativeCommandError rather than as a failed probe. An
    # unversioned Setup is a perfectly good Setup; it just cannot say which commit it is.
    $describe = $null
    $sha = $null
    $isCheckout = Test-Path -LiteralPath (Join-Path $Source '.git')
    if ($isCheckout -and (Get-Command git -ErrorAction SilentlyContinue)) {
        # 2>&1 into a variable rather than 2>$null, so a surprise on stderr is captured and discarded
        # instead of raised. -C rather than a working-directory change, which is why git is usable here
        # without touching anything else the script depends on.
        $null = & git -C $Source rev-parse --show-toplevel 2>&1
        if ($LASTEXITCODE -eq 0) {
            $described = & git -C $Source describe --tags --always 2>&1
            if ($LASTEXITCODE -eq 0) { $describe = ("$described").Trim() }
            $short = & git -C $Source rev-parse --short HEAD 2>&1
            if ($LASTEXITCODE -eq 0) { $sha = ("$short").Trim() }
        }
    }

    if (-not $describe) {
        $describe = if ($isCheckout) { 'unknown (git reported no tag or commit)' }
                    else { 'unversioned (not a git checkout)' }
    }

    # The first line is the whole contract: SetupRunner reads it back and shows it on the Setup tab, so
    # it has to be a single self-contained line and nothing else may come before it.
    $lines = @("Setup $describe")
    if ($sha) {
        $lines += "commit $sha"
        $lines += ''
        $lines += 'Bundled with ScamWYF.Launcher by build.ps1, from a Setup checkout. Not a Setup'
        $lines += 'release: to update it, fetch Setup, point -SetupPath at it, and rebuild.'
    }

    Set-Content -LiteralPath (Join-Path $dest 'SETUP-VERSION.txt') -Value $lines -Encoding UTF8

    $bytes = (Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  setup:    {0} files, {1:N1} MB, {2}" -f ($files.Count + 1), ($bytes / 1MB), $describe)

    return $describe
}

$setupVersion = $null
if ($NoSetup) {
    # Removed, not merely skipped. Leaving a payload from an earlier build in place would make
    # `build.ps1 -NoSetup` produce a bin\ that looks complete and contains somebody else's Setup, and a
    # bin\ is exactly what gets zipped. -NoSetup means the exe and nothing else, so that is what it has
    # to mean.
    $stale = Join-Path $outDir 'Setup'
    if (Test-Path $stale) {
        Remove-Item $stale -Recurse -Force
        Write-Host "  (removed bin\Setup from an earlier build)"
    }
    Write-Warning "-NoSetup was given, so bin\Setup will not exist and the Setup tab will be disabled."
} else {
    # Resolved here and staged below, after the build: resolving is cheap and fails fast, which is worth
    # knowing before two minutes of compiling, but nothing is written until the exe exists so that a
    # failed build does not leave a bin\ that looks publishable.
    $setupSource = Resolve-Setup $SetupPath
}

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
Write-Host "  setup:    $(if ($setupSource) { $setupSource } else { '(skipped)' })"
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

# Cecil next to the exe, so the tool finds its dependency without a search. The csproj copies it too,
# but doing it here means -Cecil is honoured: pointing at a different copy changes the one that ships.
Copy-Item $cecil (Join-Path $outDir 'Mono.Cecil.dll') -Force

# The SDK writes an app.config for any .NET Framework project, and there is no property that stops it:
# both AutoGenerateBindingRedirects and GenerateAppConfig are ignored for this. Its entire contents are a
# supportedRuntime line naming .NET Framework 4.8, which is what the executable already targets and what
# every Windows 10 and 11 has by default.
#
# Removed rather than shipped, because it is pure noise: the download is the exe, Mono.Cecil, and the
# Setup folder, and an app.config whose only content restates the target framework adds nothing to that.
# The README, the release notes and the release workflow all describe what is published, and quietly
# adding a fourth thing to bin\ would make zipped-up bin\ folders disagree with the published ones.
$generatedConfig = Join-Path $outDir 'ScamWYF.Launcher.exe.config'
if (Test-Path $generatedConfig) {
    Remove-Item $generatedConfig -Force
    Write-Host "  (removed the SDK-generated app.config, which net48 does not need)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- Setup payload

# After the build, not before. bin\ is the build's output directory, and writing into it first would
# mean a failed compile leaves a bin\ that looks like a finished one.
if ($setupSource) {
    $setupVersion = Install-Setup $setupSource
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

if ($setupVersion) {
    Write-Host "    Setup\                      $($setupVersion)"
} else {
    Write-Host "    Setup\                      (absent - the Setup tab is disabled)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Run it:  $exe" -ForegroundColor Green
Write-Host "It finds the game itself; pass a path as the first argument if it cannot." -ForegroundColor DarkGray