# Changelog

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
