#!/usr/bin/env python3
"""Compare two emitted doc XMLs BY MEMBER, never by line.

THE ORACLE FOR "EVERY /// SURVIVED, AND STILL SAYS THE SAME THING". The
decompile diff cannot see a comment, and total-content only proves the lines
exist SOMEWHERE -- neither notices a summary that ended up attached to the
wrong member, or a class doc concatenated with another part's.

WHY BY MEMBER. The compiler emits <member> entries in source order, so
splitting a file into parts reorders them wholesale. On NetLog.cs a plain
`diff` of the two XMLs printed ~150 lines of pure reordering with nothing
lost -- output that is indistinguishable at a glance from real damage, and
the fastest way to teach yourself to stop reading this oracle. Compared as a
set keyed by member name, the same split is silent.

Whitespace inside a summary is normalised: the compiler rewraps nothing, but
indentation shifts when a member moves between files, and that is not a
change to what the doc SAYS.

REFUSES A FILE IT BARELY PARSED, for the reason ilcanon does: an empty parse
compares clean against another empty parse, which is the most convincing
possible way to prove nothing. GenerateDocumentationFile being off, or a path
typo, both land here.

Usage: docxml.py <before.xml> <after.xml>
"""
import collections
import re
import sys

MIN_MEMBERS = 20   # any real assembly in this repo emits far more than this


def members(path):
    with open(path) as fh:
        text = fh.read()
    out = {}
    dupes = []
    for name, body in re.findall(r'<member name="(.*?)">(.*?)</member>',
                                 text, re.S):
        body = re.sub(r'\s+', ' ', body).strip()
        if name in out and out[name] != body:
            dupes.append(name)
        out[name] = body
    if len(out) < MIN_MEMBERS:
        raise SystemExit(
            f"REFUSING {path}: parsed only {len(out)} member entries, which is "
            f"too few to be a real doc XML. An empty parse compares clean "
            f"against another empty parse.")
    return out, dupes


def main(argv):
    if len(argv) != 3:
        raise SystemExit(__doc__.strip().splitlines()[-1])
    before, dup_b = members(argv[1])
    after, dup_a = members(argv[2])
    print(f"members: {len(before)} before, {len(after)} after")

    bad = 0
    for name in sorted(set(before) - set(after)):
        print(f"  LOST     {name}")
        bad += 1
    for name in sorted(set(after) - set(before)):
        print(f"  GAINED   {name}")
        bad += 1
    for name in sorted(set(before) & set(after)):
        if before[name] != after[name]:
            print(f"  CHANGED  {name}")
            print(f"      before: {before[name][:160]}")
            print(f"      after:  {after[name][:160]}")
            bad += 1
    # A duplicated name with DIFFERENT bodies is the concatenated-class-doc
    # shape: two parts each carrying ///, spliced by the compiler into one
    # entry. Report it even when both sides agree, because both being wrong
    # is exactly what a before/after comparison cannot see.
    for name in sorted(set(dup_a)):
        print(f"  SPLICED  {name} -- more than one /// feeds this entry")
        bad += 1

    print("doc XML identical by member" if not bad
          else f"doc XML DIFFERS: {bad} member(s)")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
