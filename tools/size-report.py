#!/usr/bin/env python3
"""Advisory size report: files over a line budget, methods over a code budget.

Two limits, two metrics, and that pairing is the point rather than an oddity:

  FILE     -> TOTAL lines.  The rationale is CONTEXT COST. An agent loading a
              file loads its comments too, so comments are part of the cost and
              must be counted.
  FUNCTION -> CODE lines.   The rationale is COMPREHENSIBILITY, which is about
              the code. This repo carries heavy in-body prose, so counting
              comments here would penalise the one practice the project rests
              on -- measured: 40 real-logic methods exceed 60 TOTAL lines, but
              only 21 exceed 60 CODE lines. DriveState is 176 total and 67 code.
              The same instinct already lives in `wire-surface-hash`, which
              strips comment lines so documenting a message cannot fail a build.

DELTA RATCHET. Reports only what THIS commit made worse: something newly over a
limit, or something already over that grew. Existing debt stays silent until
touched, because 67% of code commits here touch a file over the file budget and
a gate that fires two times in three is a tax rather than a guard.

MEMBER IDENTITY is (enclosing type, name, arity), matched ANYWHERE in the tree.
Without that, relocating an over-limit method reads as brand new to a
file-scoped diff and the ratchet fires on every split commit -- self-defeating
for a program whose whole purpose is splitting files.

ADVISORY. Never wired into `dist`, and it exits 0 even when it reports. It gates
nothing, because the thing it would gate is `deploy`, and `deploy` gates the
two-instance playtest rig -- the scarcest resource in this project. A feature
branch adding one line to a 2343-line session class must not be unable to
playtest until that class is split mid-milestone.
"""
import re, subprocess, sys, collections

FILE_LIMIT = 500
FUNC_LIMIT = 60

DECL_START = re.compile(
    r'^(?P<indent>[ \t]*)'
    r'(?!.*\b(?:if|for|foreach|while|switch|catch|using|lock|fixed|do|return|new)\s*\()'
    r'(?:\[[^\]]*\]\s*)*'
    r'(?:(?:public|private|protected|internal|static|sealed|override|virtual|'
    r'async|extern|unsafe|new|partial|readonly)\s+)+'
    r'[\w<>\[\],\.\?\s]*?'
    r'(?P<name>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(')
TYPE = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|'
                  r'static|sealed|abstract|partial|readonly|record)\s+)*'
                  r'(?:class|struct|interface|record)\s+(?P<name>[A-Za-z_]\w*)')


def arity(params):
    p = params.strip()
    if not p:
        return 0
    depth, n = 0, 1
    for ch in p:
        if ch in '<([':
            depth += 1
        elif ch in '>)]':
            depth -= 1
        elif ch == ',' and depth == 0:
            n += 1
    return n


def shape(path, name, body):
    """Why a method is allowed to be long. Settled BEFORE the gate ships, because
    an open exemption question inside a live guard is a false-positive factory."""
    if path.startswith('tests/'):
        return 'test'
    if path.endswith('SelfTest.cs') and name.startswith('Run'):
        return 'selftest scenario'
    if len(re.findall(r'^\s*case\s', body, re.M)) >= 5:
        return 'closed-set dispatch'
    if len(re.findall(r'\.Append', body)) >= 15:
        return 'diagnostic dump'
    return None


def measure(files):
    """files: {path: text}. Returns (file_sizes, {identity: (path, code_lines)})."""
    sizes, methods = {}, {}
    seen_any = False
    for path, text in files.items():
        lines = text.split("\n")
        sizes[path] = len(lines)
        enclosing, i = [], 0
        while i < len(lines):
            t = TYPE.match(lines[i])
            if t:
                enclosing.append(t.group('name'))
            m = DECL_START.match(lines[i])
            if m and not TYPE.match(lines[i]):
                # Accumulate until the parameter list closes. 125 declaration
                # sites in this repo wrap their parameters, and a pattern that
                # required them to close on the declaration line skipped every
                # one -- so the report counted fewer methods than exist and a
                # long method with a wrapped signature could never be flagged.
                depth, sig_end, buf = 0, None, []
                for k in range(i, min(i + 25, len(lines))):
                    buf.append(lines[k])
                    depth += lines[k].count('(') - lines[k].count(')')
                    if depth == 0:
                        sig_end = k
                        break
                sig = "\n".join(buf)
                if sig_end is not None and ';' not in sig and '=>' not in sig:
                    j = sig_end + 1
                    while j < len(lines) and (lines[j].strip() == ''
                                              or lines[j].strip().startswith('where ')):
                        j += 1
                    if j < len(lines) and lines[j].strip() == '{':
                        depth, end = 0, None
                        for k in range(j, len(lines)):
                            depth += lines[k].count('{') - lines[k].count('}')
                            if depth == 0:
                                end = k
                                break
                        if end:
                            body = "\n".join(lines[i:end + 1])
                            code = len([l for l in lines[i:end + 1]
                                        if l.strip() and not l.strip().startswith('//')])
                            owner = enclosing[-1] if enclosing else '?'
                            params = sig[sig.index('(') + 1:sig.rindex(')')]
                            ident = (owner, m.group('name'), arity(params))
                            methods[ident] = (path, code, shape(path, m.group('name'), body))
                            seen_any = True
                            i = end
            i += 1
    # Positive control: a matcher that matches nothing reports "no violations",
    # which is the same output as "everything is fine". Refuse instead.
    if files and not seen_any:
        sys.exit("FATAL: matched no methods at all — the declaration regex has rotted.")
    return sizes, methods


def from_disk(paths):
    out = {}
    for p in paths:
        try:
            out[p] = open(p, encoding='utf-8').read()
        except OSError:
            pass
    return out


def from_rev(rev, paths):
    out = {}
    for p in paths:
        r = subprocess.run(['git', 'show', f'{rev}:{p}'], capture_output=True, text=True)
        if r.returncode == 0:
            out[p] = r.stdout
    return out


def tracked(rev=None):
    cmd = ['git', 'ls-tree', '-r', '--name-only', rev or 'HEAD', '--', 'src', 'tools', 'tests']
    return [p for p in subprocess.run(cmd, capture_output=True, text=True).stdout.split("\n")
            if p.endswith('.cs') and '/obj/' not in p and '/bin/' not in p]



def compare(was_sizes, was_methods, now_sizes, now_methods):
    """The ratchet itself, separated from git so the fixture can drive it."""
    findings = []
    for path, n in sorted(now_sizes.items()):
        was = was_sizes.get(path)
        if n <= FILE_LIMIT:
            continue
        if was is None:
            findings.append(f"  NEW FILE over {FILE_LIMIT}: {path} ({n} lines)")
        elif was <= FILE_LIMIT:
            findings.append(f"  CROSSED {FILE_LIMIT}: {path} ({was} -> {n} lines)")
        elif n > was:
            findings.append(f"  GREW while over: {path} ({was} -> {n} lines)")

    for ident, (path, code, sh) in sorted(now_methods.items()):
        if sh or code <= FUNC_LIMIT:
            continue
        prev = was_methods.get(ident)
        owner, name, ar = ident
        label = f"{path}  {owner}.{name}/{ar}"
        if prev is None:
            findings.append(f"  NEW METHOD over {FUNC_LIMIT} code lines: {label} ({code})")
        elif prev[1] <= FUNC_LIMIT:
            findings.append(f"  CROSSED {FUNC_LIMIT} code lines: {label} ({prev[1]} -> {code})")
        elif code > prev[1]:
            # Identity is tree-wide, so a method that only MOVED lands here with
            # prev[1] == code and is silent. That is the case the fixture pins.
            findings.append(f"  GREW while over: {label} ({prev[1]} -> {code})")
    return findings


def selftest():
    """Every case made to fail, or pass, deliberately. A guard whose negative
    case was never seen to fail is an assertion about nothing."""
    over = ('T', 'M', 1)
    ok = 0

    def check(name, got, want):
        nonlocal ok
        hit = bool(got)
        print(f"  {'ok  ' if hit == want else 'FAIL'}  {name}")
        ok += 0 if hit == want else 1

    # ⭐ THE MANDATORY FIXTURE: an over-limit method MOVED between files, body
    # identical. Must be SILENT, or the ratchet fires on every split commit and
    # the program it exists to support cannot proceed.
    check("moved over-limit method, body identical -> silent",
          compare({}, {over: ('a.cs', 99, None)}, {}, {over: ('b.cs', 99, None)}), False)
    check("moved AND grew -> reported",
          compare({}, {over: ('a.cs', 99, None)}, {}, {over: ('b.cs', 120, None)}), True)
    check("method crossed the limit -> reported",
          compare({}, {over: ('a.cs', 10, None)}, {}, {over: ('a.cs', 99, None)}), True)
    check("brand new over-limit method -> reported",
          compare({}, {}, {}, {over: ('a.cs', 99, None)}), True)
    check("already over, unchanged -> silent (existing debt)",
          compare({}, {over: ('a.cs', 99, None)}, {}, {over: ('a.cs', 99, None)}), False)
    check("already over, SHRANK -> silent",
          compare({}, {over: ('a.cs', 99, None)}, {}, {over: ('a.cs', 70, None)}), False)
    check("exempt shape, huge -> silent",
          compare({}, {}, {}, {over: ('a.cs', 999, 'closed-set dispatch')}), False)
    check("file crossed the limit -> reported",
          compare({'a.cs': 400}, {}, {'a.cs': 501}, {}), True)
    check("file already over, grew -> reported",
          compare({'a.cs': 600}, {}, {'a.cs': 601}, {}), True)
    check("file already over, unchanged -> silent",
          compare({'a.cs': 600}, {}, {'a.cs': 600}, {}), False)

    # PARSING, not just the ratchet. The synthetic cases above drive compare()
    # and would pass with a parser that saw no methods at all. This case pins the
    # defect that shipped in the first version: a wrapped parameter list made a
    # method invisible, so the report silently measured a smaller codebase than
    # it had. 65 methods here were hidden that way.
    wrapped = """namespace N {
    internal static class C {
        private static int Wrapped(
            int a,
            int b)
        {
            return a + b;
        }
    }
}"""
    _, m = measure({'w.cs': wrapped})
    check("method with a WRAPPED parameter list is seen", m, True)
    check("  ... and its arity is right", [k for k in m if k == ('C', 'Wrapped', 2)], True)

    # The instrument's own vacuity control, exercised rather than trusted.
    try:
        measure({'x.cs': 'namespace N { }'})
        print("  FAIL  empty-match refusal")
        ok += 1
    except SystemExit:
        print("  ok    matcher that finds nothing REFUSES rather than reporting clean")

    print(f"size-report selftest: {'ALL PASS' if ok == 0 else str(ok) + ' FAILED'}")
    return ok


def census():
    """Every current violation, ignoring the ratchet. For triage and for
    setting limits -- never for gating, since it reports standing debt."""
    sizes, methods = measure(from_disk(tracked()))
    big = sorted(((n, p) for p, n in sizes.items() if n > FILE_LIMIT), reverse=True)
    shapes = collections.Counter()
    over = []
    for (owner, name, ar), (path, code, sh) in methods.items():
        if code > FUNC_LIMIT:
            shapes[sh or 'REAL LOGIC'] += 1
            if not sh:
                over.append((code, path, f"{owner}.{name}/{ar}"))
    print(f"{len(sizes)} files, {len(methods)} methods")
    print(f"\nfiles over {FILE_LIMIT} total lines: {len(big)}")
    for n, p in big[:10]:
        print(f"  {n:>5}  {p}")
    print(f"\nmethods over {FUNC_LIMIT} code lines, by shape:")
    for k, v in shapes.most_common():
        print(f"  {v:>4}  {k}")
    print(f"\nenforceable (no exempt shape): {len(over)}")
    for n, path, label in sorted(over, reverse=True)[:10]:
        print(f"  {n:>5}  {path}  {label}")
    return 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == '--selftest':
        return selftest()
    if len(sys.argv) > 1 and sys.argv[1] == '--census':
        return census()
    base = sys.argv[1] if len(sys.argv) > 1 else 'HEAD^'
    if subprocess.run(['git', 'rev-parse', '--verify', '--quiet', base],
                      capture_output=True).returncode != 0:
        # No parent (initial commit) or a bad ref: report nothing rather than
        # everything. A ratchet with no baseline that falls back to "flag it all"
        # is how a guard gets switched off in week one.
        print(f"size report: no baseline at '{base}' — nothing to compare, skipping.")
        return 0

    now_sizes, now_methods = measure(from_disk(tracked()))
    was_sizes, was_methods = measure(from_rev(base, tracked(base)))

    findings = compare(was_sizes, was_methods, now_sizes, now_methods)

    if not findings:
        print(f"size report: nothing crossed or grew since {base}. "
              f"({len(now_sizes)} files, {len(now_methods)} methods)")
        return 0
    print(f"size report vs {base} — ADVISORY, this gates nothing:")
    for f in findings:
        print(f)
    print(f"  ({len(findings)} item(s). Existing debt is deliberately silent until touched.)")
    return 0


if __name__ == '__main__':
    sys.exit(main())
