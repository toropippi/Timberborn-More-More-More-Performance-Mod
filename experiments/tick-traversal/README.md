# Tick traversal experiment

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `tick-index`
in [evidence.json](../../docs/evidence.json).

The C# files preserve the indexed implementation and its native comparison.
They are outside the product and test-driver projects. Runner copies retain the
former K arm and expect their original `scripts/` location; reconstruct them
only in an isolated checkout. Frontier's live guards are in the product source.
