# Movement sub-steps experiment

Shipped in v1.2.2: the code now lives in `src/T3MP/Runtime/MovementSubsteps.cs` and is
compiled unconditionally (symbol `MOVEMENT_SUBSTEPS`); coarse stepping is the default and
`-t3mpTestNoMovementCoarse` / `-t3mpTestNoMovementSubsteps` switch it off. The
`-p:MovementSubstepsExperiment` build flag no longer exists. Release evidence:
`release-1.2.2-20260915` in evidence.json. The rest of this file is the experiment record.

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `movement-substeps-20260915`
in [evidence.json](../../docs/evidence.json).

`PathFollower.MoveAlongPath` advances a walker in 0.1-unit sub-steps (about 52 per
call on n10c) and writes `Transform.position` on every sub-step, reading it back for the
next one. Walker transforms are root objects with about 40 model descendants, so each
write costs about 53 ns of subtree dirtying against 7.6 ns per read (measured in place by
`src/T3MPTestDriver/MovementProbe.cs`, `-t3mpTestMovementProbe`, runner
`scripts/run_movement_probe.ps1`). `MovementSubsteps.cs` keeps the position in a local
and writes the transform only before speed-provider calls, after the loop, and on the
exception path. Float operations and their order are unchanged.

Build: `dotnet build src/T3MP/T3MP.csproj -c Release -p:MovementSubstepsExperiment=true -o <dir>`.
The file joins `WalkerSpeedDelegates` as a partial class: same Harmony owner, same
fingerprint and shape guards, same invalidation. `-t3mpTestNoMovementSubsteps` keeps the
native body; a foreign patch on `ReachedLastPathCorner`/`AddAnimatedPathCorner` switches
the replacement to the native helper sequence.

Validation: `run_fixed_tick_ab.ps1 -Order P -ComparisonDll <dll> -ExpectedExperiment MovementSubsteps -MovementValidation -MatchedConditions`
keeps the native body and replays the local loop on a scratch list before every call,
restoring the transform bits and corner index; the postfix compares every animated corner,
the corner index and the final position bit for bit.
Performance: the same command without `-MovementValidation`, `-Order BPPB`.

## Coarse stepping (`-t3mpTestMovementCoarse`, not bit-exact)

Native movement advances in 0.1-unit sub-steps only so that the stop check at the path
end runs every 0.1 units; the corners are known in advance. With the flag, the loop
advances corner to corner in one `MoveToward` step unless the segment's closest point to
the path end lies inside the stopping proximity (`SegmentTouchesEnd`, a superset of the
native grid check); such segments keep native 0.1-unit stepping. Provider calls stay at
the same corners with the same count. Low-order position bits and the number of animated
corners change. Audit (`-MovementValidation -MovementCoarse`) counts corner-count and
position differences separately from decision differences (corner index, arrival), and
the script passes `-t3mpTestMovementCoarse` only to the P arm (`-MovementCoarse`).
The closest point is pulled `TouchMargin` (0.01 units) toward the path end before the
proximity test, because native sub-step positions drift off the exact segment by rounding
(review finding). Known, accepted deviation: at tick edges (remaining time within about
1e-4 s of a step boundary) a corner arrival and its provider call can shift by one tick,
because the native `RemainingTimeThreshold`/`RemainingDistanceThreshold` checks between
0.1-unit steps are skipped.
n10c 2026-09-15: audits of 521,566 and 520,264 calls, 3 and 2 index differences,
0 arrival differences, max position delta 0.002, 0 transform integrity failures;
x50 +16%/+9%/+10% (three valid pairs, pooled 1.113), x7 FPS +14%/+13%.
Run the scripts with PowerShell 7 and keep the game window focused for every arm.
