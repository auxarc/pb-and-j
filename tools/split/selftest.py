#!/usr/bin/env python3
"""Self-test for the split kit. Every case is a defect that ACTUALLY BIT.

The tools in this directory have a bad history: four separate defects were
found only by using them, each after passing its positive test. So the cases
below are negative ones -- what must be REFUSED -- and each names the incident
it comes from. A tool that cannot fail is not a check.

  python3 tools/split/selftest.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True   # keep __pycache__ out of the tree entirely

HERE = os.path.dirname(os.path.abspath(__file__))
PASS, FAIL = [], []


def check(name, cond, detail=""):
    (PASS if cond else FAIL).append(name)
    print(f"  {'ok  ' if cond else 'FAIL'}  {name}" + (f"  [{detail}]" if detail and not cond else ""))


def run(script, *args, env=None):
    e = dict(os.environ)
    e["PYTHONDONTWRITEBYTECODE"] = "1"   # subprocesses would litter too
    if env:
        e.update(env)
    p = subprocess.run([sys.executable, os.path.join(HERE, script), *args],
                       capture_output=True, text=True, env=e)
    return p.returncode, p.stdout + p.stderr


SAMPLE = '''using Xunit;

namespace Demo
{
    public class SampleTests
    {
        private static string Helper(
            string a,
            string b)
        {
            return a + b;
        }

        [Fact]
        public void First_Works()
        {
            Assert.Equal("ab", Helper("a", "b"));
        }

        // --- a banner that heads what follows ---

        [Fact]
        public void Second_Works()
        {
            Assert.Equal("cd", Helper("c", "d"));
        }
    }
}
'''


def spec_for(tmp, members, synthetic, parts, forward_gaps=None):
    return {
        "root": tmp,
        "source": "SampleTests.cs",
        "outdir": ".",
        "namespace": "Demo",
        "members": members,
        "synthetic": synthetic,
        "forward_gaps": forward_gaps or {},
        "parts": parts,
    }


def write_spec(tmp, obj, name="spec.json"):
    path = os.path.join(tmp, name)
    with open(path, "w") as fh:
        json.dump(obj, fh)
    return path


def main():
    sys.path.insert(0, HERE)
    from splitspec import member_map

    tmp = tempfile.mkdtemp(prefix="pbj-splitkit-selftest-")
    src = os.path.join(tmp, "SampleTests.cs")
    with open(src, "w") as fh:
        fh.write(SAMPLE)

    print("member map")
    mm = member_map(src)
    names = [m[1] for m in mm]
    check("finds a member whose signature WRAPS over three lines "
          "(size-report missed 65 of these)", "Helper" in names, str(names))
    check("finds all three members", len(mm) == 3, str(names))
    first = [m for m in mm if m[1] == "First_Works"][0]
    check("pulls a member's start back over its [Fact]",
          SAMPLE.split("\n")[first[2] - 1].strip() == "[Fact]")

    GOOD_PARTS = {
        "primary": {"file": "SampleTests.cs", "class": "SampleTests",
                    "partial": True, "usings": ["Xunit"], "header": "primary"},
        "second": {"file": "SampleTests.Second.cs", "class": "SampleTests",
                   "partial": True, "usings": ["Xunit"], "header": "second"},
    }
    GOOD_MEMBERS = {"Helper": "primary", "First_Works": "primary",
                    "Second_Works": "second"}
    NLINES = len(SAMPLE.split("\n"))
    GOOD_SYNTH = [
        {"lines": [1, 5], "part": "primary", "why": "usings + namespace"},
        {"lines": [6, 6], "part": "primary", "why": "class open brace"},
        {"lines": [NLINES - 2, NLINES], "part": "primary", "why": "closers"},
    ]
    FWD = {"Second_Works": [20, 22]}

    print("stale-plan guard (a stray cp left a plan for a DIFFERENT file "
          "mid-split; two of three tools ran on happily)")
    bad = dict(GOOD_MEMBERS)
    bad["NotInThisFile"] = "primary"
    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, bad, GOOD_SYNTH, GOOD_PARTS, FWD), "s1.json"))
    check("refuses a spec naming a member the file does not have",
          rc != 0 and "in spec, not in file" in out, out[:200])

    short = {k: v for k, v in GOOD_MEMBERS.items() if k != "Helper"}
    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, short, GOOD_SYNTH, GOOD_PARTS, FWD), "s2.json"))
    check("refuses a spec missing a member the file has",
          rc != 0 and "in file, not in spec" in out, out[:200])

    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, {**GOOD_MEMBERS, "Helper": "nosuchpart"}, GOOD_SYNTH, GOOD_PARTS,
        FWD), "s3.json"))
    check("refuses a member assigned to an undeclared part",
          rc != 0 and "undeclared parts" in out, out[:200])

    print("partition")
    good = write_spec(tmp, spec_for(tmp, GOOD_MEMBERS, GOOD_SYNTH, GOOD_PARTS,
                                    FWD), "good.json")
    rc, out = run("partition.py", good)
    check("a sound spec passes", rc == 0 and "partition OK" in out, out[:300])

    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, GOOD_MEMBERS, GOOD_SYNTH[:1] + [
            {"lines": [1, 3], "part": "second", "why": "overlaps on purpose"}
        ] + GOOD_SYNTH[1:], GOOD_PARTS, FWD), "dup.json"))
    check("reports a line claimed by two parts",
          rc != 0 and "DUPLICATED" in out, out[:300])

    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, GOOD_MEMBERS, GOOD_SYNTH[1:], GOOD_PARTS, FWD), "gap.json"))
    check("reports lines assigned to no part at all",
          rc != 0 and "UNASSIGNED" in out, out[:300])

    print("gap direction (a dropped comment is the split defect NO other "
          "oracle sees)")
    rc, out = run("partition.py", write_spec(tmp, spec_for(
        tmp, GOOD_MEMBERS, GOOD_SYNTH, GOOD_PARTS, None), "nofwd.json"))
    check("fails on a banner in a gap whose direction nobody decided",
          rc != 0, out[:300])
    check("and EXPLAINS it -- naming the line and quoting the banner, not just "
          "calling it unassigned",
          "NOT DECIDED" in out and "a banner that heads what follows" in out,
          out[:300])
    check("by the expected mechanism: blank-line absorption stops dead at "
          "content, so the banner lands UNASSIGNED rather than being swallowed",
          "UNASSIGNED line 20" in out and "INVARIANT BROKEN" not in out,
          out[:300])
    rc, out = run("partition.py", good)
    check("accepts it once the spec names the direction",
          rc == 0 and "direction named in the spec" in out, out[:300])

    print("writeparts")
    rc, out = run("writeparts.py", write_spec(tmp, spec_for(
        tmp, GOOD_MEMBERS, GOOD_SYNTH[1:], GOOD_PARTS, FWD), "gap2.json"))
    check("REFUSES to write when the tiling is not a proven partition",
          rc != 0 and "REFUSING" in out, out[:300])

    rc, out = run("writeparts.py", good)
    check("writes every declared part", rc == 0 and
          os.path.exists(os.path.join(tmp, "SampleTests.Second.cs")), out[:300])

    print("total content")
    orig_copy = os.path.join(tmp, "original.cs")
    shutil.copy(os.path.join(HERE, "selftest.py"), orig_copy)  # placeholder
    with open(orig_copy, "w") as fh:
        fh.write(SAMPLE)
    parts = [os.path.join(tmp, "SampleTests.cs"),
             os.path.join(tmp, "SampleTests.Second.cs")]
    rc, out = run("totalcontent.py", orig_copy, *parts)
    check("REFUSES without PBJ_EXPECT_LOST (an earlier version always exited 0)",
          rc == 2 and "REFUSING" in out, out[-200:])
    rc, out = run("totalcontent.py", orig_copy, *parts,
                  env={"PBJ_EXPECT_LOST": "1"})
    check("passes with the one declared loss: the class line that gained "
          "`partial`", rc == 0 and "OK: losses match" in out, out[-300:])
    rc, out = run("totalcontent.py", orig_copy, parts[0],
                  env={"PBJ_EXPECT_LOST": "1"})
    check("FAILS when a whole part file is left out",
          rc == 1 and "FAIL:" in out, out[-200:])
    rc, out = run("totalcontent.py", orig_copy, env={"PBJ_EXPECT_LOST": "1"})
    check("refuses when given no part files at all",
          rc != 0 and "REFUSING" in out, out[-200:])

    print("ownership (three generations of this were wrong; only the SHAPE of "
          "a call is evidence of a call)")
    prose = os.path.join(tmp, "prose.cs")
    with open(prose, "w") as fh:
        fh.write('''class A {
    /// <summary>See <see cref="Target"/> for the walk.</summary>
    // Target(x) is deliberately NOT shared with this one.
    void Caller() { var s = "Target(1)"; Other.Target(2); items.Select(x => x); }
}
''')
    rc, prose_out = run("ownership.py", "Target", prose)
    check("counts no call from a <see cref>, a // mention, a string, or a "
          "dotted Other.Target(", "NO CALL SITES AT ALL" in prose_out,
          prose_out[:400])
    check("never reports a NEGATIVE count -- subtracting the declaration once "
          "matched the real call on the SAME LINE and the total reached minus "
          "one", "-1  " not in prose_out and "-1 call" not in prose_out,
          prose_out[:400])

    real = os.path.join(tmp, "real.cs")
    with open(real, "w") as fh:
        fh.write('class B { void C() { Target(1); Target(2); } }\n')
    rc, out = run("ownership.py", "Target", real)
    check("counts a real call", "2 call sites" in out, out[:300])

    verbatim = os.path.join(tmp, "verbatim.cs")
    with open(verbatim, "w") as fh:
        fh.write('class C { string s = @"Target(1)"; }\n')
    rc, out = run("ownership.py", "Target", verbatim)
    check('REFUSES a verbatim @"..." string rather than mis-parsing it',
          rc != 0 and "REFUSING" in out, out[:300])

    nostrip = os.path.join(tmp, "nostrip.cs")
    with open(nostrip, "w") as fh:
        fh.write('class D { void E() { Target(1); } }\n')
    rc, out = run("ownership.py", "Target", nostrip)
    check("counts a comment-free file normally instead of calling it vacuous",
          rc == 0 and "1 call sites" in out, out[:300])

    import ownership
    check("the stripper proves itself on a canary before any counting",
          ownership.prove_stripper() is None)
    real_strip = ownership.strip
    try:
        ownership.strip = lambda text, path: text          # a no-op stripper
        broke = False
        try:
            ownership.prove_stripper()
        except SystemExit:
            broke = True
        check("and that canary CATCHES a stripper gone no-op, which would "
              "leave comments standing as code", broke)
    finally:
        ownership.strip = real_strip

    print("ilcanon")
    tiny = os.path.join(tmp, "tiny.cs")
    with open(tiny, "w") as fh:
        fh.write("namespace N { class C { void M() { } } }\n")
    rc, out = run("ilcanon.py", tiny)
    check("REFUSES a decompile it barely parsed, rather than reporting a "
          "clean empty comparison", rc != 0 and "VACUOUS" in out, out[:300])

    shutil.rmtree(tmp)
    print(f"\nsplit kit selftest: {len(PASS)} passed, {len(FAIL)} failed")
    for f in FAIL:
        print(f"  FAILED: {f}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
