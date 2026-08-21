#!/usr/bin/env python3
"""Which part file does each member of a split family live in? The ledger.

GROUPING IS THE ONE PROPERTY NO OTHER ORACLE HERE CAN SEE, and this file is the
only thing that keeps watching it after the split lands. A split can be
byte-identical under ilcanon.py, identical by member under docxml.py, complete
under totalcontent.py, green on every test -- and have every member in the
wrong file. Those tools compare a before to an after; once the split is
committed there is no more "before", and nothing notices a member that drifts.

What drifts, in practice, is not an existing member moving. It is a NEW member
landing in whichever part file the author had open. That is a placement
decision made by accident, and the split's whole value is that placement is
decided on purpose. So the ledger records the decision and `make
check-split-grouping` refuses a build that departs from it, exactly the way
wire-surface.lock refuses an unrecorded wire change.

A FAMILY IS `Foo.cs` PLUS ITS `Foo.*.cs` SIBLINGS in the same directory, which
is the convention every split in this programme has followed: the primary part
keeps the original file name. That rule is a claim about the tree, so the lock
records the family LIST as well as the members -- otherwise a split that named
its primary something else would simply vanish from the ledger and take its
members with it, and a check that silently stops looking at a family is worse
than no check at all.

OVERLOADS ARE COUNTED, NOT NUMBERED. Three `NetGlue.Host` in one part file are
one row with count 3. Keying them by line number, the way splitspec.py does,
would make the lock churn on every unrelated edit above them and train everyone
to re-record without reading -- which is how a lock stops being evidence.

  grouping.py                 print the ledger
  grouping.py --check <lock>  compare the tree against a recorded ledger
"""
import collections
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from splitspec import member_map  # noqa: E402

ROOTS = ("src", "tools", "tests")
SKIP = ("/obj/", "/bin/")


def families(root_dir="."):
    """(directory, family root) -> the part files, primary first."""
    found = {}
    for r in ROOTS:
        top = os.path.join(root_dir, r)
        for dirpath, _, filenames in os.walk(top):
            norm = dirpath.replace(os.sep, "/") + "/"
            if any(s in norm for s in SKIP):
                continue
            names = {f for f in filenames if f.endswith(".cs")}
            groups = collections.defaultdict(list)
            for f in sorted(names):
                stem = f[:-3]
                if "." not in stem:
                    continue
                primary = stem.split(".")[0]
                if primary + ".cs" in names:
                    groups[primary].append(f)
            for primary, parts in groups.items():
                rel = os.path.relpath(dirpath, root_dir).replace(os.sep, "/")
                found[(rel, primary)] = [primary + ".cs"] + sorted(parts)
    return found


def prove_discovery():
    """The rule must be shown to find a family it is given.

    THE VACUITY GUARD GOES ON THE INSTRUMENT, NOT ON THE INPUT. Asserting "the
    tree contains at least N families" reads like a check and is not one: it
    passes for a repo that happens to have splits and fails for one that does
    not, while saying nothing about whether the rule still works. So the rule
    is run against a directory built to contain exactly one family, and one
    decoy that must NOT be collected -- a `Foo.Bar.cs` with no `Foo.cs` beside
    it is not a split, it is just a dotted file name.
    """
    with tempfile.TemporaryDirectory() as tmp:
        d = os.path.join(tmp, "src", "N")
        os.makedirs(d)
        for name in ("Fam.cs", "Fam.One.cs", "Fam.Two.Deep.cs",
                     "Lonely.Part.cs", "Plain.cs"):
            open(os.path.join(d, name), "w").close()
        got = families(tmp)
        key = ("src/N", "Fam")
        if list(got) != [key]:
            raise SystemExit(f"VACUOUS: the family rule collected {list(got)} "
                             f"from a directory built to contain exactly "
                             f"{key} -- it no longer describes the tree")
        if got[key] != ["Fam.cs", "Fam.One.cs", "Fam.Two.Deep.cs"]:
            raise SystemExit(f"VACUOUS: the family rule collected the wrong "
                             f"parts: {got[key]}")


def ledger(root_dir="."):
    prove_discovery()
    rows = []
    fams = families(root_dir)
    if not fams:
        raise SystemExit("VACUOUS: no split families found at all. Either the "
                         "naming convention changed or the walk is looking in "
                         "the wrong place; an empty ledger is not a clean one.")
    for (rel, primary), parts in sorted(fams.items()):
        for part in parts:
            path = os.path.join(root_dir, rel, part)
            members = member_map(path)
            if not members:
                raise SystemExit(
                    f"VACUOUS: the member map found NOTHING in {rel}/{part}. A "
                    f"part file with no members means the map did not descend, "
                    f"not that the file is empty -- and a ledger built from it "
                    f"would record a member's absence as its correct place.")
            counted = collections.Counter(f"{cls}.{name}"
                                          for cls, name, _, _ in members)
            for member, n in sorted(counted.items()):
                rows.append((rel, primary, part, member, str(n)))
    return rows


def render(rows):
    fams = len({(r[0], r[1]) for r in rows})
    parts = len({(r[0], r[2]) for r in rows})
    total = sum(int(r[4]) for r in rows)
    out = [
        "# Which part file each member of a split family lives in.",
        "#",
        "# Grouping is the one property no other oracle in tools/split/ can",
        "# see, and the others stop looking once the split is committed. A NEW",
        "# member landing in whichever part file was open is a placement",
        "# decision made by accident; this is what refuses it.",
        "#",
        "# Re-record only when you have DECIDED the placement is the one you",
        "# want:  make record-split-grouping",
        "#",
        f"# families: {fams}   part files: {parts}   members: {total}",
        "",
    ]
    out += ["\t".join(r) for r in rows]
    return "\n".join(out) + "\n"


def parse(text):
    return [tuple(line.split("\t")) for line in text.split("\n")
            if line.strip() and not line.startswith("#")]


def check(lock_path, root_dir="."):
    if not os.path.exists(lock_path):
        print(f"FATAL: {lock_path} is missing — run 'make record-split-grouping'")
        return 1
    want = parse(open(lock_path).read())
    got = ledger(root_dir)

    want_fams = {(r[0], r[1]) for r in want}
    got_fams = {(r[0], r[1]) for r in got}
    # A FAMILY THAT DISAPPEARS IS THE DANGEROUS DIRECTION. The naming rule is
    # how families are found, so a split whose primary got renamed stops being
    # looked at entirely and takes every one of its members with it -- silently,
    # because each member simply stops appearing on both sides.
    gone = sorted(want_fams - got_fams)
    new_fams = sorted(got_fams - want_fams)

    # A MEMBER NAME CAN LIVE IN TWO PART FILES AT ONCE, so this cannot key on
    # (directory, member). Overloads are a single name, and a split is free to
    # put them in different parts when their callers differ -- HostSession.Reject
    # is in Handshake.cs and Turn.cs, KeyframePlayer.Dress in Assets.cs and
    # Sleep.cs, both on purpose. Keying by name collapsed those pairs, dropped a
    # row on each side, and would have reported a move that never happened.
    def by_member(rows):
        out = collections.defaultdict(dict)
        for rel, _, part, member, n in rows:
            out[(rel, member)][part] = n
        return out

    want_at, got_at = by_member(want), by_member(got)
    moved, added, removed = [], [], []
    for key in sorted(set(want_at) | set(got_at)):
        w, g = want_at.get(key), got_at.get(key)
        if w is None:
            added.append((key, sorted(g)))
        elif g is None:
            removed.append((key, sorted(w)))
        elif w != g:
            moved.append((key, w, g))

    if not (gone or new_fams or moved or added or removed):
        fams = len(got_fams)
        parts = len({(r[0], r[2]) for r in got})
        total = sum(int(r[4]) for r in got)
        print(f"split grouping OK ({fams} families, {parts} parts, "
              f"{total} members)")
        return 0

    print("FATAL: split grouping drifted from the lock:")
    for rel, primary in gone:
        print(f"  - FAMILY GONE   {rel}/{primary}.* — renamed, merged or "
              f"deleted; its members are no longer checked at all")
    for rel, primary in new_fams:
        print(f"  + FAMILY NEW    {rel}/{primary}.*")
    def where(d):
        return ", ".join(f"{p}" + (f" x{n}" if n != "1" else "")
                         for p, n in sorted(d.items()))
    for (rel, member), w, g in moved:
        print(f"  ~ MOVED         {rel}  {member}: "
              f"{where(w)} -> {where(g)}")
    for (rel, member), parts in added:
        print(f"  + NEW MEMBER    {rel}  {member}  in {', '.join(parts)}")
    for (rel, member), parts in removed:
        print(f"  - GONE          {rel}  {member}  was in {', '.join(parts)}")
    print("  A new member in a split family is a PLACEMENT decision: the whole")
    print("  point of the split is that which file a member lives in was")
    print("  decided on purpose. Decide the part — tools/split/README.md step 5")
    print("  is the rule — then: make record-split-grouping")
    return 1


def main(argv):
    root = argv[argv.index("--root") + 1] if "--root" in argv else "."
    if "--check" in argv:
        return check(argv[argv.index("--check") + 1], root)
    sys.stdout.write(render(ledger(root)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
