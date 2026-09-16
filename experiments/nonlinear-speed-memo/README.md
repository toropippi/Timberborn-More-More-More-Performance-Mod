# Nonlinear animation speed memo (not adopted)

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `nonlinear-speed-memo-20260915`
in [evidence.json](../../docs/evidence.json).

`NonlinearAnimationManager.SpeedMultiplier` evaluates `Pow(timeScale, Exponent) / timeScale`
once per animated modular-shaft segment per tick. `NonlinearSpeedMemo.cs` replaces the getter
body with a one-entry memo keyed on the bit patterns of the live time scale and `Exponent`;
a miss recomputes with the native expression, invalidation runs the native sequence through
the original `NonlinearSpeed` property. Guards follow `TerrainNeighborVisits`: fingerprints of
both getters (identical in 1.1.2.4 and 1.0.13.1), foreign-patch checks and observe transpilers
on `NonlinearSpeed` and `Mathf.Pow`, a foreign-patch check on `Time.get_timeScale`.

n10c x50 BPPB against installed v1.2.2: 22.22 -> 22.34 ticks/s (+0.55%, adjacent 1.008/1.003),
within run-to-run noise. Not shipped.

To retry: copy the file into `src/T3MP/Runtime/`, add `<Publicize Include="Timberborn.TimeSystem" .../>`
to `src/T3MP/T3MP.csproj`, add `ModSettings.EnableNonlinearSpeedMemo`, call
`NonlinearSpeedMemo.Install` from `RuntimePatches.Install` and `Revalidate` from
`LoadPatches`, then measure with `run_fixed_tick_ab.ps1 -Order BPPB -ComparisonDll <dll>
-ExpectedExperiment NonlinearSpeedMemo -MatchedConditions -Speed 50`.
