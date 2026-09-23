# v0.1.0 基準状態（SD-001）

記録日：2026-09-23（日本時間）
作業ブランチ：`feature/v0.1.0`
記録者：実装エージェント

## 基準と現在地

| 項目 | 値 |
|---|---|
| 計画書の基準コミット | `22a2d751ff54c6db26f3032c287e22ef4d9a446d`（tag `v0.0.4`） |
| SD-001開始時のHEAD | `5c8d8d938a290225f640fde8352e40c97ef41499` |
| 基準コミットからの差分 | `.gitignore` に `.local/` を1行追加したのみ |
| SD-001開始時の未コミット変更 | なし（ワーキングツリーはクリーン） |
| 製品バージョン | `package.json` = `0.0.4` |

利用者の未コミット変更は存在せず、破棄・上書きは行っていない。

## 実測環境

| 項目 | 値 |
|---|---|
| OS | Windows 11（10.0.26200） |
| CPU / RAM | Intel Core Ultra 7 258V / 31.6 GB |
| Node.js / npm | v24.13.0 / 11.17.0 |
| .NET SDK | 10.0.300（`C:\Users\h_tky\AppData\Local\dotnet\sdk`） |
| .NET ランタイム | Microsoft.NETCore.App 8.0.27 / 10.0.8 |
| VS Code | 未計測（拡張機能の実機E2EはSD-027で実施） |

## 現行コマンドの実行結果（すべてSD-001開始時に実行）

| コマンド | 結果 | 備考 |
|---|---|---|
| `npm run compile` | 成功 | `tsc --noEmit`、診断なし |
| `npm run lint` | 成功 | `eslint .`、警告なし |
| `npm run format:check` | 成功 | 全ファイルがPrettier準拠 |
| `npm test` | 成功 | 2ファイル / 15テスト（既存分）。SD-001で追加した分を含めると4ファイル / 19テスト |
| `npm run build:analyzer` | 成功 | file-based publish。復元457 ms、`analyzer/bin/code-map.dll` を生成 |
| `npm run build` | 成功 | `out/extension.js` 22.8 KB、`media/viewer.js` 7.6 MB |
| `npm run package` | 成功 | `sharpdeps-0.0.4.vsix`、修正前54ファイル / 6.27 MB |

**基準時点で失敗している検証はない。** 改修で新たに発生した失敗と区別するため、この状態を出発点とする。

## VSIX内容の確認

`npx vsce ls` で確認した同梱ファイル（`.vscodeignore` 修正後、41ファイル）。

- 必須：`package.json`、`README.md`、`CHANGELOG.md`、`LICENSE`、`resources/icon.png`、`out/extension.js`、`media/viewer.js`、`media/viewer.css`、`analyzer/code-map.cs`、`analyzer/bin/**`（Roslyn本体、satellite assembly、`code-map.dll`/`.deps.json`/`.runtimeconfig.json`/`.exe`）
- `analyzer/code-map.cs` は `analyzer/bin/code-map.dll` が無い場合の開発用フォールバックとして同梱される（`src/analyzer/runAnalyzer.ts` の `locateAnalyzer`）。

### 発見した不備と修正

`.vscodeignore` に `.local/` の除外がなく、`sharpdeps-0.0.4.vsix` に内部計画書
（`SharpDeps_v0.1.0_Implementation_Plan.md` など）と作業ログが同梱されていた。
`.local/**`、`docs/**`、`tests/**`、`schemas/**` を除外対象へ追加し、41ファイルへ減らした。
`.local/` はGit管理外（`.gitignore` 済み）だが、VSIXには別途除外が必要だった。

## 既存の入口（回帰対象）

| 種別 | 値 |
|---|---|
| コマンドID | `sharpdeps.showDependencyMap` / `sharpdeps.refresh` / `sharpdeps.copyMermaid` / `sharpdeps.exportSvg` / `sharpdeps.exportPng` |
| 設定キー | `sharpdeps.maxProjects`（既定60） / `sharpdeps.maxEdges`（既定200） / `sharpdeps.dotnetPath`（既定空） |
| Explorer対象拡張子 | `.sln` / `.slnx` / `.csproj` / `.fsproj` / `.vbproj` / `.vcxproj` |
| 拡張機能依存 | `ms-dotnettools.vscode-dotnet-runtime` |
| Webview panel ID | `sharpdeps.codeMap` |

これらは v0.1.0 でも維持する（SD-019の受入条件）。

## 回帰基準fixture

`tests/fixtures/quick-baseline/` に正解付きの小規模ソリューションを追加した。
プロジェクト循環、条件付き `ProjectReference`、名前空間循環、未使用 `using` による
推定のみの辺、エイリアス `using`、global using、multi-TFM、非C#プロジェクトを含む。

`expected/quick-report.json` が現行アナライザー出力の正規化スナップショットで、
`tests/analyzer/quickBaseline.test.ts` が比較する。詳細は fixture の `README.md`。

## 未検証事項

- VS Code実機でのUI操作（プロジェクト/名前空間切替、循環表示、テスト非表示、ズーム、
  SVG/PNG出力、AIコピー）は未実施。チェックリストを
  `existing-features.md` に用意し、SD-027で実施する。
- Linux / macOS での実行は未実施。
- SDKのない環境でのQuick動作は未実施（SD-003、SD-029で確認する）。
