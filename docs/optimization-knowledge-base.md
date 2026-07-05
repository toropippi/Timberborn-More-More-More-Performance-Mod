# T3MP optimization knowledge base — READ THIS FIRST

Audience: any agent (or human) starting performance work on this mod. This file
consolidates everything learned across the 2026-06 → 2026-07 optimization
campaign, **including the negative results**, so nobody re-runs a dead-end
investigation. Companion docs:

- `docs/automation-guide.md` — hands-free game launch / speed control / verification recipes.
- `docs/optimization-history.md` — chronological record of every *shipped* win + LOO ablation how-to.
- `docs/optimization-attribution.md` — v1.0 leave-one-out share table.
- `docs/dev-findings.md` — externally-shareable engine findings (GC, alloc paths).
- `git log` — every round has a descriptive commit message; flag comments in
  `src/T3MP/BenchmarkSettings.cs` are the per-experiment source of truth.

Last full update: **2026-07-05** (after the peek-tick suppression fix, commit 6decc74).

---

## 1. Hard rules (user-set, non-negotiable)

1. **Behavior-exact or don't ship.** Shipped features must produce sim results
   identical to vanilla: never skip/approximate/throttle gameplay ticks,
   behaviors, reservations, inventory, water sim, or pathing state.
   Visual/render/UI/audio updates MAY be discarded or deferred (the safe
   category). The one deliberate exception is `EnableGameSpeedThrottlerRemoval`
   (user-requested, disclosed in the manifest/README).
2. **NEVER `git push`.** Local commits are fine and encouraged.
3. **Never edit text files via PowerShell string ops** (`Set-Content`,
   `.Replace()`, `WriteAllText`): it mojibake'd UTF-8 Japanese twice. Use
   dedicated file-edit tooling only.
4. Preserve `mod/workshop_data.json` (Workshop identity).
5. Never kill a Timberborn process the user started; instances launched by the
   automation may be killed.
6. Before any store build: `EnableTopologyUiProbe=false`,
   `EnableBenchmarkMeasurement=false`, `EnableHotOptimizerMetrics=false`.
   `-benchTypeSort` must never ship *active* (arg-gated dormant is fine — it
   changes intra-bucket tick order, i.e. sim results).
7. Public texts (store, README, reddit): player language only, no unconfirmed
   or experimental claims, numbers must be reproduced before publication.

## 2. Benchmark rules (violating any of these produced false results at least once)

Operational how-to (commands, launch args, speed control, recipes) lives in
`docs/automation-guide.md` — this section is only the *rules* that make a
comparison valid:

- **Speed wall**: ideal ticks/s = effectiveSpeed / 0.6, and vanilla's
  `GameSpeedThrottler` population-scales requested speed (x50 → effective
  ~x20.6 at ~660 beavers unless throttler removal is on). If measured ticks/s
  ≈ the ideal ceiling, the run was speed-limited, not CPU-limited — raise the
  requested speed. This masked flat dispatch v2's real +25.8% as "+10%".
  x7 is NOT CPU-bound on n10c; use x50+.
- **Game version anchors**: sim cost changed 1.1.0.1→1.1.0.2; never compare
  across game updates. Check `Starting game version` in the log.
- **`n10c.timber` was overwritten 2026-07-05** (rearranged for the topology-UI
  scenario). All ticks/s anchors from before that date (21.09 baseline, v1.1
  per-day 19.58/29.69/47.18) are NOT comparable to runs on the new save.
- Aggregate 1 is warmup — always compare aggregates 2+ over equal-length runs
  (sim cost drifts upward within a run as the colony evolves).
- **Probe contamination**: builds with hotspot/type profilers or
  `EnableHotOptimizerMetrics` are attribution-only; per-call stopwatches on
  multi-million-call paths cost up to ~35% tick rate. Never quote absolute
  ticks/s from them; A/B numbers come from clean builds with only
  `EnableBenchmarkMeasurement=true`.
- Verify a diagnostic/ablation actually engages (log line, count) before
  trusting a null result — an unwired const produced a false negative once.

## 3. Current state (2026-07-05, new n10c save, blackout ceiling)

Clean A/B after the entity spawn/delete tax round (matched game-day aggregates,
same build/day): **39.00 fullTicks/s** (spawn/wander opts ON) vs 37.29 (OFF),
**+4.6%**, plus **load time 60.9 → 50.9 s (−16%)**. Earlier peek-fix A/B on this
save was 35.08 vs 33.99. Lineage on the OLD save (not comparable, shape only):
vanilla 17.95 → v1.0 31.24 → flat dispatch v2 40.2 → active mirror 43.9.

Decide split on the new save (`-benchDecide`, 20 s window; DecideRoot ~5.0 s +
DecideExec ~3.4 s ≈ 42% of wall): WorkerRoot 1.7 s (56k calls, 30 µs, 26%
release), Wander 1.08 s (17.5k, 61 µs — now road-cached), NeederRoot 0.77 s
(66% release), CarryRoot 0.65 s (94% release), CriticalNeeder 0.33 s.
Executors: PlantExecutor 1.5–3.4 s (spawn tax, now cut), RemoveYieldExecutor
0.8 s (delete tax, now cut), WalkInside 0.38 s, Produce 0.24 s.
Spawn split (`-benchSpawn`): after the sentinel fix, per-spawn
`EntityInitializedEvent` = 622 µs (was 3081), ~925 handlers, top handler is
`TickableEntityLifecycleManager.OnEntityInitialized` ~30 µs (was 998 — it
CONTAINED the nested sentinel AddComponent). Remaining spawn cost is inherent
Unity instantiate + the event fanout body.

Fresh per-type hotspot table (`-benchHotspot`, 120 s blackout, share of ~96 s
component-tick time):

| Type | Share | Note |
|---|---|---|
| BehaviorManager | **51.7%** (13.9 µs/call) | decide + executor; cost currency = pathfinding / spot search |
| NeedManager | 7.0% | per-beaver per-tick need updates (scattered heap) |
| WalkerMover | 6.3% | post-peek-fix (was 11.8%); mostly the exact fast move now |
| RangedEffectSubject | 6.3% | |
| ContaminationApplier | 5.1% | |
| LifeProgressor | 3.2% | |
| DistrictResourceCounter | 2.4% | **301 µs/call — anomalous unit cost, open lead** |
| CriticalNeedActionStatusRegistrar | 2.1% | |
| Worker / Walker / Mortal / WalkerSpeedManager | ~1-2% each | |

Older BehaviorManager split (2026-07-04, per 60 s): Decide 13–16 s (WorkerRoot
7–9 s @66 µs × 136k, NeederRoot 1.7–2.5 s, CarryRoot 1.5 s = A* Launch),
executor ticks 3.1 s (PlantExecutor 1.6 s = entity-spawn tax via
EntityInitializedEvent ~680-subscriber fanout). Workplace Decides are small
individually (WaitInsideIdly 0.7 s, Planter 0.55 s @512 µs/call, Labor 0.53 s).

## 4. NEGATIVE RESULTS — do not re-investigate without new evidence

| # | Idea | Date | Result | Why / evidence | Revisit only if |
|---|---|---|---|---|---|
| 1 | ECS-style type-sorted bucket sweep (group component ticks by type for icache/branch locality) | 07-05 | **~0%** (33.12 vs 32.91, noise) | Dispatch order is not the bottleneck; tick cost is the Tick() bodies' scattered heap data, which a mod cannot re-layout (true SoA needs the game to own the data). Kept behind `-benchTypeSort` (commit e9e3c1e), never ship active. | the game itself moves data to SoA |
| 2 | Merging N buckets per frame | 07-05 | no-op by construction | Buckets are slices of the same work; boundaries cost ~nothing (measured floor). Same total work either way. | — |
| 3 | Further dispatch micro-opts after flat v2 + mirror | 07-04 | floor reached (~150 ns/entityTick residual) | Enabled-only array compaction: bit tests are free. Zero-component entity skip: no such entities exist (Count>0 guard at registration). activeInHierarchy: already mirrored (+8%); further hooking free. | — |
| 4 | Branch-and-bound pruning of need/behavior candidate evaluation | 07-02/03 | **permanently OFF** after 3 runtime-guard trips | No geometric lower bound exists: navmesh edge spec costs are arbitrary (0.25 tubes, 0.0 entrance links). Also the travel cache made candidate eval sub-µs, so pruning is pointless — the cache IS the scaling asset. | — |
| 5 | FillFlowField (road Dijkstra) micro-optimization | 07-05 | shipped, but only **~5%** | The fill is memory-bound on adjacency + binary heap, near its ceiling; dictionary overhead was not the cost. NOTE: `AccessFlowField`'s Dictionary insertion-order enumeration is load-bearing (AssignDistrictToRoadMap, spill-field tie-breaking) — do not replace with unordered structures. | algorithmic change (incremental Dijkstra), not micro-opt |
| 6 | TravelDistanceCache as a speed win | 07-03 | ~0% speed | Kept only for behavior-exactness; the *shadow* cache is the speed win (9.2% LOO share). | — |
| 7 | Fixing the periodic ~2 s freeze mod-side | 07-03 | **not fixable** | It is a full blocking Mono GC (measured 1.1–1.9 s); pause is mark-phase/live-set dominated (freeing ~nothing still took 1.15 s); incremental GC is disabled in the shipped player build and cannot be enabled by a mod. Only lever: reduce allocation *rate* (frequency, not length). | game enables incremental GC |
| 8 | Load-time (63–66 s) reduction | 07-02/04 | no safe exact win found | 13.6 s Unity InstantiateEntities + ~17 s per-entity init + 13.4 s EventBus.PostLoad where the cost is handler BODIES (compiled-delegate dispatch won only ~2–3 s). RangedEffectService.SetApplier (3.8 s) is tractable but risky (ordering semantics). | accepting behavior risk |
| 9 | Remaining allocation leftovers | 07-04 | assessed low-value / inherent | PlantExecutor ~40 MB/20 s is mostly Unity Instantiate (sapling spawn); WorkerRoot residual ~35 MB + Labor ~18 MB are diffuse; SetRunningBehavior ~8.5 MB is a log string SAVED into the save file — cannot skip. Big concentrated allocators (SortedSet churn, LINQ AppraiseGood, `__args` boxing) are already fixed (heap growth −45%). | new attribution shows a new concentrated source |
| 10 | Shift+P blackout as a general speedup | 07-03 | ~0% below the compute ceiling | Below the ceiling the sim is speed-limited with idle CPU; blackout only raises throughput when pushed past the ceiling (+10–18% at x80+). Public texts already corrected. | — |
| 11 | Smooth frame pacing v1 (clamp only Ticker.Update) | 07-04 | ships OFF — visual desync | Clamping only the ticker breaks frame-time==sim-time; models run ahead and "walk in place" (user-repro'd, sim state verified correct). Superseded by v2: Shift+O `SmoothTimeScaleGovernor` (governs Time.timeScale itself — all clocks consistent by construction). | — |
| 12 | InvisibleAnimatorPoseSkip upside in benchmarks | 07-04 | no measurable gain in worst case (29.0 vs 28.6 fps, overhead camera) | Benefit only expected zoomed-in (most animators off-screen); zero overhead cost, so it ships ON. User eyeball pending. | — |
| 13 | Gameplay-tick throttles (need ticks, decide skipping, etc.) | 06→07 | rejected as a class | Violate rule #1 (behavior-exact). Several default-false flags in `BenchmarkSettings.cs` are the residue of tried/rejected/inconclusive experiments — e.g. `EnableNeedManagerFastTick`, `EnableWaitInsideIdlyOptimizer`, `EnableBehaviorManagerProcessOptimizer`, `EnableFillInputWorkplaceOptimizer`, `EnableWorkplaceNoActionFrameCache`, `EnableHaulCandidateOrderCache` (~0 gain), `EnableWalkerDistanceCache`, `EnablePickBestTravelCache`, `EnableInventoryCapacityDistanceCache`, `EnableGoodCarrierLiftingCapacityFrameCache`, `EnableAnimatedPathFollowerHorizontalOptimizer`, `EnableFastYielderNoCapacityPrecheck`. **Check the flag comment and git history for the specific story before re-attempting any of them.** | a no-action frame cache with a proven invariant (the shipped Haul one = 18.3% LOO share is the pattern to copy) |
| 14 | UI Toolkit version of the ticks/s meter | 07-02 | off — attached to a UIDocument that wasn't the on-screen HUD | IMGUI meter is the reliable default. | — |
| 15 | Topology-UI leftovers | 07-05 | remaining known ceilings | Overlay mesh upload ~14–17 ms/frame (engine); highlight first paint budgeted 400 ops/frame; sim-side road flow-field refill storms (~465 ms/5 s peak at x50) = walker cache invalidation by construction — NOT throttleable (sim correctness), only fixable by a cheaper fill or fewer navmesh invalidations. | — |
| 16 | RecalculateNavMeshObject bursts during play | 07-05 | load-time only | Confirmed by probe: not a gameplay cost. | — |
| 17 | Render-side CPU optimization for the x50-rendered stutter | 07-05 | **futile for x50** | FrameCpu profile (rendered x50, MainLoopProfiler): a frame is ~1560 ms at ~0.6 fps, of which **ticker/SIM = ~1550 ms (99%)** and ALL render + Update + LateUpdate = ~20 ms (1.3%: AnimatorRegistry 5.9, TubeVisitorUpdater 2.7, MovementAnimator 2.1, MechGraphModelUpdater 1.9 spiky, SoundListener 1.0 ms/frame — the top two already throttled). At x50 the frame is sim-bound by construction (`maximumDeltaTime 0.6 × 50` = ~50 ticks/frame), so the ONLY levers for smooth high-speed rendered play are (a) a faster SIM or (b) the Shift+O timeScale governor (trades speed for fps). Render micro-opts would matter only at x1–3 (where the same ~20 ms is the whole frame) — not the user's complaint. | user wants smoother NORMAL-speed play (then AnimatorRegistry/TubeVisitor/MovementAnimator are the targets) |

## 5. Measured system facts (save re-derivation time)

- **Tick scheduling**: 1 full tick = 129 buckets = 0.6 game-sec.
  `Time.maximumDeltaTime = 0.6` clamps REAL frame time, then ×timeScale; the
  setter silently refuses values below fixedDeltaTime (0.6). fps ≥ 1.67 →
  ticks/s = timeScale/0.6; fps < 1.67 → ticks/s = timeScale×fps.
- **Peek-tick rule (2026-07-05)**: because of that clamp, the 1-frame render
  peek carries up to ~40 ticks; ~half of all blackout ticks executed inside
  peek frames. Any tick-side suppression must gate on
  `BenchmarkModeController.BlackoutTickSuppressionActive` (peek-agnostic), NOT
  `RenderBlackoutActive` (frame-side only). Getting this wrong cost 3.2%.
- **GameSpeedThrottler**: vanilla scales requested speed by population
  (30→200 beavers ⇒ scale 1.0→0.4): effective = 1+(req−1)×scale. The mod
  removes it by default (disclosed behavior change; Harmony prefix on
  `SpeedManager.ChangeSpeedScale`, param name `speedScale`).
- **Frame budget** (n10c, 2026-07-04): paused = exactly 60 fps (vsync);
  engine render itself is light (~5–7 ms); fps drops at x3+ come from VSYNC
  QUANTIZATION (work crossing 16.7 ms snaps to 33/50 ms slots) plus GC spikes.
  Anim sampling ~4.5 ms/frame unpaused (AnimatorRegistry samples even paused:
  2.0 ms); script Updates ~3.3 ms.
- **Animation architecture (decompiled 2026-07-05, why anim can rival the sim
  per frame)**: beavers use Timbermesh VERTEX animation (skinning on the GPU
  via a shader that reads `_Offsets`/`_Rotations` textures + a per-instance
  `_AnimationTime` float). `AnimatorRegistry.UpdateSingleton` loops EVERY
  registered `TimbermeshAnimator` each frame; per animator it calls
  `TimbermeshAnimator.UpdateAnimation` → per animated mesh node a
  `VertexAnimationUpdater.UpdateAnimation` that does
  `_meshRenderer.material.SetFloat(_AnimationTime, t)`. So the CPU cost is
  ~`animatorCount × (Renderer.material getter + Material.SetFloat)` of Mono→
  native interop every frame (a model has one updater PER animated node, so
  the updater count exceeds the beaver count). `MovementAnimator.Update`
  (per character/frame, IUpdatableComponent) adds two SEPARATE native transform
  writes (`CharacterModel.Position` then `.Rotation`, each `_model.position=/
  rotation=`). Node-animated props (gears, drills) instead write a transform
  per bone (`NodeAnimationUpdater`, already uses `SetLocalPositionAndRotation`).
  `AnimationUpdatedEventArgs` is a `readonly struct` (no alloc — that GC lead is
  dead). **Root cause of "anim costs more than sim": per-object native Unity
  calls at scale, not one hot loop — and Timberborn uses `Renderer.material`
  (a unique instanced material per beaver) instead of a `MaterialPropertyBlock`,
  which DEFEATS GPU instancing → the render thread also issues ~one draw call
  per beaver (CPU-bound draw submission, matches "not GPU-bound").** Attribution
  probe `-benchAnim` (AnimSplitProbe) splits vertex-material-set vs
  node-transform-write vs loop overhead.
- **`-benchAnim` MEASURED (2026-07-05, rendered x3, overview camera = all
  visible = worst case), per frame:** AnimatorRegistry loop total **8.3 ms**;
  of that **node (bone) transform writes = 3.3 ms across ~10,465 writes/frame**
  (~656 animators × ~16 bones, 0.31 µs each), vanilla loop + visibility-check
  overhead ~2.5–3 ms, **vertex material-set only 0.9 ms** (656 calls, 1.38 µs),
  plus ~1 ms probe overhead. **This OVERTURNED the `.material`/vertex hypothesis:
  the dominant animation CPU cost is SKELETAL BONE TRANSFORM WRITES, not the
  vertex material set.** Bone writes are inherent Unity CPU skeletal animation
  (per-`Transform.SetLocalPositionAndRotation` native, no batch API without a
  Jobs/ECS/`TransformAccessArray` rewrite the game would have to own) — there is
  NO single implementation bug a mod can exploit for a big win. Only reductions:
  (a) visibility culling — the shipped `InvisibleAnimatorPoseSkip` already skips
  whole off-screen animators (the overview benchmark camera defeats it; real
  zoomed play benefits a lot), (b) accepting a quality tradeoff (animation
  thinning — user REFUSED, 2026-07-05). The `.material`-instead-of-MPB pattern
  still affects DRAW-CALL submission (render thread, separate from this 8.3 ms
  sampling and NOT yet measured); converting to `MaterialPropertyBlock` +
  instancing is the only remaining "render" lever but is a risky rendering
  change needing the vertex-anim shader to declare instancing support (can't be
  verified from managed DLLs — asset-bundle shader). Safe micro-wins (material
  ref cache 0.9 ms path; combine `MovementAnimator`'s two transform writes) are
  sub-millisecond — not worth the Harmony overhead they add.
- **EventBus**: ~680 subscribers; every entity spawn pays an
  EntityInitializedEvent fanout (~2 ms per PlantExecutor sapling spawn even
  with compiled delegates — the cost is the handler bodies).
- **Decide cost currency is pathfinding / spot search**, not bookkeeping.
- **MechanicalGraph IS the maintained connected component** — never DFS a
  finished network (use `root.Graph.Nodes`); DFS is only needed for preview/
  unfinished topology.
- Preview pipeline: `PreviewPlacer.ShowPreviews` (every frame while a tool is
  up) → preview navmesh → `PreviewDistrictMap` invalidation → full district
  Dijkstra (`DistrictRoadFlowFieldGenerator` has NO distance cap for roads) →
  `DistrictPathNavRangeDrawer` overlay rebuild. `BuildingModelUpdater` fires
  per changed coordinate AND per tile of multi-tile objects (water churn at
  x50 → 276k UpdateModel calls/366 ms per 5 s before batching).
- `Walker.Stopped()` is `_currentDestination == null` (cheap);
  `WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition` reads
  `Transform.position` natively + water map per call — it is called at path
  CORNERS, so its inputs are corner positions in both vanilla and the fast
  path (exactness anchor for any future rewrite).

## 6. Open leads (ranked, as of 2026-07-05, after the spawn/delete round)

1. **BehaviorManager Decide ≈ 42% of wall** — WorkerRoot 1.7 s is the biggest
   single root (26% release rate). The only proven pattern is a no-action frame
   cache with an airtight invariant (the shipped Haul one = 18.3% LOO). Wander
   (1.08 s) is now partly road-cached; the terrain BFS in the same picker is
   NOT cached (start node varies per pick → low hit rate, not worth it).
2. Per-beaver maintenance pool (NeedManager 7%, RangedEffectSubject 6.3%,
   ContaminationApplier 5.1%, LifeProgressor 3.2%) ≈ 21% combined — scattered-
   heap reads, hardest class. Decompiled 2026-07-05: each Tick body is cheap
   and mostly early-outs (Contamination: dry-land beavers no-op after a
   position read + water-map lookup; RangedEffect: outdoor beavers get an empty
   effect list; LifeProgressor: one dict-keyed bonus lookup + float add). The
   cost is the sheer count (×660 beavers × ~35 ticks/s), not any one body, so
   there is no concentrated target — only a lazy-accumulator or
   disable-when-idle redesign per component, each with its own exactness proof.
   Low value per unit of risk; deprioritized.
3. EventBus fanout body: `EntityInitializedEvent` is 622 µs/post over ~925
   handlers (0.67 µs each, mostly compiled-delegate dispatch + handler
   early-outs). Reducing the fanout needs per-handler relevance filtering =
   risky. Inherent for now.
4. Fast-move transform hoisting (WalkerMover 6.3%): keep `transform.position`
   in a local through the corner loop, write at corner boundaries (before each
   speed-provider call) and at loop exit. Bit-exactness requires the provider
   to keep reading the corner position it reads today; see §5 last bullet.
5. Visible-fps side: animation stride at normal speeds (needs user approval —
   visual tradeoff), engine render floor is out of reach.

**Closed this round:** DistrictResourceCounter 301 µs/call (was lead #2) —
assessed as a full O(inventories) recount per tick; an incremental/event-driven
rewrite touches a large exact surface (stock/capacity/carrier/processor events)
feeding UI + possibly behavior reads, too risky for 2.4%. Existing interval=2
throttle is the pragmatic lever; not extending.

## 7. Tooling

- **Decompiler**: `~\.dotnet\tools\ilspycmd.exe` (not on PATH), e.g.
  `ilspycmd "<Managed>\Timberborn.WalkingSystem.dll" -t Timberborn.WalkingSystem.WalkerMover`.
- **Publicizer**: Krafs.Publicizer in the csproj (Navigation,
  BuildingsNavigation, BaseComponentSystem). MUST set
  `IncludeCompilerGeneratedMembers="false"` or event backing fields collide
  (CS0229). Unity Mono does not enforce member visibility at runtime.
- **Benchmark harnesses, launch args, dev measurement flags, and automation
  pitfalls**: see `docs/automation-guide.md` (single source of truth for
  operations). Reminder only: `EnableBenchmarkMeasurement` /
  `EnableHotOptimizerMetrics` / `EnableTopologyUiProbe` must all be false in
  store builds, and `-benchTypeSort` must never ship active.

## 8. Harmony / measurement pitfalls (each cost real time at least once)

1. Never use `object[] __args` in hot-path patches (array + boxing per call);
   bind parameters by NAME with exact vanilla names — verify names from
   decompile and grep the log for `Failed to patch` after installing.
2. Two patches with different `__state` types on one method fail — use a
   ThreadStatic state stack instead.
3. A prefix that returns false suppresses LATER prefixes too — patch-order
   matters when two optimizers share a target method.
4. `IUpdatableComponent`/singleton implementers must be found via
   `GetInterfaceMap` (base-class walking misses them).
5. Stack samplers must skip Harmony DMD wrapper frames
   (`DynamicMethodDefinition`/`_Patch` names) or they attribute everything to
   the wrapper.
6. Tag strings built per-call in a hot postfix self-pollute the alloc numbers
   (~37 MB/20 s) — key metrics dictionaries by `MethodBase`.
7. `GC.GetAllocatedBytesForCurrentThread()` returns 0 on this Mono; alloc
   attribution = `GC.GetTotalMemory(false)` deltas (~4 KB quantized, valid in
   aggregate only).
8. PowerShell `.Replace()` on CRLF files silently no-ops with LF needles —
   verify edits landed (and see hard rule #3: don't edit text with PowerShell
   at all).
