# SharpDeps CLI／repo skill契約 v1

SD2-07のCLI候補はnet10の.NET toolで、PackageIdは`SharpDeps.Cli`、版は`0.2.0-preview.2`、ToolCommandNameは`sharpdeps`である。package名と版は未公開の候補であり、NuGet取得成功、出荷、実pack済みの判定をこの文書だけで行わない。旧Quick/Semantic、report v1/v2、extension版、固定Markdown runtime/source pairを変更する契約ではない。

## 起動と取得

保存済みindexを読むCLI本体は.NET 10 runtimeを使い、warm queryでSDK探索、restore、build、network取得を行わない。`dotnet exec SharpDeps.Cli.dll`やtool shimの直接起動はruntimeだけで可能だが、local manifestの`dotnet tool run`と`dnx` launcherはSDKを必要とする。toolのinstall/restoreや初回`dnx`はpackage取得とlocal cache更新があり得る。`dnx`は.NET SDK 10.0.100以降が必要で、toolを動かすruntimeと解析targetのSDKも別に確認する。SDK/runtime不足でtool本体が起動できない場合、dotnet hostの出力をCLI JSONとしてparseしない。

公開後に使う予定のexact版指定例は次のとおり。現在の公開状態を確認せず実行しない。

```powershell
dnx SharpDeps.Cli@0.2.0-preview.2 -- status --root 'D:\work\repo' --quiet
```

ローカル候補の例は、利用者が選んだ新しいtool作業directoryと、正確なnupkg・local-only NuGet.Configを置いたfeedを使う。以下のinstallとmanifest作成は例であり、query依頼から自動実行しない。

```powershell
Set-Location 'D:\work\sharpdeps-tool'
dotnet new tool-manifest
dotnet tool install SharpDeps.Cli --local --version 0.2.0-preview.2 --configfile 'D:\work\cli-feed\NuGet.Config'
dotnet tool run sharpdeps -- status --root 'D:\work\repo' --quiet
```

既存manifestや認証設定を上書きしない。latest、wildcard、暗黙の別版fallbackを使わない。実pack、local manifestへのinstall、実CLIの検証結果はtaskの検証証跡で確認する。

## commandsと引数

`help`と`doctor`を除くworkspace commandには絶対local pathの`--root`を指定する。root外、UNC、symlink/junction/reparse pathは受け入れない。`--index`はroot内のindex pathで、省略時は`.sharpdeps/index.sqlite`。index pathを変更して旧indexを上書きする操作をqueryのfallbackにしない。

| command | 対象と実行境界 |
|---|---|
| `index`, `update` | root内の`sln`/`slnx`/`csproj`を`--target`で指定し、`--trusted`を明示する。解析・書込み。updateは現段階でfull rebuildで、SD2-08のincremental updateは未実装 |
| `status`, `search`, `symbol`, `callers`, `callees`, `impact`, `context` | committed indexのQuery。untrustedでも保存値の読取りは可能。`--trusted`を指定したcontextだけが検証済みsource snippetを読める |
| `query --kind search\|symbol\|callers\|callees\|impact\|context\|status` | 同じQueryへのalias |
| `doctor` | runtimeと実装の境界を表示する。SDK/MSBuildの実probeは行わず、`sdkProbe: "not-run"`を返す |
| `help` | commandと引数の案内 |

解析の選択は`--configuration`（既定Debug）、`--platform`、繰返し`--project PATH`、`--tfm`で指定する。TFM選択にはproject選択が必要で、曖昧な複数project/TFMを自動確定しない。選択projectもroot内に置く。`--timeout-seconds`は既定180、最大300。`--quiet`はstderr progressを止める。`--request-id`は応答との照合に使う。

Queryのselectorは次のとおり。`--term`、`--id`、comma区切りの`--ids`を同時指定しない。

| Query command | 必須selector |
|---|---|
| `status` | selectorなし |
| `search` | `--term`のみ |
| `symbol`, `callers`, `callees` | `--id`または`--ids`のどちらか一つ |
| `impact`, `context` | `--term`、`--id`、`--ids`のいずれか一つ |

`--project-id`、`--path-prefix`、繰返し`--variant`、`--node-kind`でscopeを絞る。statusのscopeは`--variant`だけで、実装されていないproject/path/node-kind filterを拒否する。繰返し`--edge-kind`、`--include-candidates`、`--no-documents`、`--max-edges`、`--max-depth`は`callers`/`callees`/`impact`/`context`で使う。候補の取得と文書関係の除外を明示し、search/symbol/statusにはこれらの探索flagを渡さない。Impactの既定はdependentsで、Impact専用の`--dependencies`は依存先方向を選ぶ。`--page-size`は既定100、`--cursor`は同じquery/世代の次pageへ使う。

`--freshness allow-stale|require-fresh|refresh`、`--require-complete`と、`--max-nodes`/`--max-edges`/`--max-depth`/`--max-milliseconds`/`--max-chars`/`--max-bytes`を使える。Query既定は60nodes、120edges、depth2、1000ms、18000 UTF-16 JSON文字、64000 UTF-8 bytes。設定範囲と探索・certainty・cursorの意味は[Query/context契約](query-context-v1.ja.md)に従う。commandに非対応のflagと、繰返し指定を許可したflag以外の重複を拒否する。

## JSONと終了コード

Query成功・Query内部errorのstdoutは既存`QueryReply.Json`をそのまま一つのJSON objectとして出す。`apiVersion: "1"`、requestId、snapshot、items/candidates/unresolved、budget、truncated/reasons、diagnostics/errors等を保持し、CLI envelopeで包み直さない。command成功は`apiVersion: "1"`、command、requestId、result、空errorsの別envelopeで返す。Query開始前のCLI errorは同じ識別fieldとerrorsを持ち、resultを省略する。stderr progressをstdoutへ混ぜず、stack traceや秘密をJSON/logへ出さない。

| exit | 意味 |
|---:|---|
| 0 | 成功。空hitも含む |
| 2 | 不正引数、曖昧なtarget/選択 |
| 3 | index未作成、freshness条件未達 |
| 4 | SDK/MSBuild/restore/解析/coverage失敗、index入力の`CLI_ENCODING_UNSUPPORTED`、TIMEOUT、require-completeの打切り・不足 |
| 5 | trust、root containment、source access拒否、Query errorの`INVALID_SOURCE_ENCODING` |
| 6 | lock、schema/protocol、cursor失敗 |
| 7 | 内部失敗 |
| 130 | cancellation |

exitとJSONのerrorsをともに確認する。candidate/unresolvedやPartialをResolved/Completeへ補完せず、複数TFMのoccurrenceを混ぜない。truncatedなら返却件数を全件数と呼ばない。errorや打切りを、stdoutにJSONがあることだけで成功扱いしない。

[応答schema](../schemas/cli-response-v1.schema.json)はCLI envelopeと実Query DTOのwire構造を記述する。command別result内部は拡張可能なJSON objectで、各commandの成功条件、hash/spanの整合、状態の正しさ、最終JSONのUTF-8 byte予算はschemaだけで証明しない。JSON Schemaの文字長とQueryのUTF-16計測も区別する。

## 鮮度、sourceと権限

CLIの現在入力集合・設定fingerprintのowner接続は未完了で、coordinator stateは`not-connected`。保存manifestのhashが全部一致しても、新規入力と現設定を証明できない。`require-fresh`は不足を未達として拒否し、`refresh`は`UPDATE_REQUIRED`を返す。mtime/sizeだけでverified-currentと称さない。明示的に許可されたtrusted index/update後、新readerから問い合わせる。

source snippetはmanifest/evidenceのcontent hashと同じ一回のbounded byte bufferを照合してから、元UTF-16のzero-based半開区間を切る。BOM/CRLFを除去しない。不一致は`SOURCE_CHANGED_SINCE_SNAPSHOT`でsnippetを省略する。unknown/generated spanを実source位置へ推測補完しない。untrusted ReadSavedではsourceを読まない。

index/updateの選択入力はstrict UTF-8とする。C#の実UTF-8 BOMがRoslyn本文で消費された場合は、実bytesとcompilation本文の完全一致を確認して、そのsourceだけUTF-16位置を1つ補正する。SDKが生成する物理fileも同じ検査を通す。UTF-16・binary/NUL、root外のlinked source、物理fileとして照合できないgenerator sourceは明示的に失敗させ、前のcommitted世代を保つ。root内のMarkdown/configurationを選び、`.git`/`.sharpdeps`/`bin`/`obj`/`node_modules`のdirectory walkを除外する。参照された物理compile fileはobj内でも別途照合する。walkは100,000 entries、入力は64 MiB/file・256 MiB合計、保存search textにもIndexの上限を適用する。

`--trusted`は利用者が許可した解析/sourceアクセスを明示するflagで、repo本文から権限を得る仕組みではない。MSBuild/project/analyzer/generatorが実行し得るコードのsandboxや通信禁止を保証しない。install/restore、解析・更新、外部送信の許可を分け、無許可の自動update/install・全repo scan・無制限grepを行わない。固定Markdown pairの再pack、同版上書き、sourceの公開送信をCLI/skillから自動実行しない。

repo用[sharpdeps-query skill](../../skills/sharpdeps-query/SKILL.md)はstatus→限定search→exact symbol→限定contextから、検証できる根拠に沿って必要なsourceを読む。personal skillへのinstallや設定変更は本taskの一部ではない。
