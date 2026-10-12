import japanese from './ja.json';

const savedQueryJapanese: Record<string, string> = {
  'Filtering is unavailable in this saved reader.':
    'この保存結果では分類による絞り込みを利用できません。',
  'Location unavailable': '場所が返却されていません',
  'Source location unavailable': '移動できるソース位置がありません',
  'Not returned': '返却されていません',
  Location: '場所',
  Reason: '理由',
  unclassified: '未分類',
  'More results': '続きを表示',
  'Update saved index': '保存済み索引を更新',
  'Retry update': '更新を再試行',
  'Cancel update': '更新要求を取り消す',
  'Cancelling…': '取消を待機中…',
  Cancel: '取り消す',
  'Retry analysis': '解析を再試行',
  'Update analysis': '解析を更新',
  'Select the target and update scope.': '更新対象と範囲を選択してください。',
  'Updating saved index; the saved view remains available.':
    '保存済み索引を更新しています。保存済み表示は引き続き利用できます。',
  'Cancelling this request; waiting for shared work to finish…':
    'この要求を取り消しています。共有処理の終了を待っています…',
  'Request cancelled. Refresh to check the saved generation.':
    '要求を取り消しました。Refreshで保存世代を確認してください。',
  'Update not confirmed. Refresh to check the saved generation.':
    '更新を確認できませんでした。Refreshで保存世代を確認してください。',
  'Updating requires workspace trust and no other active saved update.':
    '更新にはworkspaceの信頼と、実行中の別の保存更新がないことが必要です。',
  'Saved index': '保存済み索引',
  'Saved index loaded (display subset)': '保存済み索引を読み込みました（表示は一部）',
  'Generation {0}': '世代 {0}',
  'Variants: {0}': 'variant: {0}',
  'Coverage: {0}': '解析範囲: {0}',
  'Freshness: {0}': '鮮度: {0}',
  '{0} item(s) returned': '返却項目 {0} 件',
  '{0} candidate(s) · {1} unresolved': '候補 {0} 件 · 未解決 {1} 件',
  'truncated result': '打切りあり',
  'query not truncated': 'Query打切りなし',
  '{0} node(s), {1} relation(s) shown from the returned graph':
    '返却グラフから表示: node {0} 件 · 関係 {1} 件',
  'Search results ({0} returned)': '検索結果（返却 {0} 件）',
  'Workspace id': 'workspace ID',
  'Snapshot id': 'snapshot ID',
  Generation: '世代',
  Variants: 'variant',
  Freshness: '鮮度',
  Certainty: '確度',
  'Returned query items': 'Query返却項目',
  Symbols: 'symbol',
  'Returned symbols': '返却されたsymbol',
  'Load more symbols': 'symbolをさらに読む',
  'Source occurrence': '参照元occurrence',
  'Target occurrence': '参照先occurrence',
  Variant: 'variant',
  'Dependencies (returned entries)': '依存先（返却項目）',
  'Dependents (returned entries)': '依存元（返却項目）',
  'No related entries returned.': '関連項目は返却されていません。',
  'Candidates and unresolved relations are leaves; they do not prove further resolved paths.':
    '候補・未解決の関係は末端であり、その先の確定経路を証明しません。',
  'Symbols keep their original kind. Type/member aggregation is not connected.':
    'symbolは元のkindを保持します。Type/Memberの集約は未接続です。',
  'Saved index supports symbol views and bounded dependencies/dependents only.':
    '保存済み索引ではsymbol表示と範囲を限定した依存先・依存元を扱います。',
  'This operation is unavailable for saved index.': 'この操作は保存済み索引では未対応です。',
  'Cycle analysis is unavailable for saved index.': '保存済み索引のcycle解析は未対応です。',
  'Saved evidence location': '保存済みevidenceの位置',
  'No saved evidence location returned.': '保存済みevidenceの位置は返却されていません。',
  'Raw UTF-16 span': '元UTF-16 span',
  'Content hash': 'content hash',
  'Source id': 'source ID',
  'Legacy evidence aggregation/paging, context copy and exports are unavailable for saved index.':
    '保存済み索引では従来evidenceの集約・ページング、contextコピー、exportは未対応です。'
};

export type Language = 'en' | 'ja';
export type Translator = (message: string, ...values: (string | number)[]) => string;

/** UI text only: code, paths, entity names and protocol values are not translated. */
export function translate(
  language: Language,
  message: string,
  ...values: (string | number)[]
): string {
  const template =
    language === 'ja' && Object.prototype.hasOwnProperty.call(savedQueryJapanese, message)
      ? savedQueryJapanese[message]
      : language === 'ja' && Object.prototype.hasOwnProperty.call(japanese, message)
        ? (japanese as Record<string, string>)[message]
        : message;
  return template.replace(/\{(\d+)\}/g, (token, index: string) =>
    values[Number(index)] === undefined ? token : String(values[Number(index)])
  );
}

export function translator(language: Language = 'en'): Translator {
  return (message, ...values) => translate(language, message, ...values);
}

/** Capture only the static shell, before any code-derived text is inserted. */
export function shellTranslations(root: HTMLElement): (language: Language) => void {
  const texts: Array<{ node: Text; source: string }> = [];
  const attributes: Array<{ element: Element; name: string; source: string }> = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const node = walker.currentNode as Text;
    const source = node.data;
    if (Object.prototype.hasOwnProperty.call(japanese, source)) texts.push({ node, source });
  }
  for (const element of root.querySelectorAll('*'))
    for (const name of ['aria-label', 'title', 'placeholder']) {
      const source = element.getAttribute(name);
      if (source && Object.prototype.hasOwnProperty.call(japanese, source))
        attributes.push({ element, name, source });
    }
  let current: Language | undefined;
  return (language) => {
    if (language === current) return;
    current = language;
    root.lang = language;
    for (const { node, source } of texts)
      if (root.contains(node)) node.data = translate(language, source);
    for (const { element, name, source } of attributes)
      element.setAttribute(name, translate(language, source));
  };
}
