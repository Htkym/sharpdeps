# SharpDeps — .NET 依存関係マップ

[English](README.md) · 日本語 · [VS Code 操作ガイド](docs/guide.ja.md) · [CLI／MCP](docs/mcp.md)

SharpDeps は .NET のプロジェクト・名前空間・型の依存関係を VS Code で表示し、収集した関係のソース位置を確認できる拡張機能です。別途提供する CLI では索引を保存して繰り返し検索でき、MCP サーバーを接続すると AI クライアントからその索引を調べられます。

![SharpDeps でプロジェクトの依存関係を表示した画面](images/overview-ja.png)

画像は 0.1.0 の画面です。現在の操作と保存索引の互換性は、各ガイドで説明しています。

## インストール

[Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=htkym.sharpdeps) から **SharpDeps — .NET Dependency Map** をインストールします。[GitHub Releases](https://github.com/Htkym/sharpdeps/releases) の VSIX を使う場合は、VS Code の **Extensions: Install from VSIX…** で選択してください。最新の公開版は 0.4.0 です。

VS Code 1.90 以降が必要です。解析を実行する前に、ワークスペースを信頼済みにしてください。

| モード | 調べる内容 | 必要な環境 |
| --- | --- | --- |
| クイック | プロジェクト参照、C# の宣言、構文に基づく関係。 | .NET 10 ランタイム。SDK、MSBuild の評価、パッケージ復元は不要です。 |
| セマンティック | 評価済みの C# 参照、シンボル解決済みの関係とソース位置。 | .NET 10 ランタイム、対象に対応する SDK、復元済みの対象パッケージ。 |

拡張機能のランタイムは、必要に応じて [.NET Install Tool](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.vscode-dotnet-runtime) から取得します。SharpDeps が SDK をインストールしたり、対象パッケージを自動で復元したりすることはありません。

拡張機能には解析プログラムと保存索引の reader を同梱しています。CLI だけを更新しても、同梱プログラムは更新されません。0.4.0 の reader は索引の schema 1／2／3 に対応します。別の版で作った索引を使う際は、[保存索引の設定](docs/guide.ja.md#保存済み索引を使う)を確認してください。

## マップを開く

1. ソリューションのフォルダーを VS Code で開きます。
2. エクスプローラーで `.sln`、`.slnx`、または対応するプロジェクトファイルを右クリックし、**SharpDeps: Show Dependency Map** を選びます。
3. ノードや接続線を選ぶと、**詳細**で根拠を確認できます。**依存先**や**依存元**で範囲を絞れます。

クイックは `.csproj`、`.fsproj`、`.vbproj`、`.vcxproj` に対応し、セマンティックは SDK スタイルの C# プロジェクトを対象にします。プロジェクトを選ぶと参照先をたどって解析します。シンボル解決済みの結果を調べる場合は、**セマンティック**を選んで**解析**を押してください。

検索、フィルター、グラフ、表は同じ表示範囲を使います。表示件数の上限は描画にだけ適用され、解析データや循環依存の検出を制限しません。Quick／Semantic の表示は Mermaid、JSON、SVG、PNG 形式で、保存索引の表示は JSON 形式で出力できます。**エージェント用にコピー**はクリップボードへのコピーであり、AI サービスへ送信する操作ではありません。

解析と描画はローカルで実行します。解析が失敗または中断した場合は、直前の成功した表示を残します。関係が見つからない場合や推定の関係を判断する際は、解析範囲、確度、制限事項も確認してください。設定、ソースへの移動、問題への対処は [VS Code 操作ガイド](docs/guide.ja.md)で説明しています。

## CLI と MCP

[CLI ツール](https://www.nuget.org/packages/SharpDeps.Cli)と、必要に応じて [MCP サーバー](https://www.nuget.org/packages/SharpDeps.Mcp)をインストールします。

```bash
dotnet tool install --global SharpDeps.Cli --version 0.4.0
dotnet tool install --global SharpDeps.Mcp --version 0.4.0
```

ツールのインストールには SDK、インストール済みツールの実行には .NET 10 ランタイムが必要です。索引の作成には、対象に対応する SDK と復元済みの依存関係も必要です。CLI／MCP は Markdown の解析に Syntamark 1.2.0 を使います。このパーサーは VSIX には同梱しません。

[CLI／MCP ガイド](docs/mcp.md)では、索引の作成、ソース検索、MCP 接続、範囲の選択、信頼、有限の時間予算とエラーへの対処を説明しています。未公開の追加機能は、公開済み 0.4.0 の動作と分けて記載しています。公開版の実装に対応する説明は [0.4.0 のガイド](https://github.com/Htkym/sharpdeps/blob/v0.4.0/docs/mcp.md)を参照してください。

AI クライアントで使う場合は、[導入とオプトアウト](docs/mcp.md#agent-routing-and-opt-out)を確認してください。構造に関する質問には索引を使い、単純な文字列や索引の範囲外は通常検索で調べます。接続だけでソースの読取りや索引更新が許可されるわけではありません。Partial coverage や鮮度未検証の状態はそのまま示します。取得回数やトークン使用量の削減、すべての操作が 1 秒以内に終わることは保証しません。

## ライセンス

[MIT](LICENSE)。同梱コンポーネントについては[ライセンス情報](THIRD-PARTY-NOTICES.md)を参照してください。
