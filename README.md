# More More More Performance! (T3MP)

**v1.2 is a rebuild.** All 1.0–1.1.7 runtime optimizations were removed after the
review in `docs/runtime-t3mp-t4mp-review-2026-09-10.md` and the Workshop bug
reports traced back to them. The mod now has two parts:

1. **Save-load optimizations** (`src/T3MP/Loading`, `src/Shared`): about 29 s
   instead of about 65 s scene load for a 28,202-entity save on game 1.1.2.4
   (`docs/load-steam-1124-2026-09-10.md`). Every step checks the reviewed game
   modules and foreign Harmony patches and otherwise stays native.
2. **Runtime patches** (`src/T3MP/Runtime`), following the design of the
   Harmony-free T4MP prototype: behavior-exact plumbing only, no simulation
   state cached across ticks, nothing keyed on frames, nothing rendered less
   often, and a raw-IL fingerprint of each patched vanilla method so a game
   update silently falls back to vanilla.

| runtime patch | target | what changes |
| --- | --- | --- |
| typed event delivery | `EventBus.RegisterMethod` | compiled `Action<T>` instead of `MethodInfo.Invoke` + `object[]` per delivery |
| index tick traversal | `TickableEntity.Tick` | index loop over the component array; an alive entity with no enabled tickable component returns before the native `activeInHierarchy` read |
| sparse bucket traversal (Frontier) | `TickableEntityBucket.TickAll`, `BaseComponent.Enable/DisableComponent`, `ComponentCache.OnDestroy/Initialize` | a per-bucket index of entities that still need a visit (enabled tickable components counted through the only two writers of `Enabled`; destroyed or untracked state = visit); the vanilla `SortedList` stays the authority, the index only supplies the next index to visit |
| water upload de-duplication | `DataTextureArray<T>.UpdateTextureArrays` | a GPU upload whose bytes equal the last bytes sent to that texture layer is skipped (native memcmp, D3D11 only) |

Measured with `scripts/run_runtime_ab.ps1` (speed button x50 = effective x20.6,
150 s per run after load, 20 s windows with the first dropped; results in
`testlogs/runtime-ab-*.json`):

| game | save | arm | ticks/s | vs vanilla |
| --- | --- | --- | ---: | ---: |
| 1.0.13.1 (Steam) | m7b | vanilla, no mod (2 runs) | 17.83 / 16.99 | 1.00 |
| 1.0.13.1 (Steam) | m7b | v1.2 without Frontier (2 runs) | 21.98 / 22.50 | 1.28 |
| 1.0.13.1 (Steam) | m7b | **v1.2 (2 runs)** | 27.66 / 26.57 | **1.56** |
| 1.0.13.1 (snapshot) | m7b | T4MP prototype (DLL rewrite, 2 runs) | 28.33 / 27.15 | 1.59 |
| 1.1.2.0 (snapshot) | n10c | v1.2 without Frontier (2 runs) | 13.89 / 13.55 | |
| 1.1.2.0 (snapshot) | n10c | **v1.2 (2 runs)** | 16.86 / 15.27 | |
| 1.1.2.4 (Steam) | n10c | runtime patches off | 12.64 | 1.00 |
| 1.1.2.4 (Steam) | n10c | v1.2 without Frontier | 13.39 | 1.06 |

On n10c the full v1.2 runtime is about **1.27x** the runtime-off baseline
(16.1 vs 12.64 ticks/s). The Frontier skips about 90% of entity visits on both
saves, but what remains on n10c is heavier per entity, so the gain is smaller.
The gain therefore depends on the save, not the game version. Water upload
de-duplication skips about 90% of uploads but does not change the tick rate.
**No fixed speedup figure is claimed.** The Harmony-free T4MP prototype's extra
3% on m7b is its `UnityEngine.Object.op_Implicit` rewrite, which a Workshop
mod cannot apply.

Mod Id: `T3MP`. Requires the Harmony mod. Game 1.0.13.1 and 1.1.2.x, Windows.

## Install

1. Install the **Harmony** mod (required).
2. Copy this mod folder into `Documents\Timberborn\Mods\`.
3. Enable it in the in-game Mod Manager and restart if prompted.

There are no controls. Optimizations apply on load; `Player.log` lists each one
as installed or retained-vanilla.

---

# How Timberborn's game speed actually works

While building this mod I decompiled and **measured** how the game turns a speed
setting into simulation ticks. Two facts surprised me enough to write them down —
both are stock vanilla behavior, not caused by this mod.

### 1. The game secretly throttles your top speed by colony size

Vanilla Timberborn has a `GameSpeedThrottler` that scales *down* the game speed as
your population grows, so **`x50` is not really `x50` on a big colony**:

```
effective speed = 1 + (requested speed − 1) × scale
scale = 1.0 at ≤30 beavers  →  0.4 at ≥200 beavers   (linear in between)
```

So on a colony of 200+ beavers (`scale = 0.4`):

| you press | you actually get | ticks/s (= effective ÷ 0.6) |
|---|---:|---:|
| x3  | 1.8×  | 3.0 |
| x7  | 3.4×  | 5.7 |
| x30 | 12.6× | 21  |
| x50 | 20.6× | 34  |

That is why the *same* speed button gives ~13 ticks/s on a fresh map but ~5.7 on a
big base — the base is being throttled to ~half speed. (Speed `x1` is never
throttled: `1 + (1−1)×scale = 1`.)

### 2. One tick, one frame, one bucket

- A **full tick** (one world update) = **129 buckets** (beavers/buildings are split
  into 128 groups + 1 for singletons) = **0.6 seconds of in-game time**.
- Each rendered **frame** advances `Time.deltaTime ÷ (0.6/129)` buckets, where
  `Time.deltaTime = min(realFrameSeconds, 0.6) × effectiveSpeed`. One frame can
  advance *many* ticks (measured: 2657 buckets = 20.6 full ticks in a single frame).
- `Time.maximumDeltaTime = 0.6` (the game's value) caps only the **real** frame time
  per frame. It bites solely when a frame takes longer than 0.6 s (**under ~1.7
  fps**); then surplus game-time is dropped and `ticks/s = effectiveSpeed × fps`.
  Above ~1.7 fps you get the full `ticks/s = effectiveSpeed ÷ 0.6`, independent of fps.

**Where this mod comes in:** even at the throttled effective speed, the simulation is
CPU-bound on one thread. This mod makes each tick cheaper to compute, so you get more
ticks/s for the same speed setting — the honest ~1.5× measured above.

---

# Development

## Requirements

- .NET SDK (builds `netstandard2.1`) and the **Harmony** mod at runtime.
- A local **Timberborn** install — the project references the game's managed
  assemblies. The build auto-detects a Steam install in the common locations. If
  yours is elsewhere, set the `TIMBERBORN_DIR` environment variable to the folder
  that contains `Timberborn_Data` (or pass `-p:TimberbornInstall="..."`). The
  lookup lives in `Directory.Build.props`.

## Build & deploy

```powershell
dotnet build .\src\T3MP\T3MP.csproj -c Release
.\scripts\deploy.ps1
```

`.\scripts\backup_mods.ps1` snapshots the mods folder first if you want a backup.
On load the mod logs `[T3MP] Loaded.` to `Player.log`.

## Measuring throughput (not part of the distributed mod)

The test driver mod (`src/T3MPTestDriver`, deployed only during runs) counts
full ticks and logs `[T3MPTEST] Simulation rate` every 20 s. Stage a copy of a
save into a disposable settlement, then:

```powershell
.\scripts\run_runtime_ab.ps1 -Settlement t3mp-ab -Save n10c -Order ABWTE -SecondsAfterLoad 150
```

`A` = runtime patches off, `B` = all on, `W`/`T`/`E` = water/tick/events only.
Results go to `testlogs/runtime-ab-<stamp>.json`. Never run the real settlement:
autosaves at x50 would land in it.
