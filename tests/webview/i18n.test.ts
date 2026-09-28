import { describe, expect, it } from 'vitest';
import japanese from '../../media/app/ja.json';
import { translate } from '../../media/app/i18n';
import { INITIAL_STATE, selectStatusMessage, viewReducer } from '../../media/app/state';
import {
  deserializeViewState,
  serializeViewState,
  VIEW_STATE_VERSION
} from '../../media/app/serializer';

describe('UI language', () => {
  it('persists the chosen language without altering the analysis or camera', () => {
    const before = {
      ...INITIAL_STATE,
      analysisId: 'an_0000000000000001',
      camera: { zoom: 1.5, scrollLeft: 30, scrollTop: 10 }
    };
    const after = viewReducer(before, { type: 'languageChanged', language: 'ja' });
    expect(after).toEqual({ ...before, language: 'ja' });
    expect(deserializeViewState(serializeViewState(after)).state.language).toBe('ja');
    for (const language of [undefined, 'fr', null, 1])
      expect(
        deserializeViewState({ version: VIEW_STATE_VERSION, language }).state.language
      ).toBeUndefined();
  });

  it('retains every format parameter and inserts code-derived values literally', () => {
    for (const [english, text] of Object.entries(japanese))
      expect(text.match(/\{\d+\}/g)?.sort() ?? []).toEqual(english.match(/\{\d+\}/g)?.sort() ?? []);
    const query = '<script>{0}$&</script>';
    expect(translate('ja', 'No match for "{0}"', query)).toContain(query);
    expect(translate('en', 'No match for "{0}"', query)).toBe(`No match for "${query}"`);
    expect(translate('ja', 'MSB0001: raw diagnostic')).toBe('MSB0001: raw diagnostic');
    for (const name of ['constructor', 'toString', '__proto__'])
      expect(translate('ja', name)).toBe(name);
  });

  it('translates progress and completion after a language change without rewriting state', () => {
    const complete = viewReducer(INITIAL_STATE, {
      type: 'analysisComplete',
      analysisId: 'an_0000000000000001',
      completeness: 'completeWithinScope',
      coverage: { discovered: 4, loaded: 3, analyzed: 2, failed: 1, skipped: 1 }
    });
    expect(selectStatusMessage({ ...complete, language: 'ja' })).toBe(
      '解析が完了しました · プロジェクト 4 件中 2 件を解析'
    );
    const progress = viewReducer(
      { ...INITIAL_STATE, status: 'analyzing' },
      { type: 'analysisProgress', stage: 'compile', loaded: 3, analyzed: 2, elapsedMs: 100 }
    );
    expect(selectStatusMessage({ ...progress, language: 'ja' })).toContain(
      'コンパイルしています · 読み込み 3 件 · 解析 2 件'
    );
    expect(complete.statusMessage).toContain('Analysis complete');
  });
});
