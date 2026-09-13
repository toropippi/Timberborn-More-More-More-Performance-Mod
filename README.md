# More More More Performance! (T3MP)

Timberbornのロードとシミュレーションの重複処理を減らすMOD。
v1.2.1はローカル試遊版。対応対象はWindowsの1.1.2.4／1.0.13.1、Harmonyが必要です。

- 遊ぶ人向けの機能・操作説明：[mod/README.md](mod/README.md)
- 開発を再開する人向け：[引継ぎ](docs/HANDOFF.md)だけを先に読む
- 候補を検討するとき：[採否・再発防止](docs/DECISIONS.md)
- 数値を確かめるとき：[計測台帳](docs/evidence.json)

## ビルド

.NET SDKとゲーム本体が必要です。標準のSteam配置は自動検出します。
別の場所なら、`TIMBERBORN_DIR`に`Timberborn_Data`の親フォルダを指定します。

```powershell
dotnet build src/T3MP/T3MP.csproj -c Release
```

生成物は`src/T3MP/bin/Release/netstandard2.1/Code.dll`。
ゲームを終了してから、`mod/`の配布ファイルとともに
`Documents/Timberborn/Mods/T3MP/`へ配置します。既存版を先にバックアップしてください。
ローカル版とWorkshop版を同時に有効にしないでください。

## 記録・公開

説明は上記2文書へ更新し、作業ごとの現状報告を増やさない方針です。
新しい実験は条件・結果・採否・限界を台帳へ追記し、生ログは`testlogs/`へ保存します。

ゲームDLL・セーブ・生成アセット・秘密情報はコミットしません。
Gitの作者メールにはGitHubの非公開アドレスを使い、ステージ後に確認します。

```powershell
python scripts/check_publication.py
git diff --cached --stat
```
