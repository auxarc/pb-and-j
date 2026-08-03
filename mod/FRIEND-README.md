# pb-and-j — co-op test build

Thanks for helping test this. It is a proof-of-concept multiplayer mod for
**Phantom Brigade**, and it is early: expect rough edges, expect to restart
things, expect it to be more interesting than fun.

There are **two stages**. Do stage 1 first — it takes about five minutes, needs
no game at all, and it tells us whether the connection between our machines
works before we spend an evening on the harder half.

---

## What you need, and when

**Stage 1 needs nothing but the `pbj-peer` zip** — not the game, not the mod.
Everything below applies to stage 2.

- **Phantom Brigade `2.2.2` (build `b8339`)**, Steam app 553540, public branch.
  Check yours: pause menu → the version string is in the bottom corner. If it
  does not match, tell me before changing anything — I will rebuild against
  yours rather than have you downgrade.
- **Turn off auto-updates** so a patch cannot land mid-session:
  Steam → Phantom Brigade → Properties → Updates → *"Only update this game when
  I launch it"*.
- The passphrase and port I send you separately.

The mod refuses to connect if our mod builds or game builds differ, and says
which. That is deliberate — without it a mismatch looks like a bug in the
netcode and we would waste the evening chasing it.

---

## Stage 1 — `pbj-peer` (no game needed)

`pbj-peer.exe` is a standalone console program that speaks the same protocol the
mod does. It proves the network path — your router, my router, the latency, the
keepalives — without either of us launching the game.

1. Unzip `pbj-peer-win-x64-v0.4.0.zip` anywhere. It is one big `.exe` (~71 MB
   unzipped); nothing to install, no .NET needed. **You do not need Phantom
   Brigade for this stage at all.**

2. **Windows will almost certainly try to stop you**, because the exe is
   unsigned, downloaded from the internet, and opens a network connection. This
   is expected and not a sign anything is wrong:

   - *"Windows protected your PC"* (blue SmartScreen box) → **More info** →
     **Run anyway**.
   - If it silently refuses to start, right-click `pbj-peer.exe` → **Properties**
     → tick **Unblock** at the bottom → OK.
   - Your antivirus may quarantine it. Single-file .NET executables get false
     positives routinely. If you would rather not add an exclusion for something
     a friend emailed you — completely fair — say so and I will build it a
     different way or we will skip to stage 2.

   If you want to check what it is first: the source is at
   <https://github.com/auxarc/pb-and-j>, and this is `tools/pbj-peer` built with
   `dotnet publish -r win-x64 --self-contained`.

3. Open a terminal in that folder: Shift + right-click in the folder → *Open
   PowerShell window here* or *Open in Terminal*, depending on your Windows
   version.

4. Run, with the address, port and passphrase I sent you:

   ```
   .\pbj-peer.exe connect --host <my-address> --port 27600 --name <your-name> --passphrase <passphrase>
   ```

You should see something like:

```
[pb-and-j] connecting to <address>:27600 as <your-name>
[pb-and-j] welcome | peer #1 | session 7f3a91 | host 'my-pc' | turn 3
[pb-and-j] you control: pb_mech_02
```

That is success — you are in my combat, holding a real mech. Type `help`-ish
commands at the prompt:

| Command | What it does |
|---|---|
| `status` | Where the session thinks you are |
| `units` | Which units you have been dealt |
| `order <unit> <x> <y> <z>` | Queue a move, as an **offset in metres** from where that unit currently stands. Try `order pb_mech_02 0 0 18` |
| `ready` | Submit your orders and wait for me |
| `unready` | Take them back and re-plan |
| `snapshot` | Where everything ended up after the turn ran |
| `keyframes` | How everything *moved* during the turn |
| `scenario` | The combat save my game sent you, if it sent one |
| `pull` | Ask for that save again |
| `quit` | Leave |

The turn runs when we are **both** ready. Then `snapshot` and `keyframes` should
show your mech somewhere new.

**If it does not connect**, tell me exactly what it printed. The useful cases:

- `rejected: BadPassphrase` — I sent you the wrong one, or a typo.
- `rejected: ModVersionMismatch` / `GameBuildMismatch` — our builds differ; it
  will name both. Send me the line.
- `could not connect` with no reply at all — my port forward is wrong. My
  problem, not yours.

---

## Stage 2 — the real game

Only after stage 1 works. **I will send you `pb-and-j-mod-v0.4.0.zip` then** —
you do not need it yet.

### Install the mod

1. Unzip `pb-and-j-mod-v0.4.0.zip`.
2. Drop the whole `pb-and-j` folder into your mods folder:

   **Windows:** `%LOCALAPPDATA%\PhantomBrigade\Mods\`
   — paste that into Explorer's address bar; create `Mods` if it is not there.

   **Linux/Steam Deck (Proton):**
   `~/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/Local/PhantomBrigade/Mods/`

   You should end up with `…\Mods\pb-and-j\metadata.yaml` and
   `…\Mods\pb-and-j\Libraries\` next to it.

3. Launch the game. Nothing visible changes — the mod does nothing at all until
   you start a session.

### Connect, from the main menu

Do this **before** either of us starts a fight — the combat itself comes down the
same connection.

1. Open the dev console: **pause → F1 → type `dev`**.
2. Run, with the details I sent:

   ```
   pbj.join <my-address> 27600 <passphrase>
   ```

`pbj.net-status` should say `CLIENT` and name my machine.

### Get the same combat

The two games have to be in **the same fight**. My game sends you the save
automatically the moment we connect — you should see something like:

```
[pb-and-j] scenario 'pbj_combat_test' received | 2 files, 119,546 bytes
[pb-and-j] scenario written to 'pbj_combat_test' — run pbj.combat-load to enter it
```

Then run:

```
pbj.combat-load
```

You should land in the same combat I am in, with the same mechs.

If the transfer did not happen — nothing in the log, or you had an older copy —
run `pbj.scenario-pull` and it will fetch it again. If that says *"no combat save
to send"*, that is my end: I have not run `pbj.combat-save` yet. Tell me.

It does **not** load the save for you on its own, deliberately — being yanked out
of a menu by a network message would be worse than typing one command.

### Playing a turn

- Give orders to **your** units only — the game will let you draw orders for any
  of them, but mine get rejected. `pbj.net-status` and the log say which are
  yours.
- Press **Execute** as normal. That does not run the turn: it tells me you are
  ready. The turn runs when we both are.
- Your units then move. What you are watching is a replay of what happened on my
  machine, streamed across — so it will look slightly floaty, and mechs will
  slide rather than walk. That is expected in this build, not a bug.
- If we drop, `pbj.rejoin` gets you back with your units for two minutes.

---

## Things that are known-broken

Not worth reporting; already on the list:

- Mechs slide instead of walking during the replay. Animation poses are not
  streamed yet.
- Your turn counter may not advance. Harmless.
- Anything that was destroyed or spawned mid-turn may appear in the wrong place
  until the end-of-turn correction lands.
- Only my machine's combat is real. If we somehow end up in different fights,
  everything will look wrong — reload the save and start over.

## Worth reporting

- Anything that says `DIVERGED` or `STILL DIVERGED` — copy the whole line.
- A disconnect that `pbj.rejoin` cannot recover.
- The game hanging or crashing, especially at the moment a turn commits.
- Your log file, which is the single most useful thing you can send:

  **Windows:** `%USERPROFILE%\AppData\LocalLow\Brace Yourself Games\Phantom Brigade\Player.log`

  It is overwritten each launch, so grab it before relaunching — and
  `Player-prev.log` next to it holds the run before.

---

## A note on what this actually does

While a session is running, my machine accepts a network connection from yours,
and orders you submit are applied to units in my game. The passphrase keeps
strangers out, but it is sent in the clear over an ordinary TCP connection, so
treat it as a door lock and not an envelope. No socket exists at all until one
of us explicitly starts a session, and `pbj.net-stop` closes everything.

The mod is open source under MIT: <https://github.com/auxarc/pb-and-j>
