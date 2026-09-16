# 計測・検証コードの分離規約 — 2026-09-16

目的: 調査用コードが製品（`Code.dll`）に混ざる事故を構造で防ぐ。v1.1.6はベンチ用アセットバンドルを同梱してゲーム1.0でクラッシュした
（[採否表](DECISIONS.md)）。同じ形の事故（計測フック・検証コード・実験フックが本番に残る）を、置き場所の規約と機械的なゲートで止める。

## 三層

| 層 | 場所 | 成果物 | 配置 |
|---|---|---|---|
| 製品 | `src/T3MP`, `src/Shared`, `mod/` | `Code.dll` + `manifest.json` + README + thumbnail | Workshop、`Mods\T3MP`（`deploy.ps1`） |
| 計測ドライバ | `src/T3MPTestDriver`, `testmod/` | `T3MPTestDriver.dll` | 計測スクリプトが実行中だけ`Mods\T3MPTestDriver`に置き、終了時に撤去して`testlogs/`へ退避 |
| 実験・試験 | `experiments/`, `tests/`, `benchmark/` | なし（製品ビルドに入らない） | 候補DLLは`measure.ps1 -Candidate`で渡す |

`src/Shared`はドライバにもコンパイルされるが、製品に入る以上は製品の規約に従う。

## 製品に置いてよいもの・いけないもの

置いてよい:

- 機能本体、フィンガープリント・形状ガード・native fallback・`Revalidate()`。
- 機能OFFスイッチだけ。名前は`-t3mpTestNoXxx`または`-t3mpTestXxxBaseline`。読み取りは`ModSettings.HasCommandLineFlag`。
- 状態文字列（`Installed`/`Active`/`Summary()`）。`Summary()`は機能自身の呼出回数など素の加算カウンタだけを返す。
  計時、呼出ごとの比較、他の型の呼出回数計測（hot count）、ログ出力はしない。

置いてはいけない（ドライバへ）:

- 検証・並走比較（validate / replay / mirror / audit）。例: 移動の並走監査は[MovementValidation](../src/T3MPTestDriver/MovementValidation.cs)。
- プローブ、プロファイラ、他コードの回数計測（hot count）、計測用のHarmony prefix/postfix。
- `#if XXX_EXPERIMENT`のフック、`experiments/`の`Compile Include`、実験用ビルドプロパティ。
- テスト用アセット、ドライバへの参照（`InternalsVisibleTo`を含む）、`[T3MPTEST]`ログ。

理由: 製品にある限りスイッチ一つで本番挙動が変わり、レビュー範囲も広がる。計測フックが製品側にあると
fallbackを強制して計測値自体が製品を歪める（TickProfilerがPathFollowerを掴むとMovementSubstepsがnativeへ落ちる例）。

## ドライバの規約

- 製品型へは反射だけ（`AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.X"))`）。
  製品を参照しない。製品不在（Vアーム）でもドライバは動く。
- 製品のprivateを呼ぶときはシグネチャを契約としてコメントに書く（例: `WalkerSpeedDelegates.RunSubsteps`）。
- Harmonyは反射で`t3mp.test.*`ownerを使う。製品のガードはこれを外部パッチとして検知しfallbackするので、
  検証実行は性能実行ではない（`performanceValid=false`、`validationMode`を報告）。
- すべての入口は`-t3mpTest*`フラグなしで何もしない（tick計数の[FullTickCounter](../src/T3MPTestDriver/FullTickCounter.cs)も
  `-t3mpTest*`引数が一つもなければ入れない）。置き忘れがあっても通常プレイに影響しない。
- 実験（`#if ACTIVE_TRANSITION_EXPERIMENT`など）はドライバ側でのみ許す。

## 実験の規約

- 試作は最初から製品品質で`src/T3MP/Runtime/<Name>.cs`に書く。OFFスイッチ`-t3mpTestNo<Name>`、`Installed`/`Active`、
  観測ガードを持つ。ドライバの状態一覧（[FixedTickBenchmark](../src/T3MPTestDriver/FixedTickBenchmark.cs)）と
  `run_fixed_tick_ab.ps1`の期待表に名前を足す。
- 不採用なら`experiments/<name>/`へ移して製品から消す。製品ファイルにフックを残さない
  （2026-09-16にtick-port / tick-dispatch / harvest-tree / harvest-indexのフック28箇所を撤去。復元はgit履歴）。
- 製品が定義してよいシンボルは`MOVEMENT_SUBSTEPS`だけ（`tests/WalkerSpeedDelegates`がフィクスチャで委譲経路だけをコンパイルするため）。

## ゲート

[check_product_purity.ps1](../scripts/check_product_purity.ps1)。`deploy.ps1`、`verify_release.ps1`、`measure.ps1`が呼び、失敗で止まる。
検査: 製品ソースのプリプロセッサシンボル、`-t3mpTest*`の命名、型名（`*Probe|*Profiler|*Benchmark|*Validation|*Replay|*Monitor|*Experiment|*Audit`）、
ドライバ参照、`T3MP.csproj`のCompile/Reference/DefineConstants、`mod/`の内容と版一致、`Code.dll`の文字列ヒープ、配置フォルダの内容。
v1.1.x以来のLoading系スイッチは凍結リストにある（追加禁止、削除のみ）:
`-t3mpTestProductionBlockValidate`、`-t3mpTestNavShapeValidate`、`-t3mpTestTransputRoutingValidate`、経路構築の並列/逐次、
視覚準備・運搬品準備の個別ON。ドライバへ移す作業は6起動シナリオで再検証してから行う。

## 計測は一つのスイッチ

[measure.ps1](../scripts/measure.ps1)（Windows PowerShellからでもpwsh 7で再起動する）:

```
scripts\measure.ps1 -Kind ab -Candidate <Code.dll> -Experiment <Name> [-Save n10c2]   # 導入版B対候補P、BPPB
scripts\measure.ps1 -Kind nomod [-Save n10c2]                                         # MODなしV対導入版B、VBBV
scripts\measure.ps1 -Kind off -Arm D                                                  # 機能OFF比較（D/S/M/E/W/F）
scripts\measure.ps1 -Kind validate [-Exact] [-Candidate <Code.dll>]                   # 移動の並走監査（性能値なし）
scripts\measure.ps1 -Kind tickprofile|hotcounts|subsystem|movement [-Save n10c2]      # ドライバのプロファイラ
```

既定は×50、暖機256 tick、計測2048 tick、条件一致（`-MatchedConditions`）。実行中はPCをアイドルにする（`ab`/`nomod`/`off`/`validate`は
フォーカス変化で無効になる。プロファイラ系は時間窓の実行で条件一致・フォーカス検査を持たないので、数値は傾向としてだけ読む）。
`measure.ps1`はソースに加え、導入済みDLLと候補DLLもゲートに通してから起動する。ゲートに通らない過去の実験バイナリは
`run_fixed_tick_ab.ps1`を直接使い、理由を台帳に書く。結果は`testlogs/`に残り、数値は手で[台帳](evidence.json)に記録する。
`ab`と`nomod`以外は性能値ではない。
