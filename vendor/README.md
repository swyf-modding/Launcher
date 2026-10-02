Mono.Cecil 0.11.6, the `lib\net40` build, taken unmodified from

    https://www.nuget.org/packages/Mono.Cecil/0.11.6

Mono.Cecil is MIT licensed; the licence expression is in the package's nuspec, and the
project is at http://github.com/jbevain/cecil.

## Why it is here

The launcher reads BepInEx plugin metadata with Cecil rather than `Assembly.Load`, which
matters: loading a mod runs its static constructors, locks the file so it cannot then be
deleted, and throws on a mod built against a different BepInEx instead of reporting its
name.

It is committed rather than downloaded at build time so that:

- `.\build.ps1` works on a machine with no game installed, which is the case for anyone
  building a continuous release;
- the version cannot drift under a build, since a floating "latest" would let a release
  ship against a different Cecil than the one it was tested with.

`build.ps1` prefers this copy, then the game's `BepInEx\core`, then whatever `-Cecil`
names. The game's own copy is 0.10.4; both work, and which one is used is printed.

## Updating

    # from the nupkg, which is a zip
    expand mono.cecil.<version>.nupkg -DestinationPath mono.cecil
    copy mono.cecil\lib\net40\Mono.Cecil.dll vendor\Mono.Cecil.dll

Bump the version in that comment at the same time.
