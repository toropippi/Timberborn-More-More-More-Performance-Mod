# Movement magnitude experiment

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `movement-magnitude`
in [evidence.json](../../docs/evidence.json).

`MovementMathProbe.csproj` creates a native DLL copy in a new output directory
and compares methods under CoreCLR. Supply the matching managed directory;
set `TimberbornInstall` when building for game 1.0.

The Unity prototype is `runtime/MovementMathExperiment.cs`. Reconstruct it in an
isolated checkout by adding it to the test driver and checking/applying
`runtime/integration.patch`. The normal driver has no M benchmark arm.
The historical runner and tested driver are in the local
`testlogs/movement-math-20260913/` and `testlogs/fixed-ab-20260913-134902/` archives.
