# Pinned game build

All development targets this exact build. If the game updates, re-vendor
`PhantomBrigade_Data/Managed/` into `vendor/Managed/`, update this file, and
re-verify every Harmony patch target before trusting anything.

| Field | Value |
|---|---|
| Steam app | 553540 (Phantom Brigade, public branch) |
| Steam buildid | 24027838 |
| Game version | 2.2.2 (internal build b8339) |
| buildinfo.yaml | `2026-07-02-1334-2.2.2-b8339_Steam-e665290ed504610a72847bfebc11d9eb96413a0b` |
| Assembly-CSharp.dll SHA256 | `834f875d892f8a5e7a4c284ecfbe6f11a19797d386ac26bcdc45af12076854ea` |

The build script asserts the SHA256 of the *installed* game's Assembly-CSharp.dll
against this value and refuses to deploy on mismatch.

**Manual step (Steam client):** Phantom Brigade → Properties → Updates →
"Only update this game when I launch it", so a patch never lands mid-session.
