# 保存表示の操作と信頼の制約

この文書は拡張機能の保存表示と更新処理の技術的な参照資料です。利用手順は[操作ガイド](../guide.ja.md)、同梱 reader と外部 CLI の役割は[保存 Query provider](saved-query-provider-v1.ja.md)を参照してください。

## 表示と更新

主ボタンは表示中の provider に応じて動作します。legacy は Analyze、stale では Update analysis、失敗時は Retry analysis、実行中は Cancel を表示します。Quick／Semantic の選択は legacy に適用します。

saved 表示は Update saved index、更新失敗後は Retry update、選択中・更新中は Cancel update を表示します。取消要求後から helper の終了までは操作を無効にして Cancelling を表示します。Refresh は保存 Query の再読取りであり、更新を開始しません。

更新では host が明示的に target、configuration、platform、project、TFM を選択します。保存 variant から更新条件を推測しません。主ボタンとコマンドパレットは同じ host の経路を使い、古い analysisId の要求を現在の provider へ適用しません。保存した画面状態から実行状態や権限を復元することもありません。

## 取消と世代

取消はこの caller の AbortSignal から CLI の stdin EOF へ伝えます。共有処理の終了を待つ間も、現在の reader と stale 表示を保持します。取消は rollback や保存世代の不変を保証しません。取消や結果未確認の更新後は、Refresh で保存世代を確認してください。

更新成功時は helper の終了後に workspace／snapshot／generation の完全一致を確認し、新しい reader の結果を採用します。root の除去、provider の切替、panel の破棄は既存の解放処理を使います。コピーと JSON 出力の範囲は[保存 Query 出力](saved-query-export-v1.ja.md)を参照してください。

## 実行中に信頼を取り消す場合

saved 更新は起動前と child 終了後に現在の workspace trust を確認します。待機中の trust 変更を EOF 取消へ接続する listener はありません。`onDidGrantWorkspaceTrust` は操作の可否を再表示するために使います。

同じ extension host 内で信頼を取り消した直後に、実行中の更新が必ず止まるとは扱わないでください。取消、明示的に起動した owner／watch の停止、ウィンドウの再読み込みを行い、新しい host の信頼状態と保存世代を確認してください。別の client が既に受理された共有更新を利用している場合もあります。

workspace trust は OS の sandbox や外部 AI への送信許可ではありません。保存表示だけでソースの読取り、MSBuild や generator の実行、索引への書込みが許可されるわけではありません。
