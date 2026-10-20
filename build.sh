#!/usr/bin/env bash
#
# Builds Switchboard: one self-contained single file per platform, into dist/.
#
#   ./build.sh
#   ./build.sh --configuration Debug
#   ./build.sh --version 1.2.3
#   ./build.sh --runtime linux-x64      # just one platform
#
# What this adds over `dotnet publish`
# ------------------------------------
#   dotnet publish Switchboard.csproj -c Release -r linux-x64 --self-contained true \
#       -p:PublishSingleFile=true
#
# is a complete, equivalent command for one platform's file. What this script adds is the version
# stamped from the nearest git tag, both platforms' files in one run, and one predictable dist/ layout
# for a release to zip: dist/linux-x64/Switchboard and dist/win-x64/Switchboard.exe.
#
# There is no Setup folder beside the exe any more. The Setup tab runs the setup in-process, through
# ScamWYF.Setup.Core, and the 12 MB of corlib and Doorstop it needs is compiled into the executable as
# embedded resources, so the download really is one file. That is why nothing from Setup ships beside
# the executable - the Setup *source* is still needed to compile, through the project reference, but not
# to run.
#
# Version
# -------
# Taken from `git describe` and stamped into the assembly as AssemblyVersion (numeric) and
# AssemblyInformationalVersion (the full string, which the window title shows). Nothing has to be
# edited by hand, and no Setup checkout is consulted - the previously bundled SETUP-VERSION.txt is gone
# with the bundled scripts.
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="Release"
explicit_version=""
runtimes=()

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
    case "$1" in
        -c|--configuration) configuration="${2:?--configuration needs a value}"; shift 2 ;;
        -v|--version)       explicit_version="${2:?--version needs a value}"; shift 2 ;;
        -r|--runtime)       runtimes+=("${2:?--runtime needs a value}"); shift 2 ;;
        -h|--help)          usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; echo "try --help" >&2; exit 2 ;;
    esac
done

if [ "${#runtimes[@]}" -eq 0 ]; then
    runtimes=(linux-x64 win-x64)
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH. The switchboard is built with the .NET SDK:" >&2
    echo "  https://dotnet.microsoft.com/download" >&2
    exit 1
fi

# ---------------------------------------------------------------- version
#
# The same shape build.ps1 stamps, so a build made here and one made there are indistinguishable:
#
#   v1.2.3              ->  numeric 1.2.3          informational 1.2.3+g0a1b2c3
#   v1.2.3-4-g0a1b2c3   ->  numeric 1.2.3          informational 1.2.3+4.g0a1b2c3
#   v1.2.3-rc1          ->  numeric 1.2.3          informational 1.2.3-rc1+g0a1b2c3
#   (no tag)            ->  numeric 0.0.0          informational 0.0.0+untagged.g0a1b2c3
#
# AssemblyVersion has to be numeric because the CLR rejects a prerelease there, so a prerelease keeps
# its numeric part and carries the rest in AssemblyInformationalVersion, which is what a person sees.

convert_from_describe() {
    local describe="$1"
    local sha="${2:-}"
    local dirty=""

    [ -n "$describe" ] || describe="0.0.0"

    case "$describe" in
        *-dirty) dirty="dirty"; describe="${describe%-dirty}" ;;
    esac

    # "-<commits>-g<sha>" from the right first, because a prerelease tag can contain a dash of its own
    # ("v1.1.0-beta.2-1-g9925471"), and splitting on the first dash would lose "beta.2".
    local commits=""
    if [[ "$describe" =~ ^(.*)-([0-9]+)-g([0-9a-fA-F]+)$ ]]; then
        describe="${BASH_REMATCH[1]}"
        commits="${BASH_REMATCH[2]}"
        [ -n "$sha" ] || sha="${BASH_REMATCH[3]}"
    fi

    local tag="$describe" prerelease=""
    if [[ "$tag" == *-* ]]; then
        prerelease="${tag#*-}"
        tag="${tag%%-*}"
    fi
    tag="${tag#v}"
    tag="${tag#V}"

    local numeric=""
    if [[ "$tag" =~ ^[0-9]+(\.[0-9]+){0,3}$ ]]; then
        numeric="$tag"
    fi

    local untagged=""
    if [ -z "$numeric" ]; then
        untagged="untagged"
        numeric="0.0.0"
    fi

    # Pad to three components, which is what AssemblyVersion wants.
    while [ "$(awk -F. '{print NF}' <<<"$numeric")" -lt 3 ]; do
        numeric="$numeric.0"
    done

    local meta=""
    local part
    for part in "$untagged" "$commits" "${sha:+g$sha}" "$dirty"; do
        [ -n "$part" ] || continue
        meta="${meta:+$meta.}${part}"
    done

    local informational="$numeric"
    [ -n "$prerelease" ] && informational="$informational-$prerelease"
    [ -n "$meta" ] && informational="$informational+$meta"

    VERSION_NUMERIC="$numeric"
    VERSION_INFORMATIONAL="$informational"
    VERSION_DIRTY="$dirty"
}

if [ -n "$explicit_version" ]; then
    convert_from_describe "$explicit_version" ""
elif command -v git >/dev/null 2>&1 && git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1; then
    describe="$(git -C "$repo_root" describe --tags --dirty --always 2>/dev/null || true)"
    sha="$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || true)"
    convert_from_describe "$describe" "$sha"
else
    echo "  note: not a git checkout (or no git), so the version cannot be read from a tag."
    echo "        Pass --version to stamp one; using 0.0.0."
    convert_from_describe "0.0.0" ""
fi

if [ -n "$VERSION_DIRTY" ]; then
    echo "  note: the working tree has uncommitted changes, so this is not a clean release build."
fi

# ---------------------------------------------------------------- build

echo "=== building Switchboard"
echo "  sdk:      $(dotnet --version)"
echo "  config:   $configuration"
echo "  version:  $VERSION_INFORMATIONAL"
echo "  runtimes: ${runtimes[*]}"

project="$repo_root/Switchboard.csproj"
dist="$repo_root/dist"

for rid in "${runtimes[@]}"; do
    out="$dist/$rid"
    echo ""
    echo "--- publishing $rid -> $out"

    # Remove first rather than publish over. A stale file from an earlier build would otherwise
    # survive into dist/ and into anything that zips it, and a single-file publish is supposed to
    # leave exactly one thing behind.
    rm -rf "$out"

    dotnet publish "$project" \
        --configuration "$configuration" \
        --runtime "$rid" \
        --self-contained true \
        --nologo \
        --verbosity quiet \
        --output "$out" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none \
        -p:AssemblyVersion="$VERSION_NUMERIC" \
        -p:FileVersion="$VERSION_NUMERIC" \
        -p:InformationalVersion="$VERSION_INFORMATIONAL"
done

# ---------------------------------------------------------------- report

if [ -t 1 ]; then green=$'\033[32m'; off=$'\033[0m'; else green=""; off=""; fi

echo ""
echo "  Built:"
for rid in "${runtimes[@]}"; do
    out="$dist/$rid"
    if [ ! -d "$out" ]; then
        echo "    $out is missing - the publish reported success but wrote nothing" >&2
        exit 1
    fi

    # A single self-contained file is tens of megabytes; anything tiny means the publish silently
    # produced a framework-dependent stub, which is the failure worth failing the script over.
    found=0
    for file in "$out"/*; do
        [ -f "$file" ] || continue
        found=1
        size=$(stat -c%s "$file" 2>/dev/null || stat -f%z "$file")
        printf "    %-40s %9s bytes\n" "${file#"$repo_root"/}" "$size"
    done
    [ "$found" -eq 1 ] || { echo "    $out is empty" >&2; exit 1; }
done

echo ""
echo "${green}Done. The file is all there is: it carries the setup logic and payload embedded, so no Setup folder belongs beside it.${off}"
