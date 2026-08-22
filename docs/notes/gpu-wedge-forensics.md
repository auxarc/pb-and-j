# GPU wedge forensics — the run of 2026-08-22, re-examined from the kernel side

Written 2026-08-22 ~00:45, on the still-wedged machine, before the reboot. Everything in here
was read or run on this machine tonight unless it carries an **UNVERIFIED** tag; commands whose
absence-of-output mattered were positively controlled first (each case is noted where it arises).

✅ **THE WEDGE IS CLEARED — verified 2026-08-22, not merely reported.** `nvidia-smi -q` now says
`GPU Recovery Action : None` (the line this file quotes as `Reset`). `journalctl -k -b` for the
current boot holds **zero `NVRM: Xid` lines** — pattern positively controlled: the same journal
prints 98+ `NVRM` lines, and the one case-insensitive "xid" hit is the r8169 NIC's `XID 609`, a chip
identifier, not a fault. `journalctl --list-boots`: the previous boot ended **2026-08-22 01:32:03**
and the current one began **01:32:29**, i.e. *after* the 00:01:44 Xid 51 — so the reboot really is
on the far side of the wedge. ⚠️ **Trap for the next reader:** this boot's earliest kernel lines are
stamped in **UTC** (`Aug 21 21:32:31` = 01:32:31 EDT); do not read that as "the boot predates the
wedge". 🔴 **The §6 ladder has NOT been run** — it stays ATTENDED-ONLY, and nothing below it is
answered. Clearing the wedge restored Vulkan; it did not answer the pair question.

## The verdict

**Nothing leaked. The GPU took a scheduler fault — `Xid 51: BAD_TSG` — at 00:01:44, the moment
the second headless gamescope called `vkCreateDevice`, and the driver marked the GPU
"reset required" (`Xid 154`, recovery action → PF FLR). Since that instant the driver refuses
every new channel allocation with `NV_ERR_RESET_REQUIRED`, which userspace sees as
`VK_ERROR_INITIALIZATION_FAILED`.** The desktop keeps running because existing channels keep
running; only *new* device creation is refused. `nvidia-smi -q` says it in one line, right now:

    GPU Recovery Action                                : Reset

**Can this be settled before the reboot? Yes — it is settled.** The kernel journal held the
whole story; the reboot is needed only to (a) restore Vulkan, and (b) answer the follow-on
questions (determinism, the pair) that require launching things.

Three prior diagnoses are refuted by this evidence, including the one committed in
`docs/notes/headless-rig-feasibility.md` §14.2:

1. **The orphaned compositor did not cause the second-launch failure.** At 00:01:44 there was
   no orphan — the first compositor + game were legitimately alive (that is what `--pair`
   does). The orphan came into existence *later*, when teardown's `pkill` missed
   `gamescope-wl`. It was a real teardown bug and causally irrelevant to the wedge.
2. **SIGKILL did not trigger the wedge.** Every SIGKILL in the incident happened ≥5 minutes
   *after* the Xid. The abrupt-termination theory is exonerated for this incident (see §3).
3. **Nothing is "held" by leaked contexts.** The failure is a driver-side refusal
   (reset-required), not resource exhaustion or a stuck lease. §14.3's sentence "the leaked
   contexts outlive the processes" is wrong about the mechanism, right about the symptom.

## The timeline, from kernel log + file mtimes + git

| time (EDT) | event | source |
|---|---|---|
| Aug 21 10:19 | boot; `nvidia-drm.modeset=1`, `NVreg_PreserveVideoMemoryAllocations=1` | `journalctl -k -b` |
| 23:18:32 | Caves of Qud SIGSEGV (test vehicle from earlier headless probing) | `coredumpctl` |
| 23:20:53 | `vkcube` SIGSEGV in `demo_select_physical_device` / `terminator_GetPhysicalDeviceSurfaceSupportKHR` — a null-surface crash from running it without a display, **not** GPU damage (the GPU provably worked 35 min later) | `coredumpctl`, journal backtrace |
| 23:51:39 | Caves of Qud SIGSEGV again | `coredumpctl` |
| 23:55:17 | PR #55 merged (the experiment script) | `git log` |
| ~23:56–00:01:29 | **Experiment steps 1–4 pass.** Screenshot `/tmp/pb-headless-menu.png` written 00:01:29, 2.0 MB, full menu | file mtime, §14.1 |
| **00:01:44** | **First NVIDIA kernel event of the entire boot:** `Xid 51, BAD_TSG encountered on runlist: 0 with error code: 4`, then `Xid 154, GPU recovery action changed from 0x0 (None) to 0x1 (PF FLR)`, then two `GspRmAlloc failed … status=0x62 [NV_ERR_RESET_REQUIRED]` for client `0xc1d00660` — the pair of alloc failures a single `vkCreateDevice` produces | `journalctl -k` |
| 00:01:44.866 | `/tmp/gs-client.log` last write: second gamescope selected the 4070 Ti fine, then `vkCreateDevice failed (VkResult: -3)`, `Failed to create backend.` | file mtime + content |
| 00:07:04 | `gs-probe.log`: a third gamescope attempt, same -3; matching NVRM lines at 00:07:04 | file mtime, kernel log |
| 00:07:31 | `/tmp/gs-host.log` last write (`Broken pipe`) — the first instance was **alive until here**, i.e. for 5m47s *after* the wedge | file mtime |
| 00:07:44–00:10:39 | more failing relaunches/probes, each logging the same `NV_ERR_RESET_REQUIRED` pair | kernel log |
| 00:16:26 | PR #56 merged — the §14 write-up, blaming the orphan | `git log` |
| 00:17:44 | this forensics session's own `vulkaninfo --summary` probe fails and logs the identical NVRM pair — **the loop closes: the live failure is the same mechanism as the incident's** | run + kernel log |

93 NVRM lines in the journal since 20:00 — every one of them at or after 00:01:44. Zero before.

⚠️ `dmesg` itself returns "read kernel buffer failed: Operation not permitted" for this user —
that is a **permission** zero, established as such before use. `journalctl -k` works unprivileged
and was positively controlled (it returns boot messages for `-b`).

## 1. What actually leaked — nothing

- **fd census, now** (every `/proc/*/fd` readlink'd): 225 open fds on `/dev/nvidia*`, 36 on
  `/dev/dri/*`, **all** owned by live desktop processes (kwin_wayland, Xwayland, plasmashell,
  firefox, steam/steamwebhelper, coolercontrol, xdg-desktop-portal, xwaylandvideobridge).
  Zero game, wine, or gamescope processes exist (`ps -eo comm=` census — the §14-style
  self-match trap avoided).
- **Module refcounts, now:** `nvidia 785, nvidia_drm 144, nvidia_modeset 30, nvidia_uvm 4` —
  on a machine running *only* the desktop. The incident-time reading of `nvidia used_by=783`
  was therefore **normal for this desktop**, not "high"; each fd/mmap/subdevice takes multiple
  refs. The 783 was a misread baseline, not a leak signal.
- **GPU memory:** 1812 MiB / 12282 MiB, all attributed to desktop processes by `nvidia-smi`.
- What *is* stranded is one bit of driver state: the recovery-action flag. It is not garbage
  that ages out — it still refuses allocations 76+ minutes after the Xid.

## 2. Which actor

**The NVIDIA driver (610.57.04, open kernel module, GSP-based; built 2026-08-11) is the
faulting component.** No legal userspace request may put a GPU into reset-required; whatever
the second gamescope's `vkCreateDevice` asked for, `BAD_TSG` on the runlist plus a demanded
PF FLR is a driver/GSP fault by definition. The **triggering workload** was: one headless
gamescope compositor + a Proton/DXVK Unity game active, then a second headless gamescope
creating its Vulkan device.

Web check (2026-08-22): **no public report found** matching Xid 51 `BAD_TSG` in this shape.
The known gamescope-on-NVIDIA `vkCreateDevice` issues (e.g.
[open-gpu-kernel-modules #140](https://github.com/NVIDIA/open-gpu-kernel-modules/issues/140),
[gamescope #1454](https://github.com/ValveSoftware/gamescope/issues/1454),
[gamescope #1125](https://github.com/ValveSoftware/gamescope/issues/1125)) are *launch-time
failures on every launch* — they match our error string, **not our mechanism** (our first
launch rendered a full menu). Xid 154 semantics per
[NVIDIA XID docs](https://docs.nvidia.com/deploy/pdf/XID_Errors.pdf) and
[recovery-flag docs](https://docs.nvidia.com/deploy/a100-gpu-mem-error-mgmt/error-recovery-and-response-flags.html):
recovery action Reset ⇒ terminate all GPU processes, then reset (`nvidia-smi -r`) or reboot.
**UNVERIFIED:** that this is a 610-branch regression — the branch is three weeks old and
absence of reports is also a claim about my search patterns (sighting 13's rule).

Within 00:01:44 the Xids and the failed alloc interleave in the log; whether that exact alloc
RPC *triggered* the fault or arrived microseconds after it is not recoverable from the journal.
**UNVERIFIED** at the sub-second level; the second-launch correlation itself is exact to the
second and is the only GPU-touching event at that moment.

## 3. Was SIGKILL the trigger — no

Order of events: wedge at 00:01:44; the host compositor was alive and writing its log until
00:07:31; the orphan hunt and the SIGKILLs of `gamescope-wl`/`gamescopereaper`/`winedevice.exe`
came after that. **A cause does not postdate its effect.** Additionally, the desktop's history
(games SIGKILLed routinely by `stage_down`/`drop` across months of rig runs) never produced
this state, and today's refcount arithmetic shows the driver reclaimed everything those kills
ever held.

**Consequence for teardown design: an orderly shutdown path is good hygiene but it is not the
fix for this wedge, because teardown was not the trigger — a second concurrent launch was.**
The teardown bug (`pkill` matching nothing) cost the *diagnosis* (it created the orphan that
became the wrong suspect), not the GPU. **UNVERIFIED and left open:** whether SIGKILL of a
Vulkan client can strand driver state in other scenarios; nothing tonight tests it either way.

## 4. What clears it short of a reboot

- **Verified:** nothing available now. The recovery flow requires all GPU clients terminated,
  then a function-level reset (`nvidia-smi -r`, or PCI `reset` via sysfs as root). Every path
  to "all clients terminated" takes the desktop down, which is out of bounds tonight.
- **Logout: probably NOT sufficient by itself**, contrary to §14.3's "a logout probably does
  too". `nvidia-persistenced` is running with persistence mode Enabled, so the RM stays
  initialized after the last client exits, and nothing performs the FLR spontaneously. A
  plausible no-reboot sequence is: log out → from a TTY as root `nvidia-smi -pm 0` then
  `nvidia-smi -r` — but it still costs the session and is **UNVERIFIED**; the reboot is the
  known-good clear. **NOT TESTED (and why):** logout, display-manager restart, module unload,
  `nvidia-smi -r` — all take the user's live desktop down; explicitly forbidden.
- The wedge is stable, not degrading: the desktop has run on its existing channels for 76+
  minutes with no further Xids.

## 5. Prevention design

The wedge itself is a driver fault the scripts cannot prevent; what they can do is **never run
blind against a wedged GPU, detect the wedge at the moment it happens, and produce the
one-pass diagnosis this incident needed three wrong rounds to reach.**

### `tools/headless-experiment.sh`

1. **Run the canary pair BEFORE step 1, not only in teardown.** A pre-wedged GPU currently
   reads as "headless does not work" — exactly tonight's shape, one run later. Gate on:
   - `timeout 25 vulkaninfo --summary` succeeding, and
   - `nvidia-smi -q | grep 'GPU Recovery Action'` reporting `None`.
2. **Add a kernel Xid tripwire.** Record `CYCLE_START=$(date -Is)` at script start; after
   every launch and every teardown run
   `journalctl -k --since "$CYCLE_START" --no-pager | grep -E 'NVRM: Xid'`
   with a positive control first (`journalctl -k -b | head -1` must produce output, else the
   tripwire's zero proves nothing — the dmesg-permission trap). Any Xid ⇒ print it, mark the
   run POISONED, stop launching.
3. **On any `vkCreateDevice` failure, immediately append to the results file:** the Xid grep
   above, `nvidia-smi -q | grep -A2 'GPU Recovery Action'`, and the tail of the failing
   gamescope log. This is the entire diagnosis in one pass. If wedged, also run
   `nvidia-bug-report.sh` *before* the reboot (its value is highest pre-reset) and say where
   the tarball is.
4. **Distinguish the canary's two failure meanings in its output** (sighting 14's rule):
   `vulkaninfo` failing + Recovery Action `Reset` ⇒ "GPU wedged, reboot required, do not
   retry"; failing + `None` ⇒ "device creation broken for another reason — different bug".
5. **Timestamp every step line** (`date -Is` in `say()`). The mtime↔Xid correlation is what
   solved this; make it free next time.
6. **Keep per-run gamescope logs** (`/tmp/gs-host.$(date +%s).log`) instead of overwriting —
   tonight's overwrite is why the pre-midnight CoQ era is only reconstructible from coredumps.
7. Keep `reap_compositors` exactly as is — reframed: it prevents *contaminated experiments*
   (the orphan that misled §14), not wedges.
8. **Orderly shutdown order, as hygiene:** ask the game to quit first (a `pbj.quit` →
   `Application.Quit` drive command does not exist yet and is worth adding), wait, SIGTERM the
   game, wait, SIGTERM `gamescope-wl`, verify by name, SIGKILL only as escalation, then
   `winedevice.exe`. Not because SIGKILL wedged the GPU — it did not — but because an orderly
   path keeps Proton prefixes and Steam state clean and removes SIGKILL from the suspect list
   permanently.
9. **Unattended operation is gated:** no unattended loop until N (suggest 5) consecutive
   attended cycles finish with the tripwire and both canaries green after every cycle. On any
   red, the loop's only correct action is *stop and report* — a reset needs zero GPU clients,
   which an unattended rig on a desktop machine cannot arrange.

### `tools/playtest-m12b.sh`

- `stage_down` kills only the game. Extract the reap + canary block into a shared
  `tools/gpu-teardown.sh` and call it from both `stage_down` and `headless-experiment.sh`, so
  a compositor orphan cannot outlive *either* teardown path.

### `tools/game-instance.sh`

- No changes needed for this incident; it neither launches compositors nor kills anything.

### Is the `vulkaninfo` canary the right one?

Yes, with the recovery-action query beside it. It exercises the exact operation that wedges
(device creation), costs ~1–2 s, and tonight it detected the live wedge on the first try. Its
one blind spot: it cannot say *why* creation failed — the `GPU Recovery Action` line closes
that. Cost note: each failing probe writes 6 NVRM lines to the kernel log; harmless.

## 6. Is the pair question reachable?

**Probably yes — nothing structural says two headless games cannot work.** Two concurrent PB
instances, each with its own Vulkan device, have run on this machine's desktop for the entire
life of the rig (M12–M17 two-game verifications). The new element tonight was the *second
compositor's* device creation, and it died to a driver fault observed exactly once (n=1).
Whether it is deterministic is unknown and is the first thing to establish.

**Cheapest clean test after reboot** — a ladder, every rung bracketed by the canary pair and
the Xid tripwire, each passing rung repeated ~3× before moving up (n=1 is what burned us):

1. `vulkaninfo` + Recovery Action `None` — baseline green.
2. **Two bare headless compositors, no game:** `gamescope --backend headless -- sleep 60`
   twice, concurrently. Isolates "two compositor devices" from everything Proton.
3. One compositor + game up (steps 1–3 of the experiment), then a **bare** second compositor —
   tonight's exact failing shape minus the second game.
4. Full `--pair`.

If rung 2 or 3 reproduces Xid 51: it is deterministic, file it against
`NVIDIA/open-gpu-kernel-modules` with the `nvidia-bug-report.sh` tarball, and try (a) standing
both compositors up *before* launching either game, (b) a different driver branch. If all
rungs pass ×3: tonight was a one-shot driver fault; proceed to unattended gating (§5.9).

## What to capture on the next clean cycle

The instrumentation that would have made this one-pass:

- `date -Is` on every script step (correlates userspace actions to kernel events).
- `journalctl -k --since $CYCLE_START | grep 'NVRM: Xid'` after every launch/teardown, with
  its positive control.
- `nvidia-smi -q | grep 'GPU Recovery Action'` before step 1 and after teardown.
- Per-run, non-overwriting gamescope logs.
- On first failure: `nvidia-bug-report.sh` before anything is killed or rebooted.
- Baseline numbers while green, so "high" has a meaning next time: `nvidia` refcount with the
  desktop idle (~785 on this box), fd census total (~225), GPU memory (~1.8 GB).

## UNVERIFIED — the honest list

- **Whether logout clears the wedge** (untestable without taking the session; the
  persistenced analysis above says probably not, but that is reasoning, not a reading).
- **Whether the fault is deterministic** for the two-compositor shape (n=1).
- **Sub-second causality** at 00:01:44 — the second `vkCreateDevice` is the only candidate
  event and interleaves the Xids in the log, but microsecond ordering is not recoverable.
- **Whether SIGKILL of Vulkan clients is generally safe** — exonerated for this wedge only.
- **610-branch regression attribution** — no public report found; my search patterns are a
  claim about themselves (sighting 13).
- **That the desktop survives until the reboot** — 76 min of stability observed, nothing more.
- Everything in §5/§6 that involves launching anything — all of it is post-reboot work.

Sources consulted (web): [NVIDIA XID error docs](https://docs.nvidia.com/deploy/pdf/XID_Errors.pdf),
[error-recovery flags](https://docs.nvidia.com/deploy/a100-gpu-mem-error-mgmt/error-recovery-and-response-flags.html),
[open-gpu-kernel-modules #140](https://github.com/NVIDIA/open-gpu-kernel-modules/issues/140),
[open-gpu-kernel-modules #1134](https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1134),
[gamescope #1454](https://github.com/ValveSoftware/gamescope/issues/1454),
[gamescope #1125](https://github.com/ValveSoftware/gamescope/issues/1125),
[B200 recovery-action forum thread](https://forums.developer.nvidia.com/t/nvml-driver-reports-gpu-recovery-action-changed-from-0x0-none-to-0x1-gpu-reset-required-on-b200/356040).
