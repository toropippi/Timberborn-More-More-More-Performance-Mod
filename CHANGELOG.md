# Changelog

## v1.2.5 — game-speed patches always apply (2026-10-10)

- Game 1.1.2.7 silently disabled every game-speed patch, including the x1/x3/x7 speed policy: the raw-IL fingerprints changed with every game rebuild (metadata tokens), although the method bodies were unchanged. All runtime build checks, shape checks, foreign-patch checks, world-load revalidation and native fallbacks are removed. Runtime patches are always applied; a patch that cannot be applied throws out of StartMod, and the game shows its crash screen with a `T3MP:` message.
- Allowed-good rows follow 1.1.2.7's new `Inventory._specIgnorableCapacity` early return (older builds without the field are unaffected).
- Water upload de-duplication is no longer limited to Direct3D11.
- Mono inline limit: a runtime whose limit reader cannot be located now throws instead of being skipped (other platforms and a user-set `MONO_INLINELIMIT` are still left alone).
- Save-loading optimizations check the content of the game methods they rely on instead of exact assembly IDs, so a game rebuild with unchanged code keeps them on (all on 1.1.2.7; on 1.0.13.1 all but good stacks and entity visuals). A mismatch still falls back silently.
- The bottom-right meter can be hidden: hovering it shows a small × button (hide) or + button (show); the choice is remembered.

## v1.2.4 — runtime inline limit (2026-09-18)

- Raises the Mono JIT inline size limit from 20 to 300 IL bytes in memory after the mod's own patches are installed. The variable is located through the runtime's own MONO_INLINELIMIT reader and written only when the complete instruction template of the supported runtimes matches. Off switch `-t3mpTestNoInlineLimit`; a user-set `MONO_INLINELIMIT` wins.
- Measured on a 3,700-character colony: +4.4% simulation throughput (the environment variable itself gives +5%); load time unchanged.

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
