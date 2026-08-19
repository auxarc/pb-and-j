#!/usr/bin/env python3
"""Where is each helper actually CALLED from? The grouping check.

THE ORACLES CANNOT CHECK THE GROUPING. A split can be byte-identical under the
decompile diff, green on every test, and have every member in the wrong file.
This is the substitute: a stated, mechanical placement rule -- a helper stays
with a part only if that part is effectively its sole user (>= THRESHOLD of
call sites); anything else is shared fixture in the primary -- checked against
every declaration instead of argued case by case.

ONLY THE SHAPE OF A CALL IS EVIDENCE OF A CALL. Three generations of this tool
were wrong, each in a new way:
  v1  bare identifiers          -> counted `// see Foo` and <see cref="Foo"/>
  v2  comments/strings stripped -> still counted `State.Executing` (an enum
                                   member) and LINQ `.Select(` as calls
  v3  a call SHAPE: an identifier NOT preceded by '.', an optional <...>, a '('
The verdict survived every correction, which made it a coincidence rather than
a proof until the shape was right.

SUBTRACTING THE DECLARATION NEEDS A SHAPE TOO -- found by this kit's own
selftest on its first run. Matching "a modifier, then anything, then the name"
let `void Caller() { Other.Target(2); }` count as a DECLARATION of Target,
because `[^\n]*` walked straight over the real call on the same line. The count
came out at MINUS ONE. A declaration is now recognised by what precedes the
name on its line: type and modifier words only, no '.', '(', '=' or '{'.

THE VACUITY GUARD IS ON THE STRIPPER, NOT ON THE INPUT. Asserting "the strip
removed something" sounds right and is wrong: a file with no comments strips to
itself, and the tool refused perfectly good input. The stripper is instead
proven against a canary on every run, which is what "it works" actually means.

The strip refuses verbatim @"..." strings outright: on those an earlier version
inverted code and prose, blanking real calls and leaving a string's contents
standing as code.

Usage: ownership.py Helper1,Helper2 <file> [<file> ...]
"""
import os
import re
import sys

THRESHOLD = 90  # percent of call sites in one file to count as sole user


def strip(text, path):
    if re.search(r'@"', text):
        raise SystemExit(f'REFUSING {path}: a verbatim @"..." string is here '
                         f'and this stripper cannot parse one safely')
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and text[i + 1] == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                if text[i] == '\n':
                    out.append('\n')
                i += 1
            i += 2
        elif c == '"':
            out.append('""')
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == '\\' else 1
            i += 1
        elif c == "'":
            out.append("''")
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == '\\' else 1
            i += 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


CANARY = '''x(); // c(); /* d(); */ var s = "e();";'''


def prove_stripper():
    """The strip must be shown to WORK on every run.

    A stripper that has quietly become a no-op leaves comments and strings
    standing as code, and its output reads exactly like a clean file.
    """
    got = strip(CANARY, "<canary>")
    if "c()" in got or "d()" in got or "e()" in got or "x()" not in got:
        raise SystemExit(f"VACUOUS: the stripper failed its own canary: {got!r}")


DECL_PREFIX = re.compile(r'^[\w<>,\[\]\?\*\s]*$')


def is_declaration(line, at):
    """Is the call-shape hit at offset `at` on this line a DECLARATION?

    Only if everything before the name is type and modifier words. A '.', '(',
    '=' or '{' before it means the line is doing something else and merely
    mentions the name -- which is how the count once reached minus one.

    AND THE PREFIX MUST NOT BE EMPTY. Whitespace is in the class above, so bare
    indentation matched it and every helper invoked as a STATEMENT --

        Handshake(host);

    -- was subtracted as its own declaration. On ScenarioTransferTests.cs that
    hid 11 of Handshake's 15 call sites and reported a confident 4, which is
    the difference between "belongs in this part" (93%) and "shared fixture"
    (80%). A declaration always carries a return type or a modifier; a bare
    call carries nothing. Found by a grep disagreeing with the tool.
    """
    before = line[:at]
    if not before.strip():
        return False
    return bool(DECL_PREFIX.match(before))


def call_sites(helpers, files):
    prove_stripper()
    removed = 0
    counts = {h: {} for h in helpers}
    for f in files:
        with open(f) as fh:
            raw = fh.read()
        s = strip(raw, f)
        removed += len(raw) - len(s)
        for h in helpers:
            pat = re.compile(r'(?<![\w.])' + re.escape(h) +
                             r'\s*(?:<[^;{}()]*>)?\s*\(')
            calls = 0
            for line in s.split("\n"):
                for hit in pat.finditer(line):
                    if not is_declaration(line, hit.start()):
                        calls += 1
            counts[h][os.path.basename(f)] = calls
    return counts, removed


def main(argv):
    helpers = argv[1].split(',')
    files = argv[2:]
    counts, removed = call_sites(helpers, files)
    print(f"stripped {removed} chars of comment and string text from "
          f"{len(files)} files")
    rc = 0
    for h in helpers:
        rows = {k: v for k, v in counts[h].items() if v}
        total = sum(rows.values())
        print(f"\n{h}: {total} call sites across {len(rows)} file(s)")
        if total == 0:
            print("    NO CALL SITES AT ALL -- dead, or the shape is wrong")
            rc = 1
            continue
        for k, v in sorted(rows.items(), key=lambda kv: -kv[1]):
            pct = 100 * v // total
            mark = "  <- sole user, belongs here" if pct >= THRESHOLD else ""
            print(f"    {v:4d}  {k}   ({pct}%){mark}")
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
