# v0.1.0 リリース候補の再判定

2026-09-27。RC-01〜04 の実装不備と、RC-05/06 のローカル検証で再現した不具合を修正した。Windows の実機検証、Linux の Quick／Semantic smoke、Medium 性能測定を実施した。

既知のコード上の P1 は解消した。ただし、macOS の smoke とリモート CI は未実施であり、計画全体の完了や公開可能とは宣言しない。公開判定は No-Go を維持する。これは未検証の出荷条件によるもので、修正済みの不具合を未修正として扱うものではない。

対象は HEAD `7389c485bf449ca9b1b3fddcd5d1babbf8e60e37` と作業ツリーの変更。コミット、push、タグ、Marketplace 公開は実施していない。

## 修正結果

| 指摘 | 対応と確認 |
|---|---|
| RC-01 評価済み参照の欠落 | `projectEvaluated` をコード利用と別の関係・根拠として保持。コード利用が0件でも project view に残る。評価元は import 等もあり得るため、存在しない XML 行番号は付けない。実ホスト回帰と基準 fixture を更新 |
| RC-02 mapped location | ファイル指定あり／なしの `#line` を物理位置と別に保存。開く際に選択できる。実パスが解析ルート外へ出る junction は拒否。物理位置・mapped 行番号・実ファイル境界を回帰で確認 |
| RC-03 既存操作 | ズームボタン／slider、Fit、node/rank spacing、プロジェクト種別の色と凡例を復元。layout 中断／再試行、失敗時の表表示を追加 |
| RC-04 出力条件 | SVG/PNG に対象、実際に解析した構成・TFM、scope、検索・filter、省略、凡例を表示。各注釈群を選択可能。Mermaid/JSON/context にも同じ条件を含める |
| RC-05 実機確認 | 実 Trust 拒否、runtime-only UI、両 High Contrast と200%、通常プロセス再起動、コード移動、linked source、未保存 stale を実行。macOS・リモート CI は未検証として残す |
| RC-06 性能 | 30 projects／3,000 C# files／102,535 evidence を3回測定。100/200・300/1,000 の実 ELK、選択 p95、検索、100件根拠ページ、20回の中断・子プロセス・保存領域を測定 |

追加で、全階層の遅延取得、Medium レポートの容量上限、投影時の繰り返し走査、ELK の stack overflow、循環 scope への一時表示ノード混入、非表示 Webview への reveal 消失、Linux のパス区切り、失敗プロジェクトの欠落と状態表示を修正した。

失敗したプロジェクトは最終モデルへ残す。Inspector はプロジェクトごとに complete／partial／failed と理由を表示する。SDK 不足、生成器が失敗して生成型を参照できないプロジェクト、正常プロジェクトを含む実機ケースで確認した。

## 検証記録

- C# テストと TypeScript／契約テスト、compile、lint、format、差分検査を実行。最終件数・終了コードは `evidence/sd-030-remediation.json` を参照。
- [配布 VSIX の実機経路](evidence/sd-027-e2e-vsix.json)：Quick、Semantic、根拠、生成文書、カーソルからの依存／被依存、linked source、未保存 stale。
- [大規模探索](evidence/sd-030-exploration.json)：表示上限10、全階層の100型、全体検索から Show、100ノード・500辺の循環と根拠。解析IDは維持。
- [不完全な解析](evidence/sd-030-incomplete.json)：正常・生成型欠落・SDK不足の混在結果をプロジェクト別に表示。
- [実テーマと中断](evidence/sd-030-experience.json)：High Contrast Dark/Light、200%、キーボードで根拠→コード、20回の開閉・解析・停止。
- [再起動](evidence/sd-030-restart.json)：通常の VS Code を2回起動してタブ・検索・表状態を復元し、解析ディレクトリが増えないことを確認。テスト専用ホストの一時ストレージを永続化の証拠には使わない。
- [Trust](evidence/sd-030-trust.json)：実際に未信頼のワークスペースで両モードを拒否し、指定実行ファイル・build target の sentinel が作られないことを確認。
- [SDK 不足の UI](evidence/sd-030-runtime.json)：SDK を含まない dotnet 配置を指定し、Quick→Semantic 拒否→Quick を実行。OS 自体から SDK を削除した試験ではない。
- [Linux Quick](evidence/sd-030-linux-runtime.json)：SDK を含まない runtime コンテナで、6 projects／7 namespaces／15 relations が Windows と一致。外部通信を無効化。partial は Quick の推定・fixture の制約を表す。
- [Linux Semantic](evidence/sd-030-linux-semantic.json)：SDK 10.0.300 のコンテナで7 project variants／44 types／49 relations／71 evidence／生成文書2件、completeWithinScope。Windows golden の件数と一致。

ブラウザ fixture は実 worker と CSP を使い、1440×900、1024×768、900×900、700×800、360×740で操作と SVG/PNG を検証する。実 VS Code の200%表示は CSS幅662、横 overflow なし、主要ボタンが viewport 内にあることも確認した。

## 性能と容量

[Medium の生の測定値](evidence/sd-028-acceptance.json)、[実ブラウザ](evidence/sd-028-browser.json)、[所有プロセスと保存領域](evidence/sd-028-lifecycle.json)を保存した。実行時間は新規 analyzer プロセス3回、restore は計測外。OS cache の完全消去は主張しない。メモリは所有子プロセスを含むサンプリング値で、瞬間的な最大値を保証する測り方ではない。

最初の Medium 結果は約59 MBとなり、32 MiB の既定上限を超えた。解析結果の間引きではなく、上限を128 MiBへ変更した（[ADR-0004](../../adr/0004-medium-result-capacity.md)）。投影はノードごとの全辺走査と型一覧の繰り返し検索を除いた。ELK の node placement は固定300/1,000入力で失敗する NETWORK_SIMPLEX から BRANDES_KOEPF に変更した。

20回の実 Semantic 中断では残存する所有 PID は0、終了後の結果ディレクトリは4個以内。正常結果2個と直近試行2個を保持する仕様であり、利用中の結果を削除して容量を小さく見せてはいない。

## 全タスクの再照合

| タスク | 今回の確認範囲・残条件 |
|---|---|
| SD-001 | 基準 fixture と既存操作との差を照合、操作を復元 |
| SD-002 | C#/TS 契約、ID、host message、実ホスト JSON |
| SD-003 | Windows runtime-only と Linux Quick/Semantic。macOS は未実施 |
| SD-004 | 実 ELK worker／SVG／PNG／CSP |
| SD-005 | Quick と Semantic の分離、runtime-only Quick |
| SD-006 | Quick 基準・同名パス・表示予算分離・Linux 区切り |
| SD-007 | 構成／TFM／評価済み参照／SDK不足のプロジェクト状態 |
| SD-008 | partial・file-local・同名・多TFMの索引 |
| SD-009 | 宣言依存の種類・箇所数・公開面の回帰 |
| SD-010 | Operation、初期化子・型引数・ローカル・pattern の回帰 |
| SD-011 | 生成文書の実機 read-only、物理／mapped位置と境界 |
| SD-012 | basis分離、集約、逆探索、SCCとwitness |
| SD-013 | paging／cursor／登録世代、未完成の索引を公開しない |
| SD-014 | 実Stop、20回の所有プロセス終了・結果保持 |
| SD-015 | shell、状態、画面幅、停止、0件の説明 |
| SD-016 | 全階層の遅延取得、上限外検索、表のsort/page |
| SD-017 | 実グラフ、layoutとselectionの分離、操作・中断・表への切替 |
| SD-018 | 実根拠、生成文書、集約元と失敗対象の詳細 |
| SD-019 | カーソル→依存／被依存→根拠→物理ソース、linked source |
| SD-020 | 上限外SCC 100 nodes／500 edges、witnessから根拠 |
| SD-021 | 未保存stale、再表示、通常プロセス再起動、再解析しない |
| SD-022 | 現在の選択と出力条件、画像に見える注釈 |
| SD-023 | 実Trust、CSP、cursor／path、実junctionの境界 |
| SD-024 | キーボード、指定幅、実High Contrast両系統と200% |
| SD-025 | C#正解テスト、Semantic golden、RC-01/02と混在失敗の実ホスト回帰 |
| SD-026 | TSとprotocolの回帰、順不同応答、親別tree cursor |
| SD-027 | Windows開発版・配布版と追加実機。全OS合格とは扱わない |
| SD-028 | Medium・p95・実layout・20回の実プロセス測定 |
| SD-029 | 配布物の内容とhash、Windows導入、Linuxホスト。macOS未実施 |
| SD-030 | 再照合と未検証の分離を実施。macOS／リモートCI待ちで完了宣言を保留 |

## 公開前に残る確認

- [x] 評価済み参照・mapped位置・既存操作・出力注釈を実装し、回帰を追加する。
- [x] Windows の配布VSIXで解析からコード移動まで検証する。
- [x] 不完全性、未保存stale、再起動、Trust、性能・所有プロセスを確認する。
- [x] Linux の Quick／対応SDK-style Semantic を実行する。
- [ ] macOS の Quick／対応SDK-style Semantic smoke を実施する。このホストには macOS 実行環境がない。
- [ ] 最新変更を対象としたリモート CI を実行する。未コミットの変更をリモートで検証したとは扱わない。
- [ ] 公開前に候補 VSIX の hash と全出荷条件を再確認し、Go／No-Go を確定する。

各 UX の構成要素は上記の独立した検証で確認した。計画の全操作を一つの連続録画で通したという主張はしない。未実施の macOS とリモート CI を後続機能へ移して完了扱いにはしない。DI、Git差分、設計ルール、常駐解析などの計画対象外の機能は追加していない。

候補パッケージの正確なサイズ・SHA-256・必須entry一覧は [sd-029-vsix.json](evidence/sd-029-vsix.json) を正本とし、最終再検証の記録にも同じhashを転記する。過去の `sd-030-probes.json` と `sd-030-acceptance-audit.json` は修正前の再現記録であり、現在の未修正一覧ではない。
