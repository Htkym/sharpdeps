# M0 進捗報告（SD-001〜SD-005）

日付：2026-09-23
ブランチ：`feature/v0.1.0`
対象：計画書 12.1のマイルストーンM0「基準と技術成立性」

## 完了したタスク

| ID | タスク | コミット | 実施メモ |
|---|---|---|---|
| SD-001 | 基準状態・回帰項目の固定 | `cca701c` | `tasks/SD-001.md` |
| SD-002 | report v2・ID・プロトコルの仕様固定 | `73035aa` | `tasks/SD-002.md` |
| SD-003 | SDK/MSBuild/Roslynの実行互換性試作 | `86b8dab` | `tasks/SD-003.md` |
| SD-004 | SVG＋ELK＋Worker＋出力の試作 | `52823a1` | `tasks/SD-004.md` |
| SD-005 | C#解析器のプロジェクト分割 | `287c4bb` | `tasks/SD-005.md` |

M0の終了条件のうち、以下は成立している。

- 現行回帰基準：`tests/fixtures/quick-baseline` と正規化スナップショット、実機チェックリスト
- schema：`schemas/` の4ファイルと契約テスト
- Semantic最小ロード：`SharpDeps.SemanticHost` が `.sln`/`.slnx`/単一プロジェクトを読み、
  TFM対応と根拠を出力できる
- SVG Workerと出力：50ノードのSVG描画、Blob Worker、SVG/PNG出力をブラウザで実測

**未達**：M0はSD-023の「起動/CSP境界」も含む。Worker用CSPは実装したが、
Trust guard（未信頼workspaceでの解析起動拒否）は未着手のため、M0は完了としていない。

## 検証の実測値（2026-09-23、Windows 11、SDK 10.0.300）

| 検証 | 結果 |
|---|---|
| `npm run compile` / `lint` / `format:check` | 成功 |
| `npm test` | 9ファイル / 87テスト成功 |
| `dotnet build analyzer/SharpDeps.Analyzer.slnx -c Release` | 成功（警告0） |
| `dotnet test analyzer/SharpDeps.Analyzer.slnx -c Release` | 47テスト成功 |
| `npm run build:analyzer` | 成功（Quick 9.7MB / Semantic 16.4MB） |
| `npm run package` | 成功（47ファイル / 6.65MB） |
| Quick基準fixtureの回帰 | 分割後も一致 |
| Semanticロード（baseline） | 変種5・参照4・未解決0・約3.5秒 |
| SVG描画（50ノード/54辺） | 成功。選択でレイアウト再実行なし |
| SVG/PNG出力 | 成功。script・イベント属性・外部リンクなし |

## 主要な設計判断（ADR）

| ADR | 内容 |
|---|---|
| `docs/adr/0001-semantic-loading.md` | Semanticは別プロセス。MSBuildLocatorでSDKのMSBuildを読む。TFMはプリプロセッサシンボルから導出 |
| `docs/adr/0002-report-v2-contract.md` | 正本はAnalysisSnapshot。IDは不透明ハッシュ。v1はadapterで移行 |
| `docs/adr/0003-graph-renderer.md` | SVG自前描画＋ELK Worker。カメラはviewBoxと要素サイズで表現 |

## 実測で判明した制約（未解決）

1. 推移的な `ProjectReference` はMSBuildWorkspaceに現れない（SD-007で対処）。
2. ロードはMSBuildのdesign-time buildを通じて `obj/` へ書き込む。
3. SDKを持たない環境でのSemantic初期化失敗とQuick起動は未検証（SD-029）。
4. `.slnx` のフォルダ名（`/src/`）は現状そのまま表示される（SD-006で正規化）。
5. グラフのノードバッジが長いラベルと重なる（SD-017で修正）。

## 次の段階（M1）

M1は「一つの依存を最後まで辿る」段階で、SD-006（Quickのv2正規化）から
SD-018までの最小実データ経路を作る。推奨順序は次のとおり。

1. SD-006：Quickをreport v2へ写像し、契約テストを実出力で回す（SD-002の宿題）
2. SD-007/SD-008：Semanticローダーとシンボル索引
3. SD-009/SD-010：宣言・Operation由来の根拠抽出
4. SD-013/SD-014：結果ストアと解析ライフサイクル
5. SD-015〜SD-018：Shell・Table・SVGグラフ・Inspectorの接続

## 再現手順

```powershell
npm install
npm run build:analyzer
dotnet test analyzer/SharpDeps.Analyzer.slnx -c Release
npm run compile; npm run lint; npm run format:check; npm test

# グラフ試作（別端末で）
node scripts/serve-webview.mjs 4173 --evidence .local/evidence
# ブラウザで http://127.0.0.1:4173/tests/webview/fixtures/graph-prototype.html

# Semantic試作
dotnet analyzer/bin/semantic/sharpdeps-semantic-host.dll `
  --solution tests/fixtures/semantic-baseline/SemanticBaseline.sln `
  --output .local/evidence/semantic-probe.json --configuration Debug
```
