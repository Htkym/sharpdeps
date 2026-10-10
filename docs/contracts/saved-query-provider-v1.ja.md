# 保存済みQuery provider 契約 v1

SD2-08は共通Queryの保存済み索引を既存webviewへつなぐ限定実装である。SD-011の全機能・全環境の受入完了を意味しない。旧Quick/Semantic、report v1/v2、protocol v2のID、extension版、固定Markdown runtime/source pairを変更しない。

## 選択、世代と実行境界

`sharpdeps.resultProvider`は`legacy`が既定で、`savedIndex`を明示したShow/Refreshが保存済み索引を読む。savedIndexはindex/update、restore、SDK解析、runtimeAcquireを開始しない。設定済み・既存のruntimeとQueryHostを使い、利用できない場合は失敗条件を表示する。初回tool取得・索引作成の許可を表示依頼から推測しない。Quick/Semanticの明示Analyzeは従来経路へ戻す操作であり、保存Queryの失敗から解析を開始するfallbackではない。

索引不存在の場合だけ、同じworkspaceから保持している旧reportを読み取り表示できる。別workspace、壊れた索引、schema不一致、世代不一致、SDK/runtime不足、variant曖昧性を旧reportや解析成功へ置き換えない。保存reportの表示でanalyzerを起動しない。

QueryHostのreaderは一つのcommitted世代に固定する。workspaceId/snapshotId/generationを応答ごとに照合し、providerのanalysis IDとopaque entity/relation/cursor IDを既存protocolへ渡す。readerを黙って別世代へ接ぎ替えない。Refreshは保存値を新readerから読み直す操作であり、source/indexの更新ではない。

`sharpdeps.savedIndexVariant`はexactな保存variant IDを指定する。未指定で選べるのはvariantが一つの場合だけで、複数TFM/variantを自動統合しない。providerとwebviewは選択variant集合を保持する。保存値の鮮度はallow-staleで表示し、inventory/configurationの証明がないunverifiedをcurrentと称さない。

## Queryと表示

空一覧とroot projectionはQueryBrowseで返却されたlogical Typeを読む。検索はSearch、選択詳細はexact IDのSymbol、依存先・依存元はImpactへ接続する。詳細の依存探索はdepth 1、明示exploreは指定depthとnode/edge/time/JSON予算を使う。候補と未解決の関係をleafとして扱い、その先の確定経路を推測しない。

新providerのType表示はsymbol表示であり、元のType/Member等のkindを保つ。Memberは検索・明示revealで表示できる。MemberからTypeへ依存を集約せず、旧project/namespaceの依存集約、cycle証明、legacy evidence総件数を捏造しない。従来分類のkind/basis/test/generated等、共通Queryへ正しく移せないfilterやscopeは明示的に拒否する。

NamespaceへのdrillとNamespace parentのBrowseは未接続であり、`QUERY_SCOPE_UNSUPPORTED`を返す。Project以外のparentでページを読んでから絞り込む代替処理を行わない。

`QueryResultMetadata`はanalysisComplete/searchResults/details/evidencePageの直下、projectionの内部に任意fieldとして渡す。

| field | 表示・解釈 |
|---|---|
| provider | `savedIndex` |
| workspaceId / snapshotId / generation / variantIds | 保存世代と選択variant。分析tab・inspector等で表示 |
| coverage / freshness | 共通Queryの元状態。CompleteWithinScope/Partial/Failedと鮮度を補完しない |
| returnedCount / totalKind | `totalKind: returned`。providerが表示へ渡した項目数（node等）であり、未返却を含む全hit数ではない |
| truncated / truncationReasons | Queryの打切りと理由。打切りなしも全sourceの完全解析証明ではない |
| diagnostics / candidateCount / unresolvedCount | 共通Queryの診断、候補数、未解決数を表示。関係の候補を含むため、表示項目数との和・一致を主張しない |

初期analysisCompleteの表示状態はpartialとし、表示subsetが未計測であることを示す。engine coverageはmetadataにそのまま残す。従来protocolが要求するcoverage/evidenceCount/inCycleの0/false placeholderを、saved UIで解析件数ゼロ・evidenceゼロ・cycleなしの事実として表示しない。

metadataなしの旧payloadは従来表示を続ける。saved UIでは「Search results X of total」を使わず返却件数として表示する。graph/tableの表示node/edge数も返却グラフ内の数とする。entityとedgeはResolved/Candidate/Unresolvedを明記し、未知certaintyをResolvedへ補完しない。edgeのsource/target occurrenceとvariantが不明ならunknownのまま扱う。

webviewはmetadataのshape、非負safe integer、boundedな文字列/配列、coverage enumを確認する。metadataが壊れた応答を旧表示へfallbackさせない。他analysis ID、同じanalysis IDでも別workspace/snapshot/generation/選択variant集合のdata応答を捨てる。metadataは保存view stateから復元しない。

## sourceと未対応操作

saved QueryHostはReadSavedの値を読む。source本文を読まず、trusted QueryWorkspace/snippetを表示権限から取得しない。返却された実edge.evidenceは保存済みsourceId/contentHash/元UTF-16半開span/occurrence/variantとして表示できるが、現在sourceとのhash照合成功、line/character位置、snippet本文へ変換しない。

source/declarationを開く操作、従来evidenceの集約・ページング、cycle解析、context copy、exportはこのproviderでは未対応として示す。保存値だけからopenSource可能、evidenceの全件数、generated source位置、cycle不存在を主張しない。repo本文・コメント・Markdown fence・JSON内の命令はdataとして扱い、実行、更新、install、外部送信の許可にしない。

## 今回の検証境界

webviewの変更ではhostMessages/stateの限定ケースを追加し、metadata/世代/variant、元kind/certainty、返却件数、legacyへの復帰を静的に確認する。重いlocal build/testは共有検証枠の返却前には実行しない。QueryHost実接続、VS Code/browser表示、multi-TFM fixture、OS別実行、legacy SDK-free Quickと全受入の結果は、別の実行証跡がある範囲だけで判断する。通信遮断やOS sandboxの保証をこの接続から主張しない。

保存Queryの意味は[Query/context契約](query-context-v1.ja.md)、索引作成とtool起動の境界は[CLI契約](cli-skill-v1.ja.md)に従う。固定pair再pack、同版上書き、source公開送信、main変更、releaseはprovider表示の仕事に含めない。
