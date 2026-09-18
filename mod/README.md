# More More More Performance! (T3MP)

Version 1.2.4.

T3MP reduces repeated work during save loading and simulation. Its runtime
optimizations preserve simulation decisions and their order. Performance depends
on the colony, game version and other enabled mods.

## Features

- **Shift+O — smooth mode.** Optional, off at load. Targets 30 fps by adjusting
  whole-game speed between x1 and the selected speed. Press again to restore
  the selected speed. While governing, VSync and the frame cap are temporarily
  released. The meter shows `Smooth 30fps [Shift+O]` while enabled; `iSPD`
  remains the selected speed and `rSPD` reports measured progress.

- **Selected speed.** Speed buttons 1/2/3 select x1/x3/x7 without population-based
  reduction. `iSPD` shows the selected multiplier; `rSPD` shows measured progress
  and can be lower when the computer cannot keep up.

- **Bottom-right speed meter.** `rSPD` is measured simulation speed, `iSPD` is
  the current target speed, and `UPS` is simulation ticks per real second.
  The meter refreshes four times per second over about two seconds. Pause
  shows zero. These values describe the running game; they do not measure
  speedup against a separate unmodded run.
- **Save loading.** Entity construction, initialization, event delivery and
  navigation setup use fewer repeated lookups on supported game builds. A thin
  progress bar at the bottom of the window shows the load phases of a world load.
- **Simulation overhead.** Typed event delegates reduce reflection calls.
  Frontier avoids empty entity visits when all tickable components are disabled.
  Water texture layers with identical bytes avoid a redundant GPU upload.
  Walker speed callbacks are reused instead of re-created every tick, terrain
  path searches skip duplicate neighbor visits, inventories read allowed-good
  amounts directly, and harvest searches skip path queries for trees and crops
  that cannot change the result.
- **Movement.** Characters move corner to corner along their path instead of
  0.1-unit sub-steps. Stop rules and path choices are unchanged; positions can
  differ from vanilla in the last decimals.
- **Runtime tuning.** At startup the mod raises the Mono JIT's inline size limit
  from 20 to 300 IL bytes in memory, so the game's many small methods are inlined
  when they are compiled. No file is changed and nothing is left behind; the
  setting lasts until the game exits. It is skipped when the runtime does not
  match the supported builds or when you set `MONO_INLINELIMIT` yourself. Add
  `-t3mpTestNoInlineLimit` to the Steam launch options to turn it off. A mod that
  patches a small method only after a save has loaded may miss call sites that
  were compiled earlier.
- **Tube lighting repair.** Clears stale tube visitor registration when a
  character enters a building immediately after passing through a tube, so
  lighting reflects the remaining visitors.

## Compatibility

Requires Harmony. The compatibility targets are Timberborn 1.1.2.4 and
1.0.13.1 on Windows. Water upload optimization requires Direct3D11. Game
method checks and checks for conflicting patches retain native execution
when an optimization cannot safely apply.

The older frame-based simulation caches, animation snapping and render suppression
have been removed. Tube lighting repair does not change movement animation.

---

# 日本語

バージョン1.2.4。

T3MPはセーブのロードとシミュレーション中の重複処理を減らします。
ランタイムの最適化はシミュレーションの判断と順序を維持します。
高速化の効果は集落、ゲームの版、併用MODによって変わります。

## 機能

- **Shift+O — スムーズモード。** ロード時はOFF。30fpsを目標に、×1から選択速度までの範囲で
  ゲーム全体の速度を自動調整します。再度押すと選択速度に戻ります。自動調整中はVSyncと
  FPS上限を一時解除します。有効時はメーター上に `Smooth 30fps [Shift+O]` と表示し、
  `iSPD`は選択倍率、`rSPD`は実測倍率を示します。

- **選択した速度を維持。** 速度ボタン1・2・3は×1・×3・×7です。人口による減速を解除します。
  `iSPD`は選択倍率、`rSPD`は実測倍率です。処理能力が足りない場合、実測倍率は選択倍率を下回ります。

- **右下の速度メーター。** `rSPD`は実測の進行速度、`iSPD`は現在の設定速度、
  `UPS`は実時間1秒あたりのシミュレーションtick数です。約2秒の実測を毎秒4回更新し、
  一時停止では0を表示します。現在のゲームの速度を示すもので、MODなしとの比較倍率ではありません。
- **セーブのロード。** 対応するゲーム版で、エンティティ生成・初期化・イベント配信・
  経路網構築の重複した検索を減らします。ワールドのロード中は画面下部に細いプログレスバーを表示します。
- **本編ループの軽量化。** 型付きdelegateによるイベント配信、tick部品がすべて無効な
  エンティティの空の走査の省略、同じbyte列の水テクスチャのGPU再送抑制を行います。
  歩行者の速度コールバックの再利用、地形経路探索の重複した隣接探索の省略、
  在庫の許可品目の直接参照、結果を変えない木や作物への経路照会の省略も行います。
- **移動処理。** キャラクターは0.1マス刻みではなく経路のコーナー単位で進みます。
  停止規則と経路の選択は変わりません。座標の下位桁はバニラと一致しないことがあります。
- **ランタイムの調整。** 起動時に、Monoの関数展開の上限をメモリ上で20から300バイトへ引き上げます。
  ゲームに多い小さな関数が、機械語へ変換されるときに呼び出し元へ展開されます。ファイルは変更せず、
  効果はゲーム終了まで。対応版のランタイムと一致しない場合や、自分で`MONO_INLINELIMIT`を設定している場合は
  何もしません。無効にするにはSteamの起動オプションに`-t3mpTestNoInlineLimit`を追加します。
  セーブのロード後に小さな関数をパッチするMODは、先に変換済みの呼び出し元に効かない場合があります。
- **配管照明の修正。** 配管を通過した直後に建物へ入ったキャラクターの古い訪問登録を解除し、
  残っている訪問者に合わせて照明を更新します。

## 互換性

Harmonyが必要です。対応対象はWindowsのTimberborn 1.1.2.4と1.0.13.1です。
水テクスチャの最適化にはDirect3D11が必要です。ゲームの対象メソッドと他MODのパッチを確認し、
安全に適用できない最適化は元の処理を使います。

旧版のフレーム基準のシミュレーションキャッシュ、モデルのスナップ、描画抑制は削除しました。
配管照明修正は移動アニメーションを変更しません。
