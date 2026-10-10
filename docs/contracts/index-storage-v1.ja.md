# Index storage v1

SD2-05の`SharpDeps.Index`はContracts/Coreを参照する独立したnet10 projectである。SQLite依存はこのprojectに閉じ、既存Quick/Semantic host、report v2、旧reportStore、Markdown parserと固定packageを変更しない。新しいhost/Query/coordinatorへの接続は後続taskで行う。

## 所有権と保存範囲

trusted ownerは解析を始める前に`IndexWriter.Open(root, indexPath, workspaceUuid, true)`を呼び、抽出からcommitまで同じleaseを保持する。DB pathに隣接する`.writer.lock`を`FileShare.None`で開き、OS handleの排他で二writerを拒否する。PIDだけで所有を判断せず、process start UTC、canonical root、index path、schema versionをlock内へ記録する。正常終了とprocess終了でhandleは解放される。lock fileは削除しない。別writerは`INDEX_BUSY`を返し、無制限に待機しない。

workspace UUIDは既に永続化した非empty UUIDをownerが渡す。metadataへworkspace IDを保存し、別UUIDでのread/writeを拒否する。文書UUID、節token、descriptor、parser/contract/profile/options stamp、pathの対応とaliasの根拠・確度を世代ごとに保存する。UUID/tokenをpathやLocalKeyから勝手に再生成せず、ownerがSD2-04の照合結果をstorage DTOへ渡す。任意のmetadata内rename申告を実際のrenameとして検証したことにはしない。

保存先はownerが指定する絶対local pathであり、既定候補はworkspace内`.sharpdeps/index-v1.sqlite`である。symlink/reparse point、path traversal、特殊SQLite URIを拒否する。source rootとDB pathを変更する前にownerが配置を確認する。Unixでは作成したdirectoryとDB/sidecar/lockを利用者だけが読書きできるmodeへ設定する。Windowsでは保存先の継承ACLを使うため、ownerは利用者専用のworkspace/cache領域を指定する。repo設定やACLを無断で書き換えない。network filesystemのWALを対応済みとは扱わない。

graph、診断、宣言/evidenceのsource hash・元UTF-16位置、旧ID対応、文書/節対応、alias、入力manifest、ownerが明示的に選んだ検索用name/signature/heading/body/pathを保存する。source全文を無制限に保存しない。検索textは各fieldの上限を検証し、secret/生成物の索引除外と検索本文の選択は抽出ownerが行う。manifestには解析へ影響したproject/config/AdditionalFiles等も含める。生成された仮想sourceは実ファイルのhash検査を捏造せず、この版のlive-file locationとして受け入れない。

## 世代とtransaction

`IndexStage.Create(snapshot)`は抽出結果をtransaction外でJSONへ凍結し、完全入力検証とmanifest hashを記録する。graph headerだけでなく、参照先、variant/occurrence、位置のfile/hash/span、文書/節ID、aliasと検索nodeを検証する。staging後に呼出し側collectionを変更してもcommit対象は変わらない。

writerは実入力のbytes SHA256とbyte長、textならUTF-16長を照合してからtransactionを始める。全node、edge、occurrence、locationを含むpayload、ID対応、alias、旧ID対応、削除tombstone、診断、FTS、manifestを一つのSQLite transactionで反映する。全行の反映後に実入力を再照合し、取消を検査し、最後にactive generationを切り替えてcommitする。generationは単調増加で、snapshot IDは再利用しない。検証失敗やcommit前の取消は全変更をrollbackし、旧世代を残す。最終照合とcommitの間にも外部編集は起こり得るため、後続Queryでsnippetを読む際のhash再検査は省略しない。

WAL、private cache、synchronous FULL、busy timeout 3秒、connection pooling無効を使う。readerはreadonly connectionとreader transactionで一つのactive generationを固定し、後続commitがあってもgraph/FTSを旧世代のまま読む。世代別のgraphとFTSをjoinするため、新旧を混ぜない。過去世代はこの版では保持し、GCや大規模rebuildのpointer交換は後続の計測を伴って決める。

`IndexReader.Open(indexPath, workspaceUuid)`はsourceを読まず、DB・directory・writer lockを作らず、migrationを行わない。ReadSavedのためにnative VFSの`SQLITE_FCNTL_PERSIST_WAL`を設定し、writerが用意したWAL/SHMを通常close後も保持する。sidecarが欠ける保存済みindexはreaderとwriterの両方で`INDEX_RECOVERY_REQUIRED`とし、nativeのwritable openより前に拒否する。WALだけが残る場合にも、対応外schemaの復旧・checkpointを拒否前に実行しない。元DBと残ったsidecarを保持し、別indexへの明示的な再索引を行う。通常closeやcrash後に両sidecarが揃う場合は再openできる。これはSQLite内部の一時的なreader lockと、永続DB更新・新規sidecar作成を区別する契約である。

## FTSと互換性

Microsoft.Data.Sqlite 10.0.12を固定し、同梱native SQLiteのFTS5 unicode61を使う。name/signature/heading/body/pathを別fieldに保存し、bm25の重みを10/5/8/1/2として名前と見出しを優先する。FTS tableとexternal-content viewはgenerationごとに作り、その世代の行だけを索引へ入れる。table/viewの作成、search textとFTS行の追加、active generationの切替は同じtransactionに含める。bm25のavgdl・IDFも世代内のcorpusで計算し、過去世代の内容や回数によって同一snapshotの順位・scoreを変えない。external-content viewも同世代に限定し、FTSのrebuildで他世代を取り込まない。過去世代のFTSはreaderとともに保持する。検索入力はFTS式として実行せず、引用したliteralとして扱う。日本語のtoken境界やC#識別子の部分名は追加tokenizerを導入せず、同世代のliteral substringで補完し、`MatchMethod`へ区別を返す。これはstorageの小さい検索primitiveであり、共通Queryのcursor、freshness、context、envelope budgetを代替しない。

index schemaは`PRAGMA user_version=1`、application IDは`0x53444831`で、graph schema/report v2の版と別である。対応外のDBは`INDEX_SCHEMA_UNSUPPORTED`で拒否し、破壊的な自動migrationを行わない。移行対象の旧reportはそのまま保持し、既存providerからreadonly fallbackできる。再索引は別DB pathへ作成し、既存indexを上書きしない。`BackupTo`はtrusted writerの所有下でSQLite online backupを行い、存在する宛先を上書きしない。失敗したbackupを正常なsnapshotとは扱わない。

未出荷のレビュー版v0と現版は`user_version=1`を共用するが、FTS配置が異なるためDBの互換性を主張しない。v0の試用DBはOpenのheader検査を通っても、Searchで世代別tableが存在しない。今回の限定検証は新しい専用pathへ再索引し、v0のDBとsidecarを流用・上書き・削除しない。v0からの自動migrationも実装していない。

実装根拠は[Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)、[connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)、[SQLite WAL](https://sqlite.org/wal.html)、[FTS5](https://sqlite.org/fts5.html)を確認した。実差分、限定check結果、未実施のhost/Query接続とOS範囲はrun成果物へ記録する。
