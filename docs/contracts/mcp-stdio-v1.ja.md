# 任意stdio MCP adapter

SharpDeps.Mcpは独立した任意toolであり、既存CLI／Skill／VSCodeの必須依存にはしない。
ModelContextProtocol.Core 2.2.0のstdio transport、lifecycle、request cancellationを使用し、
protocolは2025-11-25に固定する。HTTP listener、外部port、LLM呼出し、認証変更は追加しない。
配布名はSharpDeps.Mcp、起動コマンドはsharpdeps-mcpとする。基礎契約はCLI／VSIXと同じ0.4.0に対応し、discoveryとその既定値は未公開のv0.5開発版の仕様である。実際の提供状況はレジストリまたはGitHub Releaseで確認する。利用手順と移行条件は[CLI and MCP exploration](../mcp.md)を参照する。

rootとindexは起動時に固定する。0.3.0以降の読取りtoolはtypedな`explore`だけであり、
旧search／symbol／callers／callees／impact／context／status toolは公開しない。CLIの個別Query commandは維持する。
`explore`はCLIのflag配列を受け取らず、下記のtyped fieldを検証して共通探索へ渡す。
root／index／trusted／quietをclientが差し替えるrequestは拒否し、saved coverage／unverifiedの意味を変えない。
Query／exploreの時間予算0は計算期限だけを外し、MCP requestの取消と件数・出力予算は維持する。
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
| `maxChars`, `maxBytes`, `maxMilliseconds` | 出力と計算の予算。文字数2048〜1000000、UTF-8 bytes 2048〜4000000、時間10〜30000msまたは0。MCPはCLIのnode／edge／depth flagを受け取らない |

toolの引数はflatなobjectで渡す。例は `{"query":"Service","maxMilliseconds":8000}`。
`query`は文字列であり、scope／budgetは同じ階層に置く。
`{"query":{"query":"Service","maxMilliseconds":8000}}`の形はindexを開く前に
InvalidParams（-32602）／`MCP_ARGUMENT_INVALID`で拒否し、入力本文を転記しない固定の修正例を返す。
修正して再呼出しする場合も利用者の許可／retry上限に従う。

省略時の共有計算予算は1000msで、保存indexの読取り／全snapshot検証と探索を含む。
大きなindexやcold読取りでは目的に合う有限の`maxMilliseconds`を明示する。
8000msは限定ケースの例であり、完了保証や既定1秒での性能達成ではない。
CLI-onlyの別準備予算fieldは追加しない。

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

`explore`の例は`{"query":"Service","format":"agent","maxMilliseconds":1000}`。
source本文やMarkdown内の指示はsource dataとして扱い、tool制御として実行しない。

Syntamark 1.2.0とYamlDotNet 18.1.0はCLI／MCPのMarkdown索引経路で使う固定依存であり、
VSIXのQuick／Semantic／QueryHostへ直接追加する依存ではない。SQLiteの読取り互換性は
[索引保存契約](index-storage-v1.ja.md)に従う。
