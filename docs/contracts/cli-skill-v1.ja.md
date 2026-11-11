# SharpDeps CLI／repo skill契約 v1

CLIはnet10の.NET toolで、PackageIdは`SharpDeps.Cli`、ToolCommandNameは`sharpdeps`である。基礎契約は0.4.0に対応し、discoveryとその既定値、およびexploreでのroot省略は未公開のv0.5開発版の仕様である。0.4.0ではCLI／MCPと拡張機能を配布対象とし、実際の提供状況はレジストリ、Marketplace、GitHub Releaseで確認する。Explore、MCP変更、索引の再作成条件は[CLI and MCP exploration](../mcp.md)を参照する。旧report v1/v2とQuick／Semanticの実行境界を維持し、同梱解析プログラムとQueryHostを更新する。

候補packでは基準版に`-ci.<run>.<attempt>`や`-rc.<id>`を付け、helpにも実際のpackage版を表示する。VSIXの基準版とCLIの基準版は一致させる。基準版の設定だけで安定公開済みとは判定しない。固定Syntamark runtime/source pairがpreviewの間は、release tagからの公開を拒否する。公開する最終版と配布先の判断は別に行う。

## 起動と取得

保存済みindexを読むCLI本体は.NET 10 runtimeを使い、warm queryでSDK探索、restore、build、network取得を行わない。`dotnet exec SharpDeps.Cli.dll`やtool shimの直接起動はruntimeだけで可能だが、local manifestの`dotnet tool run`と`dnx` launcherはSDKを必要とする。toolのinstall/restoreや初回`dnx`はpackage取得とlocal cache更新があり得る。`dnx`は.NET SDK 10.0.100以降が必要で、toolを動かすruntimeと解析targetのSDKも別に確認する。SDK/runtime不足でtool本体が起動できない場合、dotnet hostの出力をCLI JSONとしてparseしない。

次はexact版指定の例である。実行前に、指定版の提供状況をレジストリまたはGitHub Releaseで確認する。

```powershell
dnx SharpDeps.Cli@0.4.0 -- status --root 'D:\work\repo' --quiet
```

ローカルfeedからの取得例は、利用者が選んだ新しいtool作業directoryと、正確なnupkg・local-only NuGet.Configを置いたfeedを使う。以下のinstallとmanifest作成は例であり、query依頼から自動実行しない。

```powershell
Set-Location 'D:\work\sharpdeps-tool'
dotnet new tool-manifest
dotnet tool install SharpDeps.Cli --local --version 0.4.0 --configfile 'D:\work\cli-feed\NuGet.Config'
dotnet tool run sharpdeps -- status --root 'D:\work\repo' --quiet
```

既存manifestや認証設定を上書きしない。latest、wildcard、暗黙の別版fallbackを使わない。実pack、local manifestへのinstall、実CLIの検証結果はtaskの検証証跡で確認する。

## commandsと引数

`help`と`doctor`を除くworkspace commandには絶対local pathの`--root`を指定する。root外、UNC、symlink/junction/reparse pathは受け入れない。`--index`はroot内のindex pathで、省略時は`.sharpdeps/index.sqlite`。index pathを変更して旧indexを上書きする操作をqueryのfallbackにしない。

| command | 対象と実行境界 |
|---|---|
| `index`, `update` | root内の`sln`/`slnx`/`csproj`を`--target`で指定し、`--trusted`を明示する。解析・書込み。updateは[差分更新契約](incremental-update-v1.ja.md)で閉じた入力を照合できる場合にMarkdown差分を処理し、それ以外はfull rebuildへ戻す |
| `serve` | `--trusted`を明示し、root/indexのownerを起動する。更新は専用local IPCで受理する。`--watch --target TARGET`で同ownerに保存入力の監視を接続し、`--watch-resync-seconds`（1〜3600、既定30）で周期full抽出を行う |
| `coordinator-status` | ownerの状態を専用IPCで読む。保存Queryのstatusやfreshnessとは別 |
| `status`, `search`, `symbol`, `callers`, `callees`, `impact`, `context` | committed indexのQuery。untrustedでも保存値の読取りは可能。`--trusted`を指定したcontextだけが検証済みsource snippetを読める |
| `query --kind search\|symbol\|callers\|callees\|impact\|context\|status` | 同じQueryへのalias |
| `explore` | `--term`を入口に保存済み実装と参照根拠を予算内でまとめる。既定は`--format audit`で、`agent`も選べる。source本文には`--trusted`が必要 |
| `install`, `uninstall` | `--client copilot-cli`の明示設定操作。rootと絶対パスの`--config-file`／`--instructions-file`を指定する。installには既存実行ファイルの`--mcp-command`も必要。`--dry-run`で書込み前に確認できる。既存ファイルは所有する未変更の組だけを扱い、uninstallは隣接backupを残す |
| `doctor` | runtimeと実装の境界を表示する。SDK/MSBuildの実probeは行わず、`sdkProbe: "not-run"`を返す |
| `help` | commandと引数の案内 |

解析の選択は`--configuration`（既定Debug）、`--platform`、繰返し`--project PATH`、`--tfm`で指定する。TFM選択にはproject選択が必要で、曖昧な複数project/TFMを自動確定しない。選択projectもroot内に置く。`--timeout-seconds`は既定1800、最大3600で、通常は共有ownerへの応答待ち期限である。短命ownerは期限後も受理済み処理の終了を待つため、process全体の上限ではない。`--quiet`はstderr progressを止める。`--request-id`は応答との照合に使う。

`index`／`update`の`--timeout-seconds 0`は期限のない専有実行を明示する。既存ownerへの接続や共有ownerの起動は行わず、OS writer leaseを取得できなければ`INDEX_BUSY`で終了する。SDK・入力・公開時の検証は通常と同じで、呼出し側の取消を解析とcommitへ渡す。取消は協調的であり、同期処理の即時停止は保証しない。応答は`coordinatorState: "exclusive"`を返す。`serve --watch`とowner IPCは0を受理せず、共有ownerの3600秒期限は変わらない。

watchなしの`serve`には解析選択を付けない。watchの解析期限もownerの3600秒上限を使う。Markdown入力は`.md`／`.markdown`／`.mdx`を扱う。MDXは保存された静的Markdown本文として解析し、式・JSX・importの評価やrenderは行わない。watchの収束はlive Query freshnessを証明せず、保存QueryのPartial／unverifiedと明示更新契約を保持する。範囲と回復方法は[coordinator契約](coordinator-ipc-v1.ja.md)を参照する。

Queryのselectorは次のとおり。`--term`、`--id`、comma区切りの`--ids`を同時指定しない。

`explore`はこの表とは別のtyped探索で、`--project-id`、単一`--variant`、`--path-prefix`、`--scope-policy discover|unique|prefer-debug`を使う。通常の名前／pathでの`--scope-policy`の既定は、project／variantを明示しなければ`discover`、明示した場合は`unique`となる。exactな保存ID、`--path-to`、HttpGet literalでは`unique`を既定とし、`discover`の明示を拒否する。typed Query API自体の既定は`unique`のままである。`explore Service`と`explore --term Service`は同じ入口であり、併用しない。`explore`だけは`--root`を省略でき、その場合は絶対化した現在directoryを使い、祖先directoryを検索しない。indexがなければ選択したroot／indexと回復手順を返し、自動作成しない。

`discover`は同一snapshotからproject／variant別のgroupを最大4件返し、全体で件数・出力・計算の予算を共有する。group間の関係を一つのcall chainへまとめない。aliasはgroup内だけで有効であり、後続要求には対象scopeと保存IDを使う。予算に入らない巨大typeでは、完全に返せる関連memberの保存範囲を候補とし、source返却と省略理由を区別する。

`--query-kind http-get-literal`は保存済みのHttpGet宣言断片との完全一致を調べ、実効URLの一致を保証しない。`--path-to`はsymbol探索で、exactな開始occurrenceから対象occurrenceへの保存済みcall witnessを要求する。`--owner-status`は既存ownerを観測するだけで鮮度の証明ではない。探索形式と根拠の限界は[Exploreの案内](../mcp.md)に従う。

| Query command | 必須selector |
|---|---|
| `status` | selectorなし |
| `search` | `--term`のみ |
| `symbol`, `callers`, `callees` | `--id`または`--ids`のどちらか一つ |
| `impact`, `context` | `--term`、`--id`、`--ids`のいずれか一つ |

`--project-id`、`--path-prefix`、繰返し`--variant`、`--node-kind`でscopeを絞る。statusのscopeは`--variant`だけで、実装されていないproject/path/node-kind filterを拒否する。繰返し`--edge-kind`、`--include-candidates`、`--no-documents`、`--max-edges`、`--max-depth`は`callers`/`callees`/`impact`/`context`で使う。候補の取得と文書関係の除外を明示し、search/symbol/statusにはこれらの探索flagを渡さない。Impactの既定はdependentsで、Impact専用の`--dependencies`は依存先方向を選ぶ。`--page-size`は既定100、`--cursor`は同じquery/世代の次pageへ使う。

`--freshness allow-stale|require-fresh|refresh`、`--require-complete`と、`--max-nodes`/`--max-edges`/`--max-depth`/`--max-milliseconds`/`--max-chars`/`--max-bytes`を使える。Query既定は60nodes、120edges、depth2、1000ms、18000 UTF-16 JSON文字、64000 UTF-8 bytes。Queryと`explore`の`--max-milliseconds 0`は計算期限だけを外し、呼出し側の取消、件数・深さ・出力予算、trustと鮮度の検証は維持する。正の時間枠は10〜30000msで、1〜9は拒否する。設定範囲と探索・certainty・cursorの意味は[Query/context契約](query-context-v1.ja.md)に従う。commandに非対応のflagと、繰返し指定を許可したflag以外の重複を拒否する。

未リリースの開発版`explore` CLIだけは`--prepare-max-milliseconds N`を明示して、saved-index readerのopen・全snapshotの読取り／整合性検証／read-only化に別の準備期限を設定できる。Nは10〜30000msの有限値で、0・範囲外・不正数・重複は拒否する。省略時は従来どおり準備を含む共有の計算期限（既定1000ms）であり、既定値を延長しない。準備が期限内に完了した後、`--max-milliseconds`の計算期限を新たに開始し、探索用の派生データ準備、探索、source／鮮度検証、出力をその枠内で行う。caller取消は両段階に適用する。準備期限切れは`PREPARATION_TIMEOUT`（exit 4）、探索期限切れは`QUERY_TIMEOUT`（exit 4）、caller取消は`CANCELLED`（exit 130）でsourceを返さない。既存の`--max-milliseconds 0`は探索の計算期限だけを外し、明示した有限の準備期限は外さない。同期処理の即時停止を保証するOS制限ではない。このflagは他のQuery commandやMCP schema、既存の公開explore overloadには追加しない。

## JSONと終了コード

Query成功・Query内部errorのstdoutは既存`QueryReply.Json`をそのまま一つのJSON objectとして出す。`apiVersion: "1"`、requestId、snapshot、items/candidates/unresolved、budget、truncated/reasons、diagnostics/errors等を保持し、CLI envelopeで包み直さない。command成功は`apiVersion: "1"`、command、requestId、result、空errorsの別envelopeで返す。Query開始前のCLI errorは同じ識別fieldとerrorsを持ち、resultを省略する。stderr progressをstdoutへ混ぜず、stack traceや秘密をJSON/logへ出さない。

| exit | 意味 |
|---:|---|
| 0 | 成功。空hitも含む |
| 2 | 不正引数、曖昧なtarget/選択 |
| 3 | index未作成、freshness条件未達 |
| 4 | SDK/MSBuild/restore/解析/coverage失敗、index入力の`CLI_ENCODING_UNSUPPORTED`、TIMEOUT、require-completeの打切り・不足 |
| 5 | trust、root containment、source access拒否、保存indexのopen失敗、Query errorの`INVALID_SOURCE_ENCODING` |
| 6 | lock、schema/protocol、cursor失敗 |
| 7 | 内部失敗 |
| 130 | cancellation |

未リリースの開発版CLIはSQLiteの`SQLITE_CANTOPEN`（primary code 14、拡張codeを含む）を`INDEX_OPEN_FAILED`（exit 5）として返し、例外本文・stack trace・内部pathは出さない。これはindexまたは必要なfileを開けない状態で、sandbox拒否・OS ACL拒否・TEMP欠落のいずれかをcodeだけで断定しない。まず現在の権限内で選択したindex、既存WAL/SHM、runtimeの一時directoryの欠落・不正path・利用可能性を確認する。欠落や誤った選択はpermission reviewでは修復できず、既に許可された範囲で選択を直せない場合はblockerを報告する。pathが正しく、sandboxのaccess制限を示す根拠があり、その操作と再試行が許可されている場合だけ、clientの通常の対象限定permission reviewを依頼する。codeだけを根拠に審査や再試行をしない。OS ACL制限の確認時や審査拒否時は停止してblockerを報告し、権限変更・sandbox無効化・index複製やcheckpoint・immutable指定で迂回しない。失敗した個別fileが未特定なら未特定と明記し、許可された通常検索へ戻れる。予算延長はaccess問題の解決にならない。未知の例外は従来どおり`INTERNAL_ERROR`（exit 7）を保つ。

exitとJSONのerrorsをともに確認する。candidate/unresolvedやPartialをResolved/Completeへ補完せず、複数TFMのoccurrenceを混ぜない。truncatedなら返却件数を全件数と呼ばない。errorや打切りを、stdoutにJSONがあることだけで成功扱いしない。

`CLI_MARKDOWN_FACTS_INCOMPLETE`（exit 4）は、不正、打切り、未対応のMarkdown原文構造を拒否する。index／updateは新世代を公開せず、既存のcommitted snapshotを保持する。原文構造が完全な、対応済みopaque／decoded mappingのPartial factsは索引へ取り込み、Partial coverageを保つ。見出しをdecoded identity keyに使うには、その見出し自体の投影が信頼できる必要がある。errorには長さを制限した復旧案内を含め、parserの例外文字列やsource本文をIPCへ転送しない。入力を黙って除外せず、exact hash、runtime pin、durable section identityの検査を維持する。

[応答schema](../schemas/cli-response-v1.schema.json)はCLI envelopeと実Query DTOのwire構造を記述する。command別result内部は拡張可能なJSON objectで、各commandの成功条件、hash/spanの整合、状態の正しさ、最終JSONのUTF-8 byte予算はschemaだけで証明しない。JSON Schemaの文字長とQueryのUTF-16計測も区別する。

## 鮮度、sourceと権限

trusted更新は[coordinator／IPC契約](coordinator-ipc-v1.ja.md)でownerへ接続する。trusted `require-fresh`は同じ保存世代を明示更新したlive ownerへ入力集合・設定の再照合を依頼する。closed-input proofで内容hash、設定/variant/参照/SDKを再確認できる範囲だけ成功し、owner不在・再起動後の証明なし・別世代・更新中・未知の入力閉包・不一致は条件未達のままとする。Queryはownerを起動せず、ReadSavedの読取りにこの証明を必須としない。Query snapshotの`coordinatorState`は既存の`not-connected`を保持し、一回の入力証明と継続接続の状態を分ける。`refresh`は`UPDATE_REQUIRED`を保持し、mtime/保存bool/ownerの生存だけでverified-currentと称しない。

source snippetはmanifest/evidenceのcontent hashと同じ一回のbounded byte bufferを照合してから、元UTF-16のzero-based半開区間を切る。BOM/CRLFを除去しない。不一致は`SOURCE_CHANGED_SINCE_SNAPSHOT`でsnippetを省略する。unknown/generated spanを実source位置へ推測補完しない。untrusted ReadSavedではsourceを読まない。

index/updateの選択入力はstrict UTF-8と、BOM付きUTF-16 LE/BEを扱う。BOMを含むdecoded本文を保持し、hashとbyte長はraw bytesから計算する。C#の実BOMがRoslyn本文で消費された場合は、実bytesをdecodeした本文とcompilation本文の完全一致を確認して、そのsourceだけUTF-16位置を1つ補正する。SDKが生成する物理fileも同じ検査を通す。不正encoding・decode後のNUL/binary、root外のlinked source、物理fileとして照合できないgenerator sourceは明示的に失敗させ、前のcommitted世代を保つ。root内のMarkdown/configurationを選び、`.git`/`.sharpdeps`/`bin`/`obj`/`node_modules`のdirectory walkを除外する。参照された物理compile fileはobj内でも別途照合する。walkは100,000 entries、入力は64 MiB/file・256 MiB合計、保存search textにもIndexの上限を適用する。

`--trusted`は利用者が許可した解析/sourceアクセスを明示するflagで、repo本文から権限を得る仕組みではない。MSBuild/project/analyzer/generatorが実行し得るコードのsandboxや通信禁止を保証しない。install/restore、解析・更新、外部送信の許可を分け、無許可の自動update/install・全repo scan・無制限grepを行わない。固定Markdown pairの再pack、同版上書き、sourceの公開送信をCLI/skillから自動実行しない。

repo用[sharpdeps-query skill](../../skills/sharpdeps-query/SKILL.md)は、索引範囲内のsymbol・依存関係・保存call pathの質問で利用可能なexploreを優先し、単純な文字列検索や範囲外では通常検索へ戻る。status/search/contextの固定列は要求しない。利用者のopt-outを尊重し、無効化した設定を自動復元しない。導入・無効化は[AI向け設定の案内](../mcp.md#agent-routing-and-opt-out)に従い、personal skillや既存ユーザー設定を自動変更しない。

新規索引はSQLite schema 3で、現行readerはschema 1／2／3を受理する。古い索引をその場で変換せず、必要な本文保存機能を使う場合は旧DBとsidecarを保持した別パスへ再作成する。生成本文はschema 2以降、パッケージ本文はschema 3が必要である。VS Codeは同梱QueryHostで読むため、外部CLIの更新だけではreaderの互換性は変わらない。[索引保存契約](index-storage-v1.ja.md)と[操作ガイド](../guide.ja.md#保存済み索引を使う)を参照する。
