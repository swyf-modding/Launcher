<div align="center">

# ScamWYF.Launcher

![license](https://img.shields.io/badge/license-MIT-blue)
![version](https://img.shields.io/github/v/release/swyf-modding/Launcher?label=version)
![game build](https://img.shields.io/badge/game-v82--playtest-blue)
![framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue)

*Built with:*

![C#](https://img.shields.io/badge/C%23-512BD4?style=for-the-badge&logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-512BD4?style=for-the-badge)
![Mono.Cecil](https://img.shields.io/badge/Mono.Cecil-0.11.6-9a3d3d?style=for-the-badge)

</div>

---

## Table of Contents

- [Overview](#overview)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Developer Setup](#developer-setup)
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

**ScamWYF.Launcher** is an unofficial, community-built Windows launcher and mod manager for **Scam With
Your Friends**. Three tabs: fix the install, manage mods, read the log.

**Key Features:**

- **Diagnoses a partial install.** Reports the game, loader, corlib override, its wiring and BepInEx as
  separate checks, because a half-installed game is the case that matters and "not installed" is not a
  useful answer to it
- **Runs the real setup script.** Delegates to [Setup](https://github.com/swyf-modding/Setup)'s `setup.ps1` and streams its
  output live, rather than reimplementing it in a button handler
- **Lists and manages mods.** Every dll in `plugins` and `plugins_disabled`, read from `[BepInPlugin]`
  metadata, with enable, disable and delete
- **Shows the log where you can act on it.** `LogOutput.log` with the interesting lines picked out, and
  optional live following while the game runs
- No redistributed game DLLs, decompiled game source, telemetry, or credential collection

> [!IMPORTANT]
> The launcher **never writes to your game folder without running the setup script**. Everything on the
> Setup tab is that script's own doing; everything on the Mods tab is a move or a delete between two
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
- Windows x64 with .NET Framework 4.8 or later — preinstalled on Windows 10 and 11, and **all the
  launcher needs**. No game-side runtime, no Mono, no .NET SDK to run it
- [Setup](https://github.com/swyf-modding/Setup) alongside it, for the Setup tab

### Installation

1. Clone [Setup](https://github.com/swyf-modding/Setup) next to the launcher, so its scripts and vendored binaries are found:

   ```text
   Launcher\bin\ScamWYF.Launcher.exe
   Setup\setup.ps1
   ```

2. Run `ScamWYF.Launcher.exe`.

There is nothing to install. `bin\` holds two files and both are yours to move wherever you like.

### Developer Setup

```powershell
git clone https://github.com/swyf-modding/Launcher.git
cd Launcher
dotnet build -c Release    # a complete build on its own
.\build.ps1                # the same, plus tag versioning and a two-file bin\
.\build.ps1 -Cecil C:\path\to\Mono.Cecil.dll
.\build.ps1 -Version 1.2.3 # stamp a version rather than reading the tag
```

An SDK-style project — `Launcher.csproj` — like the mods. No NuGet packages and no restore step: the only
external dependency is [Mono.Cecil](vendor/README.md), staged in `vendor\` and referenced by path, so the
build needs neither network nor game.

`build.ps1` is a thin wrapper around `dotnet build`. It adds the two things a plain build cannot do —
stamping the version from the nearest git tag, and reducing `bin\` to the two published files — and
`dotnet build -c Release` remains a complete, equivalent command. It used to compile Roslyn by hand with
no project file; that stopped being sensible when the UI became WPF, because XAML has to be compiled by
MSBuild's markup compiler, so a `.csproj` arrived regardless and the hand-rolled `csc` line became a
second description of the same build that could disagree with the first.

**This targets the .NET Framework 4.8 reference assemblies and needs no game install at all.** The mods
compile against the game's own assemblies; this does not. A tool whose job is to repair a broken game
install cannot share a dependency with the thing it repairs.

net48 rather than a modern .NET is deliberate. A `net8.0`/`net10.0` build would be framework-dependent
too, but on a runtime that is not present by default, so a player whose game install is already broken
would have to install something before this would start. .NET Framework 4.8 ships with every Windows 10
and 11, so the download stays two files and runs on a clean machine.

The build is deterministic: the same commit compiles to the same bytes, which is what makes it worth
saying afterwards exactly which commit a published binary came from.

#### Versioning

`build.ps1` reads the version from the nearest git tag and stamps it into the assembly. Nothing to edit
by hand, so the number cannot drift from the code:

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

### Usage

**Setup** probes the install and runs the setup script.

| Check | Why it is separate |
|---|---|
| Game install | `Scam With Your Friends_Data\Managed` has to exist |
| Loader (Doorstop) | the loader half, which starts everything |
| Corlib override | the usual cause of "it crashed" — the shipped `mscorlib` is stripped and BepInEx cannot start without it |
| Override wired up | `doorstop_config.ini` has to point at it |
| BepInEx | downloaded from GitHub, or already there |

**Set up / repair** runs `setup.ps1` and streams its output into the window as it goes. **Offline** is
there for when BepInEx has already been downloaded, or there is no network. The tab says where to get
`Setup` if it is not next to the exe.

**Mods** lists every dll in both folders with **Enable**, **Disable** and **Delete**. BepInEx has no
enable/disable of its own, so a toggle is a file move between the two folders — the same thing a person
would do by hand. Changes take effect on the next launch.

A dll with no `[BepInPlugin]` is listed and marked, not hidden. The shared library is one, and seeing
"unrecognised" for it would reasonably suggest something was broken.

**Log** shows `BepInEx\LogOutput.log` with the lines that matter picked out above the raw text. A
preloader crash, before BepInEx starts, goes to `preloader_*.log` in the game folder instead, and the tab
says so.

**Play** starts the game. Passing a path as the first argument overrides where it is looked for.

### Testing

```powershell
.\tools\test-getmods.ps1            # 73 assertions, offline
.\tools\test-getmods.ps1 -Online    # 20 more, against the real GitHub API
```

```powershell
dotnet run --project tests\Launcher.Tests.csproj -c Release
```

is an equivalent command for the offline run. The two suites are separate programs rather than one with a
filter, and both compile the app's own sources rather than referencing the built launcher — so the tests
cannot pass against a stale copy of the logic.

Then check the three things a build alone would not: that it compiles, that Mono.Cecil was copied next to
the exe, and that it starts **and creates a window**. That last part matters more in WPF than it did in
WinForms. WPF resolves styles, templates and pack URIs at load time from compiled XAML, so a mistake in a
`.xaml` file is not a compile error — the build stays green and the window fails to appear when it opens.
Requiring a real window handle is what catches that, and it is why CI does this rather than leaving it to
be noticed.

#### What CI checks that a build cannot

Three of them, and all three exist because of how WPF fails:

| Check | Catches |
|---|---|
| Window handle after launch | XAML that compiles but will not load — a `StaticResource` that does not resolve, a bad `TargetName` |
| A `Color` key assigned to a brush-typed property | A trigger whose `Setter` has the wrong value type. It throws **only when the trigger fires**, so on hover, on a scrollbar thumb, or when a control is disabled |
| Hover every button on every tab | The same class of fault by any other route, and any other trigger that throws on interaction |
| Run a script through the Setup tab | Touching a control from one of the threads the app does not own |

The last two of those exist because of how WPF fails. A screenshot cannot hover anything, and a build
cannot evaluate a trigger — so a green build once shipped a launcher that threw
`InvalidOperationException` the moment anyone moved the mouse over a button.

The script run is the nastier of the two, and worth spelling out. Running `setup.ps1` is the only thing
this app does on threads it does not control: each output line arrives on the process's async reader and
the exit code on a worker thread. Reading a `DependencyProperty` from either throws — and an unhandled
exception on a thread-pool thread does not raise a dialog, it **ends the process**. That is not
hypothetical: reading one checkbox on the way past killed the launcher on every single setup run. CI now
runs a script through the tab, against a stub rather than the real one (the point is the threading, and the
real script writes to the game install), and fails if the process does not survive it.

The hover pass is best-effort and does not pretend otherwise: it depends on the cursor reaching the
window, so it warns rather than fails if it managed to hover nothing. The colour/brush check and the
script run are the deterministic ones.

Not covered by tests, and honestly so: layout and the appearance of each tab. What is covered is that the
window builds, that its resources resolve, that interacting with it does not throw, and that the one code
path which crosses threads still works.

Verified against a real install: the probes report `ready`, both installed mods appear with their real
names and versions, and the log tab picks the BepInEx channel lines out of the raw text.

No game DLL belongs in this repository or in a GitHub release.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `v82-playtest` |
| Unity | `6000.3.10f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| Target framework | .NET Framework 4.8 (WPF) |
| C# language level | `7.3` |
| Mono.Cecil | `0.11.6`, `net40` |
| Platform | Windows x64 |

The C# language level is `7.3` and matches the mods. It is old for WPF, and that is accepted rather than
fixed here: changing the language level is a separate decision from changing the UI framework, and mixing
the two makes the port harder to review than it needs to be.

Other game builds are untested. The launcher probes what it finds rather than assuming a layout, so an
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
Launcher.csproj        The project. net48 + WPF, and the only external reference is vendor\Mono.Cecil.dll
src/
|-- App.xaml(.cs)      Entry point, and the unhandled-exception reporter
|-- MainWindow.xaml    The four tabs, the status line, the Play button
|-- MainWindow.xaml.cs  Also the host the views talk to: status, busy flag, re-read both mod tabs
|-- Theme.xaml         The palette and every control style - see "How it looks"
|-- Dialogs.cs         Modal confirmations, themed; WPF's MessageBox cannot be
|-- GameInstall.cs     Finding and probing an install
|-- SetupRunner.cs     Running setup.ps1 and streaming its output
|-- ModManager.cs      Enable, disable, delete - the file operations
|-- PluginScanner.cs   Reading [BepInPlugin] metadata with Cecil
|-- PluginEntry.cs     One mod on disk
|-- Http.cs            The only code that touches the network: https only, redirects checked by hand
|-- ModCatalogue.cs    The known mods, and what GitHub says their latest release is
|-- ModInstaller.cs    Download, unpack, verify, and only then install
`-- views/
    |-- SetupView      The Setup tab
    |-- ModsView       The Mods tab
    |-- GetModsView    The Get mods tab
    `-- LogView        The Log tab
build.ps1              dotnet build, plus tag versioning and a two-file bin\
tools/
`-- test-getmods.ps1   Tests for the above: -Online also exercises a real download
tests/                 Launcher.Tests.csproj, GetModsTests.cs (offline), OnlineModTests.cs (needs network)
vendor/                Mono.Cecil, staged and pinned
```

The split between `src\` and `src\views\` is the UI-free half against the UI. Everything the tests compile
is the former, which is why `tests\Launcher.Tests.csproj` can build a test binary with no WPF at all.

### How it looks

Dark, low-chroma, and the same palette the in-game menu uses — mod-lib's `UiTheme`, which reads the game's
live colours and falls back to these. So the launcher and the menu it opens are one system rather than
two, and there is one file to change rather than a set of greys scattered through four tabs.

Everything visual is a `Style` or a `ControlTemplate` in `Theme.xaml`. That is the whole reason for the
move off WinForms, which needed three subclassed controls purely to draw chrome the framework had already
coloured from system brushes — setting `BackColor` on a stock control does nothing to chrome it draws
itself, which is most of what "looks like a 1998 app" actually is. Scrollbars were the worst of it and
could not be themed at all; here they are a template, which is also how the last light thing in the window
was dealt with.

One consequence worth knowing before editing a view: the column tables are **not** `GridView`.
`GridViewColumn.Width` is converted by a plain double converter that has no idea what `*` means, so the
one thing these tables need — a last column that fills the remaining width — cannot be expressed, and
asking for it fails at load with `'*' string cannot be converted to Length`. Each table is a header `Grid`
plus an `ItemTemplate` whose columns are tied together by `SharedSizeGroup`, which does support star
sizing. Horizontal scrolling is off, so a header that stays put while the rows scroll looks identical to
one that moves.

The window is 1060x720 because the content needs it. A row of checks with a detail column is not readable
at 940x640, and the answer to that is not a narrower column.

---

## Installing Mods

**Get mods** is a separate tab from **Mods** on purpose. The Mods tab is bookkeeping over files already
on disk and never touches the network. This one downloads code that the game will execute, so it says
what it is doing and asks before it does anything.

**From the list.** *Check for updates* asks GitHub for the latest release of each project and shows the
tag, the size, and whether it is already installed. *Install* downloads the release's zip, unpacks it,
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
| A zip entry named `../something` | Zip-slip. Every entry's resolved path is checked before it is written, so an archive cannot escape the temp folder |
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

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request, on a hosted
Windows runner: build, start the exe and check it survives, then upload `bin\`.

That is all possible because of the two decisions above — no game needed to build, and Cecil staged
in-repo. The mods' workflows are two-tiered precisely because they do need the game; see
[mod-lib's](https://github.com/swyf-modding/mod-lib#continuous-builds) for what that looks like.

---

## Releases

Publishing is one command, because this is the one repo here that needs no game to build:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

[`.github/workflows/release.yml`](.github/workflows/release.yml) builds the tagged commit, packages
`bin\` into `ScamWYF.Launcher-v1.2.3.zip` and publishes it as a GitHub release with generated notes.

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

The zip holds both files — the exe and `Mono.Cecil.dll` beside it. Shipping one without the other
gives an exe that dies on startup with a `FileNotFoundException`, so the release step checks for both.

**The mods cannot use this workflow.** They compile against the game's own assemblies, which a hosted
runner cannot have, so their releases are cut by hand on a machine with the game — build, package both
DLLs, `gh release create`. There is no self-hosted runner, and deliberately so: a self-hosted job with
no runner registered does not fail, it queues and sits for 24 hours. See
[RELEASING.md](RELEASING.md).

The mods are cut by hand instead — build, package both DLLs, `gh release create`. The full procedure
for all five repositories, including the submodule ordering that has to be right, is in
[RELEASING.md](RELEASING.md).

---

## Security

Please do not publish suspected vulnerabilities or private game data in a public issue, and do not paste
`BepInEx\LogOutput.log` contents in public: mods in this ecosystem configure API keys with it.

This tool runs a PowerShell script and moves files inside your game install. Both are deliberate and
both are visible — the Setup tab streams the script's own output rather than summarising it — but
install only releases you trust.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

[`vendor\Mono.Cecil.dll`](vendor/README.md) is a third-party binary under its own MIT licence, and is not
covered by this one.

---

## Related Projects

| Project | What it is |
|---|---|
| [Setup](https://github.com/swyf-modding/Setup) | The scripts the Setup tab runs: BepInEx, the corlib override, the vtable patches |
| [mod-lib](https://github.com/swyf-modding/mod-lib) | Shared library: base class, menu, config editor, hot reload, patch coordinator |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab — turn mods off without leaving a session |
| [AI-Backend](https://github.com/swyf-modding/AI-Backend) | Routes the game's AI calls to your own LLM |
