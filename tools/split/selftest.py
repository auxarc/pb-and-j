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
        private readonly FakeThing thing = new FakeThing();
        private Session session = null!;

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


DOC_SAMPLE = '''using Xunit;

namespace Demo
{
    /// <summary>
    /// The type's own doc, which the compiler concatenates across parts.
    /// </summary>
    public class DocTests
    {
        private static string Helper()
        {
            return "x";
        }

        [Fact]
        public void First_Works()
        {
            Assert.Equal("x", Helper());
        }

        [Fact]
        public void Second_Works()
        {
            Assert.Equal("x", Helper());
        }
    }
}
'''


OV_SAMPLE = '''using Xunit;

namespace Demo
{
    public class OvTests
    {
        private static string Show(int i)
        {
            return "i";
        }

        private static string Show(string s)
        {
            return s;
        }

        [Fact]
        public void First_Works()
        {
            Assert.Equal("i", Show(1));
        }
    }
}
'''


# Written the way REAL source is written, not the shortest thing that parses.
# A control case that cannot reach the shape it guards is not a control: the
# ownership suite's positive case put both calls on one line for years and was
# structurally unable to fail for the defect it was meant to catch.
SHAPE_SAMPLE = '''using System;
using System.Collections.Generic;

namespace Demo
{
    public sealed class Shapes : Widget, IThing
    {
        private readonly List<int> items = new List<int>();

        private static readonly string[] Names =
        {
            "a",
            "b",
        };

        public int Count
        {
            get
            {
                var n = 0;
                foreach (var i in items)
                {
                    n++;
                }
                return n;
            }
        }

        public int Doubled => Count * 2;

        public List<(int Turn, string Name)> Played { get; } =
            new List<(int, string)>();

        public static (int Count, string Digest) Compute(IEnumerable<string> keys)
        {
            return (0, "x");
        }

        public void Clamp(int lo = 0)
        {
            items.Add(lo);
        }

        public Shapes(string name)
            : base(name)
        {
        }
    }
}
'''


# Two types, the second a STRUCT declared after a class, and the class written
# `sealed partial` -- the order the kit's own output uses. Both shapes defeated
# the type matcher, and DestructionPlayback.cs (next on the split queue) has
# five types of three kinds.
KIND_SAMPLE = '''using System;

namespace Demo
{
    public sealed partial class Holder
    {
        public int Kept { get; }

        public bool Big => Kept > 10;
    }

    public readonly struct Thing
    {
        public Thing(int a)
        {
            A = a;
        }

        public int A { get; }
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
    check("finds all five members", len(mm) == 5, str(names))
    byname = {m[1]: m for m in mm}
    check("a field with a CONSTRUCTOR-CALL initialiser is a field, not a "
          "method named after its type -- this read as `method FakeThing` and "
          "swallowed the real method below it",
          "thing" in byname and "FakeThing" not in byname, str(names))
    check("...and it spans its own single line, so the member below survives",
          byname.get("thing", (0, 0, 0, 0))[3] ==
          byname.get("thing", (0, 0, 0, 0))[2], str(byname.get("thing")))
    check("a field with NO parentheses at all does not consume what follows "
          "(`private Session session = null!;` once ran on hunting a '(')",
          "session" in byname and byname["session"][3] == byname["session"][2],
          str(byname.get("session")))
    check("a field is named for its DECLARATOR, not a word from its "
          "initialiser (this once came out as `null`)",
          "null" not in byname and "FakeThing" not in byname, str(names))
    check("the method below the fields is still found whole",
          "Helper" in byname and byname["Helper"][3] > byname["Helper"][2],
          str(byname.get("Helper")))
    first = [m for m in mm if m[1] == "First_Works"][0]
    check("pulls a member's start back over its [Fact]",
          SAMPLE.split("\n")[first[2] - 1].strip() == "[Fact]")

    GOOD_PARTS = {
        "primary": {"file": "SampleTests.cs", "class": "SampleTests",
                    "partial": True, "usings": ["Xunit"], "header": "primary"},
        "second": {"file": "SampleTests.Second.cs", "class": "SampleTests",
                   "partial": True, "usings": ["Xunit"], "header": "second"},
    }
    GOOD_MEMBERS = {"thing": "primary", "session": "primary",
                    "Helper": "primary", "First_Works": "primary",
                    "Second_Works": "second"}
    NLINES = len(SAMPLE.split("\n"))
    # Derived, never hand-counted: adding two fields to SAMPLE once shifted
    # every one of these and three cases failed for a reason unrelated to what
    # they test.
    SAMPLE_LINES = SAMPLE.split("\n")
    BANNER = next(i for i, l in enumerate(SAMPLE_LINES, 1) if "--- a banner" in l)
    OPEN_BRACE = next(i for i, l in enumerate(SAMPLE_LINES, 1)
                      if l.strip() == "{" and i > 4)
    GOOD_SYNTH = [
        {"lines": [1, OPEN_BRACE - 1], "part": "primary",
         "why": "usings + namespace + class declaration"},
        {"lines": [OPEN_BRACE, OPEN_BRACE], "part": "primary",
         "why": "class open brace"},
        {"lines": [NLINES - 2, NLINES], "part": "primary", "why": "closers"},
    ]
    FWD = {"Second_Works": [BANNER - 1, BANNER + 1]}

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
          f"UNASSIGNED line {BANNER}" in out and "INVARIANT BROKEN" not in out,
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

    print("order (\"before\": a member the original filed in the wrong place)")
    with open(src, "w") as fh:       # writeparts overwrote it above
        fh.write(SAMPLE)
    ordered = dict(spec_for(tmp, {**GOOD_MEMBERS, "Second_Works": "primary"},
                            GOOD_SYNTH, GOOD_PARTS, FWD))
    ordered["before"] = {"Second_Works": "First_Works"}
    rc, out = run("writeparts.py", write_spec(tmp, ordered, "ord.json"))
    body = open(os.path.join(tmp, "SampleTests.cs")).read()
    check("emits the moved member ahead of its anchor, not in original order",
          rc == 0 and body.index("Second_Works") < body.index("First_Works"),
          out[:300])

    print("class doc (the compiler CONCATENATES /// across parts -- seen on "
          "the SelfTest split, where it spliced eleven summaries into one)")
    doc_src = os.path.join(tmp, "DocTests.cs")
    with open(doc_src, "w") as fh:
        fh.write(DOC_SAMPLE)
    DOC_MEMBERS = {"Helper": "primary", "First_Works": "primary",
                   "Second_Works": "second"}
    DOC_PARTS = {
        "primary": {"file": "DocTests.cs", "class": "DocTests", "partial": True,
                    "usings": ["Xunit"], "header": "primary"},
        "second": {"file": "DocTests.Second.cs", "class": "DocTests",
                   "partial": True, "usings": ["Xunit"], "header": "second"},
    }
    def doc_spec(entries, name):
        return write_spec(tmp, {
            "root": tmp, "source": "DocTests.cs", "outdir": ".",
            "namespace": "Demo", "members": DOC_MEMBERS,
            "synthetic": entries, "forward_gaps": {}, "parts": DOC_PARTS}, name)

    HEAD = [{"lines": [1, 4], "part": "primary", "why": "usings + namespace"},
            {"lines": [8, 9], "part": "primary", "why": "class decl"},
            {"lines": [26, -1], "part": "primary", "why": "closing braces"}]
    ONE = HEAD + [{"lines": [5, 7], "part": "primary", "emit": "class_doc",
                   "why": "the type's own doc"}]
    TWO = HEAD + [{"lines": [5, 7], "part": "primary", "emit": "class_doc",
                   "why": "the type's own doc"},
                  {"lines": [5, 7], "part": "second", "emit": "class_doc",
                   "why": "and again, which is the defect"}]

    rc, out = run("writeparts.py", doc_spec(TWO, "doc2.json"))
    check("REFUSES a spec where two parts carry the class /// doc",
          rc != 0 and "concatenates" in out, out[:300])

    rc, out = run("writeparts.py", doc_spec(ONE, "doc1.json"))
    prim = open(os.path.join(tmp, "DocTests.cs")).read()
    sec = open(os.path.join(tmp, "DocTests.Second.cs")).read()
    check("writes the /// doc above the class on the one part named",
          rc == 0 and "/// <summary>" in prim
          and prim.index("/// <summary>") < prim.index("public partial class"),
          out[:300])
    check("...and on no other part, so nothing is concatenated",
          "///" not in sec, sec[:200])
    check("separates the part header from that /// with a blank line, so the "
          "header does not read as commentary on it (on ClientSession.cs the "
          "/// immediately below belongs to an ENUM, not to the class)",
          rc == 0 and "// primary\n\n    /// <summary>" in prim,
          repr(prim[:200]))

    # THE CAP WAS PER SPEC, AND THE UNIT WAS WRONG. The compiler concatenates
    # within ONE type, never across two, so a file that is SEVERAL top-level
    # types was refused for a defect that cannot happen to it. What sits above
    # a type is not only its /// -- it is its ATTRIBUTES, and the wrapper
    # generator emits none. On NetGlue.cs that is `[HarmonyPatch(...)]` on two
    # Harmony classes: drop it and the patch silently never applies, which
    # every oracle in this kit passes (it compiles, each part decompiles the
    # same, the type is still there).
    multi_src = os.path.join(tmp, "MultiTests.cs")
    MULTI = ('using Xunit;\n\nnamespace Demo\n{\n'
             '    /// <summary>The first type.</summary>\n'
             '    public class Alpha\n    {\n'
             '        public void A_Works() { }\n    }\n\n'
             '    [Trait("kind", "beta")]\n'
             '    public class Beta\n    {\n'
             '        public void B_Works() { }\n    }\n}\n')
    with open(multi_src, "w") as fh:
        fh.write(MULTI)
    MULTI_PARTS = {
        "alpha": {"file": "MultiTests.cs", "class": "Alpha", "partial": True,
                  "usings": ["Xunit"], "header": "alpha"},
        "beta": {"file": "MultiTests.Beta.cs", "class": "Beta",
                 "partial": True, "usings": ["Xunit"], "header": "beta"},
    }
    multi = write_spec(tmp, {
        "root": tmp, "source": "MultiTests.cs", "outdir": ".",
        "namespace": "Demo", "members": {"A_Works": "alpha",
                                         "B_Works": "beta"},
        "synthetic": [
            {"lines": [1, 4], "part": "alpha", "why": "usings + namespace"},
            {"lines": [5, 5], "part": "alpha", "emit": "class_doc",
             "why": "Alpha's own doc"},
            {"lines": [6, 7], "part": "alpha", "why": "class decl"},
            {"lines": [9, 10], "part": "beta", "why": "gap"},
            {"lines": [11, 11], "part": "beta", "emit": "class_doc",
             "why": "Beta's ATTRIBUTE -- dropped without this"},
            {"lines": [12, 13], "part": "beta", "why": "class decl"},
            {"lines": [15, -1], "part": "alpha", "why": "closing braces"}],
        "forward_gaps": {}, "parts": MULTI_PARTS}, "multi.json")
    rc, out = run("writeparts.py", multi)
    check("ACCEPTS one class_doc block per CLASS in a several-type file, "
          "which the per-spec cap refused for a concatenation that cannot "
          "happen across two types", rc == 0, out[:400])
    if rc == 0:
        beta = open(os.path.join(tmp, "MultiTests.Beta.cs")).read()
        alpha = open(os.path.join(tmp, "MultiTests.cs")).read()
        check("...and carries the second type's ATTRIBUTE onto its part, "
              "which the wrapper generator would otherwise drop silently",
              '[Trait("kind", "beta")]' in beta
              and beta.index("[Trait") < beta.index("class Beta"), beta[:300])
        check("...while each type keeps only its OWN block",
              "/// <summary>The first type." in alpha
              and "[Trait" not in alpha and "///" not in beta, alpha[:300])

    with open(doc_src, "w") as fh:       # writeparts overwrote it above
        fh.write(DOC_SAMPLE)
    nh = {"root": tmp, "source": "DocTests.cs", "outdir": ".",
          "namespace": "Demo", "members": DOC_MEMBERS, "synthetic": ONE,
          "forward_gaps": {}, "parts": {
              "primary": {**DOC_PARTS["primary"], "header": ""},
              "second": {**DOC_PARTS["second"], "header": ""}}}
    rc, out = run("writeparts.py", write_spec(tmp, nh, "doc_nohdr.json"))
    nh_sec = open(os.path.join(tmp, "DocTests.Second.cs")).read()
    check("an ABSENT header emits nothing, rather than a bare `//` line the "
          "original never had and totalcontent then reports as GAINED",
          rc == 0 and "\n    //\n" not in nh_sec, repr(nh_sec[:200]))

    print("overloads (a bare NAME cannot address two members that share one -- "
          "every file left on the split queue has at least one)")
    ov_src = os.path.join(tmp, "OvTests.cs")
    with open(ov_src, "w") as fh:
        fh.write(OV_SAMPLE)
    OV_PARTS = {
        "primary": {"file": "OvTests.cs", "class": "OvTests", "partial": True,
                    "usings": ["Xunit"], "header": "primary"},
        "second": {"file": "OvTests.Second.cs", "class": "OvTests",
                   "partial": True, "usings": ["Xunit"], "header": "second"},
    }
    OV_SYNTH = [{"lines": [1, 6], "part": "primary", "why": "wrapper"},
                {"lines": [22, -1], "part": "primary", "why": "closing braces"}]
    def ov_spec(members, name):
        return write_spec(tmp, {
            "root": tmp, "source": "OvTests.cs", "outdir": ".",
            "namespace": "Demo", "members": members, "synthetic": OV_SYNTH,
            "forward_gaps": {}, "parts": OV_PARTS}, name)

    rc, out = run("partition.py", ov_spec(
        {"Show": "primary", "First_Works": "primary"}, "ov_bare.json"))
    check("refuses a plan addressing an overloaded name by its bare name",
          rc != 0 and "does not describe this file" in out, out[:400])

    rc, out = run("partition.py", ov_spec(
        {"OvTests.Show@7": "primary", "OvTests.Show@12": "second",
         "First_Works": "primary"}, "ov_ok.json"))
    check("accepts the two overloads addressed separately, and tiles them",
          rc == 0 and "partition OK" in out, out[:400])

    rc, out = run("writeparts.py", ov_spec(
        {"OvTests.Show@7": "primary", "OvTests.Show@12": "second",
         "First_Works": "primary"}, "ov_ok2.json"))
    second = open(os.path.join(tmp, "OvTests.Second.cs")).read()
    check("...and sends each overload to the part it was assigned, not both to one",
          rc == 0 and "string s" in second and "int i" not in second, out[:400])

    print("class modifiers (hardcoding \"public\" turned `public static class "
          "NetLog` into a different type)")
    st_src = os.path.join(tmp, "StTests.cs")
    with open(st_src, "w") as fh:
        fh.write(OV_SAMPLE.replace("public class OvTests",
                                   "public static class OvTests")
                          .replace("OvTests.cs", "StTests.cs"))
    def st_spec(mods, name):
        parts = {"primary": {"file": "StTests.cs", "class": "OvTests",
                             "partial": True, "usings": ["Xunit"],
                             "header": "primary"}}
        if mods is not None:
            parts["primary"]["modifiers"] = mods
        return write_spec(tmp, {
            "root": tmp, "source": "StTests.cs", "outdir": ".",
            "namespace": "Demo",
            "members": {"OvTests.Show@7": "primary", "OvTests.Show@12": "primary",
                        "First_Works": "primary"},
            "synthetic": OV_SYNTH, "forward_gaps": {}, "parts": parts}, name)

    rc, out = run("partition.py", st_spec(None, "st_bad.json"))
    check("REFUSES a part that would drop `static` from the class declaration",
          rc != 0 and "but the source declares it" in out, out[:400])

    rc, out = run("writeparts.py", st_spec("public static", "st_ok.json"))
    body = open(os.path.join(tmp, "StTests.cs")).read()
    check("writes the class with the modifiers the source actually had",
          rc == 0 and "public static partial class OvTests" in body, out[:400])

    print("declarator shapes (ClientSession.cs: an ACCESSOR LIST is not an "
          "initialiser, and a VALUE TUPLE is not a parameter list)")
    sh_src = os.path.join(tmp, "Shapes.cs")
    with open(sh_src, "w") as fh:
        fh.write(SHAPE_SAMPLE)
    SH_LINES = SHAPE_SAMPLE.split("\n")
    try:
        shm = member_map(sh_src)
    except Exception as exc:            # the pre-fix tool DIED on this shape
        shm = []
        check("does not crash on a member whose TYPE contains a tuple "
              "(member_map raised on Fakes.cs, where no identifier precedes "
              "the tuple's paren)", False, f"{type(exc).__name__}: {exc}")
    shn = {m[1]: m for m in shm}
    if shm:
        check("does not crash on a member whose TYPE contains a tuple "
              "(member_map raised on Fakes.cs, where no identifier precedes "
              "the tuple's paren)", True)
    check("a block-bodied property ENDS AT ITS CLOSING BRACE -- reading on to "
          "the next ';' is how ClientSession.cs lost a whole member",
          "Count" in shn
          and SH_LINES[shn["Count"][3] - 1].strip() == "}",
          str(shn.get("Count")) + " last line=" +
          repr(SH_LINES[shn["Count"][3] - 1] if "Count" in shn else None))
    check("...so the member BELOW it is still found, instead of being "
          "swallowed and appearing in no row at all "
          "(ClientSession.LobbyParticipantCount)",
          "Doubled" in shn, str(sorted(shn)))
    # MUST REACH THE BRACE BRANCH. `List<int> items = new List<int>();`
    # terminates on a ';' and never enters it at all, so it was no control for
    # this at all -- the same defect as the ownership suite's two-calls-on-one-
    # line case. A collection initialiser is the shape that actually collides
    # with an accessor list.
    check("an INITIALISER brace still runs past its '}' to the ';' -- the "
          "discriminator is the '=', and breaking it is the opposite defect",
          "Names" in shn
          and SH_LINES[shn["Names"][3] - 1].rstrip().endswith("};"),
          str(shn.get("Names")))
    check("a tuple-TYPED property is a property, named for its declarator",
          "Played" in shn, str(sorted(shn)))
    check("a tuple-RETURNING method is named for the method, not for the "
          "modifier before the tuple (AssetPoolDigest.Compute read as `static` "
          "and swallowed 60 lines)",
          "Compute" in shn and "static" not in shn, str(sorted(shn)))
    check("a constructor with a `: base(...)` initialiser is named for the "
          "class, not for `base` (PbjProtocolException)",
          "Shapes" in shn and "base" not in shn, str(sorted(shn)))
    check("a DEFAULT PARAMETER VALUE's '=' does not truncate the declarator "
          "into something ending in an identifier, which would read as a field",
          "Clamp" in shn and shn["Clamp"][3] > shn["Clamp"][2],
          str(shn.get("Clamp")))

    print("base list (a partial class names its bases on ONE part, and the "
          "wrapper generator emitted them on NONE)")
    SH_MEMBERS = {m[1]: "primary" for m in shm}
    first_start = min(m[2] for m in shm)
    last_end = max(m[3] for m in shm)
    SH_SYNTH = [{"lines": [1, first_start - 1], "part": "primary",
                 "why": "usings + namespace + class declaration"},
                {"lines": [last_end + 1, -1], "part": "primary",
                 "why": "closing braces"}]

    def sh_spec(parts, name):
        return write_spec(tmp, {
            "root": tmp, "source": "Shapes.cs", "outdir": ".",
            "namespace": "Demo", "members": SH_MEMBERS, "synthetic": SH_SYNTH,
            "forward_gaps": {}, "parts": parts}, name)

    def sh_parts(**extra):
        p = {"primary": {"file": "Shapes.cs", "class": "Shapes",
                         "partial": True, "modifiers": "public sealed",
                         "usings": ["System", "System.Collections.Generic"],
                         "header": "primary"}}
        p["primary"].update(extra)
        return p

    rc, out = run("partition.py", sh_spec(sh_parts(), "sh_nobase.json"))
    check("REFUSES a spec with no part carrying the base list, rather than "
          "writing parts that together implement nothing",
          rc != 0 and "no part carries it" in out, out[:400])

    rc, out = run("partition.py",
                  sh_spec(sh_parts(bases="IThing"), "sh_wrongbase.json"))
    check("REFUSES a base list that does not match the source's own text -- a "
          "retyped interface list is exactly what this kit exists to prevent",
          rc != 0 and "but the source declares" in out, out[:400])

    two = sh_parts(bases="Widget, IThing")
    two["second"] = {"file": "Shapes.Second.cs", "class": "Shapes",
                     "partial": True, "modifiers": "public sealed",
                     "usings": [], "header": "second", "bases": "Widget, IThing"}
    rc, out = run("partition.py", sh_spec(two, "sh_twobase.json"))
    check("REFUSES two parts claiming the base list (CS0263, but named)",
          rc != 0 and "may name its bases once" in out, out[:400])

    rc, out = run("writeparts.py",
                  sh_spec(sh_parts(bases="Widget, IThing"), "sh_ok.json"))
    sh_body = open(os.path.join(tmp, "Shapes.cs")).read()
    check("writes the base list verbatim on the part that claims it",
          rc == 0 and
          "public sealed partial class Shapes : Widget, IThing" in sh_body,
          out[:400] + sh_body[:300])

    print("...and the must-ACCEPT control: a class with NO base list is not "
          "required to declare one")
    nb_src = os.path.join(tmp, "NoBase.cs")
    with open(nb_src, "w") as fh:
        fh.write(SHAPE_SAMPLE.replace("class Shapes : Widget, IThing",
                                      "class Shapes"))
    nb_parts = {"primary": {"file": "NoBase.cs", "class": "Shapes",
                            "partial": True, "modifiers": "public sealed",
                            "usings": ["System"], "header": "primary"}}
    nb = write_spec(tmp, {
        "root": tmp, "source": "NoBase.cs", "outdir": ".", "namespace": "Demo",
        "members": SH_MEMBERS, "synthetic": SH_SYNTH, "forward_gaps": {},
        "parts": nb_parts}, "nb_ok.json")
    rc, out = run("partition.py", nb)
    check("accepts a baseless class with no \"bases\" key, so the guard is "
          "not simply always-on", rc == 0 and "partition OK" in out, out[:400])

    nb_parts["primary"]["bases"] = "IThing"
    rc, out = run("partition.py", write_spec(tmp, {
        "root": tmp, "source": "NoBase.cs", "outdir": ".", "namespace": "Demo",
        "members": SH_MEMBERS, "synthetic": SH_SYNTH, "forward_gaps": {},
        "parts": nb_parts}, "nb_bad.json"))
    check("...and REFUSES a \"bases\" invented for a class that has none",
          rc != 0 and "no base list" in out, out[:400])

    print("type kinds (a struct rendered as a class is a value type turned "
          "into a reference type, and no oracle here reports it)")
    kind_src = os.path.join(tmp, "Kinds.cs")
    with open(kind_src, "w") as fh:
        fh.write(KIND_SAMPLE)
    km = member_map(kind_src)
    kowner = {(c, n) for c, n, _, _ in km}
    check("attributes a STRUCT's members to the struct, not to the class "
          "declared above it -- Keyframes.cs reported JointPose's three "
          "members as UnitTrack's, which is what the Class.Name key is for",
          ("Thing", "A") in kowner and ("Holder", "A") not in kowner,
          str(sorted(kowner, key=str)))
    check("...and reads `sealed partial class`, whose modifiers are in the "
          "order this kit's OWN output writes them; the matcher wanted "
          "`partial sealed` and attributed every member to None",
          ("Holder", "Kept") in kowner and (None, "Kept") not in kowner,
          str(sorted(kowner, key=str)))

    # ONE type per source for the spec-level cases: the stale-plan guard
    # (correctly) demands that a plan name every member the map finds, and a
    # two-type file would need both types' members in one plan.
    ONE_TYPE = '''using System;

namespace Demo
{
    public sealed partial class Holder
    {
        public int Kept { get; }

        public bool Big => Kept > 10;
    }
}
'''
    kind_members = {"Kept": "primary", "Big": "primary"}
    KIND_SYNTH = [{"lines": [1, 6], "part": "primary", "why": "wrapper"},
                  {"lines": [10, -1], "part": "primary", "why": "closers"}]

    def one_type(text, fname, parts, name):
        with open(os.path.join(tmp, fname), "w") as fh:
            fh.write(text)
        return write_spec(tmp, {
            "root": tmp, "source": fname, "outdir": ".", "namespace": "Demo",
            "members": kind_members, "synthetic": KIND_SYNTH,
            "forward_gaps": {}, "parts": parts}, name)

    def kparts(fname, **extra):
        p = {"primary": {"file": fname, "class": "Holder", "partial": True,
                         "modifiers": "public sealed", "usings": ["System"],
                         "header": "h"}}
        p["primary"].update(extra)
        return p

    rc, out = run("partition.py", one_type(
        ONE_TYPE, "K1.cs", kparts("K1.cs", kind="struct"), "kind_bad.json"))
    check("REFUSES a part that would emit a class as a struct",
          rc != 0 and "but the source declares it a class" in out, out[:400])

    rc, out = run("partition.py", one_type(
        ONE_TYPE, "K2.cs", kparts("K2.cs"), "kind_ok.json"))
    check("accepts a class part with no \"kind\" key, so the guard is not "
          "simply always-on", rc == 0 and "partition OK" in out, out[:400])

    STRUCT_TYPE = ONE_TYPE.replace("public sealed partial class Holder",
                                   "public readonly struct Holder")
    rc, out = run("partition.py", one_type(
        STRUCT_TYPE, "K3.cs", kparts("K3.cs", modifiers="public readonly"),
        "st_bad.json"))
    check("REFUSES a part that would emit a STRUCT as a class -- the default, "
          "and so the one that was silently happening",
          rc != 0 and "but the source declares it a struct" in out, out[:400])

    rc, out = run("writeparts.py", one_type(
        STRUCT_TYPE, "K4.cs",
        kparts("K4.cs", modifiers="public readonly", kind="struct"),
        "st_ok.json"))
    st2_body = open(os.path.join(tmp, "K4.cs")).read()
    check("writes `public readonly partial struct`, the kind the source had",
          rc == 0 and "public readonly partial struct Holder" in st2_body,
          out[:400] + st2_body[:200])

    print("doc xml (the compiler emits <member> in SOURCE order, so a split "
          "reorders the whole file and a line diff is pure noise)")
    def xml(path, entries):
        with open(os.path.join(tmp, path), "w") as fh:
            fh.write("<doc><members>\n")
            for name, body in entries:
                fh.write(f'<member name="{name}">\n  <summary>{body}</summary>\n'
                         f'</member>\n')
            fh.write("</members></doc>\n")
        return os.path.join(tmp, path)

    base = [(f"M:N.M{i}", f"body {i}") for i in range(30)]
    a = xml("doc-a.xml", base)
    rc, out = run("docxml.py", a, xml("doc-b.xml", list(reversed(base))))
    check("is silent on a pure REORDER, which every split produces",
          rc == 0 and "identical by member" in out, out[:300])

    rc, out = run("docxml.py", a, xml("doc-c.xml", base[1:]))
    check("reports a member whose /// went missing", rc != 0 and "LOST" in out,
          out[:300])

    rc, out = run("docxml.py", a, xml("doc-d.xml",
                                      [(base[0][0], "reworded")] + base[1:]))
    check("reports a member whose /// changed wording",
          rc != 0 and "CHANGED" in out, out[:300])

    rc, out = run("docxml.py", a, xml("doc-e.xml", base[:5]))
    check("REFUSES a doc XML it barely parsed, rather than comparing nothing",
          rc != 0 and "REFUSING" in out, out[:300])

    rc, out = run("docxml.py", a, xml("doc-f.xml",
                                      base + [(base[0][0], "a second summary")]))
    check("reports the SPLICED entry two /// on one type would produce",
          rc != 0 and "SPLICED" in out, out[:300])

    with open(src, "w") as fh:
        fh.write(SAMPLE)
    bad_order = dict(ordered)
    bad_order["before"] = {"Second_Works": "NoSuchMember"}
    rc, out = run("partition.py", write_spec(tmp, bad_order, "ord2.json"))
    check("refuses a \"before\" naming a member the file does not have",
          rc != 0 and "not a member of this file" in out, out[:300])

    with open(src, "w") as fh:
        fh.write(SAMPLE)
    cross = dict(spec_for(tmp, GOOD_MEMBERS, GOOD_SYNTH, GOOD_PARTS, FWD))
    cross["before"] = {"Second_Works": "First_Works"}   # different parts
    rc, out = run("partition.py", write_spec(tmp, cross, "ord3.json"))
    check("refuses a \"before\" across two different parts, rather than "
          "silently dropping the move", rc != 0 and
          "different parts" in out, out[:300])

    # restore, then re-run the plain split so the checks below see the tree a
    # completed split leaves behind
    with open(src, "w") as fh:
        fh.write(SAMPLE)
    run("writeparts.py", good)

    print("re-running writeparts (it overwrites its own source)")
    rc, out = run("partition.py", good)
    check("after a split has been written, the guard names the CAUSE rather "
          "than listing every member as missing",
          rc != 0 and "already run and overwritten it" in out and
          "git checkout" in out, out[:400])

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

    # THE SHAPE THE CASE ABOVE CANNOT REACH. Both calls there share a line with
    # `void C() {`, so something disqualifying always precedes the name. A
    # helper invoked as a STATEMENT has nothing before it but indentation --
    # and indentation matched the declaration prefix, so every such call was
    # subtracted as its own declaration. On ScenarioTransferTests.cs that hid
    # 11 of 15 sites and turned a 93% owner into an 80% shared helper.
    stmt = os.path.join(tmp, "stmt.cs")
    with open(stmt, "w") as fh:
        fh.write("class D\n{\n"
                 "    private void Target(int i)\n    {\n    }\n\n"
                 "    private void Caller()\n    {\n"
                 "        Target(1);\n        Target(2);\n    }\n}\n")
    rc, out = run("ownership.py", "Target", stmt)
    check("counts a bare-statement call, which has only indentation before it "
          "and so once looked exactly like a declaration",
          "2 call sites" in out, out[:400])

    # THE SAME GUARD, WRONG A SECOND WAY. Identifiers, commas and whitespace
    # are all in the prefix class, so the continuation line of a wrapped call
    # read as a declaration too -- the commonest shape in NetLog.cs, where
    # every message is a multi-line string.Format. 13 of 19 sites in one part.
    cont = os.path.join(tmp, "cont.cs")
    with open(cont, "w") as fh:
        fh.write("class E\n{\n"
                 "    private static string Target(int i)\n    {\n"
                 "        return \"x\";\n    }\n\n"
                 "    private static Dictionary<string, int> Lookup(int i)\n"
                 "    {\n        return null;\n    }\n\n"
                 "    private void Caller()\n    {\n"
                 "        Format(\n"
                 "            count, Target(count));\n"
                 "        Format(\n"
                 "            a, b, Target(b), c);\n    }\n}\n")
    rc, out = run("ownership.py", "Target,Lookup", cont)
    check("counts a call on the continuation line of a wrapped argument list",
          "Target: 2 call sites" in out, out[:400])
    check("...while a declaration whose TYPE contains a comma "
          "(Dictionary<string, int>) is still a declaration",
          "Lookup: 0 call sites" in out or "Lookup: 0 " in out, out[:400])

    # THE SAME GUARD, WRONG A THIRD TIME -- and none of the four cases above
    # could reach it, because not one of them puts a call after `return`. The
    # prefix `        return ` is word characters and whitespace, so it matched
    # the declaration class; the comma guard is no help because the comma sits
    # INSIDE the parentheses, after the match. On NetGlue.cs both of Connect's
    # call sites vanished and the tool printed a confident zero.
    ret = os.path.join(tmp, "ret.cs")
    with open(ret, "w") as fh:
        fh.write("class F\n{\n"
                 "    private static string Target(int a, int b)\n"
                 "    {\n        return \"x\";\n    }\n\n"
                 "    private static string Caller()\n    {\n"
                 "        if (a) { }\n"
                 "        return Target(1, 2);\n    }\n\n"
                 "    private static string Other()\n    {\n"
                 "        return Target(3, 4) + Target(5, 6);\n    }\n}\n")
    rc, out = run("ownership.py", "Target", ret)
    check("counts a call after `return`, which has only a keyword before it "
          "and so once looked exactly like a declaration -- and counts BOTH "
          "of two such calls on one line",
          "Target: 3 call sites" in out, out[:400])

    # THE CONTROL THE FIX COULD BREAK. A statement-word denylist that is too
    # eager stops subtracting real declarations, which inflates a count instead
    # of hiding one -- the same defect pointing the other way. `new` is a
    # modifier as well as an operator, so it must NOT disqualify.
    decl = os.path.join(tmp, "decl.cs")
    with open(decl, "w") as fh:
        fh.write("class G\n{\n"
                 "    public new string Target(int a)\n"
                 "    {\n        return \"x\";\n    }\n\n"
                 "    private static Dictionary<string, int> Lookup(int i)\n"
                 "    {\n        return null;\n    }\n}\n")
    rc, out = run("ownership.py", "Target,Lookup", decl)
    check("still SUBTRACTS a real declaration whose own body returns on the "
          "same line, and one modified by `new`",
          "Target: 0 call sites" in out and "Lookup: 0 call sites" in out,
          out[:400])

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

    print("grouping (the one property no other oracle here can see, and the "
          "only one still watching after the split lands)")
    groot = os.path.join(tmp, "groot")
    gdir = os.path.join(groot, "src", "Fam")
    odir = os.path.join(groot, "src", "Other")
    os.makedirs(gdir)
    os.makedirs(odir)

    def gwrite(d, name, cls, body):
        with open(os.path.join(d, name), "w") as fh:
            fh.write("namespace Demo\n{\n    public partial class " + cls +
                     "\n    {\n" + body + "    }\n}\n")

    def fam(two, three, primary="        public void Alpha() { }\n"):
        gwrite(gdir, "Fam.cs", "Fam", primary)
        gwrite(gdir, "Fam.Two.cs", "Fam", two)
        gwrite(gdir, "Fam.Three.cs", "Fam", three)

    BETA = "        public void Beta() { }\n"
    EPS = "        public void Epsilon() { }\n"
    GAMMA = "        public void Gamma() { }\n"
    BETA_I = "        public void Beta(int i) { }\n"

    fam(BETA + EPS, GAMMA)
    gwrite(odir, "Other.cs", "Other", "        public void Zeta() { }\n")
    gwrite(odir, "Other.Bits.cs", "Other", "        public void Eta() { }\n")
    # A decoy: a dotted name with no primary beside it is not a split family.
    with open(os.path.join(gdir, "Loner.Part.cs"), "w") as fh:
        fh.write("namespace Demo { public class Loner { public void D() { } } }\n")

    glock = os.path.join(tmp, "g.lock")

    def record():
        rc, out = run("grouping.py", "--root", groot)
        with open(glock, "w") as fh:
            fh.write(out)
        return rc, out

    rc, out = record()
    check("records a ledger for every family, and leaves a dotted file with "
          "no primary beside it alone", rc == 0 and "families: 2" in out
          and "Loner" not in out, out[:400])

    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("ACCEPTS a tree that has not moved -- the control proving the "
          "refusals below are not simply always-on",
          rc == 0 and "split grouping OK" in out, out[:300])

    # THE CASE THAT MATTERS MOST: two overloads of one name, deliberately in
    # DIFFERENT part files. Keying the comparison by (directory, member)
    # collapsed such a pair, dropped a row on each side and would report a move
    # that never happened. Real instances: HostSession.Reject across
    # Handshake.cs and Turn.cs, KeyframePlayer.Dress across Assets.cs and
    # Sleep.cs -- both split on purpose, because their callers differ.
    fam(BETA + EPS, GAMMA + BETA_I)
    record()
    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("ACCEPTS one name whose overloads live in two different parts, "
          "which a comparison keyed by name alone silently collapsed",
          rc == 0 and "split grouping OK" in out, out[:400])

    # AND THE HALF THAT ACTUALLY BITES. The case above passes either way: a
    # name-keyed comparison collapses BOTH sides identically, so an unchanged
    # tree still matches and the defect hides. Collapsing drops a row, and a
    # dropped row is a change that goes UNREPORTED -- so the test has to MOVE
    # one of the two overloads and insist it is seen.
    fam(BETA + EPS, GAMMA, primary="        public void Alpha() { }\n" + BETA_I)
    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("REFUSES a move of ONE overload while its twin stays put, which a "
          "name-keyed comparison could not see at all",
          rc != 0 and "MOVED" in out and "Fam.Beta" in out, out[:400])
    fam(BETA + EPS, GAMMA + BETA_I)

    fam(BETA + EPS, GAMMA + BETA_I + "        public void Delta() { }\n")
    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("REFUSES a NEW member, which is the drift that actually happens -- "
          "a member landing in whichever part file was open",
          rc != 0 and "NEW MEMBER" in out and "Fam.Delta" in out, out[:400])

    fam(EPS, GAMMA + BETA_I, primary="        public void Alpha() { }\n" + BETA)
    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("REFUSES a member that MOVED between two parts",
          rc != 0 and "MOVED" in out and "Fam.Beta" in out, out[:400])

    # A FAMILY THAT VANISHES IS THE DANGEROUS DIRECTION. Families are found by
    # a naming rule, so renaming the primary stops the whole family being
    # looked at -- silently, because every member disappears from both sides at
    # once. The lock records the family list for exactly this. Note the OTHER
    # family must survive, or the run refuses as vacuous before reporting it.
    fam(BETA + EPS, GAMMA + BETA_I)
    os.rename(os.path.join(odir, "Other.cs"),
              os.path.join(odir, "OtherCore.cs"))
    rc, out = run("grouping.py", "--root", groot, "--check", glock)
    check("REFUSES a family whose primary was renamed, so it would otherwise "
          "stop being discovered and take every member with it",
          rc != 0 and "FAMILY GONE" in out, out[:400])
    os.rename(os.path.join(odir, "OtherCore.cs"),
              os.path.join(odir, "Other.cs"))

    rc, out = run("grouping.py", "--root", os.path.join(tmp, "nothing-here"))
    check("REFUSES a tree with no families at all rather than recording an "
          "empty ledger", rc != 0 and "VACUOUS" in out, out[:300])

    empty = os.path.join(tmp, "eroot", "src", "E")
    os.makedirs(empty)
    gwrite(empty, "E.cs", "E", "        public void One() { }\n")
    gwrite(empty, "E.Blank.cs", "E", "")
    rc, out = run("grouping.py", "--root", os.path.join(tmp, "eroot"))
    check("REFUSES a part file the member map reads NOTHING from, rather than "
          "recording a member's absence as its correct place",
          rc != 0 and "VACUOUS" in out, out[:300])

    import grouping as _g
    check("the family rule proves itself on a canary before any recording",
          _g.prove_discovery() is None)
    real_fam = _g.families
    try:
        # A rule that finds the right NUMBER of families but the wrong ones --
        # a blind spot rather than a blackout, which is the shape that would
        # actually survive review.
        _g.families = lambda root_dir=".": {("src/N", "Wrong"): ["Wrong.cs"]}
        broke = False
        try:
            _g.prove_discovery()
        except SystemExit:
            broke = True
        check("and that canary CATCHES a family rule gone blind, which would "
              "record an empty ledger and check nothing forever", broke)
    finally:
        _g.families = real_fam

    shutil.rmtree(tmp)
    print(f"\nsplit kit selftest: {len(PASS)} passed, {len(FAIL)} failed")
    for f in FAIL:
        print(f"  FAILED: {f}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
