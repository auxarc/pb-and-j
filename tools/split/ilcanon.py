#!/usr/bin/env python3
"""Canonicalise an ILSpy decompile BY MEMBER, never by line.

THE ORACLE FOR A PURE MOVE, and it makes a stronger claim than any runtime
sample: not "one frame looked right" but "the compiled code is unchanged". It
needs no playtest rig, which is the scarcest resource in this project.

  DOTNET_ROLL_FORWARD=LatestMajor ilspycmd <dll> > a.cs   # before
  DOTNET_ROLL_FORWARD=LatestMajor ilspycmd <dll> > b.cs   # after
  ilcanon.py a.cs > a.txt && ilcanon.py b.cs > b.txt && diff a.txt b.txt

Splitting a class reorders its members in the decompile, so records are sorted;
a pure move is then byte-identical and a single added statement is not.

PROVE IT BITES BEFORE TRUSTING IT, and use a statement that COMPILES: an
earlier bite test used `var unused = 1+1;`, which fails the build under
TreatWarningsAsErrors, so the dll was never rebuilt and the diff compared the
old dll to itself -- a clean zero for entirely the wrong reason.

AssemblyInformationalVersion is dropped: SourceLink stamps the commit there, so
across a commit boundary a clean pure move otherwise reads as CHANGED.

KNOWN BLIND SPOT, verify it by reading every time: static initialiser ORDER.
ILSpy renders `static readonly` initialisers as single-line members, which this
tool SORTS -- so a reordered .cctor, the one thing splitting a partial class
can really change, is invisible here.
"""
import re
import sys

MIN_RECORDS = 200  # a decompile of any real assembly has far more than this

TYPE = re.compile(
    r'^\s*(?:(?:public|private|internal|protected|sealed|static|abstract|'
    r'partial|readonly|ref|unsafe|file)\s+)*(?:class|struct|interface|enum|'
    r'record)\s+([\w<>,\s]+)')
NS = re.compile(r'^\s*namespace\s+([\w.]+)')


def canon(path):
    with open(path) as fh:
        text = fh.read()
    text = re.sub(r'^\[assembly: AssemblyInformationalVersion.*\n', '', text,
                  flags=re.M)
    lines = text.split("\n")

    def block_end(i):
        d, j, seen = 0, i, False
        while j < len(lines):
            d += lines[j].count("{") - lines[j].count("}")
            if lines[j].count("{"):
                seen = True
            if seen and d <= 0:
                return j
            j += 1
        return len(lines) - 1

    records = []

    def walk(lo, hi, prefix):
        i = lo
        while i <= hi:
            line = lines[i]
            s = line.strip()
            if not s or s.startswith("//"):
                i += 1
                continue
            ns = NS.match(line)
            if ns:
                k = i
                while k <= hi and "{" not in lines[k]:
                    k += 1
                e = block_end(k)
                walk(k + 1, e - 1, prefix + ns.group(1).strip() + ".")
                i = e + 1
                continue
            m = TYPE.match(line)
            if m and "{" in "".join(lines[i:i + 3]):
                k = i
                while k <= hi and "{" not in lines[k]:
                    k += 1
                e = block_end(k)
                walk(k + 1, e - 1, prefix + m.group(1).strip() + "::")
                i = e + 1
                continue
            start = i
            while start > lo and lines[start - 1].strip().startswith("["):
                start -= 1
            if "{" in line:
                e = block_end(i)
            elif line.rstrip().endswith(";"):
                e = i
            else:
                k = i
                while (k <= hi and "{" not in lines[k]
                       and not lines[k].rstrip().endswith(";")):
                    k += 1
                e = block_end(k) if k <= hi and "{" in lines[k] else k
            records.append(prefix + "\n" +
                           "\n".join(l.rstrip() for l in lines[start:e + 1]))
            i = e + 1

    walk(0, len(lines) - 1, "")
    return sorted(records)


def main(path):
    records = canon(path)
    if len(records) < MIN_RECORDS:
        raise SystemExit(f"VACUOUS: only {len(records)} member records from "
                         f"{path} -- the walker did not descend, and an empty "
                         f"comparison is not a clean one")
    print(f"# {len(records)} member records")
    print("\n@@@@\n".join(records))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
