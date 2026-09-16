# 開発引継ぎ — 2026-09-16

## 現状

2026-09-16: v1.2.3をローカル導入した（Code.dll SHA256先頭`59BA3069`、manifest 1.2.3、`verify_release.ps1` READY 6/6、v1.2.2の退避は`testlogs/release-1.2.3/backup-Code-1.2.2-CAA2.dll`）。
同日、ユーザーの要望でロード進捗のネイティブウィンドウ（見出し・状態行・区間表・注記つきの700×380パネル）を、文字なしのプログレスバー1本
（360×14、ワールドロード中だけゲーム画面下部中央、LoadAll終了で即非表示、メインメニューのロードでは非表示）に置き換えた
（[LoadProgress](../src/T3MP/Loading/LoadProgress.cs)、[NativeLoadProgressWindow](../src/T3MP/Loading/NativeLoadProgressWindow.cs)。
互換性報告のログ監視も削除）。バー版の識別子と起動ゲート結果は台帳`release-1.2.3-20260916`の`progress_bar_only`を正とする。追加点は収穫候補の到達性照会スキップ
（[YielderReachabilitySkip](../src/T3MP/Runtime/YielderReachabilitySkip.cs)、`-t3mpTestNoYielderReachabilitySkip`）。n10c ×50 BPPBで+0.45％（雑音内、台帳`yielder-reachability-skip-20260916`）だが
ユーザー指示（効果の有無によらず実装）で採用。同じビルドで調査用コードの分離を行った：製品から`#if`実験フック28箇所と移動の並走検証を撤去し、
検証は[MovementValidation](../src/T3MPTestDriver/MovementValidation.cs)（ドライバ、反射で製品の`RunSubsteps`を呼ぶ）へ移した。
規約は[DIAGNOSTICS](DIAGNOSTICS.md)、ゲートは`scripts/check_product_purity.ps1`（deploy/verify_release/measureが実行）、計測入口は`scripts/measure.ps1`。
撤去したフックの内容は`experiments/product-hooks-removed-20260916.md`。codexレビューは`testlogs/codex/separation-review-20260916.md`。
導入状態と起動ゲートの記録は台帳`release-1.2.3-20260916`。n10c2（VBBV、`measure.ps1 -Kind nomod -Save n10c2`）はMODなし13.58→20.79 ticks/s（**1.532倍**、台帳`n10c2-baseline-20260916`）。
収穫候補スキップはn10c2で候補経路照会の89％（255万/2304tick）を省いているが、n10cの性能差は雑音内だった（省いた照会はキャッシュ済み照会が大半）。
ビーバー数を増やしたn10c2（`Saves/n10c/n10c2.timber`）のtickプロファイル（`testlogs/tick-profile-n10c2-20260916-014013`、プロファイラ込み11.1 ticks/s）：
WalkerMover 49.5％（プロファイラのfallback込みで過大）、BehaviorManager 25.9％、NeedManager 3.5％、Walker 3.1％、ContaminationApplier 2.9％、RangedEffectSubject 2.5％。
単体では`DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered` 1.2 ms×384回、`DistrictResourceCounter` 1.2 ms/回、`DwellerHomeAssigner` 1.0 ms/tick、
`FindPathUnlimitedRange` 483k回/250tick（866 ms）が目立つ。hot-count/subsystemの実測と各項目の同値最適化可否は台帳`n10c2-hotspots-20260916`にまとめた。残る唯一の見込みある同値候補は需要行動選択`DistrictNeedBehaviorService.PickShortestAction`の下界枝刈り（候補建物ごとの経路照会2回×約1,800回/tickを、現在の最小値を超え得ない候補で省く。ノード吸着のため単純なユークリッド下界は無効で、NavigationServiceの吸着規則から有効な下界を導いてからドライバの並走監査で検証する。見込み2〜4％）。運搬候補の並べ替え（1.3 ms/回）、地区資源集計（1.2 ms/tick）、住居割当（1.0 ms/tick）は同値のままでは1％未満か、キャッシュ/判断変更が必要で対象外。

2026-09-15にv1.2.2をローカル導入した（同日夕方に許可品目の行量置換[AllowedGoodRows](../src/T3MP/Runtime/AllowedGoodRows.cs)を追加して再導入、Code.dll SHA256先頭`CAA2B508`、manifest 1.2.2。
移動変更だけの版は`5AA5DCB0`で、台帳`release-1.2.2-20260915`の測定はその版）。前版v1.2.1（`86AD2537`）は
`testlogs/release-1.2.2/backup-Code-1.2.1-86AD.dll`に退避してあり、戻すときはそのファイルを`Mods\T3MP\Code.dll`へ複製しmanifestの版を1.2.1に戻す。
v1.2.2の追加点は移動の部分ステップ処理（[MovementSubsteps](../src/T3MP/Runtime/MovementSubsteps.cs)）：位置をローカル保持し、コーナー単位で前進する。
座標の下位桁とコーナー到達のtick境界はバニラと一致しない（ユーザーが許容）。切替は`-t3mpTestNoMovementCoarse`（0.1刻みの同値版）と`-t3mpTestNoMovementSubsteps`。
導入版の監査（native並走52万回）は index 1ずれ1回、到着判定差1回、Transform整合性失敗0。台帳`movement-substeps-20260915`。
アイドル機でのv1.2.2実測（台帳`release-1.2.2-20260915`）：×50でMODなし14.74→22.16 ticks/s（**1.503倍**、目標到達だが余裕なし）、
実際のWorkshop 1.1.7は23.22 ticks/s（1.575倍）でv1.2.2はその0.955倍。coarse OFF（`-t3mpTestNoMovementCoarse`）比1.069。
×7 FPSはMODなし6.89→16.02（2.33倍）、1.1.7の16.33に対し0.97。`verify_release.ps1`は5AA5DCB0で6起動シナリオ全てPASS（READY）。AllowedGoodRowsを含む再導入版CAA2B508も6/6 PASS（`testlogs/release-20260915-181340-a9f00a8d`）。
終点線分の0.1刻み一括スキップは監査で最終位置差0.1が出たため不採用（採否表）。
v1.2.2後の追加候補はcodex（gpt-6-astra high）に現行プロファイルと採否表を渡して出させた（`testlogs/codex/ideation-20260915.md`）。
上位でも見込みは各0.4〜1.4％で、第1候補の動力軸アニメ速度倍率メモ化を試作・実測したが+0.55％（`nonlinear-speed-memo-20260915`）で不採用。
判断を変えない範囲の残り候補は同程度の小粒であり、旧版1.1.7との差4.5％を1件で埋めるものは見つかっていない。
tick時間の内訳（v1.2.2、プロファイラのfallback分を除く推定）はBehaviorManager内側の判断処理と経路探索が最大で、次が歩行者ごとの維持系component群。
Unity固有の落とし穴（生存判定・icall・文字列キー・LINQ割当）とアルゴリズム上の無駄の2観点でcodexに網羅走査させ（`testlogs/codex/pitfall-scan-20260915.md`）、
上位候補の内側回数を`-t3mpTestHotCounts`（[HotCountProbe](../src/T3MPTestDriver/HotCountProbe.cs)、`testlogs/hot-counts-v122-*`）で実測した。
1tickあたり：許可品目の線形探索`StorableGoodRegistry.GetAmount` 7,557回（平均20行、GetCapacity/在庫充填率のループ内で二重探索、見込み0.7〜1.0％）、
動力軸`PowerEfficiency` 6,840回（0.45％）、範囲効果`GetEfficiency` 4,037回（0.45％）、`NeedManager.GetNeed` 4,747回（0.4％）。
いずれも1％級で、合計しても2〜3％。移動のように単独で数％になる項目は残っていない。
このうち許可品目の線形探索はユーザーの「1％でも採用」判断で実装・採用した（4ペア合算+1.2％、台帳`allowed-good-rows-20260915`）。
残り（動力軸効率・範囲効果効率・需要検索）は未着手で、各0.4〜0.5％の見込み。
ユーザー指定の3系統（動力網と供給不足、経路探索、効果範囲建物）は[SubsystemProfiler](../src/T3MPTestDriver/SubsystemProfiler.cs)（`-t3mpTestSubsystemProfile`、33クラス267メソッドの包含時間）で実測した（台帳`subsystem-profile-20260915`）。
動力網はイベント駆動で毎tickの再計算なし（効率読取6,840回/tick≈0.6％）。効果範囲建物は範囲の適用が建設/切替時のイベント処理で、毎tickの費用は住民ごとの効果適用（約2.4効果/住民、約3.5％）。
経路探索は二分ヒープのA*で、時間の大半は行動側からのキャッシュ済みフローフィールド照会（約2,000回/tick）と経路角変換（1経路11.5µs、約150回/tick）で合計約7％。
NeedManagerは1住民あたり34需要を毎tick更新（約4〜5％）。いずれも同値を保って2％超を削れる単一項目はない。
その後ユーザーの指示で0.3〜1％級の同値改善4件（需要更新の判定手展開、動力軸効率の1回読み、経路辺の一括走査、需要評価の検索共有）を
1ビルドにまとめて測定したが、4ペア合算0.982で効果なし（台帳`exact-batch-20260915`、試作`experiments/exact-batch`）。
ガード費用が削減分を上回る規模であり、1％級の積み上げ方針は打ち切った。当時（09-15時点）の導入版はCAA2B508（ソースの再ビルドと一致）。現在の導入版は冒頭の2026-09-16の記述を正とする。
ユーザーのアニメーション・表示の異常なし報告は更新前A32版（`visual_baseline`）に対するもの。v1.2.2の表示の全面確認は未実施。
自動監視（×7、`-t3mpTestModelGap`/`-t3mpTestTubeLights`、台帳`release-1.2.2-20260915`の`visual_monitors`）では配管ライト残留0・訪問者参照の不整合0、
モデル乖離はcoarse既定で2マス超205〜307体/約1,300移動体・凍結0〜1で、同一DLLの同値ループ（314〜406体・凍結最大5）や09-11のnative移動より小さい。
試遊での目視確認は引き続きユーザーが行う。
Steam 1.1.2.4を主対象とし、1.0.13.1もサポートする。

速度ボタン1/2/3は×1/×3/×7。人口による減速解除は製品仕様として維持する。
メーターはiSPD＝選択速度、rSPD＝実測進行速度、UPS＝実時間あたりのtick数。
Shift+Oは初期OFF。30fpsを目標に×1〜選択速度でゲーム全体の速度を調整する。
ズームに応じたT3MP独自のカリングはない。

## 次の仕事

目標はn10cで旧T3MPの効果を超え、MODなし比で少なくとも1.5倍の計算処理量を得ること。
バグを再発させないことを優先する。ロード時間・継続的なtick処理・設置/選択UIの応答は別に評価する。

現行DLL・MODなし・実際のWorkshop v1.1.7をn10c/Steam 1.1.2.4で各条件2回、計12回比較済み。
実効×7のFPSは **5.66→13.00（現行）/15.21（旧版）**。現行はMODなしの2.296倍だが、TPSは速度上限。
実効×50の処理量は **14.05→19.32（現行）/21.69（旧版）ticks/s**。
現行は1.375倍、旧版は1.543倍。現行から1.5倍へ約9％、旧版超えへ約12％の上積みが必要。
（以上はv1.2.1の数値。v1.2.2はアイドル機で1.503倍、旧版は同条件で1.575倍。残る差は約4.5％。）
×50は1 FPS未満の処理量測定。実効速度を計測用ドライバーで共通化した結果で、人口減速解除の効果は含まない。
対象DLL・条件・各回の証拠は台帳の`runtime-n10c-threeway-20260913`。固定倍率として一般化しない。
追加のFrontier一致キー検索短縮・移動用delegate再利用・地形探索の重複検索削減を本体に組み込み、導入済み。
現行の主要5機能を一つずつOFFにし、n10cで各速度13回、計26回比較済み。
主力はFrontier。イベントdelegate化と水転送が次の層で、移動delegate再利用と地形探索の寄与は未確定。
×7はFPS、×50はTPSを評価し、細かい順位を強制しない。台帳`runtime-feature-ranking-20260914`を参照。
地形探索は両APIの実アセンブリで各5,000ケース一致、互換性等24シナリオ合格。CoreCLRでの検証でありUnity Mono実機検証ではない。
Frontier内の一致キー検索短縮だけの寄与は未測定。

旧版内部の機能別寄与は`legacy-feature-attribution-20260914`の47回比較を参照。
版間差は追加20回の二因子比較で確認（`version-gap-interaction-20260914`）。n10cで旧版/現行は、
×50で21.81/19.37 ticks/s、通常×7で15.37/13.41 FPS。旧版のflat配信を残してUnity有効状態追跡と追加9群を両方OFFにすると、
18.91 ticks/s・12.98 FPSとなり、現行を下回る。この2群の有無で旧版の優位が逆転する。
旧flat配信の13.4％は旧版内のNoMirror対NoFlatの差であり、Frontierのある現行への追加効果ではない。
各寄与率を足さない。追加9群には判断の再利用・間引きも含み、処理結果の一致や安全な移植は別に検証する。

tick中継処理の展開を実装・検証したが、安定した改善を確認できず、通常ビルドから除外した。
最終試作はn10cで8回比較し、高負荷の平均は19.19→18.96 ticks/s。通常×7も優劣が反転し、外部CPU負荷の増加あり。
この試作の範囲・互換性修正・再開方法は台帳`tick-dispatch-20260914`を参照。

componentの呼び出しをまとめる実装も試作済み。最終試作のn10c/×50比較は19.57→19.43 ticks/s（約0.7％低下）。
途中版の約1.5％増は最終試作で再現せず、隣接比較も反転。通常×7のFPS改善も未確定で、通常ビルドから除外した。
Unityの有効状態照会はnativeのまま。切替前の無効化も試作したが、Unityの自動アニメーション再生では入口を通らず古い値を読む。
20条件の限定試験で確認し、性能比較へ進めず不採用。n10cの表示バグ再現ではない。台帳`active-transition-guard-20260915`。
再現用ドライバーは`-p:ActiveTransitionExperiment=true`でビルドし、`-t3mpTestActiveTransitionAudit`で実行する。通常ビルドから除外。
導入DLLは86AD版を維持。最終試作の識別子・限定的な一致試験・6起動シナリオ・性能結果は台帳`tick-component-dispatch-20260914`。

現行DLLの追加プロファイル（2026-09-15、台帳`movement-substeps-20260915`のprobe）で、n10c・×50のWalkerMoverはほぼ全てが
PathFollower.MoveAlongPathであり、1呼出あたり約52回の0.1単位部分ステップごとにTransform.positionを書いて読み直している。
歩行者Transformは全て親なしで子孫約40、書込み1回約53ns・読出し7.6ns（実機計測）。位置をローカルに保持し、速度provider呼出の直前・
ループ後・例外時だけ書き戻す試作を`experiments/movement-substeps`に実装した（`-p:MovementSubstepsExperiment=true`、通常ビルドから除外）。
native本体を残した再現比較で49万回不一致0。n10c実測は×50で処理量比1.030（BP/PB各1組、隣接比1.024/1.036）、×7でFPS比1.037。
いずれも操作中の機での計測で絶対値は低い（B 16.6 ticks/s）。採用判断はアイドル機での再測定（BPPB）と6起動シナリオ・試遊後に行う。
同値版だけでは目標の1.5倍に届かない（現行1.375倍→約1.42倍相当）。
そこでユーザーの判断（2026-09-15、座標の下位桁一致は諦める）により、同じ試作に`-t3mpTestMovementCoarse`を追加した。
0.1刻みは終点の停止圏判定のためだけにあり、コーナーは経路探索時に確定しているので、線分が停止圏に触れない限りコーナー単位で一気に進む。
停止規則は維持（停止圏判定に0.01マスの丸め余裕）。tick境界ではコーナー到達とprovider呼出が1tickずれ得る（監査52万回で2〜3回、許容済み）。
n10c監査2回で到着判定の反転0、位置差最大0.002マス。×50で有効3組 +16％/+9％/+10％（合算比1.113）、×7 FPS +14％/+13％。
これが1.5倍に届く初めての単独候補だが、バニラ非同値・他MOD互換・試遊は未確認であり、通常ビルドには入れていない。
codexレビュー（P1/P2修正済み）は`testlogs/codex/movement-coarse-review-20260915.md`、測定したDLLは`testlogs/movement-substeps-20260915/`。
同値版の次候補はBehaviorManager内側（WanderRootBehavior.Decide約3％、FindPathUnlimitedRange約4％）だが判断ロジック本体であり未着手。

農場・伐採場の共通セグメント木は2段階の試作を実装・実測。通常版では無効。
採否と.NET/ゲームの測定範囲は台帳の`harvest-segment-index-20260914`、再実行は
[試作の入口](../experiments/harvest-index/README.md)を参照。既存の状態・経路確認は毎回維持する。

1. component配信とUnity状態照会の試作は`experiments/tick-port`、中継展開の旧試作は`experiments/tick-dispatch`。
   製品側のフック（`-p:TickPortExperiment=true`等）は2026-09-16に撤去した（`experiments/product-hooks-removed-20260916.md`）。
   再試作するときは別ブランチで候補DLLを作り`scripts/measure.ps1 -Kind ab -Candidate`で測る。製品にフックを戻さない。
   再試作には同期・ガード費用を含めた純増の根拠が必要。通知だけの追跡をそのまま有効にしない。
   旧版の切替DLL生成は`scripts/LegacyVariants`、実測は`run_fixed_tick_ab.ps1 -ExpectedExperiment LegacyRuntime`。
   現行の機能別比較は`run_fixed_tick_ab.ps1 -RuntimeAttribution -MatchedConditions`。
   FrontierのON/OFFがロード側の適用範囲を変えないよう、全armでPreparedEntityVisualsをOFFにする。
   外部CPU/GPU負荷も確認し、無関係な処理は勝手に停止しない。
   MOD全体の比較は`run_fixed_tick_ab.ps1 -MatchedConditions`で実効速度を揃える。
   Oは実際のWorkshop 1.1.7を指定する。選択速度だけを揃えると人口減速の差が混ざる。
   スクリプトはPowerShell 7で実行する（`%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe`）。
   Windows PowerShell 5.1ではMVID取得とmanifest読込で失敗する。操作中の機では全armのフォーカスが揃わず比較が無効になる。
   通常はすべて`scripts/measure.ps1 -Kind ab|nomod|off|validate|tickprofile|hotcounts|subsystem|movement`から実行する（pwsh 7へ自動再起動、ゲート通過後に実行）。
   試作DLLの比較は`-Kind ab -Candidate <dll> -Experiment <名前>`、移動の同値監査は`-Kind validate`（ドライバ側MovementValidation、性能値なし）。
2. 同じn10c・描画/カメラ・tick数で交互比較し、外部CPU/GPU負荷も記録する。
   細かいプロファイラーは性能測定から外す。速度上限に達した通常速度のTPS比で高速化を判定しない。
3. [採否表](DECISIONS.md)で既存の試作を確認し、根拠のある候補だけ一つずつ評価する。
   新しいデータなしに、効果を確認できなかった試作を再実装しない。

## コードを読む入口

| 調べること | 入口 |
|---|---|
| 導入機能と実行条件 | [ModSettings](../src/T3MP/ModSettings.cs)、[RuntimePatches](../src/T3MP/Runtime/RuntimePatches.cs)、[LoadPatches](../src/T3MP/Loading/LoadPatches.cs) |
| tickの空ループ削減 | [TickFrontier](../src/T3MP/Runtime/TickFrontier.cs) |
| component配信・Unity照会の試作 | [tick-port](../experiments/tick-port/)、[一致試験](../tests/TickPort/) |
| イベント・水・配管 | [EventBusFastDelegates](../src/T3MP/EventBusFastDelegates.cs)、[WaterTextureUpload](../src/T3MP/Runtime/WaterTextureUpload.cs)、[TubeVisitFix](../src/T3MP/Runtime/TubeVisitFix.cs) |
| 速度仕様と表示 | [RequestedSpeedPolicy](../src/T3MP/Runtime/RequestedSpeedPolicy.cs)、[UI](../src/T3MP/UI/) |
| ロード時の互換性 | [LoadCompatibility](../src/Shared/LoadCompatibility.cs)、[LoadEventRouter](../src/T3MP/LoadEventRouter.cs) |
| 移動の部分ステップ試作 | [movement-substeps](../experiments/movement-substeps/)、プローブ [MovementProbe](../src/T3MPTestDriver/MovementProbe.cs)、実行 `scripts/run_movement_probe.ps1` |
| 計測器・実験 | [TestDriver](../src/T3MPTestDriver/)、[experiments](../experiments/) |
| 調査コードの分離規約とゲート | [DIAGNOSTICS](DIAGNOSTICS.md)、[check_product_purity.ps1](../scripts/check_product_purity.ps1)、[measure.ps1](../scripts/measure.ps1) |
| 収穫候補の到達性照会スキップ | [YielderReachabilitySkip](../src/T3MP/Runtime/YielderReachabilitySkip.cs)、レビュー `testlogs/codex/yielder-skip-review-20260916.md` |

実装の詳細はコードを正とする。上表以外の関数一覧や逐次作業ログを読み込む必要はない。

## 守る条件と検証範囲

- tickロジック・更新順・例外・他MODとの契約を維持する。フレームをキーにしたシミュレーションのキャッシュ、
  更新の間引き、モデル位置のスナップ、シミュレーション継続中の描画抑制は導入しない。
- 速度仕様とShift+Oはユーザーが選ぶ速度制御であり、計算量削減として数えない。
- 未知のゲームIL/モジュールや競合パッチでは既存のnative fallbackを維持する。
- 導入版は両APIビルド・追加機能のオフライン試験・指定コードレビュー済み。
  6起動シナリオ、階段、冠水等の自動試験には以前のDLLでの結果が含まれる。最終版の全面合格へ読み替えない。
- 表示の試遊確認はユーザーが行う方針。新しいゲーム起動・自動計測は依頼時に行う。
  試遊中のDLL交換、元セーブの上書き、無関係なプロセスの停止は行わない。
- `scripts/verify_release.ps1`はゲームを起動するパッケージ/6起動シナリオの検証。READYは性能や全バグの保証ではない。
  [TubeProbeLifecycle](../tests/TubeProbeLifecycle.ps1)等のオフライン試験は実機確認と区別する。
- コードレビューは`codex exec -m gpt-6-astra -c model_reasoning_effort=high --sandbox read-only`。
  pushとWorkshop公開はユーザーの依頼に従う。
