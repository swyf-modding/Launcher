#!/usr/bin/env bash
#
# Runs the launcher's tests. The shell twin of tools/test-getmods.ps1.
#
#   ./tools/test-getmods.sh
#   ./tools/test-getmods.sh --online
#
# A thin wrapper around `dotnet run`, in the same way build.sh is a thin wrapper around `dotnet publish`:
#
#   dotnet run --project tests/Launcher.Tests.csproj -c Release
#
# is an equivalent command. What this adds is the --online switch and a readable summary.
#
# The two suites are separate programs rather than one with a filter, and both are compiled from the
# app's own sources. That is deliberate: these tests are about what happens to files on disk - zip-slip
# refused, a checksum mismatch caught, an enable/disable round trip - and the cheapest way to be sure
# they are testing the shipped code rather than a copy of it is to compile that code.
#
# --online needs the network and spends GitHub's unauthenticated rate limit, so it is never the default.
#
set -euo pipefail

online=false
configuration="Release"

while [ $# -gt 0 ]; do
    case "$1" in
        -o|--online)        online=true; shift ;;
        -c|--configuration) configuration="${2:?--configuration needs a value}"; shift 2 ;;
        -h|--help)          sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; echo "try --help" >&2; exit 2 ;;
    esac
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/tests/Launcher.Tests.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH. The tests are run with the .NET SDK:" >&2
    echo "  https://dotnet.microsoft.com/download" >&2
    exit 1
fi

if [ "$online" = true ]; then
    echo "=== online tests"
else
    echo "=== offline tests"
fi
echo "  project: $project"
echo ""

if [ "$online" = true ]; then
    online_flag=true
else
    online_flag=false
fi

if ! dotnet run --project "$project" --configuration "$configuration" --nologo -p:Online="$online_flag"; then
    echo ""
    echo "FAILED" >&2
    exit 1
fi

echo ""
echo "All tests passed."
