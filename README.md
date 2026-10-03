<div align="center">

# ScamWYF.Launcher

![license](https://img.shields.io/badge/license-MIT-blue)
![version](https://img.shields.io/github/v/release/swyf-modding/Launcher?label=version)
![game build](https://img.shields.io/badge/game-v82--playtest-blue)
![framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue)

*Built with:*

![C#](https://img.shields.io/badge/C%23-512BD4?style=for-the-badge&logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![WinForms](https://img.shields.io/badge/WinForms-512BD4?style=for-the-badge)
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
.\build.ps1
.\build.ps1 -NoCopy        # build without installing
.\build.ps1 -Cecil C:\path\to\Mono.Cecil.dll
.\build.ps1 -Version 1.2.3 # stamp a version rather than reading the tag
```

Roslyn directly, like the other projects here: no NuGet, no `.csproj`, no restore.

**This is a plain .NET Framework WinForms app, and that is a deliberate difference from the mods.** The
mods compile against the game's own assemblies; this compiles against the .NET Framework 4.8 reference
assemblies and needs no game install at all. A tool whose job is to repair a broken game install cannot
share a dependency with the thing it repairs.

The one external dependency is [Mono.Cecil](vendor/README.md), staged in `vendor\` so the build needs
neither network nor game, and pinned rather than floating so a release cannot ship against a different
Cecil than the one it was tested with.

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
.\build.ps1
```

Then check the three things a build alone would not: that it compiles, that Mono.Cecil was copied next to
the exe, and that it starts. That last one matters because a WinForms app that throws in its constructor
exits immediately — surviving a few seconds *is* the assertion, and it is what the CI workflow does.

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
| Target framework | .NET Framework 4.8 (WinForms) |
| C# language level | `7.3` |
| Mono.Cecil | `0.11.6`, `net40` |
| Platform | Windows x64 |

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
src/
|-- App.cs             The window, and launching the game
|-- GameInstall.cs     Finding and probing an install
|-- SetupRunner.cs     Running setup.ps1 and streaming its output
|-- SetupTab.cs        The Setup tab
|-- ModManager.cs      Enable, disable, delete - the file operations
|-- PluginScanner.cs   Reading [BepInPlugin] metadata with Cecil
|-- PluginEntry.cs     One mod on disk
|-- ModsTab.cs         The Mods tab
`-- LogTab.cs          The Log tab
build.ps1              Compiles with raw csc
vendor/                Mono.Cecil, staged and pinned
```

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
