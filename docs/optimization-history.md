# Optimization history — how T3MP got fast

A chronological, honest record of every performance change, what it measured,
and how to re-verify each piece with a leave-one-out (LOO) ablation. All
numbers are from the large late-game test save `n10c` (~660 beavers) unless
noted. "Ceiling" numbers are 20-second-window full-ticks/s at a game speed high
enough to be CPU-bound; "per-day" numbers average a full in-game day (the
fairest figure, used for public claims).

## Timeline

| Stage | Blackout ceiling | vs vanilla | What changed |
|---|---|---|---|
| Vanilla (game 1.1.0.2) | 17.95 (per-day) | 1.00x | — |
| v1.0 optimizations | 31.24 (per-day) | 1.74x | tick dispatch v1 + hot-path caches (see LOO table) |
| **Flat tick dispatch v2** (2026-07-04) | **40.2** | 2.24x | per-bucket flat component arrays + Enabled bitmask |
| Harmony `__args` boxing fix (2026-07-04) | (GC side) | — | heap growth halved: 182–186 → 82–113 MB/20s |
| Smooth frame pacing (2026-07-04) | (visible mode) | — | high-speed rendered play: 0.8 fps → ~8 fps at ~82% of max ticks |
| **activeInHierarchy mirror** (2026-07-04) | **43.9** | **2.45x** | sentinel MonoBehaviour mirrors GameObject activity into dense bits |

Visible-mode (rendered, no Shift+P, pacing off) ceiling moved 26.7 → **33.5**
full-ticks/s with flat dispatch v2.

### Per-day re-measure of the v1.1 build (2026-07-04, the public-claim figures)

Matched in-game days (768 ticks each, days 2–5 from load; day 1 is
warmup-contaminated), n10c, effective x40.2, all default settings:

| condition | per-day ticks/s | vs vanilla |
|---|---|---|
| Vanilla (rendered) | 19.58 | 1.00x |
| v1.1 always-on (rendered, smooth pacing ~8 fps) | 29.69 | **1.52x** |
| v1.1 + Shift+P blackout | 47.18 | **2.41x** |

Note the rendered ratio matches v1.0's ~1.5x even though the sim got ~26%
faster — smooth frame pacing spends the freed CPU on rendering ~8 fps instead
of ~0.8 fps. Players who prefer raw speed over smoothness can set
`EnableSmoothFramePacing = false` (rendered ceiling then ~33.5 ticks/s in
20-second windows).

### v1.0 leave-one-out attribution (measured 2026-07-03, per-day, matched game days)

Share of the always-on speedup (baseline 27.20 vs vanilla 17.95 at the compute
ceiling; shares sum ~106% due to interactions):

| Item | Share | Flag |
|---|---|---|
| Bucketed tick dispatch (v1) | 51.8% | `EnableTickDispatchOptimizer` |
| Haul no-action frame cache | 18.3% | `EnableHaulNoActionFrameCache` |
| Yielder / farm / workplace finders | 10.6% | `EnableFastYielderFinder`, `EnableFarmYielderSegmentTree`, `EnableLumberjackYielderOptimizer`, `EnableGatherWorkplaceOptimizer`, `EnableFarmHouseBehaviorDirectOptimizer`, `EnablePlantingSpotFinderOptimizer` |
| Global need-travel shadow cache | 9.2% | `EnableGlobalNeedTravelShadowCache` (`EnableTravelDistanceCache` is kept for exactness, ~0% speed) |
| Water fast-skip | 6.2% | `EnableWaterObjectServiceFastSkip` |
| Inventory caches | 4.6% | `EnableInventoryStockDistanceCache`, `EnableInventoryNeedGoodOptimizer` |
| Need appraisal | 4.1% | `EnableDistrictNeedAppraisalCache`, `EnableDistrictNeedBehaviorDirectOptimizer` |
| Worker / carry | 2.3% | `EnableWorkerRootMetricsBypass`, `EnableWorkerWorkingSpeedNoRepeatSet`, `EnableCarryAmountCalculatorOptimizer` |
| Empty inventories fast path | 1.3% | `EnableEmptyInventoriesFastPath` |
| District resource counter | 0.3% | `EnableDistrictResourceCounterThrottle` |

Full table and method: `docs/optimization-attribution.md`.

### Flat tick dispatch v2 (post-1.0)

Replaces the per-entity dispatch with, per bucket, one flat
`TickableComponent[]` swept front-to-back plus a `ulong[]` bitmask mirroring
each component's `Enabled` state. The mask stays exact because
`BaseComponent.EnableComponent`/`DisableComponent` are the only writers of
`Enabled` (verified by decompilation) and both are hooked; bucket membership
changes only on entity create/delete (hooked, snapshot rebuilt lazily). A
mid-sweep insertion into the bucket being swept falls back to vanilla
live-list iteration, reproducing vanilla's mid-tick-add semantics exactly.

- Flag: `EnableFlatTickDispatch` (false = v1 per-entity cached path, for LOO).
- Measured A/B at CPU-bound speed: 31.98 → 40.22 full-ticks/s (**+25.8%**).
- Pitfall recorded on the way: the first A/B ran at requested x50, which the
  vanilla `GameSpeedThrottler` (population scaling) turns into effective
  x20.6 = an *ideal* ceiling of 34.33 ticks/s — the optimized build was
  speed-limited, not CPU-limited, and the gain looked like only +10%. Any
  benchmark must check measured ticks/s against `effectiveSpeed / 0.6`.

### activeInHierarchy mirror

The vanilla dispatch reads `GameObject.activeInHierarchy` (a native call) per
entity per tick — ~18M calls / 20 s, measured ~8% of the tick budget once the
rest of the dispatch was flat. A tiny `ActiveInHierarchySentinel`
MonoBehaviour is attached to each tickable entity; Unity fires its
OnEnable/OnDisable synchronously at the exact moment `activeInHierarchy`
changes (including parent-chain changes and pre-destruction deactivation), so
a dense bitmask always holds the value the vanilla check would read at visit
time — no assumptions about game code paths, the engine itself reports every
transition.

- Flag: `EnableActiveInHierarchyMirror`.
- Measured: +6.4% ticks in visible mode, +9% at the blackout ceiling
  (40.2 → 43.9 full-ticks/s).
- Measurement war story: the first sizing test at requested x50 hit the
  effective-speed wall (false negative), and the second had the diagnostic
  const unwired (also a false negative). Only the third, verified-wired run at
  x99 revealed the real ~8% cost. Verify your diagnostics actually engage.

### EventBus compiled delegates

Vanilla `EventBus.RegisterMethod` wraps every `[OnEvent]` handler in
`e => method.Invoke(subscriber, new object[1] { e })` — a reflective call plus
an array allocation on **every** event delivery (EntityInitializedEvent alone
is ~26k posts × ~680 handlers at load). Replaced with a compiled
`Delegate.CreateDelegate` wrapper: same handlers, same registration order,
same validation errors, and handler exceptions still wrapped in
`TargetInvocationException`. Flag: `EnableEventBusFastDelegates`. Measured:
~2–3 s faster save-load (most of PostLoad turned out to be handler bodies, not
dispatch); removes the reflective overhead + allocation from every entity
spawn/delete and state-change event at runtime.

### Harmony `__args` boxing fix

The mod's own travel-cache prefix used `object[] __args`, which makes Harmony
allocate an array and box two `Vector3` on every call (~356k calls / 20 s).
Rewritten to by-name typed parameters. Not an optimization flag (it is a bug
fix); measured by allocation probes: heap growth 182–186 → 82–113 MB per 20 s,
so the periodic full-GC freeze happens roughly half as often. Rule: never use
`object[] __args` in hot-path patches.

### Smooth frame pacing (visible high-speed play)

At high effective speed the vanilla clamp (`Time.maximumDeltaTime = 0.6`) lets
a single rendered frame consume 0.6 × timeScale game-seconds, so the game
renders at ~1 fps while the sim monopolizes the main thread. A Harmony prefix
on `Ticker.Update` caps the game time consumed per frame (the same
drop-the-surplus semantics as the vanilla clamp — tick logic untouched, the
achieved speed remains honestly shown by the rSPD meter). While pacing is
active, Timbermesh animation sampling optionally runs every Nth frame
(movement itself stays per-frame smooth).

- Flags: `EnableSmoothFramePacing`, `SmoothFramePacingMinTimeScale` (5 — never
  engages at normal x1–3 play), `SmoothFramePacingMaxDeltaTime` (0.05),
  `SmoothPacingAnimationFrameStride` (2).
- Measured (visible, effective x40.2): 0.8 fps / 33.4 ticks/s → **8.3 fps /
  27.5 ticks/s**.
- The remaining fps floor (~30 ms/frame) is engine-side rendering
  (draw calls / UI), outside Harmony reach.
- **Ships OFF (v1 flaw, user-reported):** clamping only the *ticker* breaks
  the frame-time == sim-time invariant; per-frame systems (MovementAnimator)
  receive more game time than the sim advanced, so at very high speed some
  character models visually run ahead of their path and "walk in place"
  (carriers were the visible case). A stuck-walker probe confirmed the
  simulation itself stays correct — it is purely visual. The proper v2 is a
  *speed governor*: dynamically lower `Time.timeScale` itself to the
  achievable smooth speed, which keeps every clock consistent by construction
  (needs a control loop with headroom probing to avoid ratcheting down).

## How to run a leave-one-out ablation

1. Set the target flag(s) to `false` in `src/T3MP/BenchmarkSettings.cs`.
2. `scripts\deploy.ps1 -Configuration Release`
3. `scripts\run_autoload_probe.ps1 -SecondsAfterLoad 170 -SkipModManager -BenchAutoUltra -StopAfter`
4. Compare full-ticks/s over equal windows (aggregate 2+) against a same-day
   baseline run. Keep the game version identical; re-anchor the baseline after
   any game update. Make sure the run is CPU-bound (measured ticks/s must stay
   below `effectiveSpeed / 0.6`, see the speed-wall pitfall above).

The production build intentionally contains **no measurement scaffolding**
(probes are added temporarily during investigations and deleted afterwards);
rates for ablation runs come from a temporary windowed counter added for the
session, or from the dev measurement mode (`EnableBenchmarkMeasurement`).
