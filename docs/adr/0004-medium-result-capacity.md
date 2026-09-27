# Medium 解析結果の受入容量

2026-09-27。計画の 30 projects / 3,000 C# files に合わせた固定 fixture で、102,435 件の根拠、59,061,362 bytes の report v2、64,387,905 bytes の evidence NDJSON を実測した。

従来の report 上限 32 MiB では、Analyzer が正常終了しても Host への登録が拒否される。参照や型を捨てて上限へ合わせる案は採用しない。既定の report 上限を 128 MiB に変更する。上限自体、schema/ID/参照整合性の検証、evidence の 512 MiB 上限、保持する結果数 2 件は維持する。

この変更は性能予算の緩和ではない。Medium の 120 秒、Analyzer の 2 GiB、Host の検索・根拠応答の予算は変更しない。入力・生データ・再測定結果は `evidence/sd-028-acceptance.json` と `.local/perf-v010` に記録する。さらに大きい入力を無制限に読み込む仕様にはしない。
