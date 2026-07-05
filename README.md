# More More More Performance! (T3MP)

A Timberborn performance mod — a **real, algorithmic speedup** of the CPU-bound
simulation, **not** a speed multiplier. It applies
**behavior-exact** optimizations to the heaviest hot paths (they change how fast
the game computes, never what it computes), so high game speed and large
late-game colonies actually keep up. It does not change game speed itself — pair
it with any speed mod; Shift+P toggles an extra render blackout + animation
thinning. Unattended runs produce the exact same colony you would get at normal
speed.

Mod Id / internal codename: `T3MP` (finalized — unchanged across releases).

On the `n10c` test save, measured per full in-game day (day/night averaged) at
ultra speed with all three conditions on the **same game days**:

| condition | ticks/s | vs unmodded |
| --- | --- | --- |
| unmodded (vanilla) | 17.95 | 1.00x |
| always-on optimizations (rendered) | 26.72 | **1.49x** |
| + Shift+P blackout | 31.24 | **1.74x** |

So the reliable everyday number is ~1.5x from the optimizations; Shift+P adds
another ~17% but only when the sim is pushed past its compute ceiling (high
speed / unattended fast-forward), not at normal game speeds. The exact factor
depends on the save, the current colony load, and CPU. (An earlier heavier state
measured ~2x when vanilla bottomed near 14 ticks/s; that is a peak, not the
steady-state average.)

**v1.1** adds a flat tick-dispatch rewrite, an activeInHierarchy mirror, and a
Harmony-boxing fix that roughly halves the GC garbage rate (fewer multi-second
GC freezes). Re-measured per full in-game day on matched game days (n10c,
effective x40.2, warmup day excluded):

| condition | ticks/s | vs unmodded | notes |
| --- | --- | --- | --- |
| unmodded (vanilla) | 19.58 | 1.00x | |
| v1.1 always-on (rendered) | 29.69 | **1.52x** | measured with experimental frame pacing on; ~33.5 in 20 s windows without it |
| v1.1 + Shift+P blackout | 47.18 | **2.41x** | "up to"; depends on CPU and the population speed cap |

(An experimental smooth-frame-pacing mode — ~8 fps rendered high-speed play
instead of ~1 fps — exists behind `EnableSmoothFramePacing` but ships **off**:
its v1 lets character models visually run ahead of the simulation at very high
speed. See `docs/optimization-history.md`.) The full change-by-change record
with per-item measurements and leave-one-out instructions is in
[`docs/optimization-history.md`](docs/optimization-history.md).

**Starting new optimization work?** Read
[`docs/optimization-knowledge-base.md`](docs/optimization-knowledge-base.md)
first — it consolidates the benchmark protocol, all hard rules, the tooling,
and every **negative result** so dead-end investigations are not repeated.

## Install

1. Install the **Harmony** mod (required).
2. Copy this mod folder into your Timberborn mods folder:
   `Documents\Timberborn\Mods\`
3. Enable it in the in-game Mod Manager and restart if prompted.

## Controls

- Optimizations turn on automatically as soon as a save loads — no
  action needed. The mod does not change game speed; use the game's speed
  controls or any speed mod.
- **Shift+P**: toggle turbo rendering — a render blackout + animation thinning
  for a large extra speedup, at any game speed. One frame is drawn every 100
  ticks so you can watch progress. Press Shift+P again to restore rendering.
- A live speed meter is shown bottom-right whenever the mod is active (it keeps
  updating during a Shift+P blackout too). See below for how to read it.

## Reading the bottom-right meter

```
rSPD/iSPD  x20.4 / x20.6
UPS 34.0 ticks/s
```

- **iSPD** (*ideal speed*) — the multiple the game is **trying** to run at right
  now (`Time.timeScale`), i.e. real-time × this. Note it is often **lower than the
  speed you pressed**, because vanilla Timberborn throttles top speed by colony
  size (see below) — e.g. pressing `x50` on a big colony may show `iSPD x20.6`.
- **rSPD** (*real speed*) — the multiple the sim is **actually** achieving right
  now (measured).
- **UPS** — raw simulation updates (ticks) per second. `UPS = rSPD ÷ 0.6`.

How to read it:

| You see | Meaning |
|---|---|
| **rSPD ≈ iSPD** | The sim is keeping up with its target — the CPU has headroom at this speed. |
| **rSPD < iSPD** | The machine can't keep up — CPU-bound; extra game-time is being dropped. |
| **iSPD < the speed you set** | The game's population throttle is capping you (not this mod, not your CPU). |

This mod's job is to push **rSPD** as close to **iSPD** as possible (and raise the
ceiling of both) by making each tick cheaper — without changing the result.

## What it does

Adds behavior-exact optimizations to the hottest simulation paths: the
per-entity tick dispatch, need/behavior candidate selection, a speed-normalized
travel-distance cache invalidated on navmesh changes, and render/UI suppression
during the Shift+P blackout. Nothing that alters the outcome (pathfinding
results, reservations, inventories, water) is skipped or approximated, and the
mod does not touch game speed.

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

## Benchmark harness (not part of the distributed mod)

The A/B measurement is off in the shipped build
(`EnableBenchmarkMeasurement = false`). To measure throughput during
development, set that flag to `true`, rebuild, and run:

```powershell
.\scripts\run_autoload_probe.ps1 -SkipModManager -BenchAutoUltra -SecondsAfterLoad 170 -StopAfter
.\scripts\analyze_simprogress.ps1 -LogPath .\testlogs\autoload-<stamp>.log
```

`-SkipModManager` skips the mod manager OK screen and `-BenchAutoUltra` makes
the mod auto-apply ultra speed after load; both are opt-in test flags only.
