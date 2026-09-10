# Changelog — More More More Performance! (T3MP)

More performance. Then more. Then, because the name promised it, a little more.

---

## v1.2.0 — "less, exactly"

**Rebuilt from scratch. Every 1.0–1.1.7 runtime optimization is removed.**

- **Why.** The bugs reported for 1.1.x (beavers stuck in tubes, models jumping,
  water and flood visuals lingering, conflicts with stairs / vertical-navmesh
  mods) all traced to the old runtime systems: frame-keyed caches that spanned
  dozens of simulation ticks, static caches that outlived the world, render
  suppression while the simulation kept running, the population speed-throttle
  removal, model snapping, and whole-method replacements that hid other mods'
  patches. Rather than patch symptoms again, v1.2 deletes all of it.
- **What remains at runtime (all behavior-exact, reviewed on 1.0.13.1 and
  1.1.2.0/1.1.2.4, each guarded by a raw-IL fingerprint of the vanilla method
  and by a foreign-patch check; on mismatch it stays vanilla):**
  - `EventBus.RegisterMethod`: typed delegates instead of `MethodInfo.Invoke`
    per delivery. Same handlers, order and exception wrapping.
  - `TickableEntity.Tick`: index traversal of the component array; an alive
    entity whose tickable components are all disabled returns before the
    native `activeInHierarchy` read that vanilla would follow with an empty
    loop. Every `Enabled` flag is read live at the visit; nothing is cached.
  - `DataTextureArray<T>.UpdateTextureArrays`: a GPU upload whose bytes equal
    the bytes last submitted to that texture layer is skipped (native
    memcmp). Direct3D11 only; the simulation and data arrays are untouched.
  - `TickableEntityBucket.TickAll` (Frontier, ported from the Harmony-free
    T4MP prototype): each entity carries a count of its enabled tickable
    components, maintained by hooks on `BaseComponent.EnableComponent` /
    `DisableComponent` (the only writers of `Enabled`) and marked on
    `ComponentCache.OnDestroy` / re-`Initialize`. The bucket sweep jumps to the
    next entity that still needs a visit; an alive entity with no enabled
    tickable component is exactly the case where vanilla only reads
    `activeInHierarchy` and runs an empty loop. Unknown or untracked state is
    always visited; the vanilla `SortedList` remains the authority. Four Codex
    review passes; the last found no defect.
- **Measured** (`scripts/run_runtime_ab.ps1`, x50 = effective x20.6, 150 s,
  20 s windows, first dropped). m7b save, game 1.0.13.1: vanilla 17.83/16.99,
  v1.2 without Frontier 21.98/22.50, **v1.2 27.66/26.57 ticks/s (1.56x)**; the
  Harmony-free T4MP prototype 28.33/27.15 (1.59x). n10c save, game 1.1.2.0:
  without Frontier 13.89/13.55, **v1.2 16.86/15.27**, against 12.64 with the
  runtime patches off on 1.1.2.4 (**about 1.27x**). The Frontier skips about
  90% of entity visits on both saves; the gain depends on the save, not on the
  game version. **No fixed speedup figure is claimed.**
- **Save loading kept.** The load optimizations (event routing, construction
  plans, prepared visuals, navigation and terrain load paths) are unchanged:
  about 29 s instead of about 65 s scene load on the same 1.1.2.4 save
  (`docs/load-steam-1124-2026-09-10.md`).
- **Removed features:** Shift+P turbo, Shift+O smooth mode, the bottom-right
  speed meter, hidden speed-throttle removal, all probes and profilers.
- **Test-only flags** (never needed in play): `-t3mpTestRuntimeBaseline`,
  `-t3mpTestNoEvents`, `-t3mpTestNoTick`, `-t3mpTestNoWater`.

### 日本語

**ゼロから作り直し。1.0〜1.1.7の本編最適化はすべて撤去しました。**

- **理由。** 1.1.x系で報告された不具合（チューブ内の固着、モデルの跳躍、水・
  浸水表示の残留、階段・立体ナビ系MODとの競合）は、旧ランタイムの仕組み——
  数十tick分をまたぐフレーム基準キャッシュ、ワールドより長生きする静的キャッシュ、
  シミュレーションを進めたままの描画停止、人口スロットラーの撤廃、モデルの
  スナップ、他MODのパッチを隠すメソッド丸ごと差し替え——に行き着きました。
  症状の上書きを重ねる代わりに、v1.2は全部削除します。
- **残したもの（すべて結果を変えない変更。1.0.13.1と1.1.2.0/1.1.2.4で審査し、
  バニラ側メソッドの生IL指紋と他MODパッチの検出で守り、不一致ならバニラのまま）:**
  - `EventBus.RegisterMethod`: 配信ごとの `MethodInfo.Invoke` を型付き
    delegateに。ハンドラ・順序・例外の包み方は同じ。
  - `TickableEntity.Tick`: 部品配列を添字で走査。有効な部品が1つもない生存
    エンティティは、バニラなら空ループの前に行うネイティブの
    `activeInHierarchy` 読み取りを省いて戻る。`Enabled` は毎回その場で読み、
    キャッシュしない。
  - `DataTextureArray<T>.UpdateTextureArrays`: 前回そのテクスチャ層へ送った
    byteと一致するGPU転送を省く（ネイティブmemcmp）。Direct3D11限定。
    シミュレーションとデータ配列には触れない。
  - `TickableEntityBucket.TickAll`（Frontier。Harmony不使用のT4MP試作からの移植）：
    各エンティティが「有効なtick部品の数」を持ち、`Enabled` の唯一の書き手である
    `BaseComponent.EnableComponent`／`DisableComponent` のフックで更新、
    `ComponentCache.OnDestroy`／再`Initialize` で印を付ける。バケット走査は
    次に訪問が必要なエンティティへ飛ぶ。有効なtick部品が1つもない生存エンティティは、
    バニラが `activeInHierarchy` を読んで空ループを回すだけの場合そのもの。
    不明・未追跡の状態は必ず訪問し、バニラの `SortedList` が主で索引は従。
    Codexレビュー4回、最終回は指摘なし。
- **実測**（`scripts/run_runtime_ab.ps1`、x50＝実効x20.6、150秒、20秒窓、最初の窓は
  除外）。m7bセーブ、ゲーム1.0.13.1：バニラ 17.83/16.99、Frontierなしのv1.2
  21.98/22.50、**v1.2 27.66/26.57 ticks/s（1.56倍）**、Harmony不使用のT4MP試作
  28.33/27.15（1.59倍）。n10cセーブ、ゲーム1.1.2.0：Frontierなし 13.89/13.55、
  **v1.2 16.86/15.27**、1.1.2.4でランタイム無効の12.64に対して**約1.27倍**。
  Frontierはどちらのセーブでもエンティティ訪問の約90%を省きますが、効き方は
  セーブで決まり、ゲーム版では決まりません。**固定の倍率は主張しません。**
- **ロード高速化は継続。** ロード側の最適化（イベント経路、生成プラン、
  モデル事前準備、経路網・地形のロード経路）は変更なし。同一の1.1.2.4セーブで
  シーンロード約65秒→約29秒（`docs/load-steam-1124-2026-09-10.md`）。
- **撤去した機能:** Shift+Pターボ、Shift+Oスムーズモード、右下の速度メーター、
  速度スロットラー撤廃、各種プローブとプロファイラ。
- **テスト専用フラグ**（通常プレイでは不要）: `-t3mpTestRuntimeBaseline`、
  `-t3mpTestNoEvents`、`-t3mpTestNoTick`、`-t3mpTestNoWater`。

---

## v1.1.7 — "less baggage, more compatibility"

**Fixed a crash on game version 1.0 caused by a stray benchmark file.**

- **No more "Failed to load asset bundle t3mp-bot-instancing" crash.** v1.1.6
  accidentally shipped a development-only asset bundle (part of an unreleased
  GPU-instancing experiment) inside the mod package. The bundle was built with
  the Unity version used by Timberborn 1.1, so on game v1.0 the game's own mod
  loader failed to load it and crashed before reaching the main menu. The
  bundle is no longer part of the released mod; it now lives only in the
  development repository for local benchmarks.
- **No gameplay or performance change.** The bundle was never used by any
  shipped feature — removing it changes nothing about how the mod runs.

**Fixed: characters visually stuck inside tubes at very high speed.**

- At the mod's uncapped speeds (far beyond vanilla's x3), the visual model
  playback could no longer keep up with the simulation — especially at low
  fps. The lagging model position drives tube visuals, so the tube travel
  glow (with the character inside) stayed parked in the tube long after the
  simulation had finished the transit, looking like beavers stuck in the
  pipes. The mod now snaps a character's visual model forward whenever its
  animation falls more than ~2 tiles behind its real position — the same
  resync turbo mode already used, applied continuously. Vanilla-speed
  visuals are unaffected (the lag threshold is never reached below high
  fast-forward), and simulation results are unchanged as always.

### 日本語

**ゲーム本体 v1.0 でのクラッシュを修正しました。**

- **「Failed to load asset bundle t3mp-bot-instancing」クラッシュの修正。**
  v1.1.6 に、未公開の GPU インスタンシング実験用の開発専用アセットバンドルが
  誤って同梱されていました。このバンドルは Timberborn 1.1 の Unity で
  ビルドされているため、v1.0 ではゲーム側のMODローダーが読み込みに失敗し、
  メインメニューに到達する前にクラッシュしていました。バンドルを配布物から
  除外しました（開発リポジトリ内のベンチマーク専用に移動）。
- **ゲームプレイ・性能への影響はありません。** このバンドルは公開機能では
  一切使われていないため、除外しても動作は変わりません。

**修正：超高倍速でビーバーがチューブ内に固着して見えるバグ。**

- 本MODの上限解放速度（バニラ最大 x3 を大きく超える領域）では、見た目の
  モデル再生がシミュレーションに追いつけなくなります（特に低fps時）。
  チューブの通過表示は「モデルの位置」を参照するため、シミュレーション上は
  とっくに通過済みでも、青い通過光（とその中のビーバー）がチューブ内に
  残り続け、詰まっているように見えていました。修正後は、モデルの見た目が
  実位置から約2マス以上遅れた時点で即座に実位置へスナップします
  （ターボモードが以前から使っている再同期処理を常時適用する形）。
  バニラ速度帯では遅延がしきい値に達しないため見た目への影響はなく、
  シミュレーション結果も従来どおり完全に同一です。

---

## v1.1.6 — "more typing, fewer surprises"

**Hotkeys now respect text input, just like vanilla.**

- **No accidental mode changes while typing in chat.** Shift+P and Shift+O now
  use Timberborn's own input-blocked state before handling the mod's raw
  keyboard shortcuts. Typing those letters in chat or another focused text
  field no longer toggles turbo or smooth mode.
- **Vanilla-consistent input behavior.** The mod follows the same
  `InputBlocker` state that suppresses the base game's gameplay shortcuts, so
  normal gameplay hotkeys still work immediately after text input closes.

### 日本語

**文字入力中のホットキー動作を、バニラと同じにしました。**

- **チャット入力中にモードが誤作動しません。** Shift+P / Shift+O の判定前に、
  Timberborn 本体が使う入力ブロック状態を確認するよう変更しました。チャットや
  フォーカス中のテキスト欄で P / O を入力しても、ターボ／スムーズモードは
  切り替わりません。
- **入力欄を閉じれば通常どおり。** バニラのゲーム操作キーと同じ
  `InputBlocker` に従うため、文字入力終了後は直ちにホットキーが再び使えます。

---

## v1.1.5 — "more water, less work"

**More water objects, less per-tick work. Same vanilla result.**

- **Faster water-object updates on big, watery maps.** Every tick the game
  re-checks every water object (floodable buildings and more) to see if the
  water above it changed — ~10k-17k of them on a large late-game map. This adds
  a fast path that caches each object's map cell once (its coordinates never
  move) and then reads the live water column directly each tick, skipping the
  repeated bounds/index/wrapper overhead vanilla pays per object. Measured
  **~35% less time** on that step (e.g. ~1.6 ms → ~1.05 ms per tick with ~10.6k
  objects).
- **Exactly vanilla.** The water column is re-read from the live state every
  tick (no cached lookup that could go stale as water rises or drains), and the
  actual flooded/unflooded change + event is still performed by the game's own
  WaterObject.UpdateWaterAboveBase(), only when the value changed. It replaces
  the v1.1.4-removed skip that used the wrong signal. A tight full-vanilla safety
  pass runs periodically as belt-and-suspenders.
- **Still zero gameplay changes.** Only *when* the check is done cheaply — what
  floods, and when, is identical to vanilla.

### 日本語

**水オブジェクトは増えても、1tickの手間は減る。結果はバニラ同一。**

- **広大で水の多いマップで、水オブジェクト更新が高速に。** ゲームは毎tick、全ての
  水オブジェクト（浸水しうる建物など。大規模終盤マップで約1〜1.7万個）の水位変化を
  再チェックします。この処理に高速パスを追加：各オブジェクトのマップセルを一度だけ
  キャッシュし（座標は動きません）、以降は毎tick現在の水柱を直接読むことで、バニラが
  オブジェクトごとに払う境界判定・インデックス計算・ラッパ生成の重複を省きます。
  **約35%短縮**（例：約1.6ms→約1.05ms/tick、水オブジェクト約1.06万個時）。
- **バニラ完全一致。** 水柱は毎tick現在の状態から読み直すため（水位上昇・排水で
  古くなる索引キャッシュは持ちません）、浸水の発生も復帰も取りこぼしません。実際の
  浸水／復帰処理とイベントは、値が変わったときだけゲーム本体の
  WaterObject.UpdateWaterAboveBase() が行います。v1.1.4 で撤去した「誤った信号を使う
  スキップ」の正しい置き換えです。保険として短い間隔でフルバニラ一巡も回します。
- **ゲーム内容の変更はゼロ。** 変えたのはチェックを安くする*やり方*だけ。何がいつ
  浸水するかはバニラと同一です。

---

## v1.1.4 — "more drainage"

**More drainage. More vanilla. Zero more stuck-flooded buildings.**

- **Buildings recover from flooding again.** A building that was temporarily
  submerged could stay stuck in the "flooded" state forever after the water
  receded. The mod skipped the per-tick water-object update whenever no column
  *structurally* changed — but a building's flooded state depends on water
  *depth*, which keeps changing as water flows and drains. So a flow-driven
  recede was never noticed. Reverted that skip to vanilla behavior, which
  re-checks every water object every tick.
- **More faithful.** Flooding and un-flooding now track the water exactly like
  vanilla, with no dependence on a signal that didn't mean what it looked like.

### 日本語

**もっと水はけ。もっとバニラに。浸水したまま固まる建物をゼロに。**

- **建物が浸水から復帰するようになりました。** 一時的に水没した建物が、水が引いた
  あとも「浸水」状態のまま永久に固まることがありました。MODは「カラムの*構造*変化が
  無い tick」の水オブジェクト更新をスキップしていましたが、建物の浸水判定は*水深*で
  決まり、水深は流れ・排水で変わり続けます。そのため流れによる水位低下が検知されません
  でした。このスキップをバニラ挙動（毎tick全水オブジェクトを再チェック）に戻しました。
- **もっと忠実に。** 浸水・復帰が、見かけと意味の違う信号に依存せず、バニラと完全に
  同じく水位を追うようになりました。

---

## v1.1.3 — "more reachable"

**More reachable. More vanilla. Zero more stuck workers.**

- **Workers no longer ignore reachable resources after a route change.** If you
  rerouted a path so a fruiting tree or crop became reachable (or moved it out of
  reach), gatherers, farmhouses and lumberjacks could keep using the *old* road
  layout — walking past a ripe tree they should harvest. Fixed: the reachability
  the mod caches is now rebuilt the moment the paths change, exactly like vanilla
  recomputes it every time.
- **More faithful.** This closes a case where the mod's cached pathing could drift
  from vanilla after you edited roads. Same speed, same results — now including
  right after you change a route.

### 日本語

**もっと到達可能に。もっとバニラに。詰まる作業員をゼロに。**

- **ルート変更後、到達できる資源を作業員が無視しなくなりました。** 道を引き直して
  実った木や作物が新たに到達可能になった（または逆に届かなくなった）とき、採集者・
  農家・木こりが*古い*道の状態を使い続け、採るべき実った木の前を素通りすることが
  ありました。修正：MODがキャッシュしている到達可能性を、道が変わった瞬間に
  再構築するようにしました（バニラが毎回再計算するのと同じ挙動）。
- **もっと忠実に。** 道を編集した後にMODのキャッシュ経路がバニラからずれ得た事象を
  解消。速度も結果もそのまま——ルート変更直後も含めてバニラと一致します。

---

## v1.1.2 — "more steady"

**More steady. More live. Zero more flicker.**

- **No more blinking route lines.** While placing or connecting a road, the green
  path-range overlay used to flicker on and off — the route light strobing roughly
  every other frame. Fixed: it now stays solid the whole time you are placing.
- **More live overlay.** The green range now tracks your cursor in real time while
  you drag a road, instead of lagging a fraction of a second behind. What you are
  about to connect is shown *now*, not after it catches up.
- **Less code.** The change removed a preview-refresh shortcut that turned out to
  be the source of the flicker, plus a superseded rebuild-throttle path that no
  longer ran. Same speed, fewer moving parts.
- **Still zero more gameplay changes.** Visual fix only — the simulation result is
  *exactly* vanilla, as always.

### 日本語

**もっと安定。もっとライブ。チラつきゼロ。**

- **道のルートラインが点滅しなくなりました。** 道を設置・接続している最中、緑の
  到達範囲オーバーレイが1フレームおきくらいに点いたり消えたりしていました。修正済み：
  設置中はずっと安定して表示されます。
- **もっとライブなオーバーレイ。** 道をドラッグしている間、緑の範囲がコンマ数秒
  遅れて追従するのではなく、カーソルにリアルタイムで追従するようになりました。
  これから何が繋がるかが「今」見えます。
- **コードも削減。** 点滅の原因だったプレビュー更新のショートカットと、すでに
  使われていない旧リビルド抑制の分岐を削除しました。速度はそのまま、部品は少なく。
- **ゲーム内容の変更はゼロのまま。** 見た目だけの修正で、シミュレーション結果は
  いつもどおりバニラと完全に同一です。

---

## v1.1.1 — "even more"

**More instant. More smooth. More more.**

- **More instant highlights.** Selecting a gear or a mechanical / power network now
  lights up *instantly* — no more waiting, no more highlights drifting in from far
  away like they lost the map. Touch a piece and the whole network is right there.
  More responsive, more obvious, more done. (While drag-placing, the highlight now
  ripples *outward* from the piece in your hand — the more natural direction.)
- **More smooth at high speed — Shift+O.** New, now-official mode: press **Shift+O**
  and the game holds a steady, smooth ~30 fps while the simulation runs as fast as
  it possibly can underneath — up to the speed button you pressed, never below x1.
  More of your colony stays smooth while it flies, so you get more speed *and* more
  smoothness at the same time. The mod manages the frame cap for you: no more
  fiddling with vsync or settings. A short note pops up when you toggle it, and the
  bottom-right meter tags **iSPD** with **"(auto)"** while it is doing its thing.
- **Still zero more gameplay changes.** More speed, more smoothness — but the
  simulation result is *exactly* vanilla. Not one tick more, not one tick less.

### 日本語

**もっと即時。もっとなめらか。もっとmore。**

- **もっと即時なハイライト。** 歯車や動力ネットワークを選択したときのハイライトが
  *即座に*点くようになりました。もう待たされないし、遠くからこっちに寄ってくる
  バグっぽい表示ともお別れ。触った瞬間、ネットワーク全体がそこに。もっと軽快、
  もっと分かりやすく。（設置ドラッグ中は、掴んでいる建物から*外側へ*波及する
  自然な向きになりました。）
- **もっとなめらかな高速 — Shift+O。** 正式機能になった新モード。**Shift+O** を押すと、
  なめらかな約30fpsを保ったまま、その裏でシミュレーションを可能な限り速く自動で
  回します（上限＝押した速度ボタン、下限x1）。飛ばしてもコロニーがもっとなめらか、
  つまり「速さ」と「なめらかさ」を同時にmore。フレーム上限はMODが自動管理するので、
  vsyncも設定もいじる必要はもうありません。切り替え時に数秒だけ案内が出て、作動中は
  右下メーターの **iSPD** に **「(auto)」** が付きます。
- **ゲーム内容の変更はゼロのまま。** もっと速く、もっとなめらかに——でも
  シミュレーション結果はバニラと*完全に同一*。1ティックたりとも増えも減りもしません。

---

## v1.1.0 — the "more" that started it

The core that makes everything above possible.

- **More speed, always on.** The simulation itself runs up to about 1.5x faster on a
  large late-game colony — same turns, same order, same results, just far less
  bookkeeping per step. Install, load your save, done.
- **More turbo — Shift+P.** Skips animations while you fast-forward for up to about
  2.4x. Press again (or open the Esc menu) to return to normal.
- **More of the speed you paid for.** Removed vanilla's hidden speed-button throttle:
  on a big colony the fastest button no longer runs at less than half its real
  speed — the speed you press is the speed you get.
