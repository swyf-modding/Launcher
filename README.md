<div align="center">

# Switchboard

![license](https://img.shields.io/badge/license-MIT-blue)
![version](https://img.shields.io/github/v/release/swyf-modding/Switchboard?label=version)
![game build](https://img.shields.io/badge/game-0.1.71--swyf-blue)
![framework](https://img.shields.io/badge/.NET-10-512BD4)

*Built with:*

![C#](https://img.shields.io/badge/C%23-512BD4?style=for-the-badge&logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Avalonia](https://img.shields.io/badge/Avalonia-8B44AC?style=for-the-badge&logo=avaloniaui&logoColor=white)
![Mono.Cecil](https://img.shields.io/badge/Mono.Cecil-0.11.6-9a3d3d?style=for-the-badge)

> ⚠️ **Linux support is VERY experimental.** The switchboard runs under Avalonia on Linux and drives the
> same Setup Core under Proton, but this is validated on one machine only (NVIDIA + Wayland). Windows
> is the mature path. Known Linux traps are listed in the Setup README (`winhttp=n,b`, `-whisper-cpu`).

</div>

---

## Table of Contents

- [Overview](#overview)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Developer Setup](#developer-setup)
  - [How setup runs](#how-setup-runs)
  - [Usage](#usage)
  - [Testing](#testing)
- [Compatibility](#compatibility)
- [Installing Mods](#installing-mods)
- [Safety](#safety)
- [Project Structure](#project-structure)
- [Continuous Builds](#continuous-builds)
- [Releases](#releases)
- [Security](#security)
- [License](#license)
- [Related Projects](#related-projects)

---

## Overview

**Switchboard** is an unofficial, community-built mod manager for **Scam With Your
Friends**. Four tabs: fix the install, manage mods, get mods, read the log. It runs on **Windows** and, for
players on **Linux**, under **Proton** — the same one file on both.

**Key Features:**

- **Diagnoses a partial install.** Reports the game, loader, corlib override, its wiring and BepInEx as
  separate checks, because a half-installed game is the case that matters and "not installed" is not a
  useful answer to it
- **Runs the real setup, in-process.** Delegates to [Setup](https://github.com/swyf-modding/Setup)'s setup
  logic through its `ScamWYF.Setup.Core` library and streams the run into the window, rather than
  reimplementing it in a button handler. The vendored Doorstop and the unstripped corlib the setup needs
  are **compiled into the executable**, so the Setup tab works from a single file with no folder beside it
- **Lists and manages mods.** Every dll in `plugins` and `plugins_disabled`, read from `[BepInPlugin]`
  metadata, with enable, disable and delete
- **Installs mods from the catalogue.** Fetches the live mod list, downloads a release, verifies it, shows
  exactly what will be written, and only then installs
- **Shows the log where you can act on it.** `LogOutput.log` with the interesting lines picked out, and
  optional live following while the game runs
- No redistributed game DLLs, decompiled game source, telemetry, or credential collection

> [!IMPORTANT]
> The switchboard **never writes to your game folder without running the setup**. Everything on the
> Setup tab is that setup's own doing; everything on the Mods tab is a move or a delete between two
> folders. If a step is refused, the reason is stated rather than attempted and failed.

> [!NOTE]
> Mods cannot be changed while the game is running. BepInEx does not release plugin assemblies when it
> shuts down, so the files stay locked. The buttons disable themselves and the note column says why,
> rather than failing later with a message about a file being in use.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your Friends.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**, with the build listed under
  [Compatibility](#compatibility)
- **Windows x64 or Linux x64**, and nothing else. The download is a **self-contained** .NET 10
  executable: the runtime is inside the file, so there is no .NET to install, no PowerShell to have, and
  no second download

On Linux the game runs under Proton, and everything the switchboard does there is the same work on the same
game folder — it is Wine that needs one setting, and the switchboard checks for it and tells you how to set
it. See [Playing on Linux](#playing-on-linux).

Nothing else. [Setup](https://github.com/swyf-modding/Setup) used to be a second repository to clone
before this tool did anything; its setup logic and vendored binaries are inside the executable now.

### Installation

1. Download the release for your platform — `Switchboard.exe` for Windows, `Switchboard` for Linux.
2. Run it. On Linux, make it executable first: `chmod +x Switchboard`.

There is nothing to install, no second download, and **no folder to keep beside it**:

```text
Switchboard        Windows: Switchboard.exe
```

That is the whole download. The setup logic and the ~12 MB of corlib and Doorstop binaries it seeds into
the game are compiled into the executable as embedded resources, so there is no second file or folder a
partial download could have left out.

### Developer Setup

```bash
git clone https://github.com/swyf-modding/Switchboard.git
cd Switchboard
dotnet build -c Release     # a complete build on its own
./build.sh                  # publish both platforms, one self-contained file each, into dist/
./build.sh --version 1.2.3  # stamp a version rather than reading the tag
./build.sh --runtime linux-x64
```

On Windows the same thing is `.\build.ps1`, and it takes the same switches. Nothing else needs
cloning — there is no Setup checkout to fetch and no vendored dll to stage.

An SDK-style project — `Switchboard.csproj` — like the mods. The only external dependency is
[Mono.Cecil](https://github.com/jbevain/cecil), a NuGet package pulled in by `ScamWYF.Setup.Core`; the
switchboard's own code adds `Avalonia` and nothing else. `dotnet build -c Release` is a complete build of
the exe for the current platform; `build.sh`/`build.ps1` are thin wrappers around `dotnet publish` that
add the three things a plain build cannot do — stamping the version from the nearest git tag, publishing
**both** platforms in one run, and reducing the output to a single file in `dist/`.

**This is Avalonia on .NET 10, and it is cross-platform on purpose.** The previous build was WPF on .NET
Framework 4.8, which shipped with every Windows 10 and 11 and so needed nothing installed — but WPF is
Windows-only by construction, and .NET Framework has no Linux. Avalonia draws the same UI on Windows,
Linux and macOS from one codebase, and .NET 10 publishes self-contained, so the download is still a
single file a player can just run. The UI framework changed; the layout, the palette and the interaction
model did not, and the port kept them.

The build is deterministic: the same commit compiles to the same bytes, which is what makes it worth
saying afterwards exactly which commit a published binary came from.

#### Versioning

`build.sh` and `build.ps1` read the version from the nearest git tag and stamp it into the assembly.
Nothing to edit by hand, so the number cannot drift from the code:

| | |
|---|---|
| `AssemblyVersion`, `AssemblyFileVersion` | `1.2.3` — numeric, because the CLR rejects a prerelease here |
| `AssemblyInformationalVersion` | `1.2.3+g0a1b2c3` — what Explorer and Programs and Features show |
| Window title | the same string, so a user can read their version off the screenshot |

`git describe` decides the rest: a commit past the tag adds `+3.g0a1b2c3`, a prerelease tag keeps its
name (`v1.2.3-rc1` → `1.2.3-rc1+g0a1b2c3`), and an uncommitted tree is reported as `.dirty` and warned
about, so a local build cannot be mistaken for a release. With no tags at all the version is
`0.0.0+untagged.g0a1b2c3`, which says so rather than claiming to be 1.0.0.

Two fallbacks, because a build has to work outside a checkout: no `git`, or no `.git\`, warns and
stamps `0.0.0`; `-Version` sets the version outright, which is what a build from a source archive needs.

### How setup runs

The Setup tab is not an implementation of setup. It runs [Setup](https://github.com/swyf-modding/Setup)'s
setup logic **in-process**, through the `ScamWYF.Setup.Core` library that this project references:

```xml
<ProjectReference Include="../Setup/src/ScamWYF.Setup.Core/ScamWYF.Setup.Core.csproj" />
```

`SetupRunner.Run` is handed the game folder, the payload and a log sink, and does the five steps Setup has
always done — install the loader, seed the corlib override, restore the awaiter methods, patch the
remaining vtable breaks, verify. Every line it writes goes into the window, so a run is as visible as it
was when the tab drove `setup.ps1`; the difference is that it is a method call, not a child process.

This is why the exe is one file and there is no `Setup\` folder. The two things the setup cannot fetch at
runtime — the unstripped BCL (about 12 MB) and the vendored Doorstop — are compiled into the assembly as
**embedded resources** (`payload/corlib/...`, `payload/doorstop/...`), and `SetupPayload.ResolveDefault`
reads them back. When the switchboard is run from a source checkout, that same call finds the real `vendor/`
folder first, so a developer iterating on the corlib uses the files on disk rather than a stale embed.

Two consequences worth stating plainly:

- **There is no build-time checkout of Setup any more.** The code is a project reference, so it is
  compiled from wherever the sibling repository is, and the binaries are in `vendor/` next to it. The
  release workflow pins the Setup commit via the reference it clones, and `build.ps1`/`build.sh` still
  only need this repository plus that checkout to compile against.
- **There is no PowerShell to run.** The old tab launched `setup.ps1` as a child process and marshalled
  its output back; the port reads the same steps as library calls and marshals the log lines instead.
  Nothing about the game folder changed — the steps and their order are identical.

### Usage

**Setup** probes the install, then runs the setup when asked.

| Check | Why it is separate |
|---|---|
| Game install | `Scam With Your Friends_Data/Managed` has to exist |
| Loader (Doorstop) | the loader half, which starts everything |
| Corlib override | the usual cause of "it crashed" — the shipped `mscorlib` is stripped and BepInEx cannot start without it |
| Override wired up | `doorstop_config.ini` has to point at it |
| BepInEx | downloaded from GitHub, or already there |
| Setup payload | the only row about *this download* rather than the game — the embedded corlib and Doorstop, and whether they could be read |
| Proton winhttp override | **Linux only.** Without it, Wine's own `winhttp.dll` wins and Doorstop never loads |

**Set up / repair** runs the setup in-process and streams its log into the window as it goes. **Set up
(offline)** is there for when BepInEx has already been downloaded, or there is no network. The buttons
only need a game install to exist; the setup logic and its payload are part of the exe, so there is
nothing beside it that a partial download could have left out.

**Mods** lists every dll in both folders with **Enable**, **Disable** and **Delete**. BepInEx has no
enable/disable of its own, so a toggle is a file move between the two folders — the same thing a person
would do by hand. Changes take effect on the next launch.

A dll with no `[BepInPlugin]` is listed and marked, not hidden. The shared library is one, and seeing
"unrecognised" for it would reasonably suggest something was broken.

**Config…** opens the selected mod's settings for editing, if it has any.

BepInEx has no configuration format this tool can read a schema from, so the editor is built from the file
itself: the mod writes the type, the default and usually an explanation above every value, and that is the
only description of the setting that exists. So it is shown rather than replaced. A yes/no setting is a
checkbox, a short list of accepted values is a row of choices, and anything else is a text box.

Saving rewrites only the lines whose values changed. Comments, blank lines, section order and anything the
parser did not recognise survive untouched, so a file keeps the documentation the mod wrote for it. Values
are checked before anything is written — against the list a setting declares, or true/false for a boolean —
because the alternative is a config the game rejects on the next launch and silently falls back to its
default.

Saving is refused while the game is running. BepInEx writes a plugin's config as it shuts down, so an edit
made during a session would be overwritten a moment later.

**Log** shows `BepInEx\LogOutput.log` with the lines that matter picked out above the raw text. A
preloader crash, before BepInEx starts, goes to `preloader_*.log` in the game folder instead, and the tab
says so.

**Play game** starts the game, and refuses while it is already running. Passing a path as the first
argument overrides where the game is looked for. On Windows it starts the game's own exe; on Linux it goes
through Steam, because that is how the game is played there and it keeps the user's chosen Proton and
their overlays. See below.

#### Playing on Linux

The game is a Windows build, so on Linux it runs under **Proton**, and the switchboard does the Windows
work in the game folder as usual. One thing is different, and it is the thing that silently does nothing
if it is missed: **Wine prefers its own `winhttp.dll` over the one Doorstop installs in the game folder**,
so without an override Doorstop is never loaded and the game starts completely unmodded, with no error
anywhere.

The fix is a Steam launch option for the game:

```text
WINEDLLOVERRIDES="winhttp=n,b" %command% -whisper-cpu
```

Keep it exactly like that: `n` alone (the builtin disabled) makes the game crash in Unity's window
code a minute or so into startup, which looks like the game refusing to start.

The Setup tab shows a **Proton winhttp override** row on Linux, and **Play game** sets the override in
the Proton prefix itself when it can — best effort, because that is only safe when a prefix already
exists and Wine is not running. If it cannot, it says so and prints the launch option to paste. Steam
discovery (`SteamEnvironment`) finds the game in every Steam library, including the Flatpak and the usual
`~/.steam` and `~/.local/share/Steam` locations, so the switchboard works without being told where the game
is.

### Testing

```bash
./tools/test-getmods.sh            # 184 assertions, offline
./tools/test-getmods.sh --online   # plus a live check per known mod, against the real GitHub API
```

On Windows the equivalent is `.\tools\test-getmods.ps1` with the same switches, and

```bash
dotnet run --project tests/Switchboard.Tests.csproj -c Release
```

is an equivalent command for the offline run. The two suites are separate programs rather than one with a
filter (both define a `Main`, so `Online` is a compile-time switch), and both compile the app's own
sources rather than referencing the built switchboard — so the tests cannot pass against a stale copy of the
logic.

What is under test is the logic and the file operations: zip-slip refused in both the `/` and `\` forms, a
checksum mismatch caught, a real download unpacked and planned, catalogue validation, HTTPS-only downloads
with redirects checked by hand, config parse/validate/round-trip, and the switchboard-update comparison.

Not covered by tests, and honestly so: **the UI**. It is presentation, and these tests are about what
happens to files on disk. What that leaves is checked by running it: Avalonia resolves styles, control
themes and `StaticResource` lookups when a window opens, so a resource mistake can be a build-clean,
startup-failing error rather than a compile error. Starting the app and visiting each of the four tabs is
what catches that, and there is no display-less equivalent committed here.

Verified against a real install: the probes report `ready`, the installed mods appear with their real
names and versions, and the Log tab picks the BepInEx channel lines out of the raw text.

No game DLL belongs in this repository or in a GitHub release.

#### What CI checks that a build cannot

The offline suite is the deterministic part, and it is what CI runs. The WPF build also had a
Windows-only UI-automation pass — confirming the window handle appears, hovering every button to fire its
triggers, and running the setup through the tab against a stub so the cross-thread marshalling was
exercised. That pass is WPF's `UIAutomation` API and has **not** been ported to Avalonia yet; the
Avalonia port's own UI checking was done headlessly (window and all four views constructed, resources
resolved, every tab reloaded) at development time rather than in a committed harness. Porting it is a
known follow-up, not something this arrangement pretends is done.

The threading point behind the old script run still stands and is still the risk worth naming: the setup
reports from a thread the UI does not own — a line per log entry, and the result on a worker — and reading
a control from either throws. An unhandled exception on a thread-pool thread does not raise a dialog, it
**ends the process**. Every callback the setup is handed is marshalled onto the UI thread for exactly
that reason, and that marshalling is the one code path a real setup run exercises.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `0.1.71-swyf` |
| Unity | `6000.3.10f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| Target framework | .NET 10 (`net10.0`), self-contained |
| UI framework | Avalonia `11.3.22` |
| C# language level | `latest` |
| Mono.Cecil | `0.11.6` (NuGet, via `ScamWYF.Setup.Core`) |
| Platform | Windows x64, and Linux x64 under Proton |

The C# language level is `latest` now. The previous build pinned `7.3` to match the mods; the port to
`.NET 10` and Avalonia made that pin pointless — the SDK and the UI framework both expect modern C# — so
it was dropped here rather than carried forward as a second thing to reason about. The mods are their own
repositories and are unchanged by this.

The Linux support is the same game, the same BepInEx and the same files; what differs is that the game
runs under Proton, and the winhttp override has to be in place for Doorstop to load. The switchboard checks
for it and sets it where it can — see [Playing on Linux](#playing-on-linux).

Other game builds are untested. The switchboard probes what it finds rather than assuming a layout, so an
unexpected build will usually still work — it just reports what it actually sees.

---

## Safety

**An explicit path is never second-guessed.** Passing a folder — as an argument or through
`SWYG_GAME_DIR` — means exactly that folder. It is not quietly replaced by a Steam location that happens
to exist, because a typo in a path should say "that folder is not a game install" rather than succeed
somewhere else. Steam is only searched when nothing was specified.

**A name collision is refused, not resolved.** Two mods with the same file name is a real situation — a
downgrade, or an extract into the wrong folder. Picking a winner silently would hide it. Delete is the
exception: it has no destination folder, so it works even when a same-named file is enabled in the other
one, which is what cleaning up after a downgrade needs.

**Delete is permanent and says so.** It asks for confirmation, defaults to Cancel, and keeps no undo or
quarantine copy. A delete that quietly kept a copy would grow without bound.

**Plugin metadata is read, not executed.** `Mono.Cecil` reads `[BepInPlugin]` as metadata rather than
loading the assembly, which means a mod's static constructors never run and the file is never locked —
both of which matter for a tool whose job includes deleting mods.

---

## Project Structure

```text
Switchboard.csproj      The project: net10.0 + Avalonia, referencing ScamWYF.Setup.Core
src/
|-- Program.cs         The Avalonia entry point (BuildAvaloniaApp / StartWithClassicDesktopLifetime)
|-- App.xaml(.cs)      Application resources, the theme include, and the unhandled-exception reporter
|-- MainWindow.xaml    The four tabs, the status line, the Play game button
|-- MainWindow.xaml.cs  Also the host the views talk to: status, busy flag, the in-process setup, Play game
|-- Theme.xaml         The palette, the control themes and the styles - see "How it looks"
|-- Palette.cs         Reading the palette and control themes back out of the application resources
|-- Dialogs.cs         Modal confirmations, themed; Avalonia has no MessageBox to theme
|-- ModManager.cs      Enable, disable, delete - the file operations
|-- ModConfig.cs       Reading, checking and rewriting a mod's BepInEx config
|-- PluginScanner.cs   Reading [BepInPlugin] metadata with Cecil
|-- PluginEntry.cs     One mod on disk
|-- Http.cs            The only code that touches the network: https only, redirects checked by hand
|-- ModCatalogue.cs    The mod list, live from ModCatalogue with a built-in fallback, and what GitHub says each latest release is
|-- ModInstaller.cs    Download, unpack, verify, and only then install
|-- UpdateChecker.cs   Is a newer switchboard released? - the tag comparison behind the update bar
|-- ScriptRunner.cs    Running a mod's optional post-install script, and collecting what it prints
`-- views/
    |-- SetupView      The Setup tab
    |-- ModsView       The Mods tab
    |-- GetModsView    The Get mods tab
    |-- ConfigWindow   The per-mod settings editor
    `-- LogView        The Log tab
build.sh               Publish one self-contained file per platform into dist/, with tag versioning
build.ps1              The same, on Windows
tools/
|-- test-getmods.ps1   Tests for the above; -Online also exercises a real download
`-- test-getmods.sh    The same, on Linux
tests/                 Switchboard.Tests.csproj, GetModsTests.cs (offline), OnlineModTests.cs (needs network)
vendor/                A pinned Mono.Cecil copy, kept for the record; the build gets Cecil from NuGet
```

`dist/` is not in that list because it is build output: it holds the published files, one per platform,
each self-contained with the setup logic and payload embedded. See [How setup runs](#how-setup-runs).

`GameInstall` and the setup itself are not in `src/` any more. They live in `ScamWYF.Setup.Core`, which
this project references and which the CLI and the tests share; the switchboard presents that code rather than
duplicating it. The removed `GameInstall.cs` and `SetupRunner.cs` were the switchboard's old copies, and they
are gone rather than kept alongside the Core ones, so there is no second definition to drift.

The split between `src/` and `src/views/` is the UI-free half against the UI. Everything the tests compile
is the former (plus the referenced Core), which is why `tests/Switchboard.Tests.csproj` builds a test binary
with no Avalonia in it.

### How it looks

The website's palette, token for token: the warm brown ground, cream text, gold for the things that are
actionable, wood and paper for the two surfaces meant to be noticed. The site is where a player meets
the tool, so the app is not allowed to look like a different program. The two faces are the site's too —
Nunito Sans for the body, Bowlby One for the display — and both are bundled, so the app looks the same on
a machine that has neither installed.

The same colours are also mod-lib's `UiTheme` fallback, which the shared library reads from the game's
live colours and falls back to when it cannot. So the switchboard, the in-game menu it opens and the site
are one visual system rather than three, and there is one file to change rather than a set of greys
scattered through four tabs.

Everything visual is a `Style`, a `ControlTheme` or a resource in `Theme.xaml`, over Avalonia's
`FluentTheme` with `RequestedThemeVariant="Dark"`. Recolouring the stock controls is mostly a matter of
setting the palette resources Fluent reads; the five controls the app draws itself — the flat button, the
accent button, the ghost button, the choice chip and the tab header — are `ControlTheme`s in the same
file. That is the whole reason the UI is on a framework with templatable controls: the WinForms build
needed subclasses purely to draw chrome the framework had already coloured from system brushes, and
scrollbars could not be themed at all.

Four conventions the four tabs share, and the reason for each:

- **A line under the tab strip saying what the tab is for.** It belongs to the tab, so it is a
  sentence rather than a heading, and it is the first thing anyone reads. Each view has a `Purpose`
  property and its own `PurposeText` block.
- **An empty state wherever a table can be empty.** A table with a header row and nothing under it
  cannot be told apart from a tab that is broken, so the empty state says what is absent and what to
  do about it — "no loader installed, the Setup tab installs it" rather than an empty box.
- **Buttons that act on the selection are separated from buttons that act on everything.** On the
  Mods tab that is the difference between *Enable* and *Rescan*, which used to sit in one row and
  look like the same kind of thing.
- **A disabled button is visibly disabled.** It keeps its shape and goes quiet, the way the site's
  buttons dim to 55%, rather than disappearing into the background.

One consequence worth knowing before editing a view: the column tables are a header `Grid` plus an
`ItemTemplate` whose row grid repeats the same column widths, with a trailing `*` column so the last cell
fills the width. Avalonia has no `SharedSizeGroup`, so those widths are written twice — once for the
header, once for the rows — and that is deliberate: it is what keeps the last column filling the
remaining space instead of being a fixed width. Editing a table means editing both. Horizontal scrolling
is off, so a header that stays put while the rows scroll looks identical to one that moves.

The window is 1060x720 because the content needs it. A row of checks with a detail column is not readable
at 940x640, and the answer to that is not a narrower column.

---

## Installing Mods

**Get mods** is a separate tab from **Mods** on purpose. The Mods tab is bookkeeping over files already
on disk and never touches the network. This one downloads code that the game will execute, so it says
what it is doing and asks before it does anything.

**From the list.** *Refresh from GitHub* asks GitHub for the latest release of each project and shows
the tag, the size, and whether it is already installed. *Install* downloads the release's zip, unpacks it,
reads each file's metadata, and shows you exactly what will be written:

```text
AI Backend  v1.0.0

  Scam WYF AI Backend  1.0.0+g2ac00c8
    -> BepInEx\plugins\ScamWYF.AiBackend.dll
  ScamWYF.Modding.Core.dll  1.0.0+g2ac00c8
    -> BepInEx\core\ScamWYF.Modding.Core.dll

Files come from ScamWYF.AiBackend-v1.0.0.zip on GitHub, 56.4 KB.
SHA-256 22792e5162396844820a27ed9c846c20ea9f098262a92d9fa164523f2c4fada0
```

The version shown is read **out of each file**, not taken from the URL — that is the version that will
actually load. Replacing something already installed gets a second confirmation, and there is no undo.

**Where the list comes from.** The tab fetches `mods.json` from the
[ModCatalogue](https://github.com/swyf-modding/ModCatalogue) repository on every check, so adding a mod
means editing that file rather than cutting a switchboard release. If the fetch fails — offline, rate
limited — the tab falls back to the list compiled into the switchboard and says so on the status line.
Entries may point at repositories outside `swyf-modding`; those are marked with their owner in the list
and named again in the install confirmation, because there is no shared review behind them.

**Optional setup steps.** Some mods need something the download cannot do by itself — a stream mod's
ChatBridge, which talks to Twitch and is configured per machine. A catalogue entry can name up to four
`postInstall` scripts for that, shipped inside the release zip. Each is offered separately *after* the
files are installed, with the script's own text shown first, and each can be denied: declining changes
nothing already installed. A script the release does not contain fails the install rather than being
fetched from anywhere else, and nothing ever runs without an explicit yes.

**From a URL.** For anything not on the list. Paste an `https` link to a `.dll` or a `.zip`. The host is
named in the confirmation, because unlike the list there is nothing curated behind it. A `.zip` has each
dll placed by whether it carries a `[BepInPlugin]`: with one goes to `plugins`, without one goes to
`core`.

### What it refuses, and why

| Refused | Because |
|---|---|
| `http://` | A mod is code the game runs. Plain HTTP can be replaced in transit, and there is no signature to notice afterwards |
| A redirect to `http://` | Followed by hand rather than by the framework, so a downgrade is caught rather than obeyed |
| `file://`, or a URL with a username | That is not a download; it is a local file read |
| A zip entry named `../something` or `..\something` | Zip-slip. Every entry's resolved path is checked before it is written — with both separators treated as separators, so the answer is the same on Linux as on Windows — so an archive cannot escape the temp folder |
| A file over 32 MB | The largest thing here should be a 60 KB zip. Checked from the header, so it fails before a byte is written |
| A file that is not a .NET assembly | Checked with Cecil, the same reader the Mods tab uses |
| A mod with no `[BepInPlugin]` | BepInEx would not load it. The shared library is the exception, and it routes to `core` precisely so that is not a problem |
| A download that fails its checksum | Discarded, and nothing is installed |

Nothing is ever **loaded** to find out what a file is — only read as metadata. A mod's static
constructors do not run in this process.

### What the checksum is and is not

GitHub publishes a SHA-256 for each release asset, and a mismatch discards the download. That catches a
truncated or corrupted download, and an asset host that is not GitHub's.

It is **not** a signature, and it does not make a mod safe: the digest comes from the same place as the
file. Nothing here can tell you whether a mod is malicious. The tab says so where you will read it.

### Rate limits

The GitHub API allows 60 unauthenticated requests an hour per address, and one lookup per known mod.
Hitting it says so, rather than reporting a generic failure.

---

## Continuous Builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request: build, run the
offline tests, `./build.sh` to publish both platforms, confirm each is a single self-contained file, and
upload `dist/`. That is possible because the switchboard needs no game to build — the setup is a library
reference and its payload is embedded — so the runner only needs this repository and a checkout of Setup
to compile against.

> [!NOTE]
> There is no display-less UI pass yet. The WPF build had a Windows-only UI-automation pass — require a
> real window handle, hover every button, run the setup through the tab — and that is WPF's `UIAutomation`
> API. It has not been ported to Avalonia; see
> [What CI checks that a build cannot](#what-ci-checks-that-a-build-cannot). That is a known gap, not a
> guarantee.

The mods' workflows are two-tiered precisely because they do need the game; see
[mod-lib's](https://github.com/swyf-modding/mod-lib#continuous-builds) for what that looks like.

---

## Releases

Publishing is one command, because this is the one repo here that needs no game to build:

```bash
git tag v1.2.3
git push origin v1.2.3
```

[`.github/workflows/release.yml`](.github/workflows/release.yml) builds the tagged commit and publishes a
GitHub release with generated notes. What it publishes is the output of `build.sh`/`build.ps1`: one
self-contained file per platform, `Switchboard` and `Switchboard.exe`.

Three checks stand between a tag and a published binary, because a wrong version in a release is worse
than no release:

| Check | Fails when |
|---|---|
| Tag on the commit | `git describe` does not report the tag — the tag points somewhere unexpected |
| Clean tree | the build has uncommitted changes in it |
| Version matches | the binary's stamped version is not the tag, or does not name a commit |

It **rebuilds** rather than reusing the `ci.yml` artefact, on purpose: artefacts expire after 90 days
and tags do not, so a release driven from an artefact would one day have nothing to publish. The build
is deterministic, so the bytes are the same either way.

A release used to be a zip holding the exe, `Mono.Cecil.dll` and a `Setup\` folder. None of that is
needed now: Cecil is inside the single file, and the setup payload is embedded in it, so the release is
exactly the two published files and there is nothing beside them to keep, lose, or check.

**The mods cannot use this workflow.** They compile against the game's own assemblies, which a hosted
runner cannot have, so their releases are cut by hand on a machine with the game — build, package both
DLLs, `gh release create`. There is no self-hosted runner, and deliberately so: a self-hosted job with
no runner registered does not fail, it queues and sits for 24 hours. See
[RELEASING.md](RELEASING.md).

---

## Security

Please do not publish suspected vulnerabilities or private game data in a public issue, and do not paste
`BepInEx\LogOutput.log` contents in public: mods in this ecosystem configure API keys with it.

This tool runs the setup and moves files inside your game install. Both are deliberate and both are
visible — the Setup tab streams the run's own log rather than summarising it — but install only releases
you trust.

The setup it runs is compiled into the download rather than fetched at runtime, so what runs is what was
reviewed and released. The payload it seeds — the Mono class libraries (MIT) and Doorstop and BepInEx
(LGPL-2.1) — is embedded in that same file, and the licence texts for it live in the
[Setup](https://github.com/swyf-modding/Setup) repository it comes from.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

[`vendor/Mono.Cecil.dll`](vendor/README.md) is a third-party binary under its own MIT licence, and is not
covered by this one. The build uses the Mono.Cecil NuGet package rather than this copy, but the file is
kept in the repository. The corlib and Doorstop payload embedded in the executable is redistributed
unmodified from the [Setup](https://github.com/swyf-modding/Setup) repository under its own licence,
together with the licence texts for the Mono class libraries and Doorstop binaries it carries.

---

## Related Projects

| Project | What it is |
|---|---|
| [Setup](https://github.com/swyf-modding/Setup) | The setup logic the Setup tab runs, as `ScamWYF.Setup.Core`: BepInEx, the corlib override, the vtable patches |
| [mod-lib](https://github.com/swyf-modding/mod-lib) | Shared library: base class, menu, config editor, hot reload, patch coordinator |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab — turn mods off without leaving a session |
| [AI-Backend](https://github.com/swyf-modding/AI-Backend) | Routes the game's AI calls to your own LLM |
