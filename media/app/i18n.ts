import japanese from './ja.json';

export type Language = 'en' | 'ja';
export type Translator = (message: string, ...values: (string | number)[]) => string;

/** UI text only: code, paths, entity names and protocol values are not translated. */
export function translate(
  language: Language,
  message: string,
  ...values: (string | number)[]
): string {
  const template =
    language === 'ja' && Object.prototype.hasOwnProperty.call(japanese, message)
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
