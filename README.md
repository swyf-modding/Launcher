# ScamWYF.Launcher

A launcher and mod manager for **Scam With Your Friends**. Three tabs: fix the install, manage mods,
read the log.

```powershell
git clone https://github.com/swyf-modding/Launcher.git
cd Launcher
.\build.ps1
.\bin\ScamWYF.Launcher.exe
```

It finds the game by itself. Pass a path as the first argument if it cannot:

```powershell
.\bin\ScamWYF.Launcher.exe "D:\SteamLibrary\steamapps\common\Scam With Your Friends"
```

---

## What it does

### Setup

Probes the install and reports each piece separately, because a partly-installed game is the case
that matters:

| Check | Why it is separate |
|---|---|
| Game install | `Scam With Your Friends_Data\Managed` has to exist |
| Loader (Doorstop) | the loader half, which starts everything |
| Corlib override | **the usual cause of "it crashed"** — the shipped `mscorlib` is stripped and BepInEx cannot start without the override |
| Override wired up | `doorstop_config.ini` has to point at it |
| BepInEx | downloaded from GitHub, or already there |

**Set up / repair** runs [`scam-wyf-setup`](../Setup)'s `setup.ps1` and streams its output into the
window as it goes. That script is the engine; this tab is plumbing around it. The scripts stay
separate on purpose — they are idempotent, CI runs them, and their logic is Cecil work with real edge
cases that has no business living in a button handler.

The tab tells you where to get `Setup` if it is not next to the exe.

### Mods

Lists every `.dll` in `BepInEx\plugins` and `BepInEx\plugins_disabled`, reading each one's
`[BepInPlugin]` attribute for its name, version and guid. **Enable**, **disable** and **delete**.

BepInEx has no enable/disable of its own, so a toggle is a file move between the two folders — the same
thing a person would do by hand. Changes take effect on the next launch.

A dll with no `[BepInPlugin]` is listed and marked, not hidden. The shared library is one, and seeing
"unrecognised" for it would reasonably suggest something was broken.

Delete asks for confirmation and has no undo. That asymmetry is deliberate: a delete that quietly kept
a copy would grow without bound.

### Log

Shows `BepInEx\LogOutput.log`, with the lines that matter picked out above the raw text. A BepInEx log
is thousands of lines of which the useful part is three, and scrolling for them is the whole problem.
**Follow while the game runs** polls the file once a second.

A preloader crash, before BepInEx starts, goes to `preloader_*.log` instead — the tab says so.

---

## How it is built

```powershell
.\build.ps1
.\build.ps1 -CscDll C:\path\to\csc.dll
.\build.ps1 -Cecil "C:\path\to\Mono.Cecil.dll"
```

Roslyn directly, like the other repos here: no NuGet, no `.csproj`, no restore.

**It is a plain .NET Framework WinForms app, and that is a deliberate difference from the mods.** The
mods compile against the game's own assemblies; this compiles against the .NET Framework 4.8 reference
assemblies and runs on any Windows with 4.x — no game install, no BepInEx, no Mono. A tool whose job is
to repair a broken game install cannot share a dependency with the thing it repairs. It also means
`build.ps1` works on a CI runner, which is why [the workflow](.github/workflows/ci.yml) needs nothing
special.

The one external dependency is `Mono.Cecil`, staged in [`vendor\`](vendor/README.md) so the build needs
no network and no game; the game's own copy is used if there is one. Reading plugin metadata with Cecil
rather than `Assembly.Load` is load-bearing:

- loading a mod runs its static constructors, and this tool exists partly to delete mods;
- a loaded assembly is locked, so the file could not then be moved or deleted;
- a mod built against a different BepInEx would throw on load instead of simply telling you its name.

C# 7.3, two files in `bin\`, nothing to install.

## Continuous builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request, on a
hosted Windows runner: build, then start the exe and check it survives, then upload `bin\`.

That is all possible because of the two decisions above — no game needed to build, and Cecil staged
in-repo. The mods' workflows are two-tiered precisely because they do need the game; see
[mod-lib's](https://github.com/swyf-modding/mod-lib/blob/main/.github/workflows/ci.yml) for what that
looks like.

---

## Notes

**An explicit path is never second-guessed.** Passing a folder — as an argument or through
`SWYG_GAME_DIR` — means exactly that folder. It is not quietly replaced by a Steam location that
happens to exist, because a typo in a path should say "that folder is not a game install" rather than
succeed somewhere else. Steam is only searched when nothing was specified.

**Mods cannot be changed while the game is running.** BepInEx does not release plugin assemblies when
it shuts down, so the files stay locked. The buttons disable themselves and the note column says why,
rather than failing later with a message about a file being in use.

**A name collision is refused, not resolved.** Two mods with the same file name is a real situation — a
downgrade, or an extract into the wrong folder. Picking a winner silently would hide it. Delete is the
exception: it has no destination folder, so it works even when a same-named file is enabled in the
other one, which is what cleaning up after a downgrade needs.

**The game is started from its exe, not through Steam.** That needs no protocol handler, so it works
whether or not the Steam client is up. If launching through Steam turns out to matter, it belongs in a
setting rather than being guessed at.

## Related

| Repo | |
|---|---|
| [Setup](../Setup) | the scripts this tool drives |
| [mod-lib](../mod-lib) | shared library: base class, patches, hotkeys, the in-game menu |
| [Mod-Handler](../Mod-Handler) | in-game Plugins tab, so mods can be turned off without leaving the game |
| [AI-Backend](../AI-Backend) | routes the game's AI calls to your own LLM |
