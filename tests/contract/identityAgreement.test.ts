// Cross-language identity agreement.
//
// The analyzer (C#, analyzer/src/SharpDeps.Analysis.Core/Identity/Identity.cs) and
// the extension (TypeScript, src/analyzer/identity.ts) must produce the same ids for
// the same logical inputs. Both sides pin the same golden values, so a change to one
// implementation fails here and in the analyzer tests (IdentityTests) at once.

import { describe, expect, it } from 'vitest';
import {
  documentId,
  namespaceId,
  profileHash,
  projectLogicalId,
  projectVariantId,
  relationId,
  workspaceRootId
} from '../../src/analyzer/identity';

describe('identity agreement', () => {
  it('produces the golden ids shared with the analyzer', () => {
    const rootId = workspaceRootId('C:/repo');
    const logicalId = projectLogicalId(rootId, 'src/App/App.csproj');
    const variantId = projectVariantId(logicalId, 'net10.0', 'Debug', null);

    expect(rootId).toBe('wrk_65aa6de15d22cae5');
    expect(logicalId).toBe('prj_e69fd820b24f1b1f');
    expect(variantId).toBe('var_7b4b9ff45d2adef1');
    expect(namespaceId(variantId, 'Core')).toBe('ns_f9fa382aaca132c4');
    expect(
      relationId({
        basis: 'projectDeclared',
        sourceEntityId: 'prj_0123456789abcdef',
        targetEntityId: 'prj_fedcba9876543210',
        profileHash: '0123456789abcdef'
      })
    ).toBe('rel_3d6733779d039822');
    expect(documentId(rootId, 'src/App/Program.cs', 'userSource')).toBe('doc_ca362217930aa10a');
  });

  it('normalizes profile inputs the same way as the analyzer', () => {
    const upper = profileHash({
      configuration: 'Debug',
      projectVariants: [{ projectLogicalId: 'prj_1', targetFramework: 'NET10.0' }]
    });
    const lower = profileHash({
      configuration: 'debug',
      projectVariants: [{ projectLogicalId: 'prj_1', targetFramework: 'net10.0' }]
    });

    expect(upper).toBe('e909108ec79fc382');
    expect(lower).toBe(upper);
  });
});
