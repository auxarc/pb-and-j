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
   **Count its rows against a grep of the file's declaration lines.** The map
   is a parser and it has been wrong four times; on `ClientSession.cs` it
   swallowed `LobbyParticipantCount` whole — the member appeared in NO row,
   and nothing else in the kit can see that, because the line IS claimed, the
   decompile IS identical and only the grouping is wrong.
3. **Write the spec** (see below) assigning every member to a part.
4. **Prove the tiling.** `partition.py <spec>` — every line in exactly one
   part. Run this *before* any edit; on `HostSession.cs` it caught a plan
   defect that would have silently emigrated a 72-line block.
5. **Check the grouping.** `ownership.py Helper1,Helper2 <files>` — a helper
   stays with a part only if that part is effectively its sole user. This is
   the one property no oracle below can see: a split can be byte-identical,
   green everywhere, and have every member in the wrong file.

   **Ask it about HELPERS, not about handlers.** On a dispatcher-shaped file
   the rule is degenerate: `ClientSession.cs` is one switch calling twenty
   handlers exactly once each, so all twenty read "100%, sole user, belongs
   here" — with the dispatcher. Followed literally that un-splits the file.
   The seam between handlers comes from SUBJECT; ownership decides only where
   the shared helpers land, and there it did discriminate (three below the
   threshold, two at 100%). Run it over every helper and check that the
   verdicts are not all the same answer, which is what a degenerate rule looks
   like.

   It is also **blind to fields and constants** — it counts an identifier
   followed by a call-paren — and it says so rather than printing a bare zero.
   Tally those by hand.

   **A ZERO IS A CLAIM ABOUT THE SHAPE, NOT ABOUT THE CODE.** On NetGlue.cs it
   reported `Connect: 0 call sites` when the file plainly calls it twice:
   `return Connect(...)` has nothing but a keyword before the name, so it
   matched the declaration prefix and was subtracted as its own declaration.
   That is the *third* generation of one defect — bare statements (#45),
   wrapped continuation lines (#46), now statement keywords — and every fix
   only narrows the character class. The tool prints its own alternative
   hypothesis next to a zero for exactly this reason. Grep it.
6. **Cut.** `writeparts.py <spec>` slices bodies out of the original bytes.
   Nothing is retyped. It refuses to run on an unproven partition.
7. **Run the oracles**, and prove each one BITES before believing it:
   - `listtests.sh` — the test-name SET, for a test-file split;
   - `ilcanon.py` — the decompile, for "the compiled code is unchanged".
     Decompile BOTH dlls **in place**, from `bin/Release/net472`: ilspycmd
     resolves neighbouring assemblies and the pdb out of that directory, so a
     dll copied to a scratchpad decompiles differently — different variable
     names, extra casts, `//IL_` notes — and the diff fills with changes to
     files you never touched. It also refuses under MIN_RECORDS rather than
     report a clean empty comparison, which is what caught its own failure to
     descend into an attributed type;
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
9. **Record the decision.** `make record-split-grouping`, and commit the spec
   to `specs/`. See "After the split" below — everything above compares a
   before to an after, and once the split lands there is no before.

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

Five keys exist because leaving them out changed the code:

- **`"kind"`** on a part, default `"class"`. `class` was hardcoded in the
  generator, and both guards below searched only for `class <name>` — so a
  `public readonly struct` matched neither, both skipped, and the part came
  out `public partial class`. **A value type silently became a reference
  type.** Proven by running the kit over a one-struct file, not inferred.
  A part whose kind disagrees with the source is now refused by name.

- **`"bases"`** on a part: the class's base and interface list, verbatim.
  A partial class may name its bases on ONE declaration, and the wrapper
  generator emitted them on NONE — so splitting
  `public sealed class ClientSession : IPbjSession` would have produced parts
  that together implement nothing. splitspec checks the value against the
  source's own declaration and refuses a missing, mismatched or doubled one.

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

90 cases: what each tool must REFUSE, and the sound input it must still
accept. Each names a defect that actually bit, or the control proving the
refusal is not simply always-on. The suite has been mutation-checked —
breaking any one guard makes it fail — because a bite test that cannot fail is
this project's most repeated mistake.

## What these tools cannot tell you

- **Grouping**, at the moment of the split. Step 5 is a substitute, not a
  proof. What `split-grouping.lock` adds is the *next* day: it cannot tell you
  the placement was right, only that nobody has quietly changed it since.
- **Which overload.** `ownership.py` counts by NAME, so two overloads of one
  name share a single tally. splitspec can address them separately; the
  ownership check cannot tell them apart, and a helper with overloads needs
  its call sites read by hand.
- **Static initialiser order.** `ilcanon.py` sorts single-line members, so a
  reordered `.cctor` — the one thing splitting a partial class can really
  change — is invisible. Verify it by reading, every time.
- **Whether a comment is still true** where it landed.

## After the split

Every oracle in step 7 compares a before to an after. Once the split is
committed there is no before any more, and **nothing notices a member drifting
into the wrong part file.** What drifts is rarely an existing member moving; it
is a NEW member landing in whichever part the author had open — a placement
decision made by accident, when deciding placement on purpose is the whole
value of the split.

`split-grouping.lock` at the repo root records which part file every member of
every split family lives in. `make check-split-grouping` runs from `dist` and
refuses a build that departs from it; `make record-split-grouping` re-records
once you have DECIDED the placement. It is the same shape as
`wire-surface.lock`, for the same reason: a change that must be deliberate
should have to be written down.

A family is `Foo.cs` plus its `Foo.*.cs` siblings — the convention every split
here has followed, since the primary part keeps the original name. Because that
rule is how families are FOUND, the lock also records the family list: a split
whose primary got renamed would otherwise stop being looked at entirely and take
every one of its members with it, silently, since each member simply stops
appearing on both sides at once.

Overloads are counted, not numbered — three `NetGlue.Host` in one part is one
row with count 3. Line numbers would churn the lock on every unrelated edit and
train everyone to re-record without reading, which is how a lock stops being
evidence. One name CAN legitimately live in two parts: `HostSession.Reject` and
`KeyframePlayer.Dress` are both split across two files on purpose.

And **commit the spec** to `specs/`. Fourteen of the first fifteen were written
to `/tmp` and lost; the lock records *that* a member sits somewhere, the spec
records *why*.

## A file that is SEVERAL TYPES

Most of this assumes one type split into partial parts. A file that is several
top-level types wants one type per file instead, and that now works: give each
type its own part with its own `"class"`, `"kind"` and `"modifiers"`, and its
own `emit: "class_doc"` block.

**The `class_doc` cap is PER CLASS, and it used to be per spec.** The defect it
guards is the compiler concatenating the `///` of every part of one partial
class — which cannot happen across two different types, so the old unit refused
a sound spec. Getting this right is not cosmetic: what sits above a type is not
only its doc but its ATTRIBUTES, and the wrapper generator emits none of its
own. `NetGlue.cs` is `NetGlue` plus two Harmony patch classes, each carrying
`[HarmonyPatch(...)]`; without a block per class that attribute is dropped and
the patch silently never applies — which **every oracle here passes**. It
compiles, each part decompiles the same, the type is still present, and only
the running game knows.

`DestructionPlayback.cs` (688) is five top-level types — `DestructionDrive` and
`UnitWreckDrive` structs, `DestructionUpdate`, `DestructionRamp` static,
`DestructionState` 508 lines — and is the next candidate for this shape.

The alternative that needs no per-type parts: leave the extra types where they
are and split only the largest into partials, carrying the others as one
`class_doc` block above one part's declaration. That is what was done for
`ClientSession.cs`'s enum. It works, but it leaves the file named for a subject
rather than a type, so decide which shape is wanted first.
