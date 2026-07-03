# Timberborn performance findings (from building "More More More Performance!")

Shared in the spirit of "here is what we measured," not "please merge these
patches." Everything below is reproducible; method names come from decompiled
1.1.0.2 assemblies (Unity 6000.4.6f1), measured on a large late-game save
("n10c", ~3 GB managed heap). We believe our own changes are behavior-exact but
would welcome scrutiny — the findings here are independent of the mod.

## TL;DR for the engine team

1. **Incremental GC is off in the shipped build, and that is the biggest
   single cause of the periodic multi-second stutter on large colonies.**
   Enabling it is a one-setting change with essentially no gameplay risk.
2. The GC pause length is dominated by the **live-set mark phase**, so it scales
   with colony size, not with allocation rate.
3. If you also want fewer GCs, the concentrated per-tick garbage is in the
   **need/behavior decision path** (details below).

---

## 1. The periodic ~2 s freeze is a full GC — and incremental GC is disabled

Players report a recurring ~2 s freeze during play, unrelated to autosave. We
confirmed it is a full, blocking Mono GC by **direct measurement**, not
correlation:

- `UnityEngine.Scripting.GarbageCollector.isIncremental == false` on the shipped
  build.
- Timing a forced full collection on the grown heap
  (`GC.Collect(2, Forced, blocking:true)` around a `Stopwatch`) gave **1.1–1.9 s**
  every time (6/6 probes) on the ~3 GB heap.
- The game's own hitch instrumentation independently caught spontaneous freezes
  with `gcDelta = 1/1/1` and a matching multi-hundred-MB heap drop.

Crucially, probes that freed almost nothing (3–21 MB) **still took ~1.15 s** —
so the cost is the mark phase walking the live object graph, i.e. it grows with
the size of the colony, not with the amount of garbage.

**Suggested fix (low effort, high impact): enable incremental GC**
(`Project Settings → Player → Configuration → Use incremental GC` /
`PlayerSettings.gcIncremental = true`). It slices the collection across frames,
turning a single ~1.5 s stall into sub-frame increments. It is broadly safe with
the Mono backend and would remove the most-complained-about hitch for every
player with a big base, with no simulation changes on your side.

Related, separate finding: an **autosave** produces its own ~2 s stutter (the
"Saved game in ~2.25 s" serialization, and it can also trip a GC because
serialization allocates heavily). Distinct from the idle GC freeze, but worth
noting if you profile save time.

## 2. Where the per-tick garbage comes from (if you want fewer GCs)

Incremental GC addresses pause *length*; reducing allocation addresses
*frequency*. Attributing allocation on this build is awkward
(`GC.GetAllocatedBytesForCurrentThread()` returns 0 under this Mono, so we
sampled `GC.GetTotalMemory(false)` deltas around the main-loop phases). With
that caveat, the **concentrated, large per-call** allocations are all in the
need/behavior decision tree — e.g. single `BehaviorManager.Tick` /
`NeederRootBehavior.Decide` calls committing 1.5–3.3 MB. Specifics:

- **`DistrictNeedBehaviorService.PickBestAction`** appraises into a
  `SortedSet<AppraisedNeedBehaviorGroup>` that is `.Clear()`ed every call, so it
  churns a red-black-tree node per candidate group, per beaver, per decision.
  (A reused list + in-place sort would remove that, but note the comparer never
  returns 0 — tie order is implementation-defined, which is exactly why we did
  **not** ship this change.)
- **`InventoryNeedBehavior.AppraiseGood` / `GoodEffects`** build a LINQ
  `.Select(...)` (closure + iterator + `IEnumerable` boxing) per candidate good.
  Interestingly, `Appraiser` already has an allocation-free
  `AppraiseEffects(ImmutableArray<InstantEffect>, NeedFilter)` overload; the
  inventory path just doesn't use it.
- **`DistrictNeedBehaviorService.GetNeedBehaviors`** uses
  `.Select(...).ToImmutableArray()` on the cache-miss path.

The bulk of remaining allocation is diffuse per-frame component `Update()` work
with no single hotspot, which is much harder to attack from the outside.

## 3. Tick dispatch is the clearest CPU win we found

Reworking per-entity tick dispatch (bucketed, cache-friendly iteration over
`TickableEntityBucket` / component ticks) is the single biggest CPU win.
Combined with the other hot-path caches (need/behavior candidate selection, a
speed-normalized travel-distance cache invalidated on navmesh changes), the mod
computes the **same** colony faster. Measured per full in-game day (day/night
averaged) with all conditions on the **same game days** on this save at ultra
speed: unmodded **17.95** ticks/s, optimizations-on (rendered) **26.72**
(**1.49x**), optimizations + render blackout **31.24** (**1.74x**). The always-on
compute win is thus ~1.5x; the rest is from suppressing rendering. (A cruder
fixed-window A/B on an earlier, heavier moment showed ~2x when the unmodded rate
bottomed near 14 ticks/s — that is a peak, not the steady-state average; the
per-day numbers above are the honest figure.) This is a "we found headroom here"
signal, not a claim your dispatch is wrong.

## Method / reproducibility

- Decompiled game assemblies (ILSpy) to read the exact hot methods.
- A/B by forcing optimized vs vanilla for equal-length windows and comparing
  full-ticks/s; behavior verified by unattended runs producing an identical
  colony.
- GC confirmed by direct `GC.Collect` timing and by hitch logs carrying
  `GC.CollectionCount` deltas + `GetTotalMemory` deltas per long frame.

Happy to share the raw logs, the exact save, or the measurement harness if
useful.

## Where to send this
- Official feedback / suggestions portal: <https://timberborn.featureupvote.com/>
  (the incremental-GC item is a clean, actionable suggestion).
- Modding Discord (#mod-creators) and the mod.io page for the profiling detail.
