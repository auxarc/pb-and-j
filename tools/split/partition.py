#!/usr/bin/env python3
"""Prove the split's line tiling is a PARTITION of the original file.

THE MEMBER TABLE IS NOT THE SAFETY PROPERTY. "Every member is in exactly one
part" leaves whole runs of lines -- comments, property blocks, banners -- free
to emigrate or vanish. "Every LINE lands in exactly one part" is the property,
and it is the first thing to run: on HostSession.cs it caught a defect in the
plan BEFORE any edit was made.

Reports, and fails on:
  - a line claimed by two parts, or by none;
  - a gap line carrying CONTENT that the spec has not decided the direction of
    (see splitspec.py on gap direction).

Usage: partition.py <spec.json>
"""
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from splitspec import Spec  # noqa: E402


def main(spec_path):
    spec = Spec(spec_path)
    print(f"{os.path.relpath(spec.source)}: {spec.n} lines, "
          f"{len(spec.members)} members, {len(spec.parts)} parts")

    bad = 0
    for i, a, b in spec.dup:
        print(f"  DUPLICATED line {i}: claimed by {a} and {b}")
        bad += 1
    for i in spec.unassigned:
        print(f"  UNASSIGNED line {i}: {spec.lines[i - 1]!r}")
        bad += 1

    # THE GAP REPORT IS THE EXPLANATION, NOT A SECOND VERDICT. An undecided
    # content gap is always ALSO an unassigned line -- blank-line absorption
    # stops dead at content -- so the verdict above already covers it. What
    # this adds is the WHY: "line 384 is a banner and nobody said which side it
    # belongs to" beats "line 384 is unassigned". The subset relation is
    # asserted rather than assumed, so a future change to the tiling that lets
    # a content gap slip through ASSIGNED is loud instead of silent.
    gaps = spec.content_gaps()
    undecided = [g for g in gaps if not g[2]]
    for i, text, named in gaps:
        where = "direction named in the spec" if named else "NOT DECIDED"
        print(f"  content in a gap, line {i} ({where}): {text.strip()!r}")
    escaped = [i for i, _, named in gaps
               if not named and i not in spec.unassigned]
    for i in escaped:
        print(f"  INVARIANT BROKEN: line {i} carries content, its direction is "
              f"undecided, and yet it was assigned to a part")
    bad += len(escaped)

    print(f"lines: {spec.n}   duplicated: {len(spec.dup)}   "
          f"unassigned: {len(spec.unassigned)}   "
          f"undecided content gaps: {len(undecided)}")
    for part, blocks in sorted(spec.blocks.items(),
                               key=lambda kv: -sum(b - a + 1 for a, b in kv[1])):
        n = sum(b - a + 1 for a, b in blocks)
        print(f"  {part:14s} {n:5d} original lines in {len(blocks)} blocks")
    if bad:
        print("PARTITION FAILED")
        return 1
    print("partition OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
