# ADR 0002: report v2・ID・プロトコルの契約

- 日付：2026-09-23
- 状態：採用（SD-002）
- 関連：計画書 5章、8章 SD-002、14.3（ロールバック）
- 成果物：`schemas/report-v2.schema.json`、`schemas/evidence-v2.schema.json`、`schemas/protocol-v2.schema.json`、`schemas/view-state-v1.schema.json`、`src/analyzer/reportV2.ts`、`src/analyzer/identity.ts`、`src/analyzer/reportV2Validation.ts`、`src/view/protocolV2.ts`

## 背景

v1レポート（`src/analyzer/types.ts`）は描画用のノード/辺と件数だけを持ち、
根拠の位置・参照種別・解析条件・不完全性を表現できない。v0.1.0では
「依存を選択→根拠を確認→コードへ移動」を成立させるため、正本となる
`AnalysisSnapshot`（report v2）と、その派生物（SVG・Mermaid・表・Problems）を分離する。

## 決定

### 正本と派生物

- 正本は `AnalysisSnapshot`。描画座標やDOM情報を混ぜない。
- 辺は `relationId`、根拠は `evidenceId` で参照し、根拠の本体は
  `evidenceIndex` が指すNDJSONサイドファイルへ置く。Webviewへ全根拠を一括送信しない。
- `evidenceIndex` は `null` を許す。レガシーadapterのように根拠をメモリ上にしか持たない
  生産者を表現するためで、その場合は `capabilities.evidence` と合わせてUIが扱いを決める。

### 識別子

- すべての共有IDは `prefix_` + SHA-256先頭16桁の16進。OS固有の絶対パスや
  シンボル表示名を含めない。ハッシュ入力は `\u001f` で区切って曖昧さを除く。
- `ProjectLogicalId` はワークスペースルートID + ルートからの相対パス。
  `ProjectVariantId` はそれにTFM・Configuration・Platformを加える。
  Namespaceは `ProjectVariantId` + 完全修飾名、Type/Memberは `ProjectVariantId` +
  宣言キー（documentation comment IDを優先し、無ければ構造型キー）。
- partial宣言は呼び出し側で1つの宣言キーへ統合し、`declarationCount` で宣言数を保持する。
- 外部型はAssemblyIdentity + 宣言キーで識別し、NuGetパッケージ名を推定しない。
- DOM IDは `domId()` がIDから生成し、シンボル名をセレクターへ直接入れない。
- 同じ入力・同じプロファイルなら同じIDになる。名前変更や署名変更をまたいだ安定性は保証しない。

### 関係と根拠の種別

- `basis` は `projectDeclared | projectEvaluated | usingInferred | symbolResolved`。
- `kinds` は計画書6.4の10種に加えて `projectDeclared | projectEvaluated | usingInferred` を
  許す。宣言レベルの関係にも種別が必要で、`basis` と一致する場合に限り使う。
- `Relation.evidenceCount` は正規化済み根拠件数であり、`counts` の重複排除規則は
  `docs/analysis-semantics.md` に固定する。

### 検証

- JSON Schemaを規範とし、`ajv`（devDependency、同梱しない）で契約テストを実行する。
- 実行時は依存を増やさない自作validator（`reportV2Validation.ts`）を使い、
  構造・enum・ID書式・参照整合性を検査する。参照整合性はJSON Schemaで表現できないため
  runtime validatorだけが担当し、契約テストで両者の一致を確認する。
- 未知の追加プロパティは許容する（新しいアナライザーと古いホストの互換のため）。
  未知の `schemaVersion`、未知のenum値、未知のメッセージ `type` は拒否する。

### プロトコル

- `protocolVersion: 2` は `ready` と `capabilities` で一度だけ交換し、以降のメッセージは
  `requestId` と `analysisId` で対応付ける。`analysisId` が一致しない応答は破棄する。
- Webviewはパスやbyte offsetを送らず、不透明なIDと `cur_` cursorだけを送る。

### 移行

- v1の型・プロトコル・UIはそのまま残す。v2は追加であり、v1の外部JSON出力を
  無言で置き換えない。
- `adaptLegacyReport` がv1レポートをv2へ写像する。推定であること・根拠位置が無いこと・
  witnessが無いことを `limitations` と `confidence: inferred` で明示する。
- Webviewとパネルをv2へ切り替えるのはSD-013/SD-015以降。SD-002時点では
  契約・型・検証・ID生成・adapterまでを固定する。

## 代替案と却下理由

| 案 | 却下理由 |
|---|---|
| IDに可読なパスやシンボル名を含める | エクスポートにホスト固有のパスが漏れる。名前変更でIDが壊れる |
| 根拠をsnapshotへ全件埋め込む | 中規模ソリューションで数万件になり、Webviewの初回描画と状態保存が破綻する |
| 実行時検証にajvを同梱する | 依存とVSIXサイズを増やす。必要な検査は自作validatorで足りる |
| JSON Schemaで参照整合性も表現する | 標準のJSON Schemaでは他要素のID集合を参照できない |
| v1型を削除してv2へ置換する | ロールバックと移行期間のQuick互換adapterが成立しない（14.3） |
| 宣言レベルの関係を `kinds: []` にする | `kinds` が空の辺はUIで種別表示ができず、根拠の意味が失われる |

## 帰結

- 移行期間はv1とv2の2つのプロトコルが併存する。SD-013/SD-015で解消する。
- 契約テストのsemantic fixtureは現時点では手書きである。SD-006/SD-007で
  実アナライザーがv2を出力したら、契約テストを実出力に対して実行するよう差し替える。
  それまでは「C#が出したJSONを検証した」とは報告しない。
- `ajv` がdevDependencyとして増える（`package-lock.json` に固定）。
- 根拠NDJSONのbyte offset運用（UTF-8境界、行単位読み込み）はSD-013の実装対象。
