#!/usr/bin/env python3
"""Write the part files from the ORIGINAL BYTES. Nothing is ever retyped.

Bodies are sliced out of the original line list using the tiling splitspec.py
computed and partition.py proved. Only the wrapper is generated: usings,
namespace, the class declaration, and the header comment from the spec.

RUN partition.py FIRST. This tool refuses to write if the tiling is not a
partition, because a split that drops a line writes seven plausible files and
no error.

  writeparts.py <spec.json>            write the parts
  writeparts.py <spec.json> --check    write to a temp dir and diff instead

The `partial` keyword on a split class is the ONE content line a pure move is
expected to lose; totalcontent.py is where that is declared and checked.

A synthetic block marked `"emit": "class_doc"` is the one exception to "the
wrapper is regenerated": it is copied verbatim from the original bytes above
that part's class declaration, because a class-level /// doc belongs to the
type, not to the wrapper. splitspec.py refuses more than one.
"""
import filecmp
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from splitspec import Spec  # noqa: E402


def render(spec, part):
    cfg = spec.parts[part]
    out = [f"using {u};" for u in cfg.get("usings", [])]
    out += ["", f"namespace {spec.namespace}", "{"]
    # An ABSENT header emits nothing. Splitting the empty string still yields
    # one element, so the old loop wrote a bare `    //` -- a line the original
    # never had, which totalcontent.py then reports as GAINED.
    header = cfg.get("header", "").rstrip("\n")
    if header:
        for line in header.split("\n"):
            out.append(("    // " + line).rstrip())
    # The class-level /// doc, kept verbatim from the original bytes on the
    # one part the spec names. Not retyped, and not regenerated: see
    # splitspec.py on why exactly one part may carry it.
    doc = getattr(spec, "class_doc", None)
    if doc and doc["part"] == part:
        # A blank line between the two. Without it the part header butts
        # straight against the /// below and reads as commentary on it --
        # on ClientSession.cs that /// belongs to an enum, not to the class.
        if header:
            out.append("")
        a, b = doc["lines"]
        out += spec.lines[a - 1:b]
    kw = "partial " if cfg.get("partial") else ""
    # MODIFIERS ARE NOT ALWAYS "public". Hardcoding it silently turned
    # `public static class NetLog` into `public partial class NetLog` -- a
    # different type, and one that no longer refuses instantiation. splitspec
    # checks this against the original declaration rather than trusting it.
    # THE BASE LIST BELONGS TO EXACTLY ONE PART. A partial class may name its
    # bases and interfaces on one declaration only, and the wrapper generator
    # emitted none at all -- so splitting `class ClientSession : IPbjSession`
    # produced eleven parts that together implement nothing. splitspec.py
    # checks the value against the source's own declaration.
    bases = cfg.get("bases")
    suffix = f" : {bases}" if bases else ""
    # AND NOT ALWAYS A CLASS. `class` was hardcoded here too, so a
    # `public readonly struct` came out `public partial class` -- a value type
    # rendered as a reference type, which no oracle in this kit reports.
    # splitspec refuses a kind that disagrees with the source.
    out.append(f"    {cfg.get('modifiers', 'public')} {kw}"
               f"{cfg.get('kind', 'class')} {cfg['class']}{suffix}")
    out.append("    {")
    body = []
    for a, b in spec.blocks[part]:
        body += spec.lines[a - 1:b]
    while body and not body[-1].strip():
        body.pop()
    while body and not body[0].strip():
        body.pop(0)
    out += body
    out += ["    }", "}", ""]
    return "\n".join(out)


def main(argv):
    spec = Spec(argv[1])
    check = "--check" in argv

    if spec.dup or spec.unassigned or [g for g in spec.content_gaps() if not g[2]]:
        raise SystemExit("REFUSING to write: the tiling is not a proven "
                         "partition. Run partition.py and fix the spec.")

    target = tempfile.mkdtemp(prefix="pbj-split-") if check else spec.outdir
    written = []
    for part in spec.parts:
        path = os.path.join(target, spec.parts[part]["file"])
        with open(path, "w") as fh:
            fh.write(render(spec, part))
        written.append((part, path))

    rc = 0
    for part, path in written:
        n = sum(1 for _ in open(path)) 
        if check:
            live = os.path.join(spec.outdir, spec.parts[part]["file"])
            same = os.path.exists(live) and filecmp.cmp(live, path, shallow=False)
            print(f"{spec.parts[part]['file']:46s} {n:5d} lines  "
                  f"{'matches the tree' if same else 'DIFFERS FROM THE TREE'}")
            rc |= 0 if same else 1
        else:
            print(f"{spec.parts[part]['file']:46s} {n:5d} lines")
    if check:
        shutil.rmtree(target)
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
