# Promotion drafts — Reddit / X

Rules these drafts follow: **the hook is the speedup — this mod is an
optimization mod first**; the throttle removal is a supporting feature, never
the headline. Player language only (speed buttons, not internal multipliers),
measured numbers only (~1.5x always-on, x3.4-vs-x7 throttle, ~2.4x Shift+P).
Replace `[STEAM_URL]` / `[MODIO_URL]` after publishing.

## Reddit — r/Timberborn

**Attach:** `assets/graph_measured.png` + `assets/graph_throttle_bars.png`
(graphs carry the post; the thumbnail is store-only).

**Title:**

> I rebuilt Timberborn's simulation hot paths: a free performance mod that
> runs the sim ~1.5x faster always-on, up to ~2.4x with turbo (measured)

**Body:**

> Big colonies get slow because the simulation is CPU-bound on one thread. I
> profiled the actual hot paths (per-entity tick dispatch, need/behavior
> selection, travel-distance estimation) and rebuilt them to compute the
> **same result faster** — this is a real algorithmic speedup, not a speed
> multiplier. The result: **More More More Performance! (T3MP)**.
>
> 1. **Always-on optimizations** — the simulation runs up to **~1.5x faster**
>    the moment your save loads. Measured per full in-game day on a large
>    late-game colony, same colony and same days as the vanilla run.
> 2. **Optional Shift+P turbo** — for leaving a heavy colony fast-forwarding
>    unattended: skips rendering/animation work, up to **~2.4x vanilla**
>    measured. Press Shift+P again to go back to normal.
> 3. **Bonus:** while profiling I found vanilla quietly throttles your speed
>    buttons as population grows (at 200+ beavers the fastest button delivers
>    x3.4 instead of x7 — measured). The mod removes that hidden throttle, so
>    the speed you press is the speed you get. This is the mod's one
>    deliberate behavior change.
>
> Everything else is a pure optimization: the sim computes the **same**
> results as vanilla (verified by running the same colony with and without
> the mod). Works fine together with speed mods. Requires the Harmony mod.
>
> Steam Workshop: [STEAM_URL]
> mod.io: [MODIO_URL]
>
> Happy to answer technical questions — the optimization write-up is in the
> GitHub repo if you're curious how it works.

## X (Twitter) — Japanese

**Attach:** `assets/thumbnail_v5.png` or `assets/graph_measured.png`

> 【Timberborn】大コロニーで重くなるシミュレーション本体を高速化する無料MODを
> 公開しました。導入するだけで常時約1.5倍、ターボ(Shift+P)で最大2.4倍（実測）。
> おまけに、人口が増えると速度ボタンがこっそり減速される隠し仕様も撤廃します。
> Steam / mod.io で「More More More Performance!」で検索
> [STEAM_URL]

## X (Twitter) — English

> I made a free Timberborn performance mod: the simulation itself runs ~1.5x
> faster always-on, up to 2.4x with turbo (measured on a large late-game
> colony). It also removes the hidden speed-button throttle at high population.
> "More More More Performance!" on Steam Workshop & mod.io
> [STEAM_URL]

## Posting notes

- Post from your own accounts; fill the URLs in after the Steam/mod.io pages
  are live so the links are clickable from day one.
- Reddit: r/Timberborn allows mod showcases; graphs as inline images perform
  better than a bare link. Reply to early comments with the GitHub repo for
  the technically curious.
- X: the JP and EN posts are independent tweets, not a thread; attach the
  image directly (link-preview cards are often cropped).
