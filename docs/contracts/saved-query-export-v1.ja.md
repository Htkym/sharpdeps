# Saved Query context / JSON export v1

この段階はhostのpinned SavedQueryProviderからのデータ準備と純粋な文字列変換を定義する。UI・protocol・clipboard・save dialog・file書込みには接続しない。saved向けの実Copy/Export操作は引き続き未対応であり、legacy ContextExportInput/report JSONへ保存結果を詰めない。

## 情報源とsession

`SavedQueryProvider.prepareSavedExport(analysisId)`は最新のprojection要求が成功した場合、そのQuery responseから作った独立したコピーを返す。遅れて完了した旧要求はcacheを更新せず、最新要求が失敗した場合も旧結果を復活させない。追加Query、source本文・snippet・source locationの読取り、writer/restore、SDK/editor、network送信は行わない。最初のprojection成功前、認識済みprojection処理の失敗後、異なるanalysis ID、dispose後、別providerへの置換では利用できない。search/detailsだけからprojectionを作り直さない。

内部の`buildSavedQueryExport`はtyped・validatedのhost-owned inputを純粋変換する。各responseのworkspace/snapshot/generationがpinned snapshotと一致し、variantCountが1で選択済variantと完全一致することを要求する。失敗responseは拒否する。これはunknown webview payloadのvalidatorではなく、呼出し元のprotocol/Query検証を省略する入口ではない。

## JSON契約

kindは`sharpdeps.saved-query-export`、versionは`1`。TypeScript定義は`src/export/savedQueryExport.ts`のSavedQueryExportである。

| field | 意味 |
|---|---|
| analysisId | exportを準備したhost providerのsession ID |
| snapshot | workspaceId、id（saved snapshot ID）、generation、variantIds/variantCount、coverage、freshnessをpinned headerのまま保持 |
| selectedVariantId | 明示選択済みの単一variant。variantIdsは選択scopeであり全variant inventoryとは称さない |
| scope | projectionの要求scope。idはopaque aliasから解決した元saved ID。要求depthは範囲の完全性を証明しない |
| includedNodeIds | 明示表示要求を元saved node IDへ解決したもの |
| display | granularity、実表示node/edgeの元ID、local node表示制限のtruncated |
| observedInputChange | host watcherが観測した変更理由。snapshot freshnessを更新した証拠ではない |
| responses | 主Browse/Search/Impactと、既存の追加Symbol responseを別recordとして保持 |

各responseはoperation/apiVersion/requestId/snapshot、items/candidates/unresolvedの元bucket、returnedCount、totalKind=`returned`、truncated/truncationReasons、hasMore、diagnosticsを保持する。returnedCountはそのresponseの3bucketの返却要素数である。別response間の同一IDを統合・重複除去せず、合計unique数や全scope件数を推測しない。表示のtruncatedと各Queryのtruncatedは別に記録する。

各itemのid/kind/label/certainty、nodeのid/kind/name/parentId/signature、edgeのid/kind/certainty/producer・元source/target node ID・occurrence ID・variant IDを保つ。itemのCandidateとedgeのResolvedが異なる場合もそれぞれ保ち、Resolvedへ昇格させない。edge.hasEvidenceは保存された位置metadataの存在だけで、source本文や現在の位置の照合を意味しない。

許可したfieldだけを組み立て、source path・location/rawSpan/hash・source本文/snippet・opaque page cursor・未知fieldは出力しない。cursorはhasMoreにだけ変換する。返却データのlabel/signature等は保存済みmetadataであり、実行指示として解釈しない。

## 純粋な文字列変換

`serializeSavedQueryExport`はindent 2のJSONと末尾LFを返す。field値を切り詰めたりcertainty/coverage/freshnessを置き換えたりしない。JSON parseで文字列値が復元できるため、Unicode・astral・引用符・CR/LFを保持する。UTF8 bytes化と実書込みは後続gateで行う。

`buildSavedContextExport`は返却subset・Query側のcoverage/freshness・Candidate/Unresolvedの限界を示す固定説明と、同じJSONを文字列として返す。clipboardや外部AIには送らない。SVG/PNG/Mermaid等は追加しない。

## 後続の出力gate

実Copy/JSON Saveは別実装・別受入れとする。hostのcurrent provider identity、analysis ID、pinned workspace/snapshot/generation/variant、準備したprojection provenance/scope/subsetを、dialog後とclipboard/file出力直前に再照合する。切替・dispose・新projection・対象差異なら古い準備値を出力しない。webviewから結果本体/path/argvを受け取らず、hostがpinned providerから組み立てる。

実clipboard/file、保存取消とsession切替、UIボタン、installed VSIX、trust設定を維持した実操作は未検証である。同hosttrust revokeのlistener gapやpublic visibility制約を、この純粋変換から解消したとは扱わない。
