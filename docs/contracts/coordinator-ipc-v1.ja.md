# SharpDeps coordinator／local IPC契約 v1

SD2-10は、trusted `index`／`update`と明示起動の`serve`で同じ更新経路を使う。SD2-11の候補では、`serve --watch --target TARGET`が同じownerへ保存入力のwatch/resyncを接続する。既存のQuick／Semantic host、保存Queryのstdio、SQLite schema、固定Markdown runtime/source pairは保持する。MCP adapterは後続のタスクで接続する。

## 起動と保存読取り

```text
sharpdeps serve --root ROOT_ABS --trusted
sharpdeps coordinator-status --root ROOT_ABS --quiet
sharpdeps update --root ROOT_ABS --target App.csproj --trusted --timeout-seconds 180
```

`serve`はrootとindexに一つのwriter leaseを保持し、終了要求まで待つ。`--watch`なしの起動だけではSDK探索、解析、restoreを行わない。`--watch`は`--target`を必須とし、起動時full抽出と通知後の更新を行う。`--watch-resync-seconds`は1〜3600秒、既定30秒で、入力hashが変わらなくても周期ごとにfull抽出する。新しいwriterやNode processは作らない。更新にはそのclientも`--trusted`を明示する。`coordinator-status`はownerの状態を読むだけで、保存Queryの`status`とは別のcommandである。`serve`の終了要求はowner全体を停止する。更新clientのCtrl-C、timeout、接続断はそのclientの応答待ちを取り消す。

既存ownerがあれば`index`／`update`は専用IPCへ送る。接続できないときはOS leaseの実取得を試みる。取得成功時だけ短命ownerとして更新し、受理済みの他clientを処理して終了する。保存Queryはownerを起動せず、committed generationを固定したreaderで動く。更新後のQueryでは新readerを開く。古いreader／cursorの世代を途中で置き換えない。

ownerの`idle`、`dirty`、`updating`、`degraded`、`stopping`は更新処理の状態である。生存やidleだけでlive入力の完全な一致やQuery freshnessを証明しない。statusの`configurationProof`は`unverified`を保持する。trusted `verify-query`だけは保存snapshot ID/generationを照合し、直近の成功したproduction更新が持つclosed-input proofで現在の入力集合・設定/variant/参照/SDKを再評価する。照合の前後でowner revisionが同じidleの場合だけQuery用inventoryを返す。証明なし・不一致・更新中・既存64KiB frame上限超過は成功としない。client切断はその検査だけを取り消す。保存Queryの`refresh`は従来どおり`UPDATE_REQUIRED`であり、自動更新しない。

watchも既存v1のstateへ写像し、wire fieldやstate名を追加しない。native監視が使えない場合は周期resyncを続け、idle相当をdegradedとして表示する。watchの失敗はstatusのerrorCodeへ保持し、次のwatchによるdurable commitが成功したときに消す。入力検査やSDK選択の失敗、停止後も、古いcommitted Queryは読める。extensionのtrust変更とwatch UIは未接続であり、CLIのtrust撤回にはowner processを停止する。

## 所有と回復

既存IndexWriterの`.writer.lock`を抽出前からowner終了まで保持する。所有権の根拠はOS leaseだけであり、PID、起動時刻、JSONの存在や古さから所有権を奪わない。lockを削除しない。

別の`INDEX.owner.json`にprotocol、workspace UUID、canonical root/index、random owner instance、endpoint、PID、process起動時刻を記録する。bounded JSONを一時fileからatomic replaceし、Unixでは所有userだけのread/writeにする。cursor key、source本文、request本文を含めない。終了後のdescriptorは診断用に残す。新ownerはOS leaseの取得後に新しいinstanceとendpointへ置き換える。

ownerが異常終了した場合はOSがleaseを解放する。次の明示更新でleaseを取得し、既存UUIDと最新committed snapshotを保持して再開する。DB／WAL／SHMを削除しない。既存IndexWriterのschema、workspace、sidecar検査を使い、回復条件を満たさない場合は`INDEX_RECOVERY_REQUIRED`を保持する。

応答を受け取る前に接続が切れた場合は`COORDINATOR_RESULT_UNKNOWN`とし、commitの成否を推測しない。保存Queryでcommitted generationを確認する。応答喪失時に自動再送しない。protocol不一致や応答しないownerは診断し、killやlock unlinkをしない。

## request、取消、世代

待機中の同じtarget／configuration／platform／project selection／TFM／full要求を一batchへまとめる。clientのrequest ID、quiet、応答待ちtimeoutを解析条件に混ぜない。full `index`をreuse `update`で代用しない。running batchには後から合流させず、次のpending batchへ送る。batch開始ごとに最新committed snapshotを新readerで読み直す。

pending batch受理でdesired revisionを増やし、抽出開始時にcaptureする。公開時は同じgateの内側でrevision照合からIndexWriter.Commit完了まで行う。既知の新revisionや公開guardでの入力変更には一回だけ再抽出を試みる。同じ条件のpending batchがあればwaiterを統合する。変更が続く場合はdegradedと原因を返す。request数やrevisionをDB generationとして保存しない。

watch通知はowner gateを取る前に独立revisionを進める。root内の保存bytes/inventoryのbounded SHA-256 snapshotを抽出前にcaptureし、既存IndexWriterのtransaction callback内で同じhashとrevisionを再検査する。rowsとFTS作成後の検査に失敗すればactive generationは変更しない。watch batchは専用の内部waiterとguardを持ち、通常clientのbatchには合流しない。失敗後は保存した古いsnapshotで再抽出せず、次のhintか周期でsnapshotを取り直す。周期tickは進行中のstageを失効させず、完了後に次のfull抽出を行う。成功結果を作るのはCommit完了後だけである。

通知とwatch snapshotは.git/.sharpdeps/bin/obj、custom index本体、WAL/SHM/journal、writer lock、owner descriptorとその一時fileを除外する。full抽出の既存publication guardはbin/objの意味入力を引き続き検査する。root外の祖先config、AdditionalFiles、任意PE等は周期fullで再評価する機会を持つが、未知external入力のstage中変更を完全に捕捉する保証はない。Partial/unverifiedを保持し、SDK identity変更はrestart-requiredで止める。large rootやlink、入力budgetを超えるtreeの自動watch成功は主張しない。

各clientの取消はそのwaiterだけを外す。他clientの共有更新tokenへ接続しない。受理済みの共有更新はownerの300秒deadline、owner終了要求、公開guardで中断できる。すべてのclientが取消された未開始batchは抽出しない。短命ownerは自分のclient完了／取消後に新規更新の受理を閉じ、受理済みの処理を終える。

SDK登録はprocess全体に固定される。ownerは実SDK path/versionに固定し、同じSDKを使う別target directoryの依頼を受ける。各buildではtarget directoryから祖先のglobal.json集合とraw bytes、SDK探索に関係する環境を照合する。登録による環境初期化後のstampを使い、公開直前にも同じtargetのstampを検査する。公開guardは全dotnet設置先の候補を照合し、選択SDKが登録済みSDKと合わない場合やSDKが見つからなくなった場合は`COORDINATOR_SDK_RESTART_REQUIRED`を返す。旧SDKで新設定を公開しない。利用者がownerを停止して新processで起動する。複数のdotnet設置先がある環境では、意図したSDKの設置先を`DOTNET_ROOT`等で明示して起動する必要がある。ownerが任意root／SDKを同じprocessで再登録する機能はない。

## IPC境界と予算

local .NET named pipeを`CurrentUserOnly`で使用する。Windowsはlocal named pipe、Unixは.NETのlocal pipe実装を使う。TCPやremote shellを開かない。endpointは固定prefixとowner instanceから作り、descriptor中の任意command／URLを実行しない。OSごとの動作確認は各taskの検証証跡で判定する。

一connectionにつき`status`か`update`を一回送る。frameは4-byte little-endian lengthとstrict UTF-8 JSON。protocol、owner instance、workspace UUID、root/index、request IDを照合する。未知field、duplicate key、不正UTF-8、深すぎるJSONを拒否する。updateは解析条件とtrustだけを受け取り、本文やshell commandを受け取らない。client cancelはconnection closeで伝える。

| 上限 | 値 |
| --- | ---: |
| frame | 65,536 bytes |
| descriptor | 8,192 bytes |
| JSON depth | 8 |
| 同時connection | 32 |
| pending batch | 16 |
| pending waiter | 64 |
| handshake | 3秒 |
| ownerの一抽出 | 300秒 |
| 一connection全体 | 305秒 |
| client応答待ち | 1〜300秒、既定180秒 |

responseは元request ID、owner instance、captured／desired revision、owner状態、committed結果またはerror codeを返す。ownerが返したrevisionと保存generationは別の値である。

主な診断は`COORDINATOR_BUSY`、`COORDINATOR_STOPPING`、`COORDINATOR_PROTOCOL_MISMATCH`、`COORDINATOR_OWNER_INVALID`、`COORDINATOR_FRAME_INVALID`、`COORDINATOR_REPLY_INVALID`、`COORDINATOR_RESULT_UNKNOWN`、`COORDINATOR_SDK_RESTART_REQUIRED`で、exit 6を使う。変更が継続して収束しない`COORDINATOR_SUPERSEDED`はexit 3、client取消は130、client／owner deadlineは`TIMEOUT`のexit 4となる。
