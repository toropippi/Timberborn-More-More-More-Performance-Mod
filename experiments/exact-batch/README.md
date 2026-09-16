# Exact sub-1% batch (not adopted)

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `exact-batch-20260915` in
[evidence.json](../../docs/evidence.json).

Four output-identical rewrites, each estimated at 0.3-1% of the n10c x50 tick, measured
together in one build against installed v1.2.2 (CAA2B508):

| File | Native method replaced | Idea |
|---|---|---|
| `NeedUpdateInline.cs` | `NeedManager.UpdateNeed` | predicates computed from the same fields instead of getter calls; events raised with delegate captured first |
| `ShaftEfficiencyOnce.cs` | `ModularShaftAnimator.UpdateAnimation` | power efficiency and speed multiplier read once per shaft when all animators are `TimbermeshAnimator` |
| `PathEdgeSingleScan.cs` | `FlowFieldPathBuilder.AddEdgeNode` | one neighbor scan for connection cost and group id |
| `NeedAppraisalLookup.cs` | `Appraiser.AppraiseEffects` | need resolved once for appraisal and filter when the filter uses the same NeedManager |

Result (x50, 256+2048 ticks, idle machine): BPPB 22.14 -> 22.08 ticks/s (0.997), PBBP
22.50 -> 21.75 (0.967); four pairs pooled 0.982. Guard costs (per-row `Active` reads,
observe transpilers on 20+ helpers, exact-type checks) outweigh the savings. The codex
review (`testlogs/codex/exact-batch-review-20260915.md`) findings were fixed before the
second block. Not shipped.

To retry one of them: copy the file into `src/T3MP/Runtime/`, restore the `Publicize`
entries (NeedSystem, NeedBehaviorSystem, MechanicalSystem, TimeSystem, ModularShafts plus the
ModularShafts reference) in `src/T3MP/T3MP.csproj`, add its `ModSettings` flag, install it from
`RuntimePatches.Install`, revalidate from `LoadPatches`, and measure with
`run_fixed_tick_ab.ps1 -ExpectedExperiment ExactBatch` (expects all four) or add its own name.
