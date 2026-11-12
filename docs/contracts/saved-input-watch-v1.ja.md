# 保存済み入力のwatch収束部品

現行のproduction watchはCLI owner内のC# FileSystemWatcherを使う。拡張機能は明示起動済みownerの状態を読み、新しいcommitted世代だけを反映する。CLIの現在の起動・取消・予算は[coordinator契約](coordinator-ipc-v1.ja.md)、利用手順は[操作ガイド](../guide.ja.md#保存済み索引を使う)を参照する。

## 既存watchのcommitted世代をVS Codeで追従する限定接続

`sharpdeps.savedIndexAutoRefresh`は既定falseで、trustedな単一folderのsavedIndex表示だけへ適用する。
利用者が既存CLIの`serve --watch`を明示起動し、互換CLIの絶対pathをsavedIndexCliPathに設定する。
拡張はread-onlyのcoordinator-statusを直列に取得するだけで、writer、watch、解析、restoreを起動しない。
完了後2秒で次の照会を予約し、status processは5秒・出力64KiBで制限する。設定無効化、root/provider/panelの
終了で監視を解放し、variant/runtime設定変更は明示Refreshまで自動反映を止める。

idle ownerの新committed tuple（workspace/snapshot/generation）を新QueryHost readerで読取り、前後のowner instance、
revision/desiredRevision、committed tupleとreaderのtupleが一致する場合だけ表示を交換する。旧providerはその間保持し、
途中の入力/context変更やowner交替、dirty/updating/degraded、停止、読取失敗は旧表示とstaleを保持する。
新世代では旧cursor/selection/evidenceを再利用せず、旧readerの遅延応答と失敗をpanelから送信しない。
未保存bufferのstale、Partial/unverifiedと既存source hash/UTF-16検査を維持し、owner idleをfreshness証明とはしない。
statusはownerのwatch有無を証明しない。watch起動と対象選択は利用者が行い、拡張はownerのcommitted結果だけを追う。
即時trust revokeの制約は[保存表示の操作](saved-primary-action-v1.ja.md)を参照する。この接続だけで全環境のwatch動作や性能を保証しない。

## CLI ownerのwatch

`serve --trusted --watch --target TARGET`は実OS leaseを持つCLI owner内でC#の
FileSystemWatcherを開始します。TS部品やNode runtimeをproductionで起動せず、
二つ目のwriterは作りません。root内のbounded raw bytes/inventoryのSHA-256を抽出前に
captureし、同じwatch revisionとhashを既存IndexWriterのtransaction callbackで照合します。
通知revisionはowner gateの前に進めるため、Commit中の通知もguardから検査できます。
成功結果はdurable commit後だけ作り、入力変更、停止、予算超過、SDK選択失敗では
旧generation／Queryを残します。

起動時と周期resyncはfull抽出、通常通知は既存のclosed-input update経路を使います。
既定30秒の周期はstage完了後から数え、遅い抽出を周期だけで失効させません。
rootのhashが同じでも周期fullを省略しないため、祖先config／SDK／外部入力を再評価する
機会があります。未知external入力のstage中変更を完全に検知する保証はなく、
Partial／unverifiedを維持します。SDK identityが変わった場合はownerの再起動が必要です。

通知とwatch scanは.git/.sharpdeps/bin/objを除外します。custom index本体、WAL/SHM/journal、
writer lock、owner descriptorとその一時fileも両方から除外します。親directory全体を
除外して利用者のsourceを消すことはありません。CLIのfull publication guardは
bin/objを含む既存の意味入力を検査します。TS部品はextension未接続のため、独自の
拡張子選別manifestをCLIの完全な入力閉包とは扱いません。

CLIとTSのMarkdown拡張子は.md/.markdown/.mdxへ揃えます。MDXは保存本文の静的
Markdown解析に限り、JSXや式を実行しません。CLI watchは開始時にtrustedを必須とし、
trust撤回にはowner processを停止します。sleep/resume、network mountなどを含むすべての環境での動作を保証するものではありません。
