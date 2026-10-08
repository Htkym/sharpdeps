# SharpDeps harness契約 v1

SD2-01では、既存report v2から独立したharnessのidentity、graph envelope、read/write trustを追加します。基準SHAは19f2e1993bd24828b61a922a2f719c72be441776、branchはfeature/0.2.0です。IN-01の前提SD2-03がSD2-01へ依存するため、SharpDeps側の先行契約を優先します。MD-05は15edc2765198e48ba9200bae8d3fd6d4ae127eb8の全11ファイルをBocchiがレビューし、合格しました。既存2.0.0-preview.1のartifact bytesは保持します。

## 新旧の境界

既存Identity、TypeScript identity、AnalysisSnapshot、CodeMapJsonContext、report-v2.schema.json、legacyAdapterと旧providerは変更しません。旧wrk/prj/var/ty/mb/docなどの16桁hashは現在の意味を保ちます。新graphのformatはsharpdeps-harness-graph、schemaVersionは1、identityVersionはsharpdeps-harness/1です。旧reportのschemaVersion 2や旧CodeMapReportと同じserializerへ混ぜません。

新DTOはContracts/Harness、pure identity helperはCore/Identity/HarnessIdentityに置きます。Contracts/Core/QuickへMarkdown、MSBuild、SQLite、VS CodeやNode依存を追加しません。既存Quick/Semantic、command、settings、reportの挙動とextensionの版0.1.0を維持します。新entryへの接続、保存とUIは後続taskです。

## durable identity

workspace UUIDには呼出し側が永続化した非empty Guidを渡します。ID helperがrootからUUIDを生成したり、untrusted状態でsidecarを作成したりしません。WorkspaceIdはhw_とlowercase UUIDのN形式です。同じworkspaceを移動する場合はUUIDを保持し、canonical rootは属性として更新します。別workspaceは別UUIDです。

| ID | 入力と意味 |
| --- | --- |
| hpr | workspace UUIDと正規化した相対project pathからの初期seed |
| hsym | workspace UUID、persisted project ID（hpr）、symbol kind、Roslyn側で正規化した完全signature |
| hvar | workspace UUID、project ID、TFM、Configuration、nullable Platform/RIDの選択条件 |
| hocc | workspace UUID、logical symbol ID、variant ID |
| hdoc | workspace UUIDと永続document UUID。pathは入力にしない |
| hsec | workspace UUID、document ID、確定した永続section token |

hpr以降はfull SHA256の64桁です。hashはidentityVersion、prefix、各fieldを順に処理します。fieldはnullなら1byteの0、既知なら1byteの1、UTF-8 byte長のuint64 big-endian、strict UTF-8 bytesです。framingではnullと空文字を区別し、曖昧なseparator連結を使いません。Platform/RIDのselectionだけは先にnull/空/whitespaceを未指定のnullへ正規化します。MSBuildの取得経路によって同じ選択が二つのvariantへ分かれないようにします。

project seedはslashを統一し、空segmentとdotを除き、absolute/colon/parent traversalを拒否します。caseは保持します。これだけでsymlinkやroot内アクセス権を証明するわけではありません。project移動やcase aliasは、後続storage/updateで確証と対応表を保存した場合だけ同じpersisted IDへ結びます。移動後のpathからseedを再計算した結果を同じIDとは扱いません。

今回のLogicalSymbolId helperはproject scope（hpr）を受け取ります。external assemblyの識別とそのsymbol producerはSD2-02で定義し、assembly IDをこのhelperへ無条件に渡しません。

symbol IDにfile path、行、TFM、analysis fingerprintを含めません。TFM別の宣言・関係はoccurrenceへ保持し、異なるvariantの辺を合算しません。VariantIdは選択keyで、SDK/参照/flags/import/generator等を含むanalysis fingerprintは別fieldです。古いfingerprintの結果をcurrentとして扱う処理は後続coordinatorで扱います。

document UUIDとsection tokenの割当・保存・move照合はSD2-04/05/09で実装します。parser SourceId、heading LocalKeyや重複slugからdurable IDを無条件に作りません。UUIDの紛失時に旧path hashから同じidentityへ復元できるとは主張しません。

## graph envelopeと位置

HarnessGraphEnvelopeはlogical node、symbol occurrence、variant、edge、旧ID対応とcoverageを分けます。edgeはlogical端点とnullable occurrence/variantを持ち、Candidate/UnresolvedをResolvedへ昇格させません。具体的なmember edgeの抽出と所有・read/write provenanceはSD2-02、Markdownのnode/位置投影はSD2-03で実装します。

位置は元入力UTF-16、zero-based半開区間です。unknownはnull、既知の空範囲はlength 0です。source identity/version/content hashを位置と一緒に持ちます。shared Markdownのraw segmentsやUnknownを位置が確定した証拠へ変換しません。Markdown metadataはnullableで、未接続を空の完全解析とは扱いません。

JSON property名はcamelCase、enum値はC# enum名の文字列です。新HarnessGraphJsonContextを使います。旧snapshotは既存CodeMapJsonContext/legacyAdapterで読み、必要な旧ID→新node/occurrence対応をLegacyReferencesへ記録します。旧root IDから新UUIDへの暗黙変換や破壊的上書きをしません。

## read/write trust

| 操作 | trusted | untrusted |
| --- | --- | --- |
| ReadSaved / readSaved | 許可 | 許可 |
| Index、Update、Watch、Restore、WriteStore | 許可 | 拒否 |
| 未知の操作 | 拒否 | 拒否 |

ReadSavedは保存済みの検証された結果だけを対象にします。source読取り、解析、更新、sidecar/DBの作成・migration・cache書込みを含めません。既存trustDecisionのQuick/Semantic gateとメッセージを維持します。新helperはC#とTypeScriptで同じ操作表を持ち、将来のCLI/storage/watch入口では操作前に明示適用します。このtaskでは既存extensionの許可範囲を広げません。

trust gateはOS sandboxではありません。信頼して実行するMSBuild/analyzer/generatorのnetworkやroot外アクセスを防ぐ機構ではありません。root/path検証、保存snapshot検証、各processの権限は既存境界と後続taskで別に扱います。workspace trustを外部AIへのsource送信許可とみなしません。

## 小さい確認と引継ぎ

新identityのUUID/root移動、同signatureのlogical ID、TFM別occurrence、別workspace、未指定/空の選択値の一致、旧ID goldenを小fixtureへ記録します。new graphのserializer roundtrip、unknown/既知空span、trustのread/writeと未知操作拒否を少数のcheckで確認します。旧report読取は既存QuickV2Mapper/legacyAdapterの小checkを再利用します。

Bocchiの検証枠許可後、restore、限定C# test、tsc、限定Vitestが初回で成功しました。C# testは16件・warning 0、Vitestは指定3ファイルの14件です。4コマンドの実行時間合計は約18秒でした。全suite、pack、Semantic MSBuild integrationは実行していません。Bocchiの実差分レビューに合格するまでSD2-02/03へ進みません。IN-01の実Roslyn host/両repo matrixとMD-06の移管は未完了です。

HarnessGraphContract.ValidateHeaderでformat/schema/identity version、UUID、generation、必要な配列を確認します。これはgraph全体のroot/path、node参照やpayloadの検証を代替しません。保存・Queryの完全入力検証はSD2-05/06で扱います。harness-v1.fixtures.jsonは独立したNode計算の契約例です。限定C# testではproject/symbol/net10 variant・occurrenceと旧IDのgolden、別TFM/別workspace、未指定選択値、graph読込、trustを確認しました。fixture全項目をC#のgolden assertionへ含めたとは主張しません。

読込はHarnessGraphContract.Readを使い、必須constructor項目の欠落、header不一致、不正spanをJsonExceptionとして拒否します。coverageやcertaintyの欠落をComplete/Resolvedへ補完しません。nullのnullable項目もJSONに明示し、未指定と欠落を混同しません。
