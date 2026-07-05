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
| **Entity spawn/delete tax** (2026-07-05) | (see below) | — | sentinel template injection (−16% load), registry fast-remove, road-reachability cache: +4.6% on the new n10c save clean A/B |

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

### Smooth mode v2 — Shift+O timeScale governor (2026-07-05, commit 62baa8f)

Replaces pacing v1's flawed ticker clamp with a governor on `Time.timeScale`
itself: while enabled (Shift+O toggle, whole-game switch chosen by the user),
the requested speed is multiplicatively adjusted toward a 30 fps target with a
floor of `max(requested×0.15, min(requested, 3))`. All clocks stay consistent
by construction, so v1's walk-in-place desync cannot occur. Verified at x50:
8 fps → 17–27 fps at a governed ~x7.5. Flags: `EnableSmoothTimeScaleGovernor`,
`GovernorTargetFps`, `-benchSmoothMode` starts it enabled.

### Smooth mode v3 — fps-priority auto-max governor (2026-07-05)

User goal: pin the frame rate at 60 and continuously run the HIGHEST sim speed
that sustains it, always, during rendered play (instead of the v2 "cap a
requested speed down to 30 fps"). Same `Time.timeScale` control law, but the
speed floats in `[FpsPriorityMinSpeed, FpsPriorityMaxSpeed]` (the pressed speed
button is ignored as a ceiling; only a real pause suspends it), and the
controller uncaps the frame rate while the mode is active so free-running fps is
a true CPU signal — a vsync cap would quantize 60↔30 and hide the headroom the
governor climbs into. Flags: `EnableFpsPriorityAutoSpeed` (mode on; Shift+O then
toggles this instead of the v2 cap), `FpsPriorityAutoStartAfterLoad` (engage
after load for always-on play), `FpsPriorityTargetFps` (60), `FpsPriorityMaxSpeed`
(50). Sim stays exactly vanilla for the speed achieved (only moves timeScale).

Verified (rendered, n10c ~660 beavers, overview camera = worst case): the climb
works — at target 45 fps the governor holds ~44–46 fps and lifts speed to
~x1.8–2.0; 0 exceptions. **Key finding:** at target 60 fps this heavy save sits
at x1 because fully-rendered it already renders only ~52 fps at x1 — the gate is
per-frame ANIMATION cost (AnimatorRegistry 5.9, MovementAnimator 2.1 ms/frame,
etc.), not the sim. So on a big rendered colony the achievable "smooth speed" is
animation-bound; zoomed-in play (InvisibleAnimatorPoseSkip trims off-screen
animators) has more headroom. This is the case where render-side optimization
finally pays off (it raises both the fps floor AND the speed the governor can add
at 60 fps) — unlike the x50-blackout case (negative result #17).

### Topology-UI round (2026-07-05, commits 5384d85…567a3a5)

UI-only lag fixes for gear/path placement on huge networks (sim untouched):
diff-based + budgeted mechanical-graph highlight (46.3 → 6.2 ms worst
refresh), rate-limited + amortized path overlay rebuild (worst frame 46 →
22.5 ms), overlay invalidation filtered to affected drawers, frame-batched
`BlockObjectModelController.UpdateModel` (741 → 202 ms, worst 36.8 → 2.25 ms),
preview re-adds capped at 10 Hz while dragging, and a bit-identical
`FillFlowField` fast path (~5%, memory-bound ceiling). Details and remaining
ceilings: `docs/optimization-knowledge-base.md` §4–5.

### Type-sorted dispatch experiment — negative result (2026-07-05, commit e9e3c1e)

ECS-style grouping of component ticks by type within each bucket measured
**~0%** (33.12 vs 32.91 full-ticks/s): dispatch order / icache is not the
bottleneck; the cost is the Tick() bodies' scattered heap data, which a mod
cannot re-layout. Kept behind `-benchTypeSort` (changes tick order — never
ship active). Bucket merging is a no-op by construction.

### Peek-tick suppression fix (2026-07-05, commit 6decc74)

The 1-frame render peek is frame-time-clamp limited, so that single frame
carries up to ~40 ticks — measured ~half of ALL blackout ticks executed inside
peek frames and bypassed every tick-side blackout fast path. New peek-agnostic
gate `BlackoutTickSuppressionActive` keeps the exact fast move and the
tick-driven cosmetic suppressions engaged for those ticks; frame-driven visual
updaters still key off `RenderBlackoutActive` so the peek frame renders fresh.
Clean A/B: 33.99 → **35.08** full-ticks/s (**+3.2%**, new n10c save baseline).
Rule: any future tick-side suppression gates on `BlackoutTickSuppressionActive`.

### Entity spawn/delete tax round (2026-07-05, commit after 6decc74)

Two new attribution probes (`-benchDecide` splits `Behavior.Decide` by type +
per-executor tick timing; `-benchSpawn` splits the spawn/delete tax into fixed
sites, per-event-type `EventBus.PostNow`, and per-`[OnEvent]`-handler bodies)
found that the mod's own `activeInHierarchy` sentinel was the dominant per-spawn
cost, and it was NESTED inside `EntityInitializedEvent` handling (so it inflated
every parent site). Wins, all behavior-exact:

- **Sentinel template injection** (`EnableSentinelTemplateInjection`): add the
  `ActiveInHierarchySentinel` once to each cached template GameObject (inactive,
  no callbacks fire) so `Object.Instantiate` clones carry it natively, instead
  of `AddComponent` per entity. Clones start `SlotIndex = -1` (field not
  Unity-serialized) exactly like a fresh AddComponent. Measured per-spawn:
  `bucket.Add` 2754 → 7.6 µs, `EntityInitializedEvent` 3081 → 622 µs,
  `EntityComponent.Initialize` 3573 → 817 µs; **load time 60.9 → 50.9 s (−16%)**.
- **Registry fast-remove** (`EnableEntityRegistryFastRemove`,
  `EnableComponentRegistryFastRemove`): replace the O(n) `List.Remove` linear
  scans on entity delete with a stamp-ordered binary search + `RemoveAt`
  (`OrderedListFastRemove`) — list identity and ordering preserved exactly.
  `EntityRegistry.RemoveEntity` 435 → ~85 µs/delete (~90% hit the fast path,
  the rest safely fall back to vanilla).
- **Road reachability cache** (`EnableRoadReachabilityCache`): exact result
  cache for the radius-10 road BFS behind wander decides (99.4% hit rate,
  wander destinations pick from the beaver's home road node). Invalidated
  directly on `RoadNavMeshGraph.ConnectNodes`/`DisconnectNodes` (the only graph
  mutators) so it can never serve a stale result — required because wander
  destinations feed the sim RNG stream.

Clean A/B (matched game-day aggregates 2–8, all four flags on vs off, same
build/day): **37.29 → 39.00 full-ticks/s (+4.6%)**, consistent +3.1…5.4% across
every aggregate. Plus the −16% load-time win above.

Negative sub-result: turning the shipped-default `EnableTopologyUiProbe` on vs
off during blackout play was 38.91 vs 39.04 (−0.3%, within noise) — the topology
stopwatch probe does NOT contaminate the sim during normal play (no tool
selected → the road-flow-field methods it patches are cold). The KB "set false
for shipped builds" rule is dev hygiene, not a measurable perf lever. Set false
regardless (it is measurement scaffolding).

## How to run a leave-one-out ablation

Set the target flag(s) to `false` in `src/T3MP/BenchmarkSettings.cs` and run
a clean A/B — the full procedure and validity rules are in
`docs/automation-guide.md` (recipe B) and
`docs/optimization-knowledge-base.md` §2. The production build intentionally
contains **no measurement scaffolding**; ablation rates come from the dev
measurement mode (`EnableBenchmarkMeasurement`, restore to false after).
