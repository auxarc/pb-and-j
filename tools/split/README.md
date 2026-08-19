# The split kit

Tools for cutting an oversized file into parts **without changing what it
compiles to**. Built during the modularization programme, after nine splits;
persisted here because the previous session's copies lived in `/tmp`, `/tmp`
was cleaned, and the tenth split had to rebuild all of them from scratch.

Everything here exists because something went wrong once. The docstrings name
the incident; read them before trusting a tool.

## The workflow

1. **Measure the banners first.** How the author sectioned the file decides the
   rule, and the right rule is not the same twice:
   - *coarse* banners (few, some over the size gate) → follow them, and
     subdivide only a section that exceeds the gate;
   - *fine* banners (many, none over the gate) → nothing has to be divided;
     the whole job is which neighbours to group;
   - *no banners* → the seams come from subject, which is where grouping
     errors live. Lean much harder on step 5.

   Then check each banner still heads what it introduced. A later commit can
   insert whole sections between a banner and its tests: on
   ScenarioPayloadTests.cs, M11e left `--- digest agreement ---` heading
   nothing and its four tests stranded 265 lines below, under a banner about
   something else. `git log -S` on the banner text dates both.
2. **Map the members.** `splitspec.py <file>` prints the member table.
3. **Write the spec** (see below) assigning every member to a part.
4. **Prove the tiling.** `partition.py <spec>` — every line in exactly one
   part. Run this *before* any edit; on `HostSession.cs` it caught a plan
   defect that would have silently emigrated a 72-line block.
5. **Check the grouping.** `ownership.py Helper1,Helper2 <files>` — a helper
   stays with a part only if that part is effectively its sole user. This is
   the one property no oracle below can see: a split can be byte-identical,
   green everywhere, and have every member in the wrong file.
6. **Cut.** `writeparts.py <spec>` slices bodies out of the original bytes.
   Nothing is retyped. It refuses to run on an unproven partition.
7. **Run the oracles**, and prove each one BITES before believing it:
   - `listtests.sh` — the test-name SET, for a test-file split;
   - `ilcanon.py` — the decompile, for "the compiled code is unchanged";
   - `docxml.py` — the emitted doc XML, for "every `///` survived and still
     says the same thing". Compare it BY MEMBER: the compiler emits entries in
     source order, so a split reorders the whole file and `diff` shows ~150
     lines of noise that look exactly like damage;
   - `totalcontent.py` — every non-blank line, the only oracle that reads
     comments.
8. **Review the prose.** Every `//` header is an unchecked claim. Five splits
   running, the mechanical oracles were byte-perfect and the headers were not:
   four to six false claims each time. An enumeration is a completeness claim;
   a count ages the moment the plan changes; a rationale must be READ, not
   recalled.

## The spec

```json
{
  "source": "tests/PBAndJ.Core.Tests/Net/FooTests.cs",
  "outdir": "tests/PBAndJ.Core.Tests/Net",
  "namespace": "PBAndJ.Core.Tests.Net",
  "members":   { "Helper": "primary", "Bar_Works": "bar" },
  "synthetic": [ { "lines": [1, 7], "part": "primary",
                   "why": "usings + namespace, regenerated per part" } ],
  "forward_gaps": { "Bar_Works": [383, 385] },
  "parts": {
    "primary": { "file": "FooTests.cs", "class": "FooTests",
                 "partial": true, "usings": ["Xunit"],
                 "header": "Where to start reading..." }
  }
}
```

`synthetic` declares the real lines the member map does not model. `forward_gaps`
decides which side a **comment** between two members belongs to — the one
question no oracle can answer for you, because every one of them is blind to
comments.

Three keys exist because leaving them out changed the code:

- **`"modifiers"`** on a part, default `"public"`. Hardcoding it turned
  `public static class NetLog` into `public class NetLog`. splitspec checks
  each part against the original declaration and refuses a mismatch.
- **`"emit": "class_doc"`** on a synthetic block keeps the class-level `///`
  instead of regenerating it away, on exactly one part. Two parts carrying it
  makes the compiler *concatenate* the summaries — which is what happened to
  the eleven-part SelfTest split.
- **member keys**, when a bare name is ambiguous: `Class.Name`, or
  `Class.Name@line` for true overloads in one class. Every file left on the
  split queue has at least one repeated name.

## Selftest

```
make split-selftest
```

52 cases: what each tool must REFUSE, and the sound input it must still
accept. Each names a defect that actually bit, or the control proving the
refusal is not simply always-on. The suite has been mutation-checked —
breaking any one guard makes it fail — because a bite test that cannot fail is
this project's most repeated mistake.

## What these tools cannot tell you

- **Grouping.** Step 5 is a substitute, not a proof.
- **Which overload.** `ownership.py` counts by NAME, so two overloads of one
  name share a single tally. splitspec can address them separately; the
  ownership check cannot tell them apart, and a helper with overloads needs
  its call sites read by hand.
- **Static initialiser order.** `ilcanon.py` sorts single-line members, so a
  reordered `.cctor` — the one thing splitting a partial class can really
  change — is invisible. Verify it by reading, every time.
- **Whether a comment is still true** where it landed.
