# Releasing

How a release is cut across these five repositories, with the `gh` CLI.

## The shape of it

| Repository | Has releases? | Why |
|---|---|---|
| **Launcher** | yes, automated | Builds on a hosted runner — no game needed. Tag it and CI publishes. |
| **Mod-Handler** | yes, manual | Compiles against the game's own assemblies, so it builds only on a machine that has the game. |
| **AI-Backend** | yes, manual | Same. |
| **mod-lib** | no | A library, not something anyone installs. It has tags, because the mods resolve their versions from *their own* repo, but no release artefacts. |
| **Setup** | no | Scripts, not binaries. A tag is enough; GitHub's source zip is the artefact. |

The rule behind the split: **a repository gets a release if it produces a file somebody downloads.**
mod-lib is consumed as a submodule and Setup as a clone, so there is nothing to attach.

## Order matters

`mod-lib` is a submodule of both mods, and the version stamping lives in it. So:

```
mod-lib  →  Mod-Handler  →  AI-Backend
```

Publishing a mod before `mod-lib` is on the remote gives you a repo whose `build.ps1` dot-sources a
`Version.ps1` that is not in the pinned submodule, and the failure is
`The term 'Version.ps1' is not recognized as the name of a cmdlet` — which does not obviously mean
"bump your submodule".

## Cutting a Launcher release

Completely automated. From a clean tree:

```powershell
cd Launcher
.\build.ps1                        # what CI will do; catch it before the tag
.\tools\test-getmods.ps1          # the offline assertions
git tag v1.2.3
git push origin v1.2.3
```

`release.yml` then builds the tagged commit on a hosted runner and publishes
`ScamWYF.Launcher-v1.2.3.zip`. Watch it with:

```powershell
gh run watch --repo swyf-modding/Launcher
gh release view v1.2.3 --repo swyf-modding/Launcher
```

Three gates stand between the tag and the published binary, because a wrong version in a release is
worse than no release:

- the tag must be on the commit that was built (`git describe` has to report the tag)
- the tree must be clean — a build with uncommitted changes in it is refused
- the binary's stamped version must equal the tag, and must name a commit

If a gate fails, nothing is published. That is deliberate: fix the tag and re-push rather than
uploading a binary by hand.

To check a tag exists and points where you expect before pushing it:

```powershell
gh api repos/swyf-modding/Launcher/git/ref/tags/v1.2.3
```

## Cutting a mod release

Manual, because the build has to happen where the game is installed.

**1. Make sure `mod-lib` is published first**, if it changed:

```powershell
cd mod-lib
.\build.ps1 -LibraryOnly -Test
git tag v1.4.0
git push origin v1.4.0
```

Then in **each** mod, bump the pointer — this is the step that is easy to forget:

```powershell
cd ..\Mod-Handler
git submodule update --remote
git add vendor/ScamWYF.Modding.Core
git commit -m "Bump the shared library for v1.4.0"
git push origin main
```

**2. Tag and build the mod.** Tag first: `build.ps1` reads the nearest tag to stamp the version, so
tagging after building gives you a binary that reports `untagged`.

```powershell
cd ..\Mod-Handler
git tag v1.2.0
.\build.ps1
```

`-Test` matters here. It runs the API surface check, which catches a mod calling a library member
that no longer exists — the failure a compile will *not* catch, because the reference is to the DLL it
just built.

**3. Confirm the version before you publish it.** This is the check that catches a mistyped tag:

```powershell
Get-Item bin\ScamWYF.ModHandler.dll | % {
  [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName) |
    Select-Object FileVersion, ProductVersion
}
```

`FileVersion` must be the numeric part of the tag and `ProductVersion` must be the tag plus a commit.
If they say `0.0.0+untagged`, the tag did not exist when you built.

**4. Package both DLLs.** The library is not optional:

```powershell
Compress-Archive `
  -Path bin\ScamWYF.ModHandler.dll, vendor\ScamWYF.Modding.Core\bin\ScamWYF.Modding.Core.dll `
  -DestinationPath ScamWYF.ModHandler-v1.2.0.zip
```

BepInEx loads `ScamWYF.Modding.Core.dll` from `core\` and the mod from `plugins\`. A zip with only
the mod produces an install that loads nothing and explains nothing. Keep the `.pdb` files out of it.

**5. Publish:**

```powershell
gh release create v1.2.0 ScamWYF.ModHandler-v1.2.0.zip `
  --repo swyf-modding/Mod-Handler `
  --title "v1.2.0" `
  --generate-notes
```

Then verify, because `gh release create` succeeds if the upload worked and tells you nothing about
whether the contents are right:

```powershell
gh release view v1.2.0 --repo swyf-modding/Mod-Handler
gh release download v1.2.0 --repo swyf-modding/Mod-Handler --dir .\check
Get-ChildItem .\check
```

For **AI-Backend**, substitute `ScamWYF.AiBackend.dll` and `ScamWYF.AiBackend-v1.2.0.zip`.

## Versioning, in one paragraph

The version comes from the nearest git tag, stamped at build time — `AssemblyVersion` numerically
because the CLR rejects a prerelease there, `AssemblyInformationalVersion` with the tag, the prerelease
name and the commit. Nothing is edited by hand. A commit past the tag adds `+3.g<sha>`, an uncommitted
tree is marked `.dirty`, and an untagged tree says `0.0.0+untagged.g<sha>` rather than claiming to be
1.0.0. Use `-Version x.y.z` only to reproduce an old build or to build from a source archive with no
`.git`.

## If something goes wrong

A bad release can be taken down while keeping the tag:

```powershell
gh release delete v1.2.0 --repo swyf-modding/Mod-Handler --yes
```

Deleting a GitHub release does **not** delete the tag, so the commit stays reachable. Do not move a
published tag to a different commit — delete the release, tag the right commit, and cut a new version.

To see everything published so far:

```powershell
gh release list --repo swyf-modding/Mod-Handler
gh api repos/swyf-modding/Mod-Handler/tags
```

## Why there is no CI job that builds the mods

Worth writing down, because it looks like an omission otherwise.

A hosted runner cannot compile a mod: the build needs the game's own assemblies, and the game is not ours
to redistribute. The obvious alternative is a self-hosted runner on a machine that has the game. There
isn't one, and that is a decision rather than a gap — a self-hosted job with no runner registered does
not *fail*, it queues, and GitHub cancels it 24 hours later. Every push to `main` left a job stuck for a
day and the run red. Narrowing it to `workflow_dispatch` only moved the problem: the "Run workflow"
button in the Actions tab then did exactly the same thing, on demand.

So the mods' CI runs only what a hosted runner can do — the shared library's behaviour tests — and the
build, the API surface check and the release are manual, against your own install. That is one command:

```powershell
.\build.ps1
```

If you ever do register a runner and want the compile automated, it is a small job: `runs-on:
self-hosted`, the same steps as `build.ps1` and `tools\test-getmods.ps1`, and `fetch-depth: 0` so the tag is present
for the version stamping. Set `SWYG_GAME_DIR` on the runner so it does not depend on where Steam put
things. Keep the `if:` excluding `pull_request` — a fork's pull request would otherwise run code from
the fork on your machine.