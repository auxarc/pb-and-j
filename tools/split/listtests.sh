#!/usr/bin/env bash
# Capture the test-name SET -- the oracle the source-side splits never had.
#
# A test lost in a member-map gap is THE failure mode of a test-file split, and
# unlike a dropped comment this sees it. Compare the two captures as SETS: a
# split reorders names, so a line-ordered diff would be noise.
#
# PROVE IT BITES: delete one test, capture again, and check the diff NAMES it.
#
# Refuses a capture too small to be real. A pattern that matches nothing
# produces an empty file, and an empty file diffs clean against another empty
# one -- the most convincing possible way to prove nothing at all.
#
#   listtests.sh <out-file> [min-expected]
set -euo pipefail
out="$1"
min="${2:-1000}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

distrobox enter pb-dev -- bash -lc \
  "export NUGET_PACKAGES=$repo/.packages; cd $repo; \
   dotnet test tests/PBAndJ.Core.Tests --list-tests" \
  | grep -E '^ +PBAndJ\.Core\.Tests\.' | sed 's/^ *//' | sort > "$out"

n=$(wc -l < "$out")
if [ "$n" -lt "$min" ]; then
  echo "VACUOUS: captured only $n test names, expected at least $min" >&2
  exit 1
fi
echo "captured $n test names -> $out"
