# ADR 0003: 対話グラフの描画方式（SVG＋ELK＋Worker）

- 日付：2026-09-23
- 状態：採用（SD-004の試作で実測。本実装はSD-017）
- 関連：計画書 4.2、8章 SD-004/SD-017、14.3（ロールバック）
- 成果物：`media/graph/`、`media/workers/elkLayout.worker.ts`、`media/styles/graph.css`、`tests/webview/fixtures/graph-prototype.html`

## 背景

v0.0.4の対話グラフはMermaidが生成したDOMを後処理して選択・ズーム・出力を実現している。
この方式ではノード/辺のIDを解析モデルと一致させられず、辺の選択や根拠への移動が
DOM構造に依存する。v0.1.0では「依存を選択→根拠を確認→コードへ移動」を成立させるため、
描画を自前で制御する必要がある。

## 決定

### 描画

- 対話ビューは**SVGの自前描画**とする。Mermaidは共有・コピー用のエクスポート形式として維持する。
- ノードは `g/rect/text` で描画し、コード由来の文字列は `textContent` で投入する。
  `foreignObject` と任意HTMLは使わない。
- 辺は「線」と「太い透明のhit-area」を別要素にする（実測：線1.5pxに対しhit-area 14px）。
- 矢印は `marker` ではなく多角形で描画する。出力SVGでスケールと色を制御でき、
  外部参照が増えない。
- 双方向の辺は、同じ無向ペアの2本目以降を垂直方向へオフセットして重なりを避ける
  （実測：forwardとbackwardで異なるパスになることを確認）。

### レイアウト

- ELK layeredをWorker内で実行する。Workerは単一バンドル（`media/workers/elkLayout.worker.js`）とし、
  WebviewはリソースURIをfetchしてBlob URLから起動する。
- ノード寸法はWebview側で決めてWorkerへ渡す（WorkerはフォントやDOMを持たない）。
- レイアウト要求に `requestId` を付け、古い応答は破棄する。キャンセルはWorkerの終了で行い、
  pendingのPromiseを必ずrejectする（放置しない）。
- 選択・ホバー・テーマ変更では再レイアウトしない（実測：選択後もレイアウト回数は1のまま）。
  スコープ変更時にのみ再レイアウトする。
- 同じ入力・同じ設定なら同じ並びになる（ELKの決定的なレイヤー配置に依存）。

### ELKの読み込み

- `elkjs` は `elk-api.js` と `elk-worker.min.js` を組み合わせ、`workerFactory` で
  **同期実行の "fake worker"** を使う。ネストしたWorkerは2つ目のスクリプトURLを必要とし、
  WebviewのCSP（`worker-src blob:`のみ）と単一バンドル方針に反するため採用しない。
- `elk-worker.min.js` は読み込み時に「実Workerとして登録する」か「Workerクラスをexportする」かを
  環境で判定する。esbuildでこのモジュールにだけ `var document = {};` を前置し、
  Nodeと同じ非Worker経路（export）を選ばせる（`esbuild.js` の `elkFakeWorkerPlugin`）。
  この1行はエンジンの挙動に必要なため、コメントで理由を残している。

### カメラと選択

- ズームはSVG要素の表示サイズと `viewBox`（レイアウト座標）の比で表現する。
  コンテナのスクロールでパンする。transformとviewBoxを併用すると二重に拡大されるため使わない。
- Fit は初回表示と明示操作のみ。選択やInspector開閉で自動Fitしない（実測：選択でcamera不変）。
- ノード/辺は `tabindex` を持ち、Enterで選択・有効化、Escapeで解除する。
  マウス操作に依存しない導線を最初から入れる。

### 出力

- SVGは表示中の射影をそのままシリアライズし、テーマ値をインライン化した `<style>` を付ける。
  `tabindex`/`role`/`aria-*` を除去し、script・イベント属性・外部リンクを含めない
  （実測：出力47.8KB、50ノード・54辺、外部URLは `xmlns` のみ）。
- PNGは同じSVGを `Image` → `canvas` でラスタライズする（実測：PNG署名で始まる34KBの画像）。
- 出力は「表示中の範囲」を保存する。画面外の解析結果を含む全件画像は作らない。

### CSP

- `src/view/html.ts` に `worker-src blob:` と `connect-src ${webview.cspSource}` を追加した。
  外部CDN・`unsafe-eval`・外部connectは許可しない。
- WorkerのURIは `#app` の `data-worker-uri` としてホストが注入する（Webviewはパスを組み立てない）。

## 代替案と却下理由

| 案 | 却下理由 |
|---|---|
| MermaidのDOMを後処理し続ける | ノード/辺IDを解析モデルと一致させられない。選択と根拠移動がDOM構造に依存する |
| React/Blazorへ移行する | v0.1.0の目的は依存理解であり、技術移行を同時に増やさない |
| メインスレッドでELKを実行する | 300ノード級でUIがブロックする。計画書4.1のWorker方針に反する |
| ネストしたWorkerでELKを動かす | 2つ目のスクリプトを配布・CSP許可する必要があり、単一バンドル方針に反する |
| `marker` で矢印を描く | 出力SVGで色・サイズの制御が難しく、`url(#id)` 参照が増える |
| transformとviewBoxの併用でズームする | 二重にスケールされ、Fitとスクロールが破綻する（実装中に実測で確認） |
| 常時forceレイアウト・ミニマップ・手動ピン | v0.1.0の完了条件に含めない（計画書4.2） |

## 帰結

- `media/graph/*.js` と `media/workers/*.js` はビルド生成物。Git管理せず、
  `npm run build` で生成する（`viewer.js` と同じ扱い）。
- Workerバンドルは約2.7MB（ELKのGWTエンジンを含む、開発ビルド）。本番ビルドではminifyされる。
- 試作時点の既知の見た目上の課題：ノード右上のバッジ（推定/生成）が長いラベルと重なる。
  SD-017/SD-024でラベル領域とバッジ領域を分離する。
- 本番UIへの接続（投影の生成、Inspector連携、状態保存）はSD-015/SD-017で行う。
  それまで試作は `tests/webview/fixtures/graph-prototype.html` からのみ動く。
