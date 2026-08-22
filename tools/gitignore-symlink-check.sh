#!/usr/bin/env bash
#
# Prove that the gitignore patterns for the lane-worktree dependencies actually
# ignore a SYMLINK, not merely a directory.
#
#   tools/gitignore-symlink-check.sh
#
# WHY THIS EXISTS. `vendor/`, `decompiled/`, `references/` and `.packages/` are
# gitignored, so a fresh `git worktree` does not have them and CANNOT BUILD. The
# lane recipe symlinks them back to the main checkout. But a gitignore pattern
# with a TRAILING SLASH is directory-only and does not match a symlink — so the
# link shows as untracked and `git add -A` in a lane STAGES IT, committing this
# machine's absolute path (home directory and username included) into the repo.
#
# Measured 2026-08-22: pattern `vendor/` staged the link; pattern `vendor` did
# not. The trailing slashes were dropped. This script is what stops them coming
# back — a one-character regression that nothing else in the build would notice,
# in a direction that leaks a home path into a public repo.
#
# ⚠️ THE AUTHORITY ON WHAT GIT IGNORES IS GIT. This deliberately does NOT grep
# .gitignore for a pattern: a regex matching `^vendor$` passes while a later
# negation, a nested .gitignore or a core.excludesFile changes the real answer.
# It runs git, in a throwaway repo under mktemp, against the REAL .gitignore.
#
# ⚠️ AND IT CARRIES ITS OWN CANARY. The sister project's equivalent suite was
# found VACUOUS within an hour of being written: its helper reported "not
# staged" whenever git failed, so "correctly ignored" and "I measured nothing"
# printed identically — five of six cases went green against a git that always
# exits 1. Case 0 below is the positive control: a file that MUST stage. If it
# does not, the harness is broken and every other verdict here is worthless, so
# the script fails rather than reporting passes.

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO="$PWD"
IGNORE_FILE="$REPO/.gitignore"

# The entries the lane recipe symlinks. Listed rather than derived, so adding one
# to the recipe without adding it here is a visible omission instead of a silent
# gap in coverage.
DEPS=(vendor decompiled decompiled-firstpass references .packages)

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/target"
echo payload > "$TMP/target/file"

mk_repo() {
  rm -rf "$TMP/repo"; mkdir -p "$TMP/repo"
  git -C "$TMP/repo" init -q .
  git -C "$TMP/repo" config user.email check@local
  git -C "$TMP/repo" config user.name check
  cp "$IGNORE_FILE" "$TMP/repo/.gitignore"
}

staged_count() {  # $1 = path to look for in the index
  git -C "$TMP/repo" add -A >/dev/null 2>&1
  git -C "$TMP/repo" diff --cached --name-only 2>/dev/null | grep -cx -- "$1"
}

fails=0

# ---- Case 0: the positive control. Without this the rest proves nothing. -----
mk_repo
echo hello > "$TMP/repo/definitely-tracked.txt"
if [ "$(staged_count 'definitely-tracked.txt')" -eq 1 ]; then
  echo "  PASS  case 0 (control): the harness can see a staged file"
else
  echo "  FAIL  case 0 (control): a plain file did NOT stage."
  echo "        The harness is broken — git may be missing, failing, or refusing to init."
  echo "        Every 'ignored' verdict below would be indistinguishable from"
  echo "        'I measured nothing'. Not reporting them."
  exit 1
fi

# ---- Case 1..N: each dependency, as a SYMLINK, must not stage ---------------
for dep in "${DEPS[@]}"; do
  mk_repo
  ln -s "$TMP/target" "$TMP/repo/$dep"
  n="$(staged_count "$dep")"
  if [ "$n" -eq 0 ]; then
    echo "  PASS  '$dep' symlink is ignored"
  else
    echo "  FAIL  '$dep' symlink STAGED — .gitignore likely has a trailing slash on it."
    echo "        A lane worktree running 'git add -A' would commit an absolute path"
    echo "        containing this machine's home directory and username."
    fails=$((fails+1))
  fi
done

# ---- Case N+1: the negative case. A REAL directory must also stay ignored, ---
# so the fix did not simply stop ignoring these paths altogether.
mk_repo
mkdir -p "$TMP/repo/vendor/Managed"
echo dll > "$TMP/repo/vendor/Managed/Some.dll"
if [ "$(git -C "$TMP/repo" status --porcelain --ignored 2>/dev/null | grep -c '^!! vendor/')" -ge 1 ]; then
  echo "  PASS  a real 'vendor' DIRECTORY is still ignored (the fix did not un-ignore it)"
else
  echo "  FAIL  a real 'vendor' directory is no longer ignored — the pattern is now too narrow."
  fails=$((fails+1))
fi

echo
if [ "$fails" -eq 0 ]; then
  echo "gitignore symlink check OK ($(( ${#DEPS[@]} + 2 )) cases, control included)"
  exit 0
fi
echo "gitignore symlink check FAILED ($fails case(s))"
exit 1
