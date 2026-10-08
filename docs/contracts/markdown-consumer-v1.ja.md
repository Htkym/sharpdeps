# SharpDepsのMarkdown共有契約 v1採用案

J-01として、[LithoSharpの公開契約案](https://github.com/Htkym/lithosharp/blob/feature/2.0.0/docs/contracts/markdown-v1.ja.md)をSharpDeps 0.2.0候補へ採用する方針を記録します。Bocchiの設計レビュー待ちです。runtime/source package、Markdown harness、graph/SQLite実装が完了したことを示す文書ではありません。

既存実装の基準SHAは`562aacf22822d19cf022a4fdcc67735d7049be85`です。原資料7件の照合は[J-00の共通基準](https://github.com/Htkym/lithosharp/blob/feature/2.0.0/docs/development/2.0.0-baseline.ja.md#計画の参照基準)を引き継ぎ、詳細一覧はローカル記録に保持します。

## 採用する契約とconsumerの責務

| 項目 | SharpDepsが採用する条件 | SharpDepsに残す処理 |
| --- | --- | --- |
| 入力とidentity | supplied text、opaque ScopeId/SourceId/SourceVersion、I/Oなし。Scopeはworkspace instanceごと | filesystem正規化、workspace/project variant/documentの既存graph ID |
| 位置 | 原文全体UTF-16のzero-based半開区間。Unknownはnull | editor表示位置、graph evidenceへの投影 |
| decoded対応 | Linear/Atomic/Unknown segmentsとRawFragments。entityやprefix gapをExactの1:1としない | evidence選択、表示用text結合 |
| facts | immutable frontmatter/headings/sections/links/fences/text regions | Markdown→graph、永続ID、SQLite、Query、client |
| 解決 | Markdown definition内のreference解決まで | route/member/symbol/contextの解決と世代管理 |
| hash | full raw TextHash、独立OptionsHash。SemanticHashを位置cacheへ使わない | indexへの契約metadata記録、再索引判定 |
| coverage | parser facts、raw位置、decoded対応の状態を個別に確認する | 既存report v2のcoverage/capabilitiesと全体のcompleteness |
| 診断 | LMDの新prefix予約と既存LIT。nullable raw span、reason | 既存consumer診断、表示、解析失敗の集約 |
| host | runtimeとsourceを同じcanonical実装/versionから供給する | Quick/Semantic host、既存provider、command/settings、trust |

`analyzer/src/SharpDeps.Analysis.Core/Identity/Identity.cs`の`DocumentId`は既存の永続graph IDです。parserのSourceIdやsection LocalKeyをそのまま置き換えに使いません。LocalKeyは文書内の順序keyで、編集により変わる場合があります。

`analyzer/src/SharpDeps.Analysis.Contracts/AnalysisSnapshot.cs`のreport v2と`schemas/report-v2.schema.json`を維持します。共有DTOのContractVersion 1.0とreport v2のschema versionを混同しません。新graph/SQLite schemaや移行は後続の既存タスクで扱い、旧snapshotのfallbackやrollbackを無断で外しません。

`src/security/trust.ts`の`trustDecision`を維持します。untrusted workspaceで既存analyzerやrestoreを実行しません。保存結果の表示は引き続き許可します。pure text parserの導入だけを理由にQuick/Semanticの実行許可を広げません。HTML/MDX/fenceを実行・描画する責務も共有parserに追加しません。

## 同版artifactの取り込み

runtime IDは`LithoSharp.Markdown`、compiler host用source IDは`LithoSharp.Markdown.Source`とする設計案です。両方ともimmutableな同じV=`2.0.0-preview.N`から始め、stable候補は2.0.0です。SharpDeps自体の次版候補0.2.0とは別に管理します。

net10 runtimeはpublic facadeを使います。netstandard2.0 Generator/Analyzerはinternal portable factsを固定版source packageからCompileへ取り込み、net10 DLLをhostへ読み込みません。hostの必要依存はexact版で直接参照し、初期YamlDotNet 18.1.0とcompiler hostの同梱方針をmanifestで確認します。runtime nupkgの依存も`[18.1.0]`とし、復元結果とロード済み依存identityがmanifestと一致することを確認します。不一致/確認不能なら新entryの起動を拒否し、そのcacheを再利用しません。

source取り込みは`LithoSharpMarkdownIncludeSource=true`でopt-inするbuild targetsで行います。PackageReferenceはexact range `[V]`、PrivateAssets=all、IncludeAssets=buildとします。project property設定前のpropsに条件付きCompileを置きません。buildTransitive、浮動版、latest checkout、手動copyで供給を代用しません。

移管前も両repo hostが同じ固定版source packageを復元します。独立componentはGenerator/Analyzer/siteを参照せずに先にpackし、未公開のlocal feedへ同版pairを置きます。host側はmanifest照合後にrestore/buildします。公開NuGetへのpublishやlatest sourceの直接Compileをbootstrapの前提にしません。

pair manifestから、componentVersion、canonicalSourceHash、ParserVersion、ContractVersion、ProfileId、dependenciesを照合します。同じkindのartifact hashはconsumer間で一致させます。runtime nupkgとsource nupkgは別kindなので、互いのhash一致は要求しません。

canonicalSourceHashは選択commitのportable source Git blob bytesとdependency/profile/options規範filesに基づきます。移管時にpackage/API/契約を変えず、旧commit→新commit対応を記録します。再packでartifact bytesが変われば新preview版が必要です。現時点で実artifact hashや公開済み版を発行したとは扱いません。

## cache、世代、Partialの扱い

parse cacheはworkspace instanceに属し、ScopeId、SourceId、SourceVersion、TextHash、ParserVersion、ContractVersion、ProfileId、OptionsHashをkeyにします。同じ本文でも他文書・他workspaceのbindingを返しません。source version stringは比較用の数値ではありません。

解析前にgeneration/version/epochを予約し、publish前に再確認します。新generationの予約後やRemove後に古い結果が届いてもpublishしません。source hashの一致だけでsymbol/route/contextの世代を再利用せず、syntax cache hitでも外部contextが変われば再解決します。

legacy adapterの結果は新parse cacheへの保存も参照もしません。既存consumerの結果再利用とは別管理です。

新共有cacheのdefaultは64 entries / 16 MiB logical retained bytes、最新1件/document、LRUです。CompleteかつTextHashありの結果だけをcacheし、Partial/Failed/取消結果をcacheしません。これは新cacheの設計条件で、現行SharpDepsに実装済みとは記述しません。

Unknownな位置やdecoded mappingは、resolved evidenceや完全なMarkdown coverageへ昇格させません。Partial結果の未生成一覧を空の完全結果として扱いません。新strict entryの入力上限・Unicode処理と、既存C# analyzer/reportの挙動を分けます。

## 小さい確認と停止条件

両repoの`markdown-v1.fixtures.json`には同一の5件の机上契約例を置きます。AbsentのBOM付きheading例を含め、invalid UnicodeはrawUtf16CodeUnits=[55296]から構成します。JSON読戻しはNode.jsで確認します。BOM/CRLF/frontmatter、entity/escape/surrogate pair、prefix gap、同本文・別binding、new strict entryのinvalid Unicodeを扱います。原文長、既知slice、Linear対応、strict UTF-8 hash、bindingの区別を軽いscriptで確認しました。実parser出力やhost compileを確認したとは扱いません。

YAML Mark位置単位の実測はMD-02、runtime adapterはMD-03、host compileはMD-04、runtime/sourceの最終packはMD-05、両consumerの同profile出力一致はIN-01で確認します。stable公開や別repo移管は同版の実証後です。J-01ではfull build/testやローカルrestoreを起動しません。同PCの計測へ影響する検証はBocchiと調整します。

J-00修正SHA `46f910b3744a5e07328a388119910f4d1ab70342`のCI run `37709418593`はLinux成功、macOS失敗でした。既存`tests/analyzer/reviewRegression.test.ts:136`のfixture用restoreが失敗し、詳細stderrは保存されていません。文書だけの差分との因果は確認できず、原因は未確定です。今回のpush後のCIはSHAとともに別途報告します。

J-01では公開契約・採用表・fixtureだけをcommit/pushし、実diff、確認結果、未実行理由を返します。Bocchiが設計を採用するまでMD-01/J-02およびharness実装へ進みません。
