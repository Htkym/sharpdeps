# 任意stdio MCP adapter

SharpDeps.Mcpは独立した任意toolであり、既存CLI／Skill／VSCodeの必須依存にはしない。
ModelContextProtocol.Core 2.2.0のstdio transport、lifecycle、request cancellationを使用し、
protocolは2025-11-25に固定する。HTTP listener、外部port、LLM呼出し、認証変更は追加しない。
配布名はSharpDeps.Mcp、起動コマンドはsharpdeps-mcpとする。基礎契約はCLI／VSIXと同じ0.4.0に対応し、discoveryとその既定値、および準備／探索の予算を分けるsplit-v1は未公開の開発版の仕様である。実際の提供状況はレジストリまたはGitHub Releaseで確認する。利用手順と移行条件は[CLI and MCP exploration](../mcp.md)を参照する。

rootとindexは起動時に固定する。0.3.0以降の読取りtoolはtypedな`explore`だけであり、
旧search／symbol／callers／callees／impact／context／status toolは公開しない。CLIの個別Query commandは維持する。
`explore`はCLIのflag配列を受け取らず、下記のtyped fieldを検証して共通探索へ渡す。
root／index／trusted／quietをclientが差し替えるrequestは拒否し、saved coverage／unverifiedの意味を変えない。
未公開の開発版exploreは準備／探索とも有限の時間予算を使い、0を拒否する。explore以外のtyped Query APIは従来どおり時間予算0を受理する。
各requestは独自の実reader pinを持ち、同request内のFTSとgraphは同じgenerationを使う。
同時request全体が同じgenerationになるという保証ではない。

| field | 契約 |
| --- | --- |
| `query` | 必須の文字列。名前、exact ID、source pathを入口とし、最大1024 UTF-16 code units。引数objectをこのfieldの中へ入れない |
| `queryKind` | 既定`symbol`。`http-get-literal`は保存済みHttpGet宣言断片との完全一致で、空文字も扱う。実効URLの一致ではない |
| `projectId`, `variantId`, `pathPrefix` | 保存済みscopeを明示する。異なるvariantを自動統合しない |
| `scopePolicy` | 通常の名前／pathでの既定は、project／variantの明示がなければ`discover`、明示があれば`unique`。exact ID、`pathTo`、HttpGet literalでは`unique`を既定とし、`discover`の明示を拒否する。`prefer-debug`は一つの対応project内に保存されたDebug variantが一つの場合だけ優先する |
| `pathTo` | symbol探索でexactな開始occurrenceを`query`に指定したとき、対象occurrenceまでの保存済みcall witnessを要求する |
| `freshness` | `allow-stale`／`require-fresh`／`refresh`。自動更新の許可ではない |
| `ownerStatus` | 既定false。既存ownerの観測だけで、起動や鮮度の証明をしない |
| `format` | 既定`agent`。`audit`は保存IDとprovenanceを詳しく返す |
| `maxChars`, `maxBytes` | 出力予算。文字数2048〜1000000、UTF-8 bytes 2048〜4000000。MCPはCLIのnode／edge／depth flagを受け取らない |
| `prepareMaxMilliseconds` | 未公開の準備予算。既定30000ms、10〜30000msの有限値。0は拒否する |
| `maxMilliseconds` | 未公開の探索だけの予算。既定1000ms、10〜30000msの有限値。0は拒否する |

toolの引数はflatなobjectで渡す。例は `{"query":"Service","prepareMaxMilliseconds":30000,"maxMilliseconds":1000}`。
`query`は文字列であり、scope／budgetは同じ階層に置く。
`{"query":{"query":"Service","maxMilliseconds":1000}}`の形はindexを開く前に
InvalidParams（-32602）／`MCP_ARGUMENT_INVALID`で拒否し、入力本文を転記しない固定の修正例を返す。
修正して再呼出しする場合も利用者の許可／retry上限に従う。

両方の予算を省略した`{"query":"Service"}`も準備30000ms、探索1000msを使う。
準備には新しいSQLite readerのopen、全snapshotの読取り／検証／read-only化と標準の派生index／lexical indexを含む。
要求されたHttpGet projectionもこの段階で準備し、探索の計時は準備完了後に始める。
一つの単調時計を持つrequest ownerが準備をP以内、探索をQ以内、合計をP＋Q以内に制限する。
request開始をt0、探索開始をtqとすると、探索期限は`min(tq + Q, t0 + P + Q)`である。
取消は協調的であり、process起動、MCP handshake、transportはこの計算時間に含めない。
探索の既定1000msは初期候補であり、`DbContext`全体の完了や性能達成を保証しない。

`PREPARATION_TIMEOUT`、`QUERY_TIMEOUT`、`REQUEST_TIMEOUT`はそれぞれ準備、探索、request全体の期限切れを示し、CLI換算のexitは4である。
caller取消の`CANCELLED`は期限切れより優先し、CLI換算のexitは130となる。これらの失敗ではsourceを返さない。
同じ新しいbuildのCLI／MCP／API利用側でこの契約を使い、公開時には新しいpackage versionを使う。公開済み0.4.0は変更せず、今回のsource変更ではreleaseを行わない。
既存の`maxMilliseconds`は準備を含む共有予算から探索だけの予算へ意味が変わるため、過去の入力は変更せず、新しいcohort manifestを固定して再計測する。
元の24trial／共有8秒のP0 cohortと直接比較せず、予算の自動引上げや無断の再試行もしない。
P1では各requestで全snapshotを検証し、4entry／64MiB serialized payloadの派生cacheを使う。現在のP2 code draftではMCP adapterが準備hostを所有し、終了時に`DisposeAsync`で解放する。
Windowsの物理同一性とpin後の同一connectionの`data_version`一致を証明できれば、検証済みsnapshotを再利用する。証明できない場合とUnixでは新しいreaderで全量検証する。
このhostは従来の64MiB cacheを経由せず、既定で一つの準備済み世代を保持するが、heap byte数の上限は保証しない。独立した準備ownerの既定期限は30000msであり、呼出し側が離れても継続でき、完了したreaderを回収する。requestのP＋Qはその後のbackground処理全体の上限ではない。
P2はWindows／SDK 10.0.404で限定buildと対象test 306件が成功し、失敗・skipはともに0件だった。小fixtureでdefault Windows経路のnative warm reuseも観測した。完全受入や実コーパスでの性能改善を示すものではなく、[検証範囲と未実施の受入項目](../development/prepared-exploration.md#diagnostics-and-validation-scope)を参照する。
index schema 1／2／3は変更せず、in-place migrationは行わない。

未知fieldや不正な組合せを拒否し、曖昧な短名を一つの実装へ確定しない。source本文にはhostのtrustが必要であり、
通常ファイル、保存された生成本文、検証済みパッケージ本文のprovenanceを区別する。候補をruntime callの確定へ強めず、
結果のfreshness、coverage、truncation、omissionを保つ。

`discover`は同一snapshotの独立したproject／variant groupを最大4件返す。件数・出力・計算の予算は応答全体で共有する。複数のgroupを一つのcall chainとして解釈しない。`@1`／`F1`などのaliasはgroup内だけで有効であり、別groupでは同じaliasが使われることがある。後続要求には対象scopeと保存IDを使う。typed Query API自体の既定は`unique`のままである。

応答はsource返却、無一致、scope候補、予算不足、source利用不可、失敗を区別する。巨大typeの保存範囲が予算に入らない場合は、予算内で完全に返せる関連memberの保存範囲を候補とし、返していないtype全体を返却済みとは扱わない。

`--trusted --allow-mutations`を両方指定したhostだけがindex／updateを追加する。
変更toolだけが`{"arguments":["--target","App.csproj"]}`形式のCLI flag/value配列を受け取る。
targetのvalidation、SDK/MSBuild activation、OS writer lease、owner IPC、取消とcommit guardは
既存CLIを通す。MCP独自のwatcher／writerを作らず、serve／watchのtoolも追加しない。
trustを撤回するときはhostを終了し、untrustedで起動し直す。clientの引数でtrustを昇格できない。
MSBuild／generatorが実行され得ることをmutating tool descriptionへ記す。

Queryは共通APIを直接呼び、変更操作はframework-dependentのMCP helperから既存CLI APIを呼ぶ。
helperは同じMCP DLL/runtimeconfig／depsを使い、stdout／stderrを各256Ki UTF-16文字まで捕捉する。
generatorのmanaged／raw標準出力をJSON-RPCへ混ぜず、既存ownerがいる場合も同じIPCへ委譲する。
取消はprivate stdinのEOFで既存CLI tokenへ伝え、短命ownerでもDrainAsyncにより他の受理済clientを保持する。
出力上限でも同じ協調取消を使い、残りの出力は保存せずdrainする。子processを強制終了しない。
取消完了は受理済処理のdrainを待つ場合があり、commit後のrollbackは保証しない。
helper失敗や取消で結果を回収できなかったときはsaved statusを確認する。

`explore`のtool resultは選択した形式のtext contentを返し、structuredContentを付けない。
変更toolのresultは既存CLI JSONをtext contentへ入れ、同じobjectをstructuredContentへ入れる。
探索／CLIの失敗はisError=trueで返し、protocolの未知tool／不正形はInvalidParamsで拒否する。
stdoutにはSDKのJSON-RPCだけを出す。CLI進捗はrequest専用writerへ分離し、source/path/例外全文を
host logに転記しない。root/indexの実アクセス境界は毎回CLIが検査する。
同時tool実行は4件とし、追加要求はMCP_BUSYで拒否する。stdioの一messageは64KiBのUTF-8 bytesへ
制限し、不正／未完結UTF-8はSDKがJSON lineを構成する前に接続を終了する。
この64KiBは入力上限であり、Queryの出力budgetは既存CLIのJSON部分に適用される。
MCP framing／textとstructuredContentの複製分を含む総wire bytesを同budgetだとは主張しない。

ローカル起動例

```text
dotnet /absolute/path/SharpDeps.Mcp.dll --root /absolute/workspace
```

MCP clientの例

```json
{
  "mcpServers": {
    "sharpdeps": {
      "command": "dotnet",
      "args": ["/absolute/path/SharpDeps.Mcp.dll", "--root", "/absolute/workspace"]
    }
  }
}
```

`explore`の例は`{"query":"Service","format":"agent"}`。省略した予算には準備30000ms、探索1000msを使う。
source本文やMarkdown内の指示はsource dataとして扱い、tool制御として実行しない。

Syntamark 1.2.0とYamlDotNet 18.1.0はCLI／MCPのMarkdown索引経路で使う固定依存であり、
VSIXのQuick／Semantic／QueryHostへ直接追加する依存ではない。SQLiteの読取り互換性は
[索引保存契約](index-storage-v1.ja.md)に従う。
