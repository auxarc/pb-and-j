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

    A DECLARATION IS PARSED, NOT PATTERN-MATCHED. The tool reads the
    declaration statement -- from the modifier to the first `;`, `{` or `=>`
    at paren depth zero -- and classifies it by whether a call-parenthesis
    opened before that terminator. Methods, fields, properties and nested
    types then each get the span their own shape implies.

    IT DID NOT ALWAYS. The first version asked only "does the line start with
    an access modifier and contain a '('", which on real fixture code read

        private readonly FakeGameBridge bridge = new FakeGameBridge();

    as a METHOD named FakeGameBridge, then ran its "body" to the next brace
    balance and swallowed the real method below it. On PbjRuntimeTests.cs that
    hid four members -- HostRuntime, GoodHello, WithHandshakenPeer and five
    fields -- while still reporting a confident 56. A field with no parens at
    all (`private HostSession host = null!;`) was worse: the scan ran forward
    hunting a '(' and consumed whatever came next.

    Only the spec's stale-plan guard caught it, by noticing the plan named
    members the map did not have. That is defence in depth working, not a
    reason to leave the map wrong.

    WRAPPED SIGNATURES ARE THE OTHER KNOWN TRAP: size-report.py missed 65
    methods whose parameter lists ran onto a second line. Accumulating the
    declaration statement across lines handles those by construction.
    """
    with open(path) as fh:
        src = fh.read().split("\n")
    n = len(src)

    def statement(i):
        """Read the declaration statement starting at line i.

        Returns (text, terminator, end_line). The terminator is ';', '{' or
        '=>' -- whichever comes first at paren depth zero.
        """
        text, depth, j = "", 0, i
        while j <= n:
            line = src[j - 1]
            k = 0
            while k < len(line):
                c = line[k]
                if c == '(':
                    depth += 1
                elif c == ')':
                    depth -= 1
                elif depth == 0:
                    if c == ';':
                        return text + line[:k], ';', j
                    if c == '{':
                        return text + line[:k], '{', j
                    if c == '=' and k + 1 < len(line) and line[k + 1] == '>':
                        return text + line[:k], '=>', j
                k += 1
            text += line + "\n"
            j += 1
        return text, None, n

    def brace_block(i):
        d, j, seen = 0, i, False
        while j <= n:
            d += src[j - 1].count('{') - src[j - 1].count('}')
            if src[j - 1].count('{'):
                seen = True
            if seen and d <= 0:
                return j
            j += 1
        return n

    def semicolon_after(i):
        j = i
        while j <= n and ';' not in src[j - 1]:
            j += 1
        return min(j, n)

    MOD = r'(?:public|private|internal|protected)'
    members = []
    depth = 0
    cls = None
    i = 1
    while i <= n:
        line = src[i - 1]
        stripped = line.strip()
        m = re.match(r'^(?:public|internal)\s+(?:partial\s+)?(?:sealed\s+)?'
                     r'(?:static\s+)?class\s+(\w+)', stripped)
        if m and depth == 1:
            cls = m.group(1)
            depth += line.count('{') - line.count('}')
            i += 1
            continue

        if depth == 2 and re.match(r'^' + MOD + r'\s', stripped):
            text, term, decl_end = statement(i)
            is_type = re.search(r'\b(class|struct|interface|enum|record)\s+\w',
                                text) is not None
            # A METHOD IS A '(' IN THE DECLARATOR -- before any '='. Testing
            # for a '(' anywhere in the statement is the original bug wearing
            # a new hat: `... FakeGameBridge bridge = new FakeGameBridge()`
            # has one, in its INITIALISER, and reads as a method declaration.
            is_method = (not is_type) and '(' in text.split('=')[0]

            if is_method:
                name = re.findall(r'(\w+)\s*(?:<[^<>]*>)?\s*\(', text)[0]
                end = (brace_block(decl_end) if term == '{'
                       else semicolon_after(decl_end) if term == '=>'
                       else decl_end)
            elif is_type:
                name = re.search(r'\b(?:class|struct|interface|enum|record)\s+'
                                 r'(\w+)', text).group(1)
                end = brace_block(decl_end)
            else:
                # the field's NAME is the last identifier of the DECLARATOR,
                # i.e. before any '='. Reading to the end of the statement
                # instead picks a word out of the initialiser: `FakeGameBridge`
                # for `... FakeGameBridge bridge = new FakeGameBridge()`, and
                # `null` for `private HostSession host = null!`.
                declarator = text.split('=')[0]
                ids = re.findall(r'\b(\w+)\b', declarator)
                name = ids[-1] if ids else f"field_line_{i}"
                end = (decl_end if term == ';'
                       else semicolon_after(brace_block(decl_end))
                       if term == '{' else semicolon_after(decl_end))
            members.append([cls, name, i, end])
            for q in range(i, end + 1):
                depth += src[q - 1].count('{') - src[q - 1].count('}')
            i = end + 1
            continue

        depth += line.count('{') - line.count('}')
        i += 1

    for mem in members:
        s0 = mem[2]
        while s0 - 1 >= 1:
            prev = src[s0 - 2].strip()
            if prev.startswith('[') or prev.startswith('//'):
                s0 -= 1
            else:
                break
        mem[2] = s0
    return [tuple(mem) for mem in members]


if __name__ == "__main__":
    for cls, name, s, e in member_map(sys.argv[1]):
        print(f"{cls}\t{name}\t{s}\t{e}")
