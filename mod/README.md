# More More More Performance! (T3MP)

**A real, algorithmic speedup for Timberborn — not a speed multiplier.** It
rebuilds the simulation's heaviest CPU hot paths so large late-game colonies and
fast-forward actually keep up, while producing the **exact same colony** as
vanilla.

- **The hidden population speed throttle is removed** — on a big colony,
  vanilla turns your x3 into ~x1.8 and x7 into ~x3.4 without telling you; with
  this mod the speed you press is the speed you get. In practice this is the
  change you feel the most.
- **~1.5x** faster simulation, always on (measured per full in-game day on a
  large colony, so day/night load is averaged out).
- **Up to ~2.4x** with the optional Shift+P animation-skip turbo (the exact
  factor depends on your CPU).
- **Behavior-identical to vanilla** — pathfinding, reservations, inventories and
  water are never skipped or approximated. Same result, every time.

## What it is (and isn't)

Other speed mods raise the game-speed **multiplier** — but the more you raise it,
the more the game stutters, because the simulation is **CPU-bound**. This mod
attacks the other side: it makes each simulation tick **cheaper to compute**, so
raising the speed (with the base game controls or any speed mod) actually
delivers instead of grinding to a halt. Pair it with your favorite speed mod.

It does **not** change any game behavior — it just does the same math with far
less overhead.

## Features

1. **Always-on optimizations** — enabled automatically as soon as a save
   loads. Nothing to configure.
2. **Population speed throttle removed** — vanilla quietly shrinks any speed
   above x1 as the colony grows: `effective = 1 + (pressed − 1) × scale`, with
   the scale falling from 1.0 at ≤30 beavers to 0.4 at ≥200. So on a big
   colony x3 really runs x1.8, x7 runs x3.4, x50 runs x20.6. With this mod
   the speed you press is the speed you get. (This is the mod's one deliberate
   behavior change; a flag in the source disables it.)
3. **Turbo rendering (Shift+P)** — for leaving a heavy colony fast-forwarding
   unattended. Toggles a render blackout + animation thinning: the screen goes
   dark (one frame is drawn every 100 ticks so you can watch progress) and the
   time normally spent rendering is given back to the simulation — measured up
   to ~2.4x the vanilla tick rate at very high speeds on the test colony. At
   the base game's normal speeds it does **not** change the tick rate, it just
   darkens the screen. Press Shift+P again to turn rendering back on.

A live speed meter is shown bottom-right whenever the mod is active:
`rSPD/iSPD` — the real vs. ideal speed multiple (real = what you are actually
getting, ideal = what the game is trying to run — with the throttle removed
this normally equals the speed you pressed) —
and `UPS` (simulation updates/ticks per second). It keeps updating during a
Shift+P blackout, so you can confirm the simulation is still running while the
screen is dark.

## Tip: V-Sync and fps at high speed

If fps sits below 60 at high speed even though the CPU is keeping up
(rSPD ≈ iSPD on the meter), that is usually V-Sync quantization: the moment a
frame takes even slightly longer than 16.7 ms, V-Sync snaps it to the next
30-fps slot. Turning **V-Sync off** (or raising the fps cap) in the game's
graphics settings lets the frame rate float at its true value instead.
Trade-off: possible screen tearing and higher GPU load — simulation
correctness is unaffected either way.

## Install

1. Install the **Harmony** mod (required).
2. Enable this mod in the in-game Mod Manager and restart if prompted.

---

## 日本語

**シミュレーション自体を速くする、本物の最適化 mod です（速度倍率を変えるもの
ではありません）。** Timberborn の重い CPU 処理を作り替えて、
大規模な終盤コロニーや高速再生が**本当に速く回る**ようにします。結果はバニラと
**完全に同じ**です。

- **隠れ人口速度制限を撤廃** — バニラは大コロニーで x3 を実質 x1.8、x7 を x3.4 に
  勝手に縮めます（無告知）。本 mod では押した速度がそのまま出ます。
  **体感ではこれが一番効きます**
- **常時約 1.5 倍**（大規模コロニーで、ゲーム内 1 日単位で計測。昼夜の負荷差を平均化）
- **Shift+P のアニメーション skip で最大約 2.4 倍**（CPU に依存）
- **挙動はバニラと完全一致** — 経路・予約・在庫・水を一切省略・近似しません。
  だから結果は毎回同じ

他の速度 mod は「倍率」を上げますが、上げるほどカクつきます（シミュが CPU 律速
のため）。この mod は逆側、**1 tick あたりの計算を軽くする**ので、（本体の速度
コントロールでも他の速度 mod でも）上げた速度が実際に出るようになります。お好みの
速度 mod と併用できます。

- **常時最適化**：セーブ読み込みと同時に自動で有効。設定不要。
- **人口速度制限の撤廃**：バニラは「1倍を超えた分」をコロニー人口で縮小します
  （実効 = 1 + (押した速度 − 1) × 係数、係数は30人以下で1.0→200人以上で0.4）。
  つまり大コロニーでは x3→実質x1.8、x7→x3.4、x50→x20.6。本modでは押した速度が
  そのまま通ります。（これが本mod唯一の意図的な挙動変更です。ソースのフラグで無効化可能）
- **ターボ描画（Shift+P）**：重いコロニーを放置で早送りするとき用。画面を暗転
  ＋アニメ間引きして、描画に使っていた時間をシミュに回します（超高速時に
  バニラ比最大約2.4倍を計測）。通常速度では tick は変わりません。
  もう一度 Shift+P で描画に戻ります。

右下に速度メーターを表示：`rSPD/iSPD`（実際／理想の倍速。実際＝出ている倍速、
理想＝人口スロットル補正後にゲームが出そうとしている倍速）と `UPS`（1秒あたりの
tick 数）。暗転中も更新され続けます。

**Tips（V-Syncとfps）**：CPUに余裕がある（メーターで rSPD≒iSPD）のに高速時に
60fpsを切る場合、大抵はV-Syncの量子化です（フレームが16.7msをわずかに超えた瞬間、
次の30fps枠に切り下げられる）。グラフィック設定で**V-SyncをOFF**（またはfps上限を
引き上げ）にするとfpsが実力値で出ます。代償はティアリングの可能性とGPU負荷増のみで、
シミュレーションの正確さには一切影響しません。

**導入**：1) **Harmony**（必須）を入れる → 2) ゲーム内 Mod Manager で有効化して再起動。
