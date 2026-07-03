# More More More Performance! (T3MP)

**A real, algorithmic speedup for Timberborn — not a speed multiplier, not a
benchmark tool.** It rebuilds the simulation's heaviest CPU hot paths so large
late-game colonies and fast-forward actually keep up, while producing the
**exact same colony** as vanilla.

- **~1.5x** faster simulation, always on (measured per full in-game day on a
  large colony, so day/night load is averaged out).
- **~1.75x** with the optional Shift+P render-skip turbo.
- **Behavior-identical to vanilla** — pathfinding, reservations, inventories and
  water are never skipped or approximated. Same result, every time: **no farmer
  bugs, no save risk.**
- **Actively maintained** for the current game version.

## What it is (and isn't)

Other speed mods raise the game-speed **multiplier** — but the more you raise it,
the more the game stutters, because the simulation is **CPU-bound**. This mod
attacks the other side: it makes each simulation tick **cheaper to compute**, so
raising the speed (with the base game controls or any speed mod) actually
delivers instead of grinding to a halt. Pair it with your favorite speed mod.

It is **not** a benchmark/diagnostic tool and it does **not** change any game
behavior — it just does the same math with far less overhead.

## Two features

1. **Always-on optimizations** — enabled automatically a few seconds after a
   save loads. Nothing to configure.
2. **Turbo rendering (Shift+P)** — for leaving a heavy colony fast-forwarding
   unattended. Toggles a render blackout + animation thinning: the screen goes
   dark (one frame is drawn every 100 ticks so you can watch progress) and the
   time normally spent rendering is given back to the simulation. This only
   raises the tick rate when the sim is already the bottleneck — i.e. when a
   speed mod pushes the game well past its normal top speed on a big base
   (measured +10–18% there). At the base game's normal speeds it does **not**
   change the tick rate, it just darkens the screen. Press Shift+P again to turn
   rendering back on.

A live simulation tick-rate meter (e.g. `SIM 28.30 ticks/s`) is shown
bottom-right whenever the mod is active. It keeps updating during a Shift+P
blackout, so you can confirm the simulation is still running while the screen is
dark.

## Install

1. Install the **Harmony** mod (required).
2. Enable this mod in the in-game Mod Manager and restart if prompted.

---

## 日本語

**シミュレーション自体を速くする、本物の最適化 mod です（速度倍率を変えるもの
でも、計測ツールでもありません）。** Timberborn の重い CPU 処理を作り替えて、
大規模な終盤コロニーや高速再生が**本当に速く回る**ようにします。結果はバニラと
**完全に同じ**です。

- **常時約 1.5 倍**（大規模コロニーで、ゲーム内 1 日単位で計測。昼夜の負荷差を平均化）
- **Shift+P のターボで約 1.75 倍**
- **挙動はバニラと完全一致** — 経路・予約・在庫・水を一切省略・近似しません。
  だから結果は毎回同じ＝**農夫バグやセーブ破損の心配なし**
- **現行バージョンをメンテ中**

他の速度 mod は「倍率」を上げますが、上げるほどカクつきます（シミュが CPU 律速
のため）。この mod は逆側、**1 tick あたりの計算を軽くする**ので、（本体の速度
コントロールでも他の速度 mod でも）上げた速度が実際に出るようになります。お好みの
速度 mod と併用できます。

- **常時最適化**：セーブ読み込みの数秒後に自動で有効。設定不要。
- **ターボ描画（Shift+P）**：重いコロニーを放置で早送りするとき用。画面を暗転
  ＋アニメ間引きして、描画に使っていた時間をシミュに回します。速度 mod で
  計算律速まで押し上げているときだけ tick が上がり（+10〜18%）、通常速度では
  tick は変わりません。もう一度 Shift+P で描画に戻ります。

右下に tick レート計（例 `SIM 28.30 ticks/s`）を表示。暗転中も更新され続けます。

**導入**：1) **Harmony**（必須）を入れる → 2) ゲーム内 Mod Manager で有効化して再起動。
