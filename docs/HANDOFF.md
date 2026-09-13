# 開発引継ぎ — 2026-09-13

## 現状

v1.2.1のローカル試遊版。ユーザーは現在の版でアニメーション・表示の異常なしと報告した。
全場面の回帰検証完了を意味しない。固定した導入DLLの識別子は[台帳](evidence.json)の`playtest`。
Steam 1.1.2.4を主対象とし、1.0.13.1もサポートする。

速度ボタン1/2/3は×1/×3/×7。人口による減速解除は製品仕様として維持する。
メーターはiSPD＝選択速度、rSPD＝実測進行速度、UPS＝実時間あたりのtick数。
Shift+Oは初期OFF。30fpsを目標に×1〜選択速度でゲーム全体の速度を調整する。
ズームに応じたT3MP独自のカリングはない。

## 次の仕事

目標はn10cで旧T3MPの効果を超え、MODなし比で少なくとも1.5倍の計算処理量を得ること。
バグを再発させないことを優先する。ロード時間・継続的なtick処理・設置/選択UIの応答は別に評価する。

最後の低外部負荷での比較は旧候補E219で**14.3188→19.7542 ticks/s、1.3796倍**。
描画ありだが極端な高速設定で1 FPS未満。通常プレイのFPS改善率ではなく、現在の試遊DLLの測定値でもない。
その後の速度仕様復元で比較条件が変わったため、過去の倍率を現在の実績に流用しない。

1. 新規の実機計測を依頼されたら、現行DLLとMODなしで実効速度を揃える。
   `run_fixed_tick_ab.ps1`の旧デフォルトをそのまま使うと、要求速度が同じでも実効速度が異なる。
2. 同じn10c・描画/カメラ・tick数で交互比較し、外部CPU/GPU負荷も記録する。
   細かいプロファイラーは性能測定から外す。速度上限に達した通常速度のTPS比で高速化を判定しない。
3. [採否表](DECISIONS.md)で既存の試作を確認し、根拠のある候補だけ一つずつ評価する。
   新しいデータなしに、効果を確認できなかった試作を再実装しない。

## コードを読む入口

| 調べること | 入口 |
|---|---|
| 導入機能と実行条件 | [ModSettings](../src/T3MP/ModSettings.cs)、[RuntimePatches](../src/T3MP/Runtime/RuntimePatches.cs)、[LoadPatches](../src/T3MP/Loading/LoadPatches.cs) |
| tickの空ループ削減 | [TickFrontier](../src/T3MP/Runtime/TickFrontier.cs) |
| イベント・水・配管 | [EventBusFastDelegates](../src/T3MP/EventBusFastDelegates.cs)、[WaterTextureUpload](../src/T3MP/Runtime/WaterTextureUpload.cs)、[TubeVisitFix](../src/T3MP/Runtime/TubeVisitFix.cs) |
| 速度仕様と表示 | [RequestedSpeedPolicy](../src/T3MP/Runtime/RequestedSpeedPolicy.cs)、[UI](../src/T3MP/UI/) |
| ロード時の互換性 | [LoadCompatibility](../src/Shared/LoadCompatibility.cs)、[LoadEventRouter](../src/T3MP/LoadEventRouter.cs) |
| 計測器・実験 | [TestDriver](../src/T3MPTestDriver/)、[experiments](../experiments/) |

実装の詳細はコードを正とする。上表以外の関数一覧や逐次作業ログを読み込む必要はない。

## 守る条件と検証範囲

- tickロジック・更新順・例外・他MODとの契約を維持する。フレームをキーにしたシミュレーションのキャッシュ、
  更新の間引き、モデル位置のスナップ、シミュレーション継続中の描画抑制は導入しない。
- 速度仕様とShift+Oはユーザーが選ぶ速度制御であり、計算量削減として数えない。
- 未知のゲームIL/モジュールや競合パッチでは既存のnative fallbackを維持する。
- 現在のA32試遊DLLは両APIビルド・速度制御/メーターのオフライン試験・指定コードレビュー済み。
  6起動シナリオ、階段、冠水等の自動試験には以前のDLLでの結果が含まれる。最終版の全面合格へ読み替えない。
- ユーザーは今回の実機確認を自分で行う方針。新しいゲーム起動・自動計測は依頼時に行う。
  試遊中のDLL交換、元セーブの上書き、無関係なプロセスの停止は行わない。
- `scripts/verify_release.ps1`はゲームを起動するパッケージ/6起動シナリオの検証。READYは性能や全バグの保証ではない。
  [TubeProbeLifecycle](../tests/TubeProbeLifecycle.ps1)等のオフライン試験は実機確認と区別する。
- コードレビューは`codex exec -m gpt-6-astra -c model_reasoning_effort=high --sandbox read-only`。
  pushとWorkshop公開はユーザーの依頼に従う。
