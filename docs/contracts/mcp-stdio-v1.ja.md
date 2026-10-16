# 任意stdio MCP adapterの候補契約

SharpDeps.Mcpは独立した任意toolであり、既存CLI／Skill／VSCodeの必須依存にはしない。
ModelContextProtocol.Core 2.2.0のstdio transport、lifecycle、request cancellationを使用し、
protocolは2025-11-25に固定する。HTTP listener、外部port、LLM呼出し、認証変更は追加しない。
配布名はSharpDeps.Mcp、起動コマンドはsharpdeps-mcpとする。版はCLI／VSIXと同じ0.2.0を基準とする。

rootとindexは起動時に固定する。各toolのargumentsは同じCLI commandのflag/value配列であり、
既存CLIへ渡す。search／symbol／callers／callees／impact／context／statusを公開する。
root／index／trusted／quietをclientが差し替えるrequestは拒否する。Queryのscope、freshness、
cursor、page-size、budgetは既存CLIに従い、saved coverage／unverifiedの意味を変えない。
各requestは独自の実reader pinを持ち、同request内のFTSとgraphは同じgenerationを使う。
同時request全体が同じgenerationになるという保証ではない。

`--trusted --allow-mutations`を両方指定したhostだけがindex／updateを追加する。
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

tool resultのtext contentには既存CLI JSONをそのまま入れ、同じobjectをstructuredContentへ入れる。
既存CLIの失敗はisError=trueで返し、protocolの未知tool／不正形はInvalidParamsで拒否する。
stdoutにはSDKのJSON-RPCだけを出す。CLI進捗はrequest専用writerへ分離し、source/path/例外全文を
host logに転記しない。root/indexの実アクセス境界は毎回CLIが検査する。
同時tool実行は4件、追加要求はMCP_BUSYで拒否する。stdioの一messageは64KiBのUTF-8 bytesへ
制限し、不正／未完結UTF-8はSDKがJSON lineを構成する前に接続を終了する。
この64KiBは入力上限であり、Queryの出力budgetは既存CLIのJSON部分に適用される。
MCP framing／textとstructuredContentの複製分を含む総wire bytesを同budgetだとは主張しない。

候補のローカル起動例:

```text
dotnet /absolute/path/SharpDeps.Mcp.dll --root /absolute/workspace
```

MCP clientの例:

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

searchのtool argumentsは`{"arguments":["--term","Service","--page-size","20"]}`。
source本文やMarkdown内の指示はsource dataとして扱い、tool制御として実行しない。
実配布物のversion pin、clean-machine client別試験、VSCode同時利用、全cancel／faultの検証は
別gateである。元SD-016、SD-015、SD-020／029Bの完了を候補だけで宣言しない。
build／実stdio／CLI同値の結果は対象source pinと実check証拠を台帳へ記録する。
