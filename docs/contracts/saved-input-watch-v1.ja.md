# 保存済み入力のwatch収束部品

SD2-11の準備実装として、`src/analyzer/savedInputWatch.ts`にopt-inの
`SavedInputWatch`を追加します。既存extensionの自動解析、Quick/Semantic、report v2、
固定Syntamark pairを変更しません。前段の部品版はWindowsで11件の限定テストを通過しました。
CLI ownerへのnative watch/resync接続候補と`.markdown`拡張子の追加は、Windowsの
限定検証を通過しました。現差分の親レビューと公開commitはまだ完了していません。

## 入力と収束

呼出し側が指定したrootをcanonical pathへ固定し、保存済みのmd/markdown/mdx、C#、project、
solution、props/targets、json/configを列挙します。.git/.local/.sharpdeps/bin/obj/node_modules/outと
symbolic linkを除外し、file数、全entry数、深さ、file/全体bytesの上限を設けます。
本文を実行・描画せず、相対path、byte長、SHA-256の不変manifestを返します。
これはテキスト解析の完了やgraph coverageを示すものではありません。

native watch通知はdirty信号としてまとめます。rename、delete、overflowを部分的な
イベント履歴だけで推測せず、起動時、通知後、明示refresh、定期照合でfull scanします。
mtime/sizeが同じ編集もcontent hashで検出します。公開前に二回のfull manifestを比較し、
走査中の変更、失敗、上限超過を「変更なし」としません。継続変更時は一定回数で処理を
譲り、後で再照合します。watcher errorや定期照合では監視handleも作り直します。
長いbackend stageを定期照合だけで失効させず、その完了後に次の照合を予約します。
default storeの.sharpdeps出力は通知とmanifestから除外します。Windowsではdirectory名のcaseを
通知とscanで共通に正規化し、.SHARPDEPS等も除外します。native watcherを作れない場合はエラーを通知してfull scanを続け、次回の照合で
監視handleを再取得します。定期照合による回復をnative通知の成功とは扱いません。

保存済みfilesystemが対象です。未保存bufferの内容や祖先のMSBuild設定は取り込みません。
root外の明示configやignore/exclusion契約を接続する責務はSD2-09/10に残ります。
path検査はOS sandboxではなく、悪意ある同時filesystem置換の完全隔離は保証しません。

## 公開とtrust

将来の接続側が`reconcile(snapshot, isCurrent)`を供給します。非同期のstage中にも
変更やtrust失効があり得るため、呼出し側は最後のatomic commit直前に`isCurrent()`を
確認し、成功後だけtrueを返します。sourceを再読込する場合はsnapshotのcontent hashを
照合します。falseや例外を成功世代として記録しません。

Index/Query/FTSのtransaction、single writer、durable identity、IPC、client cancelは
この部品に含めません。現在のreportStoreへの代入を新しいQuery更新と呼びません。
停止や失敗では最後の成功snapshotを保持し、staleを維持します。再startは入力が同じでも
ownerへfull reconciliationを要求します。開始promiseと通知callbackをowner世代に結び付け、
realpath待機中にstop→startしても新世代の開始を古いpromiseへ合流させません。古い開始の
finallyは新しい開始promiseを消しません。停止済みstageの終了を待ってから新世代の照合を
始め、停止済みwatcherの遅延callbackを無視します。disposeは所有したwatcher/timerと進行中の照合を
終了し、以後のstartを拒否します。backendのreconcileが停止に応答せず終了しない場合は
disposeも待機を続けるため、backendはisCurrentと自身の処理上限を守る必要があります。
realpathのOS処理は中止できないため、disposeは停止済み世代も含む全開始promiseをjoinします。

untrustedではwatch開始と入力読取りを拒否します。hostはtrust変更時に`trustChanged()`を
直ちに呼びます。readSavedの許可や既存Quick/Semanticのtrust gateは変更しません。
各本文readの直前にtrustと走査世代を再確認します。失効前に発行したOS I/Oは
完了を待ってhandleを閉じますが、失効後の新しい本文readや公開は開始しません。

## 前提と検証

SD2-11はSD2-10へ依存します。現checkoutではstorage、Query、限定invalidation、IPC ownerが
実装済みですが、TS部品をextensionからownerへ接続する処理はまだありません。
SD2-12はSD2-07のstdio Query adapter、SD2-13はSD2-08/11のUI接続を待ちます。
本部品の作成でSD2-11全体を完了としません。

作成したテストは実filesystemで同size/mtime編集、rename→deleteの連続操作、stage中の
再保存、overflow後の復旧、定期照合によるevent loss回復、trust失効/disposeを確認します。
通常のrace試験ではnative event源を制御可能なhandleへ置き換え、実content hashを検証します。追加の開始/停止試験は
realpathの完了を制御し、実filesystem操作はNodeへ委譲します。起動待機中のstop→start、
古い開始完了時の新promise維持、全世代のdispose join、stage中の中断・再開を検査します。
再開時の実root探索失敗と進行中stageを組み合わせたdispose待機も検査します。
別の試験では既定のnative watcherを使い、定期resync前の保存通知によるmanifest収束を
Windowsで確認しました。前段の部品版のformat、lint、TypeScript compileと対象Vitestの11件は成功です。
この結果を、現在のproduction接続候補の成功へ転用しません。

## CLI ownerへのproduction接続候補

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
trust撤回にはowner processを停止します。extensionのtrust/status UI、crossOS native、
sleep/resume、network mount、全fault matrix、preview.3 packはこの候補の完了条件に含めません。

verification-attempt02ではCLI buildの7 projectsが0 errors／0 warnings、CLI試験10件、
対象TSのformat/lint、全体typecheck、TS watch試験11件が成功しました。局所2件は
実lease/native通知、通知を無効にした周期full、自己出力除外、stage中の通知欠落による
transaction rollback、停止済みstageの公開拒否を検査します。build delegateを制御する
局所試験と、次のproduction抽出の証拠を区別します。

probe-attempt02の実driverはproduction executorを使い、保存→rename/delete/config→
native通知無効時の周期fullという操作列、durable UUID保持、同じ最終入力のfull結果、
実Query itemsとFTS hits/ranksの一致、SDK選択失敗時の旧generation保持を確認しました。
37 checksはCLI exitの確認を含む一driver内のassertionsであり、37個の独立process試験では
ありません。native通知の区間はdriverの全期限より長い周期を設定して分離し、通常updateが
この環境でfull fallbackとなったことも記録します。閉包proofがない入力をsemantic reuseの
成功として数えません。full oracleは同owner・同process・同じdurable identity lineageでの
強制full抽出で、別OS processの独立実行ではありません。

比較ではgeneration/snapshot ID、transientな補助node/edge ID、Reuse証拠とreuse診断を
正規化します。全raw JSON／全ID／Reuse catalogが一致したという主張はしません。
CLI本体は0 warningsでしたが、wildcard DLL参照を使う実driver buildにはSQLitePCLRaw.coreの
version conflict警告が1件あり、実行時のCLI DLL hash一致とQuery/FTS結果を別に確認しました。
既存Viteの将来native-loader警告も記録し、設定は変更していません。
commit／pushは親の実差分レビュー後に判断します。
