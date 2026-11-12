# SharpDeps harness契約 v1

現行のHarness graphは旧report v2から独立したidentity、graph envelope、read/write trustを使います。CLIの索引作成、保存QueryとMCPへ接続済みです。現在の入口は[CLI契約](cli-skill-v1.ja.md)、保存形式と再索引条件は[索引保存契約](index-storage-v1.ja.md)を参照します。

## 新旧の境界

既存Identity、TypeScript identity、AnalysisSnapshot、CodeMapJsonContext、report-v2.schema.json、legacyAdapterと旧providerは変更しません。旧wrk/prj/var/ty/mb/docなどの16桁hashは現在の意味を保ちます。新graphのformatはsharpdeps-harness-graph、schemaVersionは1、identityVersionはsharpdeps-harness/1です。旧reportのschemaVersion 2や旧CodeMapReportと同じserializerへ混ぜません。

DTOはContracts/Harness、pure identity helperはCore/Identity/HarnessIdentityに置きます。Contracts/Core/QuickへMarkdown、MSBuild、SQLite、VS CodeやNode依存を追加しません。旧reportの形式とQuick／Semanticの実行境界を維持します。保存とUIの現在の接続は[保存Query provider契約](saved-query-provider-v1.ja.md)に従います。

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

project seedはslashを統一し、空segmentとdotを除き、absolute/colon/parent traversalを拒否します。caseは保持します。これだけでsymlinkやroot内アクセス権を証明するわけではありません。project移動やcase aliasは、storage/updateで確証と対応表を保存した場合だけ同じpersisted IDへ結びます。移動後のpathからseedを再計算した結果を同じIDとは扱いません。

LogicalSymbolId helperはproject scope（hpr）を受け取ります。external assemblyの識別とそのsymbol producerは[member graph契約](member-graph-v1.ja.md)で定義し、assembly IDをこのhelperへ無条件に渡しません。

symbol IDにfile path、行、TFM、analysis fingerprintを含めません。TFM別の宣言・関係はoccurrenceへ保持し、異なるvariantの辺を合算しません。VariantIdは選択keyで、SDK/参照/flags/import/generator等を含むanalysis fingerprintは別fieldです。現在入力との一致は[coordinator](coordinator-ipc-v1.ja.md)と[Queryの鮮度検査](query-context-v1.ja.md)で判断します。

document UUIDとsection tokenの割当・保存・move照合は[Markdown identity契約](markdown-identity-mentions-v1.ja.md)と[差分更新契約](incremental-update-v1.ja.md)に従います。parser SourceId、heading LocalKeyや重複slugからdurable IDを無条件に作りません。UUIDの紛失時に旧path hashから同じidentityへ復元できるとは主張しません。

## graph envelopeと位置

HarnessGraphEnvelopeはlogical node、symbol occurrence、variant、edge、旧ID対応とcoverageを分けます。edgeはlogical端点とnullable occurrence/variantを持ち、Candidate/UnresolvedをResolvedへ昇格させません。具体的なmember edgeの抽出と所有・read/write provenanceは[member graph](member-graph-v1.ja.md)、Markdownのnode/位置投影は[Markdown graph](markdown-graph-v1.ja.md)に従います。

位置は元入力UTF-16、zero-based半開区間です。unknownはnull、既知の空範囲はlength 0です。source identity/version/content hashを位置と一緒に持ちます。shared Markdownのraw segmentsやUnknownを位置が確定した証拠へ変換しません。Markdown metadataはnullableで、未接続を空の完全解析とは扱いません。

JSON property名はcamelCase、enum値はC# enum名の文字列です。新HarnessGraphJsonContextを使います。旧snapshotは既存CodeMapJsonContext/legacyAdapterで読み、必要な旧ID→新node/occurrence対応をLegacyReferencesへ記録します。旧root IDから新UUIDへの暗黙変換や破壊的上書きをしません。

## read/write trust

| 操作 | trusted | untrusted |
| --- | --- | --- |
| ReadSaved / readSaved | 許可 | 許可 |
| Index、Update、Watch、Restore、WriteStore | 許可 | 拒否 |
| 未知の操作 | 拒否 | 拒否 |

ReadSavedは保存済みの検証された結果だけを対象にします。workspaceのsource読取り、解析、更新、sidecar/DBの作成・migration・cache書込みを含めません。既存trustDecisionのQuick/Semantic gateとメッセージを維持します。helperはC#とTypeScriptで同じ操作表を持ち、CLI/storage/watchの入口で操作前に明示適用します。保存表示だけでsource読取りや更新の権限を得ることはありません。

trust gateはOS sandboxではありません。信頼して実行するMSBuild/analyzer/generatorのnetworkやroot外アクセスを防ぐ機構ではありません。root/path検証、保存snapshot検証、各processの権限はそれぞれの入口で別に扱います。workspace trustを外部AIへのsource送信許可とみなしません。

## 読込みと検証

HarnessGraphContract.ValidateHeader は format／schema／identity version、UUID、generation、必要な配列を確認します。この header 検査だけでは graph 全体の root／path、node 参照、payload の検証を代替しません。保存 snapshot の検証は[索引保存](index-storage-v1.ja.md)、Query の検査は[Query context](query-context-v1.ja.md)を参照してください。harness-v1.fixtures.json は identity と graph の機械可読な契約例として保持します。

読込みは HarnessGraphContract.Read を使い、必須 constructor 項目の欠落、header 不一致、不正 span を JsonException として拒否します。coverage や certainty の欠落を Complete／Resolved へ補完しません。nullable 項目の null も JSON に明示します。