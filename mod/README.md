# More More More Performance! (T3MP) v1.2

**v1.2 is a rebuild from scratch.** Every optimization from 1.0–1.1.7 that could
change behavior or visuals has been removed. What remains is behavior-exact:
the simulation produces the same colony as vanilla, tick for tick.

## What it does

- **Faster save loading.** A large late-game save (28,000 entities) loads in
  about 29 seconds instead of about 65 seconds on the same PC (game 1.1.2.4,
  measured with the game's own `Load time` line). Entity construction,
  initialization, event delivery and navigation graph building during load are
  done with fewer repeated lookups; every step verifies the game build it was
  reviewed on and otherwise stays vanilla.
- **A lighter simulation loop.** Four exact changes: event handlers are
  called through typed delegates instead of reflection, entity ticks walk their
  component arrays by index, the tick sweep skips entities whose tickable
  components are all disabled (vanilla would only read a flag and run an empty
  loop for them; about 90% of visits on large colonies), and water texture
  uploads whose bytes did not change are not re-sent to the GPU. Measured gain
  depends on the save: about 1.56x on one large colony, about 1.27x on another
  (same on game 1.0 and 1.1). **Do not expect a fixed figure.**
- **Tube lights after building entry.** Works around a vanilla visual bug that
  leaves a character registered in its last tube after entering a building.
  Clears that stale visit so lighting reflects the remaining visitors, while
  keeping the building occupant hidden and simulation behavior unchanged.

## What is gone (and why)

Shift+P turbo, Shift+O smooth mode, the hidden speed-throttle removal, all
frame-based caches, animation snapping and render suppression. The bugs
reported for 1.1.x — beavers stuck in tubes, models jumping, water and flood
visuals lingering, conflicts with navigation mods — all traced back to those
systems. Removing them is the fix.

## Compatibility

Game 1.0.13.1 and 1.1.2.x, Windows, Direct3D11. Requires the Harmony mod. If a
game update changes one of the patched methods, that optimization detects the
mismatch and stays vanilla; the mod never guesses.

---

# 日本語

**v1.2はゼロからの作り直しです。** 1.0〜1.1.7で挙動や見た目を変え得た最適化は
すべて撤去しました。残っているのは結果を変えない変更だけで、シミュレーションは
tick単位でバニラと同じ集落になります。

## できること

- **セーブのロード高速化。** 28,000エンティティの大規模セーブが同一PCで
  約65秒→約29秒（ゲーム1.1.2.4、ゲーム自身の `Load time` 行で計測）。
  ロード中のエンティティ生成・初期化・イベント配信・経路網構築の重複処理を
  減らします。各処理は審査済みのゲームビルドかを確認し、違えばバニラのままです。
- **本編ループの軽量化。** 結果を変えない4点：イベントハンドラをリフレクション
  でなく型付きdelegateで呼ぶ、エンティティのtickで部品配列を添字で走査する、
  tick部品がすべて無効なエンティティ（バニラはフラグを読んで空ループを回すだけ。
  大規模集落で訪問の約90%）をtick走査で飛ばす、byteが変わらない水テクスチャを
  GPUへ再送しない。効き方はセーブ次第で、ある大規模集落では約1.56倍、別の集落では
  約1.27倍でした（ゲーム1.0でも1.1でも同じ）。**固定の倍率は期待しないでください。**

- **建物に入った後のチューブ照明。** 建物に入ったキャラクターが直前のチューブに
  登録されたままになるバニラの表示バグを回避します。古い訪問登録を解除して照明を
  残りの訪問者に合わせます。建物内のモデルは非表示のまま、シミュレーションは変わりません。

## 撤去したもの（理由）

Shift+Pターボ、Shift+Oスムーズモード、速度スロットラー撤廃、フレーム基準の
キャッシュ全部、モデルのスナップ、描画の停止。1.1.x系で報告されたチューブ内の
固着、モデルの跳躍、水・浸水表示の残留、ナビゲーション系MODとの競合は、
いずれもこれらが原因でした。撤去そのものが修正です。

## 互換性

ゲーム1.0.13.1と1.1.2.x、Windows、Direct3D11。Harmony必須。ゲーム更新で対象
メソッドが変わった場合、その最適化は不一致を検出してバニラのままになります。
