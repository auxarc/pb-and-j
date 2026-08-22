#!/usr/bin/env bash
#
# cite-check — resolve a document's file:line citations against the tree.
#
# Line numbers rot. This repo's answer has been "cite the MEMBER as well as the
# line", which makes a rotted citation RECOVERABLE but does not make it
# DETECTABLE. This makes it detectable.
#
# Usage:  tools/cite-check/cite-check.sh <manifest> [<manifest> ...]
#         make -n  # not wired into any make target; see NOTE at the bottom
#
# Manifest format, one citation per line:
#
#     path/relative/to/repo/root | LINE | expected substring
#
# Blank lines and lines beginning with # are ignored.
#
# ⭐ THE CANARY IS THE POINT, and it is on the INSTRUMENT rather than on the
# input. Every manifest MUST contain at least one line tagged CONTROL:
#
#     CONTROL path | LINE | a string that is deliberately NOT there
#
# A control that PASSES means the comparison is not comparing — a harness that
# cannot fail reports "all citations verified" for a tree it never opened, which
# is exactly how an unfalsifiable check reads identically to no check at all.
# When that happens this script says so and exits 2, distinct from the exit 1 a
# genuine citation failure gives.
set -uo pipefail

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)

if [ "$#" -eq 0 ]; then
    echo "usage: $0 <manifest> [<manifest> ...]" >&2
    exit 64
fi

total=0; ok=0; bad=0; controls=0; controls_misfired=0

check() { # file line expect -> 0 match, 1 mismatch/missing
    local f="$root/$1" l="$2" e="$3" got
    [ -f "$f" ] || { echo "     MISSING FILE  $1"; return 1; }
    got=$(sed -n "${l}p" "$f")
    [ -n "$got" ] || { echo "     PAST EOF      $1:$l"; return 1; }
    case "$got" in
        *"$e"*) return 0 ;;
        *)      echo "     MISMATCH      $1:$l"
                echo "                   want [$e]"
                echo "                   got  [$got]"
                return 1 ;;
    esac
}

for manifest in "$@"; do
    [ -f "$manifest" ] || { echo "no such manifest: $manifest" >&2; exit 64; }
    echo "== $manifest"
    while IFS='|' read -r col1 line expect; do
        col1="${col1#"${col1%%[![:space:]]*}"}"; col1="${col1%"${col1##*[![:space:]]}"}"
        case "$col1" in ''|\#*) continue ;; esac
        line="${line//[[:space:]]/}"
        expect="${expect#"${expect%%[![:space:]]*}"}"; expect="${expect%"${expect##*[![:space:]]}"}"

        is_control=0
        case "$col1" in
            CONTROL\ *) is_control=1; col1="${col1#CONTROL }" ;;
        esac

        total=$((total + 1))
        if check "$col1" "$line" "$expect"; then
            if [ "$is_control" -eq 1 ]; then
                controls=$((controls + 1)); controls_misfired=$((controls_misfired + 1))
                echo "  !! CONTROL MATCHED — the harness cannot fail: $col1:$line"
            else
                ok=$((ok + 1))
            fi
        else
            if [ "$is_control" -eq 1 ]; then
                controls=$((controls + 1))
                echo "     (control failed as intended — the comparison is live)"
            else
                bad=$((bad + 1))
            fi
        fi
    done < "$manifest"
done

echo
echo "cite-check: $ok verified, $bad failed, $controls control(s), $total lines"

if [ "$controls" -eq 0 ]; then
    echo "cite-check: NO CONTROL LINE — this run proves nothing. Add one." >&2
    exit 2
fi
if [ "$controls_misfired" -ne 0 ]; then
    echo "cite-check: a CONTROL matched — the instrument is broken, not the citations." >&2
    exit 2
fi
[ "$bad" -eq 0 ] || exit 1
echo "cite-check OK"

# NOTE — deliberately NOT wired into `make dist`. Gating the build on prose
# line numbers is a policy change (every future edit above a cited line becomes
# a build failure) and belongs to whoever owns the release definition, not to
# the lane that needed the check. Filed as NEW WORK instead.
