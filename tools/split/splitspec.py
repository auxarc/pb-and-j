#!/usr/bin/env python3
"""The one source of truth for a split: the member map and the line tiling.

Every other tool in this directory imports this, so the proof (partition.py)
and the surgery (writeparts.py) cannot disagree about which lines go where.
That is not hypothetical tidiness: during the DestructionPlaybackTests split
the two tools each carried their OWN copy of the synthetic-block table. They
happened to agree, and nothing whatsoever would have said so if they had not.

A SPLIT SPEC is JSON:

  {
    "source":    "tests/.../FooTests.cs",
    "outdir":    "tests/.../",
    "namespace": "Foo.Bar",
    "members":   {"MemberName": "partname", ...},        // EVERY member
    "synthetic": [{"lines": [1, 7], "part": "primary",
                   "why": "usings + namespace, regenerated per part"}],
    "forward_gaps": {"MemberName": [383, 385]},          // see below
    "parts": {
      "primary": {"file": "FooTests.cs", "class": "FooTests",
                  "partial": true, "usings": ["Xunit"], "header": "..."}
    }
  }

SYNTHETIC BLOCKS are the real lines the member map does not model -- usings,
namespace and class declarations, closing braces. They must be declared, not
absorbed, or content silently emigrates: on HostSession.cs a 72-line properties
block would have joined the following part unnoticed.

GAP DIRECTION. A blank gap between members absorbs BACKWARD onto the member
above. A gap holding a COMMENT does not: `//` banners usually document the
member BELOW them, and a dropped or misplaced comment is the one split defect
no other oracle sees -- not the decompile diff, not the doc XML, not the tests,
not the coverage gate, not the size ratchet. Direction is a content question
decided by READING, so any gap with content must be named in "forward_gaps"
(attaching it to the member below) or it stays with the member above. A gap
with content that is named in neither is reported by partition.py, loudly.
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


class Spec:
    def __init__(self, path):
        with open(path) as fh:
            self.raw = json.load(fh)
        root = self.raw.get("root") or os.getcwd()
        self.source = os.path.join(root, self.raw["source"])
        self.outdir = os.path.join(root, self.raw["outdir"])
        self.namespace = self.raw["namespace"]
        self.plan = self.raw["members"]
        self.parts = self.raw["parts"]
        self.forward_gaps = {k: tuple(v) for k, v in
                             self.raw.get("forward_gaps", {}).items()}
        with open(self.source) as fh:
            self.lines = fh.read().split("\n")
        self.n = len(self.lines)
        self.members = member_map(self.source)
        self._check_plan()
        self._tile()

    def _check_plan(self):
        """Refuse a plan that does not describe THIS file.

        A stray `cp` once left a plan for a different file in place mid-split;
        two of three tools ran to completion and printed confidently
        mislabelled output. Only the tool that asserted this refused.
        """
        have = {m[1] for m in self.members}
        want = set(self.plan)
        if have != want:
            msg = ["FATAL: the spec does not describe this file."]
            for x in sorted(have - want):
                msg.append(f"  in file, not in spec: {x}")
            for x in sorted(want - have):
                msg.append(f"  in spec, not in file: {x}")
            raise SystemExit("\n".join(msg))
        if len(self.members) != len(self.plan):
            raise SystemExit(
                f"FATAL: {len(self.members)} members but {len(self.plan)} spec "
                f"rows -- a duplicate member name?")
        unknown = set(self.plan.values()) - set(self.parts)
        if unknown:
            raise SystemExit(f"FATAL: members assigned to undeclared parts: "
                             f"{sorted(unknown)}")

    def _tile(self):
        self.owner = {}
        self.why = {}
        self.dup = []
        self.blocks = {p: [] for p in self.parts}

        def claim(a, b, part, why):
            for i in range(a, b + 1):
                if i in self.owner:
                    self.dup.append((i, self.owner[i], part))
                self.owner[i] = part
                self.why[i] = why

        for syn in self.raw.get("synthetic", []):
            a, b = syn["lines"]
            b = self.n if b in (-1, None) else b
            claim(a, b, syn["part"], "synthetic: " + syn.get("why", ""))
        self.synthetic_lines = set(self.owner)

        starts = {m[2] for m in self.members}
        for cls, name, s, e in self.members:
            part = self.plan[name]
            a = s
            fg = self.forward_gaps.get(name)
            if fg:
                a = fg[0]
            end = e
            j = e + 1
            while (j <= self.n and j not in self.owner and j not in starts
                   and not self.lines[j - 1].strip()
                   and not self._is_forward_gap_line(j)):
                end = j
                j += 1
            claim(a, end, part, f"{cls}.{name}")
            self.blocks[part].append((a, end))
        for v in self.blocks.values():
            v.sort()
        self.unassigned = [i for i in range(1, self.n + 1) if i not in self.owner]

    def _is_forward_gap_line(self, i):
        return any(a <= i <= b for a, b in self.forward_gaps.values())

    def content_gaps(self):
        """Gap lines that carry content -- the dropped-comment failure mode."""
        out = []
        member_lines = set()
        for _, _, s, e in self.members:
            member_lines.update(range(s, e + 1))
        for i in range(1, self.n + 1):
            if i in member_lines or i in self.synthetic_lines:
                continue
            if self.lines[i - 1].strip():
                named = self._is_forward_gap_line(i)
                out.append((i, self.lines[i - 1], named))
        return out


def member_map(path):
    """(class, member, first_line, last_line) for every top-level member.

    Brace-depth walk, not a line regex. first_line is pulled back over any
    attached [attr] / /// / // lines directly above the declaration.

    WRAPPED SIGNATURES ARE THE KNOWN TRAP: size-report.py missed 65 methods
    because their parameter lists ran onto a second line. A declaration here is
    recognised by its access modifier, and its span by matching braces, so a
    wrapped signature is found like any other.
    """
    with open(path) as fh:
        src = fh.read().split("\n")
    n = len(src)
    depth = 0
    cls = None
    members = []
    i = 1
    while i <= n:
        line = src[i - 1]
        stripped = line.strip()
        m = re.match(r'^(?:public|internal)\s+(?:partial\s+)?'
                     r'(?:sealed\s+)?(?:static\s+)?class\s+(\w+)', stripped)
        if m and depth == 1:
            cls = m.group(1)
        if depth == 2 and re.match(r'^(?:public|private|internal|protected)\s',
                                   stripped):
            j = i
            # a wrapped signature: walk on until the '(' list closes
            while j <= n and '(' not in src[j - 1]:
                j += 1
            name_line = " ".join(src[i - 1:j])
            hit = re.search(r'(\w+)\s*(?:<[^<>]*>)?\s*\(', name_line)
            if hit:
                name = hit.group(1)
                d = depth
                k = i
                started = False
                while k <= n:
                    for ch in src[k - 1]:
                        if ch == '{':
                            d += 1
                            started = True
                        elif ch == '}':
                            d -= 1
                    if started and d == 2:
                        break
                    k += 1
                members.append([cls, name, i, k])
                for q in range(i, k + 1):
                    for ch in src[q - 1]:
                        if ch == '{':
                            depth += 1
                        elif ch == '}':
                            depth -= 1
                i = k + 1
                continue
        for ch in line:
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
        i += 1
    for m in members:
        s = m[2]
        while s - 1 >= 1:
            prev = src[s - 2].strip()
            if prev.startswith('[') or prev.startswith('//'):
                s -= 1
            else:
                break
        m[2] = s
    return [tuple(m) for m in members]


if __name__ == "__main__":
    for cls, name, s, e in member_map(sys.argv[1]):
        print(f"{cls}\t{name}\t{s}\t{e}")
