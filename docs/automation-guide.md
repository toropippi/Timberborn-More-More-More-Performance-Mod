# Automated verification guide — how an agent runs the game hands-free

Step-by-step recipes for launching Timberborn, driving speed/modes, and
judging results with **zero human input**. Read
`docs/optimization-knowledge-base.md` first (rules + benchmark protocol);
this file is the operational how-to.

## 0. Safety preconditions (every run)

1. **Never kill a Timberborn instance the user started.** The probe script
   throws if Timberborn is already running — do NOT "fix" that by killing the
   process unless *you* launched it earlier in the same session.
2. Instances you launched may be stopped:
   `Get-Process *Timberborn* | Stop-Process -Force` (or pass `-StopAfter`).
3. Run `deploy.ps1` before every measurement — the game loads the *deployed*
   DLL from `Documents\Timberborn\Mods\T3MP`, not your build output.

## 1. The three commands

```powershell
# 1) Build + deploy (also preserves workshop_data.json)
scripts\deploy.ps1                       # -Configuration Release is the default

# 2) Launch, autoload save, run N seconds, kill the game, copy the log
scripts\run_autoload_probe.ps1 -SkipModManager -BenchAutoUltra `
    -SecondsAfterLoad 150 -StopAfter

# 3) Analyze the copied log (path is printed as "copiedLog:")
scripts\analyze_simprogress.ps1 -LogPath testlogs\autoload-<stamp>.log -Mode Optimized
```

One cycle ≈ 4.5 min (~65 s load + measurement + shutdown). Run step 2 in the
background and wait for completion; do not poll with `pgrep` (does not exist
in Git Bash here) — use `Get-Process` or just wait for the script to exit.

## 2. How launch + autoload works

`run_autoload_probe.ps1`:
- Throws if Timberborn is already running (safety guard).
- Rotates the previous `Player.log` to `testlogs\autoload-previous-<stamp>.log`.
- Starts `Timberborn.exe` with
  `-settlementName "n10c" -saveName "n10c"` (override with `-SettlementName` /
  `-SaveName`) plus whatever bench args you request — the game then loads the
  save with no menu interaction (`-skipModManager` skips the mod-manager OK
  screen; mods still load).
- Watches `Player.log` for `Load time:` (load done), then waits
  `-SecondsAfterLoad`, then (with `-StopAfter`) force-kills the game and
  copies the log to `testlogs\autoload-<stamp>.log`.
- Prints a summary: `sawException` (must be False), `loadTime`, `copiedLog`.

Key pitfall: the game loads with `Time.timeScale = 0`; the mod handles
unpausing/speed itself in bench modes — never rely on scaled clocks.

## 3. Controlling speed and modes

There is no runtime remote-control channel; speed/mode selection happens via
**launch args** (read by the mod) and **compile-time constants**:

| What you want | How |
|---|---|
| Blackout ultra benchmark (the standard) | `-BenchAutoUltra` — the mod auto-applies Optimized + render blackout + speed `BenchmarkSettings.OptimizedUltraSpeed` (99 ⇒ effective ~x40 after the population throttle) as soon as the save loads |
| A different benchmark speed | edit `OptimizedUltraSpeed` in `src/T3MP/BenchmarkSettings.cs`, rebuild+deploy (check the speed wall: measured ticks/s must stay below effectiveSpeed/0.6) |
| Rendered (visible) high-speed run | `-BenchTopoUi` forces x50 rendered with the gear/path selection scenario; or `-BenchAutoUltra` with `EnableOptimizedRenderBlackout=false` (rebuild) |
| Smooth mode (Shift+O governor) pre-enabled | `-BenchSmoothMode` |
| Per-type tick attribution | `-BenchHotspot` (see recipe C) |
| A/B a feature flag | flags are compile-time: build+run control (flag false), build+run experiment (flag true) — see recipe B |
| Send a raw keypress after load | `-ForceOptimizedAfterLoad` (Ctrl+Shift+O dev force), `-PressUltraAfterLoad` (legacy key 4 — the shipped build no longer binds it; avoid) |

In-game hotkeys (for manual/user verification, not automation): 1/2/3 speed,
Shift+P blackout toggle, Shift+O smooth mode.

## 4. Reading the results

- **Throughput**: `analyze_simprogress.ps1` prints per-aggregate
  `FullTicksPerSecond` and a weighted summary. Needs
  `EnableBenchmarkMeasurement=true` in the build (TEMP — restore to false
  after; without it there are no SimProgress rows). Use aggregates 2+ only,
  equal-length runs, and discard anomalously short aggregates.
- **Correctness gate for every run**: probe summary `sawException: False`,
  plus grep the copied log for `Failed to patch` (a silent patch failure
  means your feature never ran) and `[T3MP]` warnings (`fallback` lines mean
  an optimizer bailed to vanilla).
- **Hotspots**: `analyze_hotspot.ps1 -LogPath <log>` → per-type table
  (needs the run launched with `-BenchHotspot`).
- **Topology-UI timings**: `analyze_topoui.ps1 -LogPath <log>` (needs
  `-BenchTopoUi` and `EnableTopologyUiProbe=true`).
- Optimizer hit-rates/latencies: set `EnableHotOptimizerMetrics=true` (TEMP),
  grep the log for `aggregate=` lines (e.g.
  `PathFollowerNoAnimationFastMove aggregate=... handledRate=... avgUs=...`).

## 5. Recipes

**A. Standard throughput measurement**
1. Set `EnableBenchmarkMeasurement=true` (mark with a TEMP comment).
2. `deploy.ps1` → probe with `-SkipModManager -BenchAutoUltra
   -SecondsAfterLoad 150 -StopAfter` → `analyze_simprogress.ps1`.
3. Restore `EnableBenchmarkMeasurement=false`, rebuild, redeploy.

**B. Clean A/B of a change**
1. Guard the change behind a `BenchmarkSettings` flag.
2. Run recipe A twice: flag false (control), flag true (experiment) — same
   save, same `-SecondsAfterLoad`, same day. Compare weighted fullTicks/s
   over matching aggregates; the noise floor is roughly ±1 ticks/s (~±3%),
   so single-run differences inside that band are "no effect".
3. Both runs must be probe-clean (no `-BenchHotspot`, no
   `EnableHotOptimizerMetrics`) — profiling overhead invalidates A/B numbers.

**C. Hotspot attribution (before choosing a target)**
Run the probe with `-BenchAutoUltra -BenchHotspot -SecondsAfterLoad 150
-StopAfter`, then `analyze_hotspot.ps1`. Shares within the run are valid;
absolute ticks/s are NOT (stopwatch overhead).

**D. Verifying "no behavior change" cheaply**
Every run already asserts: 0 exceptions, 0 patch failures, 0 optimizer
fallback warnings, and (for -BenchAutoUltra) that the sim reaches the same
tick counts. For stronger evidence on risky changes, add a temporary
state-hash or detector probe (pattern: TempStuckCarrierProbe in git history)
and delete it after — do not ship detectors.

**E. Iterating**
Logs accumulate in `testlogs/` (git-ignored). Name your comparisons by the
`<stamp>` and record control/experiment pairs in the commit message, as done
throughout `git log`.

## 6. Known automation pitfalls

- `pgrep` does not exist in Git Bash on this machine — monitor scripts died
  silently on it once. Use PowerShell `Get-Process`.
- The probe's `sawException` regex is narrow; also grep the full log for
  `Exception` when a change touches new territory.
- A single visible/peek frame can carry ~40 ticks (frame-time clamp) — frame
  counts and tick counts are NOT proportional; never infer ticks from frames.
- Aggregate 1 is warmup; short trailing aggregates (< ~15 s) are
  day/night-phase contaminated — exclude both.
- If the run shows fullTicks=0: the mod's auto-force never engaged (check
  the launch args reached the game — `args:` line in the probe output — and
  that the deployed DLL is current).
