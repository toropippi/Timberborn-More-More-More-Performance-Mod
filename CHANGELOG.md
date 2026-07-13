# Changelog — More More More Performance! (T3MP)

More performance. Then more. Then, because the name promised it, a little more.

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
