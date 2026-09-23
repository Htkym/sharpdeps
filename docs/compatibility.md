# 対応表（SDK・TFM・ロード経路）

SD-003の試作で**実測した**範囲だけを「確認済み」とする。未実施は未実施と書く。
数値・バージョンは実測時のもので、環境が変われば再測定が必要。

## 実測環境

| 項目 | 値 |
|---|---|
| OS | Windows 11（10.0.26200） |
| .NET SDK | 10.0.300 |
| MSBuild | 18.6.3+caa81fa4971f74880cdab61990cb1b11420939ec |
| Roslyn（Microsoft.CodeAnalysis） | 5.9.0.0 |
| Microsoft.Build.Locator | 1.11.2（アセンブリ版 1.0.0.0） |
| ホスト ランタイム | .NET 10.0.8（framework-dependent） |
| 計測日 | 2026-09-23 |

証拠：`docs/implementation/v0.1.0/evidence/semantic-probe-baseline.json`

## 採用パッケージ（`analyzer/Directory.Packages.props` で固定）

| パッケージ | 版 | 理由 |
|---|---|---|
| Microsoft.Build.Locator | 1.11.2 | MSBuildLocatorの現行安定版。MSBL001チェックを内蔵 |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 5.9.0 | Roslyn本体系列を揃える（MSBuild 18系と同時代） |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.9.0 | 同上。MSBuildWorkspaceを含む |
| Microsoft.Build.Framework | 17.11.48 | MSBL001対応で `ExcludeAssets="runtime"` を付けるためだけに参照。版はWorkspaces.MSBuildの推移参照と一致させる |
| Microsoft.NET.Test.Sdk | 17.14.1 | テスト実行 |
| xunit / xunit.runner.visualstudio | 2.9.3 / 2.8.2 | 実測で安定動作した組み合わせ |

Roslyn 4.14.0（Quickのfile-based app）と 5.9.0（Semantic）は系列が異なる。
別プロセスで動くため共存でき、実測でも競合していない。

## 確認済みの項目

| 項目 | 結果 | 根拠 |
|---|---|---|
| `.sln` のロード | 成功（5プロジェクト） | 統合テスト `LoadsMultiTargetedVariantsSeparately` |
| `.slnx` のロード | 成功（同一構成、5プロジェクト・4参照） | 統合テスト `LoadsSlnxAndSingleProjectTargets` |
| 単一 `.csproj` のロード | 成功（参照先もソースとしてロード） | 同上 |
| multi-TFM（`netstandard2.0;net10.0`） | 2バリアントとして別々にロード | 統合テスト |
| TFMの識別 | MSBuildが渡すプリプロセッサシンボルから導出（`NET10_0` 等）。名前の `(net10.0)` 接尾辞は照合用 | `SemanticLoader.TryGetTargetFramework` |
| 異なるTFMのProjectReference | `net8.0 → netstandard2.0` を `compatible` として解決、`net10.0 → net10.0` は `exact` | 統合テスト `ResolvesReferenceToTheCompatibleVariant` |
| 条件付き `ProjectReference` | `Debug` では辺なし、`Release` では辺あり | 統合テスト `EvaluatesConditionalProjectReferencesPerConfiguration` |
| `Compile Include`（linked file） | 評価済み入力に含まれる（`shared/Shared.cs`） | 統合テスト `UsesTheEvaluatedCompileItems` |
| `Compile Remove` | 評価済み入力から除外される（`Removed.cs`） | 同上 |
| `Directory.Build.props` の `DefineConstants` | ロードしたプロジェクトのシンボルに反映 | 同上（`SEMANTIC_BASELINE`） |
| `global.json` | 検出し、要求SDKと登録SDKを記録 | 統合テスト `RecordsTheEnvironmentThatWasUsed` |
| 未restoreのプロジェクト | 例外にせず、コンパイルエラー件数と `semantic.compilationErrors` で報告 | 統合テスト `ReportsAnUnrestoredProjectWithoutThrowing` |
| 非C#プロジェクト | ロード対象外として件数と理由を記録（`semantic.nonCSharpProjects`） | 実装（fixtureでは未使用のため未検証） |
| 配布物にMSBuild本体を含めない | publish出力に `Microsoft.Build*.dll` が無い（28ファイル / 16.4 MB） | `analyzer/bin/semantic` の実測 |
| MSBuild未登録でのロード | `InvalidOperationException` で拒否 | 実装 |
| 対象が存在しない | `FileNotFoundException` | 統合テスト `FailsCleanlyForAMissingTarget` |

## 未確認の項目

| 項目 | 状態 |
|---|---|
| SDKが無い環境でのSemantic初期化失敗 | **未検証**。`DOTNET_ROOT` を空に向けても SDK 探索は成功したため、この方法では再現できなかった。ランタイムのみの環境（SD-029のVSIX検証や別VM）で確認する |
| Quickの初期化失敗がSemanticへ波及しないこと | プロセス分離の設計で担保するが、実機の失敗注入は未実施（SD-014） |
| 旧式.NET Framework（`packages.config`・非SDKスタイル） | **未検証**。対応表へ追加しない |
| VB / F# / C++ の意味解析 | 対象外。Quickの既存表示のみ |
| Linux / macOS | 未実施 |
| マルチTFMで `TargetFrameworks` に同一TFMが重複する場合 | 未実施 |
| NuGet自動restore | 実装しない（初期値OFF）。assets不足は診断のみ |

## 実測で判明した制約

1. **Roslynの `Project` にTFMプロパティが無い。** `Project.TargetFramework` は5.9.0に存在しない。
   TFMはMSBuildが渡すプリプロセッサシンボル（`NET10_0` / `NETSTANDARD2_0`）から導出し、
   出力パスのTFMセグメントを代替とする。どちらも得られない場合は「未解決」として記録し、
   名前だけで結合しない。
2. **推移的な `ProjectReference` はワークスペースに現れない。** `Infrastructure.Tests → Infrastructure → Domain`
   の構成で、`Infrastructure.Tests` の `ProjectReferences` に `Domain` は含まれない
   （実測。コンパイルも `CS0246` になる）。SD-007では推移参照を自前で解決するか、
   評価済み参照アセンブリを使う必要がある。fixtureでは明示参照を追加して基準を保った。
3. **`Microsoft.Build.Framework` を参照しないとビルドが失敗する。** MSBuildLocator 1.11.2 の
   `MSBL001` が `ExcludeAssets="runtime"` + `PrivateAssets="all"` を要求する。
4. **多TFMプロジェクトの名前は `Domain(net10.0)` になる。** 変種の識別には使えるが、
   名前だけをIDや結合キーにしない（v2の `variantId` はパス+TFM+構成から作る）。
5. **ロードはプロジェクトの `obj/` へ書き込む。** 未restoreのプロジェクトでも、design-time buildが
   `obj/<Config>/<TFM>/` に `AssemblyInfo.cs` などを生成する（`project.assets.json` は作られないため
   未restoreのまま）。ユーザーソースは変更しないが、`obj/` は変更されうる。読み取り専用を前提に
   しない。解析前のTrust確認と、`obj/` を無視するGit設定が前提になる。
