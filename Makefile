# pb-and-j build pipeline. All dotnet work runs inside the pb-dev distrobox.
# deploy is gated on: tests green + 100% line/branch/method coverage + game build hash match.

MOD_ID      := pb-and-j
# Derived from this file's own location rather than hardcoded: the absolute path
# of somebody's home directory has no business in a public repo, and this also
# makes `make -C` work. Safe to evaluate here because there are no includes, so
# MAKEFILE_LIST holds exactly this file.
# Declared rather than inherited. The partition checks below use process
# substitution, which is a bashism, and make's default shell is /bin/sh --
# bash on this machine, dash on plenty of others, where the guards would fail
# with a syntax error rather than a verdict. A guard that cannot run is worse
# than no guard, because it fails in the direction of looking fine.
SHELL       := /bin/bash

REPO        := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
DBX         := distrobox enter pb-dev --
DOTNET_ENV  := export NUGET_PACKAGES=$(REPO)/.packages;
GAME_ASM    := $(HOME)/.local/share/Steam/steamapps/common/Phantom Brigade/PhantomBrigade_Data/Managed/Assembly-CSharp.dll
MODS_DIR    := $(HOME)/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/Local/PhantomBrigade/Mods
PINNED_SHA  := $(shell grep -o '`[a-f0-9]\{64\}`' GAME_BUILD.md | tr -d '`')
PLAYER_LOG  := $(HOME)/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log

MOD_VER     := $(shell grep '^ver:' mod/metadata.yaml | awk '{print $$2}')
CORE_VER    := $(shell grep -o 'ModVersion = "[^"]*"' src/PBAndJ.Core/Net/PbjProtocol.cs | sed 's/.*"\(.*\)"/\1/')
PKG_DIR     := dist/package

# Every file that defines bytes on the wire. Listed rather than globbed so that
# adding one is a conscious act instead of a silent gap — the whole failure this
# guard exists to catch is a wire change nobody noticed making.
#
# The non-obvious members, each of which would otherwise leak:
#   OrderPayload*     the codec delegates order layout to it wholesale
#   UnitSnapshot,
#   Keyframes         the codec round-trips through their constructors, so
#                     swapping two same-typed parameters changes wire meaning
#                     with no diff in the codec at all
#   Seams.cs          OrderApplyResult lives there and crosses as a raw int cast
#   PbjProtocol.cs    same, for RejectReason
# PbjEffect.cs and PbjInboundEvent.cs are deliberately ABSENT: effects are what a
# session asks the local runtime to do and inbound events are local inputs.
# Neither is observable by a peer, so bumping the version for them is noise.
WIRE_FILES  := $(addprefix src/PBAndJ.Core/Net/, \
                 PbjMessage.cs PbjMessageCodec.cs OrderPayload.cs OrderPayloadCodec.cs \
                 UnitSnapshot.cs Keyframes.cs ReplayAssets.cs ScenarioPayload.cs \
                 PbjWriter.cs PbjReader.cs FloatBits.cs FrameEncoder.cs FrameDecoder.cs \
                 PbjProtocol.cs Seams.cs)

# The other half of the partition, and the reason it exists.
#
# 🔴 SCOPE IS ALL OF src/PBAndJ.Core, RECURSIVELY, AND BY FULL PATH — three
# corrections to the first version of this guard, each of which let a file
# through while it printed "wire partition OK":
#   * `ls .../Net/*.cs` does not descend, so the first `mkdir Net/Codec` put a
#     new wire helper outside the guard entirely. Verified by creating one and
#     watching the check pass. A modularization program is a machine for making
#     new files and directories, so this was not hypothetical.
#   * The scan stopped at Net/, but Core's root already holds eight .cs files
#     and nothing stops a wire type landing beside them.
#   * Comparing BASENAMES let two files with the same name in different
#     directories satisfy each other. Full repo-relative paths cannot.
# The rule to keep: the guard must ENUMERATE THE TREE, never a directory it was
# told about. Any narrowing of this scan is a silent re-opening.
#
# WIRE_FILES is an ALLOWLIST, and an allowlist only notices what leaves it. A
# file that goes missing is caught loudly (check-wire-surface tests for each
# one). A file that is NEWLY EXTRACTED out of a wire-bearing file is not caught
# at all: the remaining files' hash moves, the guard fires once, the developer
# bumps the version and re-records -- and from that moment the extracted layout
# is permanently unguarded while the guard keeps printing "wire surface OK".
#
# Naming both halves turns that silence into a build failure. Every .cs in
# Core/Net must appear in exactly one list, so a new file cannot be ignored by
# accident -- only by a decision someone had to write down.
# Core's root files. None carries wire layout — they are console/report/version
# helpers — but they are named so the partition can cover the whole assembly
# rather than one directory inside it.
NONWIRE_ROOT  := $(addprefix src/PBAndJ.Core/, \
                 ActionDumpFormatter.cs ActionSnapshot.cs InjectionReport.cs \
                 LoadBanner.cs ModVersion.cs SnapshotDiff.cs UpdateLog.cs \
                 UpdateOffer.cs)

NONWIRE_FILES := $(NONWIRE_ROOT) $(addprefix src/PBAndJ.Core/Net/, \
                 AssetBuffer.cs AssetPoolDigest.cs ClientSession.cs  \
                 ConnectForm.cs ConnectSettings.cs ConnectText.cs  \
                 DestructionPlayback.cs HostSession.cs  \
                 HostSession.CombatEntry.cs HostSession.Dispatch.cs  \
                 HostSession.Handshake.cs HostSession.Load.cs  \
                 HostSession.Lobby.cs HostSession.Roster.cs  \
                 HostSession.Scenario.cs HostSession.Tick.cs  \
                 HostSession.Turn.cs HostSession.TurnComplete.cs  \
                 KeyframePlayback.cs  \
                 LoadBarrier.cs LobbyBarrier.cs LobbySaveWrites.cs  \
                 LobbySaves.cs LobbyView.cs MeleeTrajectoryPlayback.cs  \
                 NetLog.cs PartIntegrityPlayback.cs PartStateDigest.cs  \
                 PassengerRules.cs PbjEffect.cs PbjInboundEvent.cs  \
                 PbjMailbox.cs PbjPeerRegistry.cs PbjProtocolException.cs  \
                 PoseDigest.cs  \
                 PbjRuntime.cs PoseBuffer.cs PoseTracks.cs ReactionPings.cs  \
                 ReplayAssetParts.cs ReplayAssetPlayback.cs  \
                 ReplayVisibility.cs StateDigest.cs TrackThinning.cs  \
                 TurnBarrier.cs UnitAssignments.cs)

.PHONY: check-file-sizes size-report-selftest measure-net-coverage test build dist deploy check-no-drive-channel check-game-hash check-mod-version check-wire-surface check-wire-partition check-coverage-scope record-wire-surface wire-surface-hash clean log peer peer-selftest peer-connect peer-listen package

# metadata.yaml is the one place PbjProtocol.ModVersion cannot reach, and a
# disagreement between them is invisible until a peer is refused by a host —
# on someone else's machine, which is where this first went wrong.
check-mod-version:
	@test -n "$(CORE_VER)" || { echo "FATAL: could not read ModVersion from PbjProtocol.cs"; exit 1; }
	@if [ "$(MOD_VER)" != "$(CORE_VER)" ]; then \
		echo "FATAL: mod version mismatch — peers would refuse each other."; \
		echo "  mod/metadata.yaml ver:       $(MOD_VER)"; \
		echo "  PbjProtocol.ModVersion:      $(CORE_VER)"; \
		exit 1; \
	fi
	@echo "mod version OK ($(MOD_VER))"

# check-mod-version proves the two version strings agree with EACH OTHER. It says
# nothing about whether either moved when the wire did — it would happily pass a
# build that added three message types and bumped nothing, which is exactly the
# mistake "move the version with the surface, not after it" exists to stop.
#
# So: hash the wire-bearing sources, and fail when that hash moves without
# ModVersion moving. Comments and blank lines are stripped first, so documenting
# a message cannot fail a build. A refactor inside the codec WILL fail it, and
# that is the intended default rather than a rough edge — the codec is the wire.
# When the change genuinely does not move any byte, re-record with:
#     make record-wire-surface
# Every .cs in Core/Net must be classified as wire-bearing or not. See
# NONWIRE_FILES for why an allowlist alone is not enough.
check-wire-partition:
	@listed=$$(printf '%s\n' $(WIRE_FILES) $(NONWIRE_FILES) | sort); \
	actual=$$(find src/PBAndJ.Core -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | sort); \
	unclassified=$$(comm -13 <(printf '%s\n' "$$listed") <(printf '%s\n' "$$actual")); \
	phantom=$$(comm -23 <(printf '%s\n' "$$listed") <(printf '%s\n' "$$actual")); \
	dupes=$$(printf '%s\n' "$$listed" | uniq -d); \
	fail=0; \
	if [ -n "$$unclassified" ]; then \
		echo "FATAL: PBAndJ.Core files in neither WIRE_FILES nor NONWIRE_FILES:"; \
		printf '  %s\n' $$unclassified; \
		echo "  Decide whether each one carries bytes a peer parses, then add it to"; \
		echo "  the matching list in the Makefile. A file extracted out of a wire type"; \
		echo "  belongs in WIRE_FILES, and leaving it out silently narrows the hash."; \
		fail=1; fi; \
	if [ -n "$$phantom" ]; then \
		echo "FATAL: listed but absent from src/PBAndJ.Core:"; printf '  %s\n' $$phantom; fail=1; fi; \
	if [ -n "$$dupes" ]; then \
		echo "FATAL: classified twice — a file must be in exactly one list:"; printf '  %s\n' $$dupes; fail=1; fi; \
	[ "$$fail" = "0" ] || exit 1
	@echo "wire partition OK ($(words $(WIRE_FILES)) wire, $(words $(NONWIRE_FILES)) not)"

check-wire-surface: check-wire-partition
	@for f in $(WIRE_FILES); do \
		test -f "$$f" || { echo "FATAL: wire-surface file missing: $$f"; \
			echo "  Fix the list in the Makefile — a missing name would silently hash nothing."; exit 1; }; \
	done
	@test -f wire-surface.lock || { echo "FATAL: wire-surface.lock is missing — run 'make record-wire-surface'"; exit 1; }
	@actual=$$($(MAKE) --no-print-directory wire-surface-hash); \
	locked=$$(grep '^sha256:' wire-surface.lock | awk '{print $$2}'); \
	lockver=$$(grep '^version:' wire-surface.lock | awk '{print $$2}'); \
	if [ "$$actual" = "$$locked" ]; then echo "wire surface OK (unchanged since $$lockver)"; exit 0; fi; \
	if [ "$(CORE_VER)" = "$$lockver" ]; then \
		echo "FATAL: the wire surface changed but ModVersion did not."; \
		echo "  ModVersion is still:  $$lockver"; \
		echo "  locked hash:          $$locked"; \
		echo "  actual hash:          $$actual"; \
		echo "  A peer admitted by a matching version string would fault on the first"; \
		echo "  message it did not expect. Bump PbjProtocol.ModVersion and mod/metadata.yaml"; \
		echo "  in the SAME commit as the surface change, then: make record-wire-surface"; \
		exit 1; \
	fi; \
	echo "FATAL: the wire surface changed and ModVersion moved to $(CORE_VER) — now record it."; \
	echo "  Run: make record-wire-surface"; \
	exit 1

# Which projects the 100% coverage gate actually measures, and which it does not.
#
# 🔴 THE GATE IS AN ASSEMBLY FILTER, and that makes its scope INVISIBLE. `test`
# passes /p:Include="[PBAndJ.Core]*", and tests/PBAndJ.Core.Tests references only
# Core. So a helper MOVED OUT of Core into a sibling produces a green build, a
# green 100%, and permanently ungated logic — with no warning anywhere, because
# the total is computed over the included assembly alone. Nothing that says
# "coverage is fine" is lying; it is answering a narrower question than it looks.
#
# This partition cannot catch that move on its own — Net is already declared
# uncovered, so relocating into it adds no new project. What it does catch is the
# scope changing by accident: a NEW project appearing and being measured by
# nobody. The move itself is a MANIFEST RULE for splits (any type leaving
# PBAndJ.Core leaves the gate, and must be acknowledged in writing), because no
# cheap mechanism detects it and pretending otherwise would be its own vacuous
# guard.
COVERED_PROJECTS   := src/PBAndJ.Core

# Each entry needs a reason, not just a name.
#   PBAndJ.Net — 601 lines of TCP transport, EXCLUDED ON PURPOSE. Every type
#     carries [ExcludeFromCodeCoverage] with the reason in its own doc comment:
#     the humble-object split puts socket-and-thread glue outside the gate, and
#     every protocol decision lives in PBAndJ.Core, which IS at 100%.
#     ⭐ MEASURED 2026-08-18, because "exercised by the self-test" was an
#     assertion nobody had checked and it is the whole justification for the
#     exclusion: `make measure-net-coverage` reports ~83% line / ~68% branch /
#     96% method from peer-selftest alone. ⚠️ Branch drifts run to run (67.4 and
#     69.8 on two consecutive runs) because this is real threads over real
#     sockets and some branches are timing-dependent — read it as a band, not a
#     figure, and never gate on it. The claim holds. The residual is
#     almost entirely `catch (SocketException)` / `catch (Exception)` blocks and
#     a few defensive guards -- failure paths a clean loopback run cannot
#     produce. Re-take the reading if the transports change.
#   PBAndJ.Mod — Unity and Harmony types. Cannot load in a test host, so it is
#     unmeasurable rather than unmeasured; this is why [ExcludeFromCodeCoverage]
#     appears there and never in Core.
UNCOVERED_PROJECTS := src/PBAndJ.Net src/PBAndJ.Mod

# Advisory size report, and ADVISORY IS THE DESIGN rather than timidity.
#
# It is NOT a dependency of dist, and it exits 0 even when it reports. The thing
# it would otherwise gate is `deploy`, and `deploy` gates the two-instance
# playtest rig — the scarcest resource in this project. A feature branch adding
# one line to a 2343-line session class must not become unable to playtest until
# that class is split mid-milestone; that trains the override habit, and an
# override habit is worth less than no gate.
#
# Measured before choosing the trigger, because the number is what says whether
# you are building a guard or a tax: 67% of code commits here touch a file over
# the 500-line budget. So the ratchet reports only what a commit MADE WORSE, and
# existing debt stays silent until someone touches it.
#
# `--selftest` drives the ratchet directly with synthetic data — eleven cases,
# each asserted in both directions, including the one the whole design turns on:
# an over-limit method MOVED between files with an identical body must be
# SILENT. Both were mutation-checked: keying identity by file (the naive
# diff-scoped version) fails that case, and dropping the shape exemption fails
# another.
check-file-sizes:
	@python3 tools/size-report.py $(BASE)

size-report-selftest:
	@python3 tools/size-report.py --selftest

# Measures what peer-selftest actually covers in PBAndJ.Net, which the gate
# cannot see because every type there is [ExcludeFromCodeCoverage].
#
# 🔴 THE REASON THIS EXISTS: adding PBAndJ.Net to the coverage filter reports
# 100% while nothing tests it -- the attributes exclude every type, so the gate
# measures an empty set and calls it perfect. Verified by doing it. That is a
# check whose "all clear" output is also its "I compared nothing" output, and it
# is the most convincing possible way to close this gap wrongly.
#
# So the honest instrument strips the attributes into a scratch copy, runs the
# real self-test over real loopback sockets, and reports the number. Never wired
# into `dist`: it needs a global tool, it mutates sources temporarily, and it
# takes as long as a full self-test. Run it when the transports change.
measure-net-coverage:
	@command -v coverlet >/dev/null || { \
		echo "FATAL: coverlet.console is not installed."; \
		echo "  dotnet tool install --global coverlet.console"; exit 1; }
	@tmp=$$(mktemp -d); trap 'for f in src/PBAndJ.Net/*.cs; do cp "$$tmp/$$(basename $$f)" "$$f"; done; rm -rf "$$tmp"' EXIT; \
	for f in src/PBAndJ.Net/*.cs; do cp "$$f" "$$tmp/$$(basename $$f)"; done; \
	sed -i '/^\s*\[ExcludeFromCodeCoverage\]$$/d' src/PBAndJ.Net/*.cs; \
	$(DOTNET_ENV) dotnet build tools/pbj-peer -c Debug >/dev/null || exit 1; \
	coverlet tools/pbj-peer/bin/Debug/net9.0 \
		--target tools/pbj-peer/bin/Debug/net9.0/pbj-peer --targetargs selftest \
		--include "[PBAndJ.Net]*" --format lcov --output "$$tmp/net" | tail -8

check-coverage-scope:
	@listed=$$(printf '%s\n' $(COVERED_PROJECTS) $(UNCOVERED_PROJECTS) | sort); \
	actual=$$(find src -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' \
	          | xargs -n1 dirname | sort); \
	unclassified=$$(printf '%s\n' "$$listed" "$$listed" "$$actual" | sort | uniq -u); \
	phantom=$$(printf '%s\n' "$$actual" "$$actual" "$$listed" | sort | uniq -u); \
	fail=0; \
	if [ -n "$$unclassified" ]; then \
		echo "FATAL: projects under src/ that the coverage gate has never been told about:"; \
		printf '  %s\n' $$unclassified; \
		echo "  Add each to COVERED_PROJECTS (and to the Include filter in 'test'),"; \
		echo "  or to UNCOVERED_PROJECTS with a written reason. A project nobody"; \
		echo "  classified is a project nobody measures, and the gate still says 100%."; \
		fail=1; fi; \
	if [ -n "$$phantom" ]; then \
		echo "FATAL: classified but no longer present:"; printf '  %s\n' $$phantom; fail=1; fi; \
	for p in $(COVERED_PROJECTS); do \
		n=$$(basename $$p); \
		grep -q "Include=\"\[$$n\]\*\"" Makefile || { \
			echo "FATAL: $$p is listed as covered but '[$$n]*' is not in the test filter."; \
			fail=1; }; \
	done; \
	[ "$$fail" = "0" ] || exit 1
	@echo "coverage scope OK ($(words $(COVERED_PROJECTS)) measured, $(words $(UNCOVERED_PROJECTS)) declared not)"

# Deliberately not a dependency of anything: re-recording is the developer saying
# "yes, I meant that", and a build step that says it for them is no guard at all.
record-wire-surface:
	@hash=$$($(MAKE) --no-print-directory wire-surface-hash); \
	{ echo "# The wire surface as of this ModVersion. See 'check-wire-surface' in the"; \
	  echo "# Makefile. Re-record only when you have decided the change is intentional."; \
	  echo "version: $(CORE_VER)"; \
	  echo "sha256: $$hash"; } > wire-surface.lock
	@echo "recorded wire surface at $(CORE_VER)"

# Order is the Makefile's order, so the hash is stable across machines.
wire-surface-hash:
	@cat $(WIRE_FILES) | grep -v '^[[:space:]]*//' | grep -v '^[[:space:]]*$$' | sha256sum | cut -d' ' -f1

test:
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet test tests/PBAndJ.Core.Tests \
		/p:CollectCoverage=true /p:Include="[PBAndJ.Core]*" \
		/p:Threshold=100 /p:ThresholdType=line%2cbranch%2cmethod /p:ThresholdStat=total'

# PBJ_DRIVE=true compiles the dev drive channel in. Default OFF, and `deploy`
# turns it on for its own dependency chain only (GNU Make passes target-specific
# variables down to prerequisites). `package` refuses to run with it set.
PBJ_DRIVE ?= false

# What the size ratchet compares against. HEAD^ for a post-commit report: it
# sidesteps every merge-base failure (inert on the default branch where
# merge-base(main,main) is HEAD, firing on foreign debt after a rebase, undefined
# in a shallow clone, and ambiguous between working tree and commits).
BASE ?= HEAD^

build: test
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet build src/PBAndJ.Mod -c Release -p:PbjDrive=$(PBJ_DRIVE)'

dist: build check-mod-version check-wire-surface check-coverage-scope
	rm -rf dist/$(MOD_ID)
	mkdir -p dist/$(MOD_ID)/Libraries
	cp mod/metadata.yaml dist/$(MOD_ID)/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Mod.dll dist/$(MOD_ID)/Libraries/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Core.dll dist/$(MOD_ID)/Libraries/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Net.dll dist/$(MOD_ID)/Libraries/

# The drive channel is a loopback socket that runs ANY console command, and
# Quantum Console's Extras assembly already registers `exec` (compile and run
# arbitrary C#), file read/write and HTTP. Shipping it would hand every player a
# remote code execution surface in their game.
#
# The mod is a compile-time opt-in (PBJ_DRIVE, see PBAndJ.Mod.csproj), so a
# shipped build genuinely does not contain the code. This proves that against
# the built artifact rather than trusting the build to have been invoked right —
# the same reasoning as check-wire-surface. What a grep of the DLL can actually
# see is narrower than it looks, and the paragraph below is the rule; this
# sentence used to say "type and literal names" and was wrong about half of it.
#
# ⭐ This is an invariant, not a nicety: the mod is intended for the Steam
# Workshop eventually, and a dev channel reaching a published build is the one
# mistake that cannot be walked back once people have installed it.
#
# TYPE NAMES ONLY, and that is not laziness. Type and member names live in the
# metadata #Strings heap as UTF-8, so a plain grep finds them; string LITERALS
# live in the #US heap as UTF-16, where "PBJ_DRIVE_PORT" is stored with a null
# byte between every character and an ASCII grep silently never matches. A check
# listing literals would look stricter while testing nothing — verified by
# building with the channel in and watching the literal not match. The type names
# are the load-bearing evidence anyway: no type, no code.
# Files that exist ONLY for the drive channel: wholly wrapped in #if PBJ_DRIVE,
# so a clean build must contain nothing they declare.
DRIVE_FILES := src/PBAndJ.Mod/Net/DriveGlue.cs src/PBAndJ.Mod/Net/DriveProbeGlue.cs

# Files that merely CALL into the drive channel behind #if PBJ_DRIVE. Their own
# types ship and must not be grepped for.
DRIVE_CALLSITE_FILES := src/PBAndJ.Mod/ModEntry.cs src/PBAndJ.Mod/Net/ActuatorGlue.cs \
                        src/PBAndJ.Mod/Net/NetGlue.cs src/PBAndJ.Mod/Net/VfxProbeGlue.cs

# Derived from DRIVE_FILES rather than written out, because a hardcoded name
# fails in two directions at once: a RENAMED type makes the grep pass
# vacuously (it looks for a string nothing is called any more), and a NEWLY
# EXTRACTED type is simply not on the list. Reading the names out of the source
# closes both -- for the files we know about; the partition check below is what
# closes the case of a drive type moving into a file nobody listed.
#
# Top-level declarations only (indented at most one level, i.e. inside the
# namespace and not inside another type). That is not a shortcut: a nested type
# cannot reach the assembly without its enclosing type, and the enclosing type
# is exactly what this greps for. It also keeps generic nested names like
# 'Request' out of the list, which would match almost any assembly and turn the
# guard into a permanent false alarm.
DRIVE_SYMBOLS = $(shell grep -hE '^ {0,4}(internal|public|private|sealed|static).*(class|struct|enum|interface) +[A-Za-z_]' \
                  $(DRIVE_FILES) | grep -oE '(class|struct|enum|interface) +[A-Za-z_][A-Za-z0-9_]*' \
                  | awk '{print $$2}' | sort -u)

check-no-drive-channel:
	@if [ "$(PBJ_DRIVE)" = "true" ]; then \
		echo "FATAL: refusing to package a build with the drive channel compiled in."; \
		echo "  Run 'make package' without PBJ_DRIVE=true."; \
		exit 1; \
	fi
	@test -f dist/$(MOD_ID)/Libraries/PBAndJ.Mod.dll || { echo "FATAL: no built assembly to check"; exit 1; }
	@touching=$$(grep -rl "PBJ_DRIVE" src --include=*.cs | sort); \
	known=$$(printf '%s\n' $(DRIVE_FILES) $(DRIVE_CALLSITE_FILES) | sort); \
	stray=$$(comm -23 <(printf '%s\n' "$$touching") <(printf '%s\n' "$$known")); \
	if [ -n "$$stray" ]; then \
		echo "FATAL: files use PBJ_DRIVE but are in neither DRIVE_FILES nor DRIVE_CALLSITE_FILES:"; \
		printf '  %s\n' $$stray; \
		echo "  A drive-only file must be in DRIVE_FILES so its types are grepped for."; \
		echo "  A file that merely calls into the channel goes in DRIVE_CALLSITE_FILES."; \
		exit 1; fi
	@test -n "$(DRIVE_SYMBOLS)" || { echo "FATAL: no drive symbols derived — the extraction regex has rotted"; exit 1; }
	@for sym in $(DRIVE_SYMBOLS); do \
		if grep -qa "$$sym" dist/$(MOD_ID)/Libraries/PBAndJ.Mod.dll; then \
			echo "FATAL: '$$sym' is present in the assembly about to ship."; \
			echo "  The drive channel must never reach a published build."; \
			exit 1; \
		fi; \
	done
	@echo "no drive channel in the shipped assembly"

check-game-hash:
	@test -n "$(PINNED_SHA)" || { echo "FATAL: no pinned SHA found in GAME_BUILD.md"; exit 1; }
	@actual=$$(sha256sum "$(GAME_ASM)" | cut -d' ' -f1); \
	if [ "$$actual" != "$(PINNED_SHA)" ]; then \
		echo "FATAL: game Assembly-CSharp.dll hash mismatch — game updated? Re-vendor and re-verify (see GAME_BUILD.md)."; \
		echo "  pinned:  $(PINNED_SHA)"; \
		echo "  actual:  $$actual"; \
		exit 1; \
	fi; \
	echo "game build hash OK"

# The standalone protocol peer. Speaks the same PBAndJ.Core the mod does, so
# a running game can be tested against a real second peer.
peer: test
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet build tools/pbj-peer -c Release'

# Real sockets, no game. Gates deploy so a broken protocol cannot reach the game.
peer-selftest: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- selftest'

HOST ?= 127.0.0.1
PORT ?= 27600
NAME ?= ally

peer-connect: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- \
		connect --host $(HOST) --port $(PORT) --name $(NAME) $(PEER_ARGS)'

peer-listen: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- \
		listen --bind $(HOST) --port $(PORT) --name $(NAME)'

deploy: PBJ_DRIVE := true
deploy: dist check-game-hash peer-selftest
	mkdir -p "$(MODS_DIR)"
	rm -rf "$(MODS_DIR)/$(MOD_ID)"
	cp -r dist/$(MOD_ID) "$(MODS_DIR)/$(MOD_ID)"
	@echo "deployed to $(MODS_DIR)/$(MOD_ID)"

# What gets sent to someone on another machine. Gated on the same things deploy
# is, because a package that fails the gate is worse than no package: the person
# receiving it cannot tell a protocol bug from their own setup.
package: dist check-game-hash peer-selftest check-no-drive-channel
	rm -rf $(PKG_DIR)
	mkdir -p $(PKG_DIR)
	cd dist && zip -qr ../$(PKG_DIR)/$(MOD_ID)-mod-v$(MOD_VER).zip $(MOD_ID)
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet publish tools/pbj-peer \
		-c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
		-o $(PKG_DIR)/peer-win-x64'
	rm -f $(PKG_DIR)/peer-win-x64/*.pdb
	cd $(PKG_DIR)/peer-win-x64 && zip -qr ../pbj-peer-win-x64-v$(MOD_VER).zip .
	rm -rf $(PKG_DIR)/peer-win-x64
	cp mod/FRIEND-README.md $(PKG_DIR)/README.md
	@echo "--- package ready in $(PKG_DIR) ---"
	@ls -lh $(PKG_DIR)
	@echo
	@echo "Game build the friend must match:"
	@grep 'buildinfo.yaml' GAME_BUILD.md
	@echo "Send the two zips + README.md. Passphrase and address go separately."

log:
	tail -n 100 -f "$(PLAYER_LOG)"

clean:
	rm -rf dist src/PBAndJ.Mod/bin src/PBAndJ.Mod/obj src/PBAndJ.Core/bin src/PBAndJ.Core/obj \
		src/PBAndJ.Net/bin src/PBAndJ.Net/obj tools/pbj-peer/bin tools/pbj-peer/obj \
		tests/PBAndJ.Core.Tests/bin tests/PBAndJ.Core.Tests/obj tests/PBAndJ.Core.Tests/coverage.json
