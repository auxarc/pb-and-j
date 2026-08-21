# The specs

One file per split, named for the file it cut. A spec says which member went
into which part and — in its `why` fields and part `header`s — what the
placement decision was.

**These are kept because fourteen of them were not.** Every split before
NetGlue.cs wrote its spec to `/tmp`, `/tmp` was cleaned, and the plan went with
it. The tooling was rebuilt from scratch once already for exactly that reason
(see the top of `../README.md`). A spec is the only record of *why* a member
sits where it does; `../../split-grouping.lock` records only *that* it does.

**A spec cannot be re-run against a tree that has already been split.** The
primary part keeps the original file name, so `spec.source` no longer holds the
pre-split file and `writeparts.py --check` will say the spec does not describe
it. That is expected, and the tool says so. To re-derive a split, restore the
source at the commit before it landed.

Only `netglue.json` exists. The fourteen earlier ones are gone and cannot be
reconstructed — their grouping survives only in `split-grouping.lock` and in
the part files themselves.
