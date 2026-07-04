# More More More Performance! (T3MP)

**A performance mod: it makes the game itself run faster — not a speed
multiplier.** It makes the simulation's heaviest CPU work much cheaper, so large
late-game colonies and fast-forward actually keep up, while producing the
**exact same colony** as vanilla.

- **~1.5x faster simulation, always on** — install it, load your save, done
  (measured per full in-game day on a large colony, so day/night load is
  averaged out).
- **Up to ~2.4x with the Shift+P turbo** — skips animations while you
  fast-forward; press Shift+P again to go back (the exact factor depends on
  your CPU).
- **Hidden speed-button slowdown removed** — as your colony grows, vanilla
  quietly weakens the speed buttons: on a big colony the fastest button runs at
  **less than half** of its real speed. With this mod the speed you press is
  the speed you get: even on a medium or large colony, **the speed feel of
  your first days on a fresh map comes right back**.
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

## Why is it faster? (the biggest win, in plain words)

Every step of game time, the game must give tens of thousands of things —
every beaver, building and tree — their turn to act. How that list is stored
decides how much time is wasted *between* the turns:

- **Vanilla** keeps the list in a structure that is slow to walk through, and
  re-checks every entry one by one, every step, thousands of times per second.
- **This mod** lays the same list out flat in memory, in the exact same order,
  so the CPU sweeps straight through it front to back — the layout CPUs read
  fastest.

Same turns, same order, same results — just far less bookkeeping per step.
That one change alone was worth roughly a quarter more speed on a large test
colony; the rest comes from many smaller changes in the same spirit, each one
measured (the full optimization history is in the GitHub repo).

## Features

1. **Always-on optimizations** — enabled automatically as soon as a save
   loads. Nothing to configure.
2. **Hidden speed-button slowdown removed** — vanilla quietly weakens every
   speed above x1 as the colony grows; the bigger the colony, the less your
   speed buttons actually deliver (less than half on a 200+ beaver colony).
   With this mod the speed you press is the speed you get. (This is the mod's
   one deliberate behavior change; a flag in the source disables it. The exact
   vanilla formula is documented in the GitHub repo.)
3. **Turbo rendering (Shift+P)** — for leaving a heavy colony fast-forwarding
   unattended. Toggles a render blackout + animation thinning: the screen goes
   dark (one frame is drawn every 100 ticks so you can watch progress) and the
   time normally spent rendering is given back to the simulation — measured up
   to ~2.4x the vanilla tick rate at very high speeds on the test colony. At
   the base game's normal speeds it does **not** change the tick rate, it just
   darkens the screen. Press Shift+P again — or simply open the Esc menu — to
   turn rendering back on (opening the menu always cancels the turbo, so it
   can never follow you into another save).

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

**高速化 MOD です。ゲームの計算を軽くして、ゲームそのものを速くします（速度
倍率を変える MOD ではありません）。** 大規模な終盤コロニーや高速再生が本当に
速く回るようになり、結果はバニラと**完全に同じ**です。

- **常時、シミュレーションが約 1.5 倍**：導入してセーブを読み込むだけ
  （大規模コロニーで、ゲーム内 1 日単位で計測。昼夜の負荷差を平均化）
- **Shift+P のターボで最大約 2.4 倍**：アニメーションを省略して早送り。
  もう一度押すと元に戻ります（CPU に依存）
- **速度ボタンの隠し減速を撤廃**：バニラは人口が増えると速度ボタンの効きを
  こっそり弱めます。大きなコロニーでは、最速ボタンを押しても**本来の半分以下**
  しか出ません。本 MOD では押した速度がそのまま出て、中規模・大規模コロニー
  でも**ゲームを始めたばかりの頃の速度感がそのまま戻ってきます**
- **挙動はバニラと完全一致** — 経路・予約・在庫・水を一切省略・近似しません。
  だから結果は毎回同じ

他の速度 mod は「倍率」を上げますが、上げるほどカクつきます（シミュが CPU 律速
のため）。この mod は逆側、**1 tick あたりの計算を軽くする**ので、（本体の速度
コントロールでも他の速度 mod でも）上げた速度が実際に出るようになります。お好みの
速度 mod と併用できます。

**なぜ結果を変えずに速くなるのか（一番効いた改良を平たく）**：ゲームは時間を
一歩進めるたびに、街の数万個の対象（ビーバー・建物・木…）に順番を回して動かし
ます。この「一覧の持ち方」で、順番を回す合間のムダの量が決まります。バニラは
たどるのが遅い形で一覧を持ち、毎回 1 件ずつ余計な確認をしながら回します——それが
毎秒何千回も。本 mod は同じ一覧を、**同じ順番のまま**、メモリ上に隙間なく並べ
直しました。CPU は前から一気になめるだけ——CPU が最も速く読める形です。回す
順番も結果も同じで、管理の手間だけが減ります。この 1 つだけで大規模コロニーの
速度が約 1/4 上がりました。残りは同じ発想の細かい改良の積み重ねです（1 つずつ
実測した記録は GitHub リポジトリの optimization history にあります）。

- **常時最適化**：セーブ読み込みと同時に自動で有効。設定不要。
- **速度ボタンの隠し減速の撤廃**：バニラは人口が増えるほど、1倍を超える速度の
  効きをこっそり弱めます（200匹以上のコロニーでは最速ボタンでも本来の半分以下）。
  本modでは押した速度がそのまま通ります。（これが本mod唯一の意図的な挙動変更です。
  ソースのフラグで無効化可能。バニラの正確な式は GitHub リポジトリに記載）
- **ターボ描画（Shift+P）**：重いコロニーを放置で早送りするとき用。画面を暗転
  ＋アニメ間引きして、描画に使っていた時間をシミュに回します（超高速時に
  バニラ比最大約2.4倍を計測）。通常速度では tick は変わりません。
  もう一度 Shift+P を押すか、Esc メニューを開くと描画に戻ります（メニューを
  開くと必ず解除されるので、別のセーブに持ち越されることはありません）。

右下に速度メーターを表示：`rSPD/iSPD`（実際／理想の倍速。実際＝出ている倍速、
理想＝人口スロットル補正後にゲームが出そうとしている倍速）と `UPS`（1秒あたりの
tick 数）。暗転中も更新され続けます。

**Tips（V-Syncとfps）**：CPUに余裕がある（メーターで rSPD≒iSPD）のに高速時に
60fpsを切る場合、大抵はV-Syncの量子化です（フレームが16.7msをわずかに超えた瞬間、
次の30fps枠に切り下げられる）。グラフィック設定で**V-SyncをOFF**（またはfps上限を
引き上げ）にするとfpsが実力値で出ます。代償はティアリングの可能性とGPU負荷増のみで、
シミュレーションの正確さには一切影響しません。

**導入**：1) **Harmony**（必須）を入れる → 2) ゲーム内 Mod Manager で有効化して再起動。
