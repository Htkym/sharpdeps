# ADR 0001: Semantic解析のロード方式

- 日付：2026-09-23
- 状態：採用（SD-003の実測にもとづく。SD-007で本実装）
- 関連：計画書 4.1・6.2・6.3、8章 SD-003/SD-005/SD-007
- 成果物：`analyzer/src/SharpDeps.Analysis.Roslyn/`、`analyzer/src/SharpDeps.SemanticHost/`、`docs/compatibility.md`

## 背景

v0.0.4の解析器は構文（`using` と `ProjectReference` の宣言）だけを読む。v0.1.0では
実際のシンボル参照を根拠として扱うため、MSBuild評価とRoslyn Compilationが必要になる。
一方でQuickは「SDK不要」を維持しなければならず、Semanticの初期化失敗がQuickへ波及してはならない。

## 決定

### プロセス分離

- Semanticは別プロセス（`SharpDeps.SemanticHost`）で動かす。Extension HostへRoslyn/MSBuildを読み込まない。
- Quickは従来どおりfile-based app（`analyzer/code-map.cs`）のままとし、MSBuild系の初期化を一切行わない。
- SemanticHostの失敗は終了コードで区別する：`0` 成功（部分成功を含む）、`2` 前提条件不足（SDK/MSBuildが無い）、`1` 想定外エラー。
  呼び出し側は `2` を「Semanticが使えない」として扱い、Quickへ自動フォールバックしない。

### MSBuildの登録

- `Microsoft.Build.Locator` で登録する。登録処理は `SemanticEnvironment.TryRegister` に分離し、
  `NoInlining` を付けてMSBuild型の読み込み順を制御する。ローダーは未登録なら例外で拒否する。
- `Microsoft.Build.Framework` を `ExcludeAssets="runtime"` + `PrivateAssets="all"` で参照する
  （MSBuildLocator 1.11.2 の `MSBL001`）。これにより配布物へMSBuild本体を含めず、
  実行時にSDK側のMSBuildを読み込む。実測でpublish出力に `Microsoft.Build*.dll` は含まれない。
- SDKの自動導入は行わない。SDKが無ければ利用者が対応できる診断を返す。

### ロード経路

- `.sln` / `.slnx` は `OpenSolutionAsync`、単一プロジェクトは `OpenProjectAsync` を使う。
- 評価済みのCompile items、project references、parse/compilation optionsを使い、`*.cs` の再帰走査で入力を決めない。
- `Configuration` は初期値 `Debug`。`Platform` は未指定なら渡さない。
- マルチTFMは**変種ごとに別Projectとして扱う**。TFMをunionしない。
- `ProjectReference` は変種単位で解決し、適合するTFMが無い場合は「未解決」として記録する。
  名前だけで結合しない。

### TFMの取得

Roslyn 5.9.0の `Project` にTFMプロパティは無い。MSBuildが渡すデータから導出する：

1. `CSharpParseOptions.PreprocessorSymbolNames` の `NET10_0` / `NETSTANDARD2_0` 形式（第一候補）
2. 出力パスのTFMセグメント（`.../bin/Debug/net8.0/A.dll`）（代替）
3. どちらも無ければ `null` とし、参照解決は「未解決」にする

`Domain(net10.0)` のようなワークスペース上の名前は照合（クロスチェック）にのみ使い、
一致しなければ `semantic.variantNameMismatch` として記録する。

## 代替案と却下理由

| 案 | 却下理由 |
|---|---|
| Extension Host内でMSBuildWorkspaceを動かす | MSBuild系アセンブリをVS Codeプロセスへ読み込み、他拡張やQuickへ影響する。キャンセル時のプロセス終了もできない |
| QuickもSemanticと同じcsproj構成に統一する | Quickの「SDK不要」要件を満たせない（SDK/MSBuildの初期化が必要になる） |
| 自動でNuGet restoreまで行う | 利用者コードの評価・ネットワーク・実行を伴う。Trustと明示操作が必要（SD-023/SD-007） |
| TFMをプロジェクト名の `(net10.0)` から取る | 表示名であって評価結果ではない。名前変更や他ツールの出力で壊れる |
| 推移的なProjectReferenceをプロジェクト名で辿る | 名前の一致は根拠にならない。評価済みのProjectIdで辿る |
| `MSBuildWorkspace.TryApplyChanges` で推移参照を追加する | **実測でプロジェクトファイルが書き換わった**（SD-007）。ユーザーソースを変更するため採用しない |
| Roslyn 4.14.0（Quickと同一系列）を採用 | MSBuild 18系との組み合わせが未検証。SDK 10.0.300の実測では5.9.0が動作した |

## 推移参照の扱い（SD-007で確定）

MSBuildWorkspaceが公開する `ProjectReferences` は**直接参照のみ**で、実際のビルドで
コンパイラーが見る推移参照は含まれない（実測：`Infrastructure.Tests → Infrastructure → Domain`
で `Domain` が見えず `CS0246`）。

対応：`SemanticLoader` が参照の推移閉包を計算し、**メモリ上のCompilationにだけ**
`Compilation.AddReferences(targetCompilation.ToMetadataReference())` で追加する。

- プロジェクトファイルもワークスペースも変更しない（`TryApplyChanges` は使わない）。
- 追加は変種単位で、同じTFMのProjectインスタンスを辿るため、TFMの混在は起きない。
- 追加件数は `ProjectVariantInfo.AddedTransitiveReferences` と制約
  `semantic.transitiveReferencesAdded` に記録する。
- 参照の循環を検出した場合は `semantic.projectReferenceCycle` として記録し、
  循環する参照は追加しない（無限再帰を避ける）。
- 対象ソース（`.cs`/`.csproj`/`.sln`/`.slnx`/props/global.json）を変更しないことを
  統合テスト `DoesNotModifyTheAnalyzedSources` で固定した。

## プロファイル

`--configuration`（既定 `Debug`）と `--platform` を解析プロファイルとし、
実際に使った値・プロジェクトごとのTFM・`profileHash` を結果へ含める。
`profileHash` が変われば別の解析として扱う（同じ結果を再利用しない）。

## 帰結

- RoslynはQuick（4.14.0）とSemantic（5.9.0）で系列が異なる。プロセスが分かれているため共存するが、
  両者の解析結果の差は「推定と実参照の差」とは別に存在しうる。混同しない。
- 推移的ProjectReferenceは現状ロードされない。SD-007で「評価済み参照アセンブリを使う」か
  「参照元プロジェクトのProjectReferenceを辿って自前で解決する」かを決めて実装する。
  それまで、推移参照に依存する型の解析は不完全になる（`limitations` へ記録する）。
- SDKが無い環境での失敗経路は未検証（`docs/compatibility.md` の未確認項目）。
- ロードはMSBuildのdesign-time buildを通じてプロジェクトの `obj/` へファイルを書き出す
  （実測：未restoreのプロジェクトでも `obj/Debug/net10.0/*.AssemblyInfo.cs` が生成される）。
  対象ソース（`.cs`・プロジェクトファイル等）は変更しないことをテストで固定しているが、
  読み取り専用の操作ではない。Trust確認とキャンセル時の一時領域管理（SD-014）で扱う。
- `analyzer/Directory.Build.props` はSemanticHost側にのみ適用する。file-based appは
  `-p:ImportDirectoryBuildProps=false` で分離し、SD-005でQuickHostへ移すまで現状を維持する。
