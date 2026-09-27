# v0.1.0 レビュー対応記録

レビュー対象は基準コミット `22a2d751ff54c6db26f3032c287e22ef4d9a446d` から `7389c485bf449ca9b1b3fddcd5d1babbf8e60e37` までの実装全体。2026-09-27 のレビューで指摘した実装上の問題を修正した。以下は修正後の状態であり、個別タスクに残る以前の検証記録を更新する。

| 指摘 | 対応と確認 |
|---|---|
| Semantic が起動せず、配布物にも含まれない | SDK の解決、SemanticHost の起動、profile の受け渡し、結果登録を接続。VSIX に本体と BuildHost を同梱し、インストールした拡張から実行した |
| Quick の初期表示が空になる | 初期粒度を project にし、Quick では型表示を無効化。実機で非空の表示を確認した |
| 状態保存のエラーと再描画の繰り返し | persistViewState をパネルで処理し、同じ状態の再送を抑制。serializer を登録し、非表示からの再表示と workspace storage への保存を確認した |
| モード、プロファイル、停止操作が未接続 | mode、Configuration、Platform、プロジェクト別 TFM を接続。開始・進捗・失敗・停止を通知し、停止後は前の結果を保持する |
| 循環表示の ID が検証で拒否される | cycle scope では cyc ID を受け付ける。循環の粒度へ切り替え、明示選択した循環のメンバーと辺は表示予算で欠落させない |
| 出力が type 固定で、表示条件を無視する | Graph/Table と同じ粒度・scope・検索・filters・一時表示 ID から出力を作る。コマンドのコピーも現在の Webview 状態を取得する |
| SVG／PNG のコマンドが通知だけになる | Webview に実出力を要求し、ELK の完了を待って保存する。ブラウザで SVG と PNG の生成を確認した |
| 同名 csproj が Quick で混同される | プロジェクト名ではなく正規化したフルパスを結合キーに変更。同名 Common.csproj と相互に異なる名前空間で回帰確認した |
| file-local 型の ID が衝突する | file-local 型とその入れ子の識別に宣言ファイルを含める。同名型が別ファイルに存在するケースを追加した |
| platform 付き TFM が縮退する | 評価済み出力パスと名前から完全な TFM を優先取得。net10.0 と net10.0-windows の共存と明示選択を確認した |
| 初期化子と型引数の根拠が欠落する | Operation の root、プロパティ初期化子、完全修飾ジェネリック、式の型引数、明示ローカル型、パターン型を採取する |
| 循環の witness と内部関係が不完全 | Type／Namespace／Project ごとに SCC を計算し、実在する経路の relation ID と全内部関係を保持する |
| ロード失敗でも complete になる | loadFailed などを完全性判定へ反映。failed のレポートは登録を拒否し、前の正常な結果を残す |
| 異なる basis の辺を一つに集約する | 集約キーに basis を含める。集約元の各関係から根拠を開けるようにした |
| 入れ子型から架空の名前空間が作られる | シンボルの NamespaceId を使用し、型の完全名を分割しない |
| 古い非同期処理が新しい結果を上書きする | 読み込み後の登録直前にも世代とキャンセルを確認。Webview でも analysisId／requestId と選択を照合する |
| 連続失敗で正常な結果の根拠が消える | 最近の実行に加え、保持中の正常な結果のディレクトリを残す。連続失敗と登録中のキャンセルを回帰確認した |
| 失敗した新しい対象のパスで古いコードを開く | 登録した analysis ごとに対象パスを保存し、開く位置と stale 判定に使用する |
| 検索結果の Show が表示範囲を変更しない | 一時表示 ID を projection request に含め、表示予算とフィルターの外にある検索結果を追加できるようにした |
| 階層の所属と表の件数が誤る | project ID／namespace ID と所属メタデータで構築。依存先数と被依存元数を全体の関係から集計する |
| 表示上限が効かず、一方で探索を打ち切る | maxProjects／maxVisibleTypes／maxEdges を projection に適用。Quick の探索には表示設定とは別の安全上限を使用し、到達時は理由を記録する |
| 外部変更や設定変更で stale にならない | ファイルの作成・更新・削除、linked source、親ディレクトリの MSBuild／SDK 設定も監視する |
| フィルター、深さ、履歴、宣言選択が未接続 | 条件を実 projection に反映し、依存／被依存、depth 1–3、ドリルダウン、Back、partial 宣言の選択を接続した |
| CI のテスト省略と古い E2E レポートによる誤成功 | ホストを先に publish し、C#、TS、ブラウザ、開発版／インストール版 E2E を必須化。実行開始時に前の E2E レポートを除去し、終了コードと生成時刻を確認する |
| 完了記録が実装や検証範囲と一致しない | status.json と SD-027〜SD-030 の記録を更新。ローカルの修正完了とリリース判定の未確認項目を分けた |

検証は Windows のローカル環境で実施した。TypeScript 221 件、C# 103 件、ブラウザ 3 ケース、開発版 VS Code E2E 17 項目、インストールした VSIX の E2E 18 項目を使用する。ブラウザケースは 1440／900／360 px と各テーマクラスで、実際のレイアウト worker、SVG／PNG 出力、操作可能性を確認する。VSIX は ZIP 内の必須ファイル、開発ファイルの混入、SHA-256 を記録する。

Semantic の golden 更新は、入れ子型から生じた架空の名前空間の除去と、初期化子・明示ローカル型などの欠落していた根拠の追加による。既存の期待値をそのまま緩めず、原因別の回帰テストと合わせて更新した。

証跡は [開発版 E2E](evidence/sd-027-e2e.json)、[インストール版 E2E](evidence/sd-027-e2e-vsix.json)、[VSIX 内容](evidence/sd-029-vsix.json) にある。再実行手順は `npm ci` → `npm run build:analyzer` → `npm test`、`dotnet test analyzer/SharpDeps.Analyzer.slnx -c Release`、`npm run build` → `npm run test:browser`、`npm run check:vsix` → `npm run test:e2e`。インストール版は環境変数 `SHARPDEPTS_E2E_MODE=vsix` を指定する。

Linux／macOS、SDK のない独立環境、未信頼ワークスペースの実機操作、ウィンドウ再読込、実際の VS Code High Contrast／200% 表示、大規模実ソリューションの性能受入は、この対応の実機確認には含めていない。CI のリモート実行も未確認。これらは SD-030 のリリース判定に残し、タグ、push、Marketplace 公開は実施していない。
