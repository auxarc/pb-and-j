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
    "before":    {"MemberX": "MemberY"},                 // emit X just before Y
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

A synthetic block is DELETED by default, because the wrapper is regenerated.
The exception is `"emit": "class_doc"`, which keeps the block and writes it
verbatim immediately above one part's class declaration. That exists because a
class-level /// doc is not wrapper: the compiler CONCATENATES the /// of every
part into a single type entry, so leaving it on all of them glues N summaries
together and dropping it loses the type's XML outright. Both were seen on the
SelfTest split. Exactly one part may carry it, and that is asserted.

ORDER. Parts emit their members in the ORIGINAL file's order, which is right
almost always -- it keeps the diff readable and the move obviously pure. The
exception is a member the original had filed in the wrong place: preserving
that order inside the new file reproduces the misfiling under a filename that
now contradicts it. "before" moves one member's block to sit immediately ahead
of another's, within the same part. Both must exist and share a part, or the
spec is refused.

GAP DIRECTION. A blank gap between members absorbs BACKWARD onto the member
above. A gap holding a COMMENT does not: `//` banners usually document the
member BELOW them, and a dropped or misplaced comment is the one split defect
no other oracle sees -- not the decompile diff, not the doc XML, not the tests,
not the coverage gate, not the size ratchet. Direction is a content question
decided by READING, so any gap with content must be named in "forward_gaps"
(attaching it to the member below) or it stays with the member above. A gap
with content that is named in neither is reported by partition.py, loudly.
"""
import collections
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def member_keys(members):
    """The name a spec uses for each member, unambiguous by construction.

    A member was addressed by its bare NAME, which silently assumes every name
    in the file is unique. Overloads break that, and so do same-named members
    of two nested classes -- `Describe(LoadOutcome)` and `Describe(string?)` in
    NetLog.cs, three repeated names in DestructionPlayback.cs, and a `Postfix`
    per patch class in NetGlue.cs. EVERY remaining file on the split queue has
    at least one. The old plan dict simply lost one of each pair, and the count
    check caught it as "a duplicate member name?" -- correctly refusing, but
    with no way to proceed.

    The shortest unambiguous form wins, so specs stay readable:
        Name                 when the name is unique in the file
        Class.Name           when it is not, but the pair is
        Class.Name@line      for true overloads in one class

    Returns the parallel list of keys.
    """
    by_name = collections.Counter(m[1] for m in members)
    by_pair = collections.Counter((m[0], m[1]) for m in members)
    keys = []
    for cls, name, start, _ in members:
        if by_name[name] == 1:
            keys.append(name)
        elif by_pair[(cls, name)] == 1:
            keys.append(f"{cls}.{name}")
        else:
            keys.append(f"{cls}.{name}@{start}")
    return keys


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
        self.before = self.raw.get("before", {})
        with open(self.source) as fh:
            self.lines = fh.read().split("\n")
        self.n = len(self.lines)
        self.members = member_map(self.source)
        self.keys = member_keys(self.members)
        self._check_plan()
        self._tile()

    def _check_plan(self):
        """Refuse a plan that does not describe THIS file.

        A stray `cp` once left a plan for a different file in place mid-split;
        two of three tools ran to completion and printed confidently
        mislabelled output. Only the tool that asserted this refused.
        """
        have = set(self.keys)
        want = set(self.plan)
        if have != want:
            msg = ["FATAL: the spec does not describe this file."]
            # THE OVERWHELMINGLY LIKELY CAUSE, because writeparts.py writes the
            # primary part OVER the source: the split has already been run once,
            # so `source` now holds one part instead of the whole file. Say so,
            # rather than printing sixty "in spec, not in file" lines and
            # leaving the reader to work it out. (It cost a cycle once.)
            outputs = {os.path.join(self.outdir, cfg["file"])
                       for cfg in self.parts.values()}
            if os.path.abspath(self.source) in {os.path.abspath(o) for o in outputs}:
                msg.append("")
                msg.append("  NOTE: this spec's source is ALSO one of its part "
                           "files, so writeparts.py has probably already run and "
                           "overwritten it.")
                msg.append(f"  Restore the original first:  git checkout "
                           f"{os.path.relpath(self.source)}")
                msg.append("")
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
        self._check_class_doc()
        self._check_modifiers()

    def _check_modifiers(self):
        """Each part must redeclare the class with the ORIGINAL's modifiers.

        writeparts once hardcoded "public", so a `public static class` came out
        as `public class` -- a semantically different type. The decompile oracle
        would eventually catch it (a static class is abstract+sealed), but only
        after a rebuild, and only if someone read the diff. This refuses first,
        and names the modifiers it expected.
        """
        for cfg in self.parts.values():
            name = cfg["class"]
            pat = re.compile(r'^\s*((?:(?:public|internal|private|protected|'
                             r'static|sealed|abstract|partial|unsafe|file)\s+)*)'
                             r'class\s+' + re.escape(name) + r'\b')
            want = None
            for line in self.lines:
                m = pat.match(line)
                if m:
                    want = [w for w in m.group(1).split() if w != "partial"]
                    break
            if want is None:
                continue          # a part may declare a class the source lacks
            got = cfg.get("modifiers", "public").split()
            if got != want:
                raise SystemExit(
                    f"FATAL: part {cfg['file']!r} would declare {name} as "
                    f"{' '.join(got)}, but the source declares it "
                    f"{' '.join(want)}. Set \"modifiers\" on the part.")

    def _check_class_doc(self):
        """At most one part may carry the class-level /// doc.

        The compiler concatenates the /// of every part of a partial class into
        ONE type entry, so two parts carrying it produce a spliced summary that
        reads as prose and is not. Caught on the SelfTest split by diffing the
        emitted XML; asserted here so it cannot recur silently.

        HONEST SCOPE: two class_doc entries almost always overlap on the same
        lines, and the tiling would refuse them anyway as a duplicated line.
        What this adds is the MESSAGE -- it runs before _tile, so the reader
        sees "the compiler concatenates these" instead of "line 5 is claimed
        twice", which does not point at the defect at all.
        """
        docs = [syn for syn in self.raw.get("synthetic", [])
                if syn.get("emit") == "class_doc"]
        for syn in docs:
            if syn["part"] not in self.parts:
                raise SystemExit(f"FATAL: class_doc assigned to undeclared "
                                 f"part {syn['part']!r}")
        if len(docs) > 1:
            where = ", ".join(sorted(d["part"] for d in docs))
            raise SystemExit(
                f"FATAL: {len(docs)} parts carry emit=class_doc ({where}). The "
                f"compiler concatenates the /// of every part into one type "
                f"entry, so this splices summaries together. Keep it on one.")
        self.class_doc = docs[0] if docs else None

    def _tile(self):
        self.owner = {}
        self.member_blocks = {}
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
        for (cls, name, s, e), key in zip(self.members, self.keys):
            part = self.plan[key]
            a = s
            fg = self.forward_gaps.get(key)
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
            self.member_blocks[key] = (a, end)
        for v in self.blocks.values():
            v.sort()
        self._apply_before()
        self.unassigned = [i for i in range(1, self.n + 1) if i not in self.owner]

    def _apply_before(self):
        """Move a member's block to sit immediately ahead of another's."""
        for mover, anchor in self.before.items():
            for who in (mover, anchor):
                if who not in self.plan:
                    raise SystemExit(f'FATAL: "before" names {who!r}, which is '
                                     f'not a member of this file')
            if self.plan[mover] != self.plan[anchor]:
                raise SystemExit(
                    f'FATAL: "before" moves {mover!r} ahead of {anchor!r}, but '
                    f'they are in different parts ({self.plan[mover]} and '
                    f'{self.plan[anchor]}). Reassign the member instead.')
            part = self.plan[mover]
            blocks = self.blocks[part]
            mb = self.member_blocks[mover]
            ab = self.member_blocks[anchor]
            blocks.remove(mb)
            blocks.insert(blocks.index(ab), mb)

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
