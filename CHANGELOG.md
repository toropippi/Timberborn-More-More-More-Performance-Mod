# Changelog

## v1.2.3 — Workshop release (2026-09-16)

- Harvest searches (lumberjacks, farms, gatherers) skip the path query for candidates that cannot change the result; the first candidate is always queried so cached flow fields fill as before.
- Inventories read allowed-good amounts directly instead of a repeated linear search.
- The large load-status window is replaced by a single text-free progress bar shown only during world loads.
- All measurement, validation and experiment code moved out of the shipped assembly; `scripts/check_product_purity.ps1` gates every build and `scripts/measure.ps1` is the single measurement entry (rules: `docs/DIAGNOSTICS.md`).

## v1.2.2 — local playtest

- Characters move corner to corner along their path instead of 0.1-unit sub-steps (stop rules and path choices unchanged; positions can differ from vanilla in the last decimals).

## v1.2.1 — local playtest

- Added the bottom-right meter and restored x1/x3/x7 selected speeds and optional Shift+O smooth mode.
- Preserved native EventBus registration and validation while using typed delegates.
- Fixed water-upload history invalidation when Harmony regenerates a patch chain.
- Refined tube-visitor cleanup and GoodStack compatibility; removed ineffective tick-index code and unused diagnostics.

Current status and validation limits: [handoff](docs/HANDOFF.md).

## v1.2.0 — runtime rebuild

Replaced the previous runtime implementation with guarded event delegates,
Frontier traversal and water-upload deduplication. Added the tube-light workaround
and expanded load optimizations. Older frame-based caches and model/render
shortcuts were removed. Reported symptoms are not all proven to share one cause.

## v1.1 history

These entries describe the earlier implementation, not the current feature set.

| Version | Main change at the time |
|---|---|
| 1.1.7 | Removed a development asset bundle that broke game 1.0 loading |
| 1.1.6 | Made mode hotkeys respect text-input blocking |
| 1.1.5 | Changed water-object update handling |
| 1.1.4 | Corrected flood-state updates |
| 1.1.3 | Corrected resource reachability updates |
| 1.1.2 | Revised path-range drawing |
| 1.1.1 | Added mechanical highlight/UI work |
| 1.1.0 | Initial simulation optimizations and optional render mode |

Detailed historical changes remain in Git history.
