# 固定Markdown packageから文書graphを作る契約

SD2-03はSharpDeps `feature/0.2.0`、基準SHA `fdadba7620f0c22e00690ede73df63a2d840aea1`から始めます。SD2-01の全9ファイルと整形修正2ファイルはBocchiが合格とし、修正SHAのCI run 37770408926も成功しました。IN-01の前提となるSD2-03を先に進めます。

新しい `SharpDeps.Analysis.Markdown` はnet10の独立adapterです。Contracts/Coreと固定runtime packageだけを参照し、Quick、既存solution/host/report v2、共通identityを変更しません。parserの手動copy、独立parser、renderer、MSBuild、SQLiteは導入しません。

## 固定artifactと起動

`LithoSharp.Markdown [2.0.0-preview.2]` と `YamlDotNet [18.1.0]` をexact参照します。MD-06では共有componentの正本とpackを別のローカルrepoへ移管し、対応するruntime/source候補を新しいimmutable版で作成しました。`markdown-runtime-pin.json` は選択版、移管先source commitと旧commitの対応、canonical hash、parser/contract/profile、依存identity、両nupkgとruntime DLL hashを固定します。MD-05のpreview.1は保持し、再pack・上書きしません。

restore後に `Verify-ConsumerArtifacts.mjs` で同pair、feedの両artifact、実際のrestored runtime nupkg/DLL、YamlDotNetのexact package versionを確認します。trusted Index入口では、ロード済みruntime DLL hashとYamlDotNet assembly/informational identity、callerが期待したParserVersionを照合し、不一致・確認不能を拒否します。parse後もfactsのparser/contract/profileを確認します。startupのassembly hash読取りはconsumer hostのI/Oで、共有parserのI/Oではありません。assembly version 18.0.0.0をpackage version 18.1.0と混同しません。

固定候補は`eng/markdown/feed`に保存し、`NuGet.Config`のexact package mappingで復元します。NuGetへの公開をbootstrapの前提にしません。既存analyzer solution/CIはMarkdown projectを含まないため、`analyzer/tests/SharpDeps.Markdown.Tests/SharpDeps.Markdown.Tests.csproj`を明示検証します。IN-01の4host小fixtureは移管後も再利用します。共有repoの正式owner/名前と公開先は未確定で、候補metadataの`sourceRepository`はローカル履歴移管を表します。

## 元factsを保持する投影

`LithoSharpMarkdownAdapter.Analyze` は供給本文とopaque scope/source/versionを公開facadeへ渡します。`MarkdownGraphProjection.Facts` とtyped bindingは、frontmatter、heading、section、link、fence、text region、diagnostics、coverageを元の不変DTOとして保持します。raw UTF-16 span、decoded source segments、Atomic/Unknown、`Map` のRawFragmentsを独自の1:1位置へ変換しません。

document、section、frontmatter、code fence、link target nodeとcontains/markdown-link edgeを新harness envelopeへ投影します。heading本文/level/anchor、section direct/subtree span、link label/target/reference/image、fence info/content/closing stateはtyped bindingから読めます。原文全体のTextHashとfenceの原文ContentSpanのhashを区別します。検索fieldは元のtext projectionを保持します。

frontmatterの意味付けは `title`、`tags`、`sharpdeps.id`、`sharpdeps.symbols` のwhitelistです。これらもsource YAML nodeとして保持し、任意の型生成・include・実行やsymbolの確定に使いません。非採用keyは一覧に記録します。frontmatter AbsentのREADMEをmissing metadata errorにしません。full source factsの保持と検索/graphの属性whitelistは別の責務です。

## identity、位置、未解決

workspace/document UUIDは永続ownerが供給します。parser SourceIdやheading LocalKeyからdurable document/section IDを割り当てません。section tokenは現在のraw TextHashに結び付いたcallerの確定済み対応だけを `hsec` へ写します。未知keyや重複identity、不一致hashを拒否します。対応がないsectionとlink/fence等のfactには `hmdf` のsnapshot-local IDを使い、durable IDとは扱いません。snapshot-local framingはdocument、scope/source/version、snapshot/generation、raw hash、parser/options、fact kind/local keyを含みます。

section所有は開始位置のbinary lookupと親階層で選び、各factでsection全件を走査しません。未知の位置はnull、既知空spanはlength 0のままです。source/version/hashを位置と一緒に保持します。

MarkdownのResolvedReferenceは同一文書のdefinition解決だけです。file/anchor/member targetの確定とは扱わず、graph edgeはCandidateまたはUnresolvedです。relative path/root外判定、broken file/anchor、symbol候補とmove/section matchingはSD2-04/09のownerへ残します。外部URLをcrawlせず、HTML/MDX/fenceを実行・描画しません。

parse Failedはgraph Failed、Partial/unknown mappingと未完了identity/target解決はgraph Partialです。parser statusとprojection reasonsを別に保持し、空配列で未完了を隠しません。coordinatorのpublish世代、cache、保存/完全graph検証はSD2-05/06/09とIN-01で扱います。

## trustと小さい確認

新Index入口はSD2-01のread/write policyでuntrustedを拒否します。既存Quick/Semantic gateを変更せず、workspace trustをOS sandboxや外部AI送信許可とは扱いません。cancel時は結果を返しません。

MD-05でruntime/固定source hostの全factsが一致した3fixtureを、SharpDepsの実adapterでも同じ正規化/hashで照合します。BOM/CRLF/YAML、entity/非BMP、重複heading/未解決referenceを同じ本文で確認します。追加の小checkはwhitelist、fence原文slice/hash、隣接section、TextHashに結び付いたID、trust/cancel、invalid Unicode/limitの保守的coverageです。実Roslyn hostと両repo matrixはIN-01の別ゲートです。

`Markdown.Tests` の明示restore/testにはimmutable local feedと既存dependency cacheを渡します。RTKで限定testを実行し、node reuse/shared compilationを無効にします。restore後のartifact gate、既存CIと同じ整形check、Quick project/Contracts/Coreの不変確認を記録します。全suiteやカバレッジ目標を追加しません。

固定packageのrestoreとartifact照合、adapter test 8件、既存のformat/lint checkが成功しました。test assertの書式warningを修正後、8件・warning 0を確認しています。Bocchiの全差分レビューが合格するまで次のタスクへ進みません。MD-06はIN-01 PASSまで開始しません。
