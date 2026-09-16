# 農場・伐採場の共通セグメント木試作

通常ビルドでは含めない。採否・実測値は [evidence.json](../../docs/evidence.json) の
`harvest-segment-index-20260914` を参照する。

- `HarvestIndex`：職場の受取Inventoryごとに、品目別の最短候補を保持する木を再利用。
  経路・生存・収穫可能状態・列挙は毎回nativeで実行し、現在の入力で木を同期する。
  数・品目の並びが変われば再構築し、距離だけの変更は点更新する。フレームキャッシュではない。
- [HarvestCandidateTree](../harvest-tree/)：比較用の小さい試作。品目別候補の集約後、
  最終選択のSortedSetだけを木に置き換える。上の索引と同時に有効化しない。

ゲームの場所は通常の自動検出、または `TIMBERBORN_DIR` を使う。
以下は索引版。最終選択版は `HarvestIndexExperiment` → `HarvestTreeExperiment`、
`HarvestIndex` → `HarvestCandidateTree` とし、出力先も分ける。

```powershell
dotnet build src/T3MP -c Release -p:HarvestIndexExperiment=true -o testlogs/harvest-index/trial
# 独立した照合。診断ログを性能値として扱わない。
./scripts/run_fixed_tick_ab.ps1 -Order P -Speed 50 -WarmupTicks 64 -MeasuredTicks 512 -MatchedConditions -ComparisonDll testlogs/harvest-index/trial/Code.dll -ExpectedExperiment HarvestIndex -HarvestTreeValidation
# 同じn10c・描画条件で交互比較。各組の間で元DLL・設定が復元される。
./scripts/run_fixed_tick_ab.ps1 -Order BPPB -Speed 7 -WarmupTicks 64 -MeasuredTicks 512 -MatchedConditions -ComparisonDll testlogs/harvest-index/trial/Code.dll -ExpectedExperiment HarvestIndex
./scripts/run_fixed_tick_ab.ps1 -Order PBBP -Speed 50 -WarmupTicks 256 -MeasuredTicks 2048 -MatchedConditions -ComparisonDll testlogs/harvest-index/trial/Code.dll -ExpectedExperiment HarvestIndex
```

`tests/HarvestIndex` は20,000組の連続変更を検査する。引数に照合ログを渡すと、
そこから採った実入力の順序を保って.NET 8で再生・計時する。
`DOTNET_TieredCompilation=0` で計時し、Unity Monoやゲーム全体の倍率へ流用しない。
`tests/HarvestCandidateTree --replay <Managed> <照合ログ> [試作DLL]` は最終選択版の
native比較器・容量判定/例外を検査する。同じハーネスの
`--index-cases <Managed> <索引DLL>` は索引版の職場分離・入れ子・例外復帰を検査し、
`-t3mpTestHarvestIndexValidate` を付けると照合モードの入れ子も検査する。
生ログ・ゲームDLL・セーブ・ビルド出力はtestlogsに置き、Gitには含めない。
