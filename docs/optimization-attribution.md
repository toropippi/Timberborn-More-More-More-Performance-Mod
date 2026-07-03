# Optimization attribution — which item bought how much of the always-on speedup

Measured 2026-07-03 on save **n10c**, Optimized-visible (optimizations ON, Shift+P
blackout OFF) driven at internal ultra speed (timeScale 50, i.e. at the sim
**compute ceiling** — the only regime where optimizations show, since below the
ceiling everything is speed-limited and identical).

**Method:** leave-one-out (LOO). Disable one optimization group, measure the drop
vs the full-optimized baseline. Throughput is real-time **ticks/s averaged per
full in-game day** (1 day = 768 ticks, day+night included so the load swing is
averaged), compared on the **same game days 2791–2793** across every run — and
because the optimizations are behavior-exact, day 2791 is the *identical* colony
state in every run, so this is a true apples-to-apples A/B.

- Baseline (all always-on optimizations, no Shift+P): **27.20** ticks/s
- Vanilla (no mod): **17.95** ticks/s
- **Total always-on gain = 9.25 ticks/s = 100%**  (this is the ~1.5x figure; Shift+P is excluded)

Contribution of X = (baseline − with-X-disabled) / (baseline − vanilla).

## Result (share of the always-on speedup)

| Rank | Optimization | ticks/s w/ it off | Share of gain |
|---|---|---:|---:|
| 1 | **Bucketed tick dispatch** (`TickDispatchOptimizer`) | 22.41 | **51.8%** |
| 2 | **Haul no-action cache** (`HaulNoActionFrameCache`) | 25.51 | **18.3%** |
| 3 | **Yielder / farm / workplace finders** (8 flags) | 26.22 | **10.6%** |
| 4 | **Need-travel shadow cache** (`GlobalNeedTravelShadowCache`) | 26.35 | **9.2%** |
| 5 | Water object service fast-skip | 26.63 | 6.2% |
| 6 | Inventory (need-good + stock-distance) | 26.77 | 4.6% |
| 7 | District need appraisal cache + direct optimizer | 26.82 | 4.1% |
| 8 | Worker / carry (root-metrics, working-speed, carry, mover cache) | 26.99 | 2.3% |
| 9 | Empty-inventories fast path | 27.08 | 1.3% |
| 10 | District resource-counter throttle | 27.17 | 0.3% |
| 11 | Visual / animation throttles | 27.46 | ~0% (noise) |

**The top 4 items = ~90% of the entire always-on speedup.** `TickDispatchOptimizer`
alone is over half.

### Item-level drill-downs
- **Haul (18.3%) is essentially all `HaulNoActionFrameCache`** — disabling just it dropped
  throughput to 25.19 (~21.7%); the ordered-cache adds ~0.
- **Need-travel (9.2%) is the shadow cache, not the distance cache** — disabling
  `EnableTravelDistanceCache` *alone* changed nothing (27.52, within noise). The distance
  cache is kept for **behavior exactness** (speed-normalized), not speed; the 9.2% comes from
  `GlobalNeedTravelShadowCache` short-circuiting need-decision travel evaluation.
- **Yielder/farm (10.6%) splits** ~5.8% yielder-finding (fast yielder finder + farm segment
  tree + lumberjack/planting) and ~4.8% farm/workplace behavior (farmhouse, gather, event-driven).
- **Visual/animation throttles contribute ~0 to sim ticks/s in visible mode** — they matter for
  rendering / the Shift+P blackout path, not for compute throughput.

## Caveats
- LOO shares sum to ~106%, not exactly 100%, because features interact/overlap slightly — it is
  an attribution, not a strict partition.
- Noise floor ≈ ±3% of the gain (the baseline reproduces in the 26.7–27.2 band). Items below ~4%
  (ranks 8–11) are at/under the noise floor — treat as "small," not precise.
- This attribution is only meaningful at high speed (at the compute ceiling). At normal game
  speeds nothing here changes the tick rate, because the sim is speed-limited, not compute-limited
  (see the tick-scheduling notes: ticks/s = timeScale/0.6 when unclamped, 0.555×fps when clamped).
