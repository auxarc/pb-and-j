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
    for line in cfg.get("header", "").rstrip("\n").split("\n"):
        out.append(("    // " + line).rstrip())
    kw = "partial " if cfg.get("partial") else ""
    out.append(f"    public {kw}class {cfg['class']}")
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
