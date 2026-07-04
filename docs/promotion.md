# Promotion drafts — Reddit / X

Rules these drafts follow: player language only (speed buttons, not internal
multipliers), measured numbers only (~1.5x always-on, x3.4-vs-x7 throttle,
~2.4x Shift+P), the throttle removal is disclosed as the one deliberate
behavior change. Replace `[STEAM_URL]` / `[MODIO_URL]` after publishing.

## Reddit — r/Timberborn

**Attach:** `assets/graph_throttle_bars.png` + `assets/graph_measured.png`
(graphs carry the post; the thumbnail is store-only).

**Title:**

> PSA: at 200+ beavers the max speed button secretly runs at less than half
> speed. I made a free mod that fixes that — and speeds up the sim ~1.5x on top

**Body:**

> While profiling Timberborn for a performance mod I found something the game
> never tells you: a hidden speed throttle tied to population. As your colony
> grows from 30 to 200 beavers, every speed above x1 is quietly scaled down.
> At 200+ beavers the fastest speed button delivers **x3.4 instead of x7**
> (measured in-game, and confirmed in the game's code). That's why big
> colonies "feel sluggish" even when your PC is fine.
>
> So I made **More More More Performance! (T3MP)**. What it does:
>
> 1. **Always-on optimizations** — the simulation itself runs up to **~1.5x
>    faster** the moment your save loads. Measured per full in-game day on a
>    large late-game colony, same colony and same days as the vanilla run.
> 2. **Removes the hidden population throttle** — the speed you press is the
>    speed you get. This is the mod's one deliberate behavior change, and
>    honestly the change you feel the most.
> 3. **Optional Shift+P turbo** — for leaving a heavy colony fast-forwarding
>    unattended: skips rendering/animation work, up to **~2.4x vanilla**
>    measured. Press Shift+P again to go back to normal.
>
> Everything except the throttle removal is a pure optimization: the sim
> computes the **same** results as vanilla (verified by running the same
> colony with and without the mod). Works fine together with speed mods.
> Requires the Harmony mod. Tested on game 1.1.0.2.
>
> Steam Workshop: [STEAM_URL]
> mod.io: [MODIO_URL]
>
> Happy to answer technical questions — the optimization write-up is in the
> GitHub repo if you're curious how it works.

## X (Twitter) — Japanese

**Attach:** `assets/thumbnail_v5.png` or `assets/graph_throttle_bars.png`

> 【Timberborn】人口が増えると速度ボタンがこっそり減速してるの、知ってました？
> 200匹コロニーだと最速ボタンはx7のはずが実際はx3.4しか出ません（実測）。
> これを撤廃し、さらにシミュ自体を最大2.4倍まで高速化する無料MODを公開しました。
> Steam / mod.io で「More More More Performance!」で検索
> [STEAM_URL]

## X (Twitter) — English

> Timberborn quietly slows your speed buttons as your colony grows — at 200+
> beavers the max button delivers x3.4, not x7 (measured). My free mod removes
> that hidden throttle and makes the sim itself up to 2.4x faster.
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
