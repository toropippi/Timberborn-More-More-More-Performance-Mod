# More More More Performance! (T3MP)

Version 1.2.1 — local playtest build. Workshop release is pending.

T3MP reduces repeated work during save loading and simulation. Its runtime
optimizations preserve simulation updates and their order. Performance depends
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
  navigation setup use fewer repeated lookups on supported game builds.
- **Simulation overhead.** Typed event delegates reduce reflection calls.
  Frontier avoids empty entity visits when all tickable components are disabled.
  Water texture layers with identical bytes avoid a redundant GPU upload.
- **Tube lighting repair.** Clears stale tube visitor registration when a
  character enters a building immediately after passing through a tube, so
  lighting reflects the remaining visitors.

## Compatibility

Requires Harmony. The compatibility targets are Timberborn 1.1.2.4 and
1.0.13.1 on Windows. Water upload optimization requires Direct3D11. Game
method checks and checks for conflicting patches retain native execution
when an optimization cannot safely apply.

The older frame-based simulation caches, animation snapping and render suppression
have been removed. Tube lighting
repair does not change movement animation.

---

# 日本語

バージョン1.2.1のローカル試遊版です。Workshop公開前の候補です。

T3MPはセーブのロードとシミュレーション中の重複処理を減らします。
ランタイムの最適化はシミュレーションの更新と順序を維持します。
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
  経路網構築の重複した検索を減らします。
- **本編ループの軽量化。** 型付きdelegateによるイベント配信、tick部品がすべて無効な
  エンティティの空の走査の省略、同じbyte列の水テクスチャのGPU再送抑制を行います。
- **配管照明の修正。** 配管を通過した直後に建物へ入ったキャラクターの古い訪問登録を解除し、
  残っている訪問者に合わせて照明を更新します。

## 互換性

Harmonyが必要です。対応対象はWindowsのTimberborn 1.1.2.4と1.0.13.1です。
水テクスチャの最適化にはDirect3D11が必要です。ゲームの対象メソッドと他MODのパッチを確認し、
安全に適用できない最適化は元の処理を使います。

旧版のフレーム基準のシミュレーションキャッシュ、モデルのスナップ、描画抑制は削除しました。
今回の配管照明修正は移動アニメーションを変更しません。
