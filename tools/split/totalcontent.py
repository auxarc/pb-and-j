#!/usr/bin/env python3
"""Multiset of every non-blank line: the original against the union of parts.

THIS IS THE ONLY ORACLE THAT READS COMMENTS. The decompile diff, the generated
doc XML, the tests, the coverage gate and the size ratchet are all blind to a
dropped comment, and on HostSession.cs 70 of 2343 lines fell in member-map
gaps with exactly one carrying content -- headed for the wrong part.

Losses must be DECLARED, not discovered: pass PBJ_EXPECT_LOST=<n>. Without it
the tool refuses, and an earlier version that always exited 0 let a whole
omitted part file pass as clean.

Usage: PBJ_EXPECT_LOST=1 totalcontent.py <original> <part> [<part> ...]
"""
import collections
import os
import sys


def bag(path):
    with open(path) as fh:
        return collections.Counter(l.rstrip() for l in fh.read().split("\n")
                                   if l.strip())


def main(argv):
    orig, parts = argv[1], argv[2:]
    if not parts:
        raise SystemExit("REFUSING: no part files given, so nothing is compared")
    expect = os.environ.get("PBJ_EXPECT_LOST")

    a = bag(orig)
    b = collections.Counter()
    for p in parts:
        b += bag(p)
    lost, gained = a - b, b - a
    nlost, ngained = sum(lost.values()), sum(gained.values())

    print(f"original non-blank lines: {sum(a.values())}   "
          f"parts: {sum(b.values())}   ({len(parts)} part files)")
    print(f"LOST {nlost}:")
    for line, k in lost.items():
        print(f"   x{k}  {line!r}")
    print(f"GAINED {ngained} (wrapper and header text is expected):")
    for line, k in sorted(gained.items(), key=lambda kv: -kv[1])[:12]:
        print(f"   x{k}  {line!r}")
    if ngained > 12:
        print(f"   ... {ngained - 12} more")

    if expect is None:
        print("REFUSING: set PBJ_EXPECT_LOST to the number of losses you have "
              "read and can name")
        return 2
    if nlost != int(expect):
        print(f"FAIL: {nlost} lines lost, PBJ_EXPECT_LOST={expect}")
        return 1
    print(f"OK: losses match PBJ_EXPECT_LOST={expect}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
