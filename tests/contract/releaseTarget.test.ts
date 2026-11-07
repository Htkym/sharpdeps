import { createRequire } from 'node:module';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';

type ReleaseChannels = {
  include_vsix: boolean;
  publish_marketplace: boolean;
  publish_open_vsx: boolean;
};

const { resolveReleaseTarget } = createRequire(path.join(process.cwd(), 'package.json'))(
  './scripts/check-release-target.js'
) as {
  resolveReleaseTarget: (
    target: string | undefined,
    environment?: Record<string, string | undefined>
  ) => ReleaseChannels;
};

describe('release publication targets', () => {
  it('keeps historical NuGet-only releases independent of extension credentials', () => {
    expect(resolveReleaseTarget('nuget-only')).toEqual({
      include_vsix: false,
      publish_marketplace: false,
      publish_open_vsx: false
    });
  });

  it('selects Marketplace and VSIX without Open VSX even when both credentials exist', () => {
    expect(
      resolveReleaseTarget('nuget-and-marketplace', {
        VSCE_PAT: 'fake-marketplace-credential',
        OVSX_PAT: 'fake-open-vsx-credential'
      })
    ).toEqual({ include_vsix: true, publish_marketplace: true, publish_open_vsx: false });
  });

  it('selects every channel only for the explicit all target', () => {
    expect(
      resolveReleaseTarget('all', {
        VSCE_PAT: 'fake-marketplace-credential',
        OVSX_PAT: 'fake-open-vsx-credential'
      })
    ).toEqual({ include_vsix: true, publish_marketplace: true, publish_open_vsx: true });
  });

  it.each([undefined, '', ' \t '])(
    'rejects missing Marketplace credentials for its selected channel: %j',
    (credential) => {
      for (const target of ['nuget-and-marketplace', 'all']) {
        expect(() =>
          resolveReleaseTarget(target, {
            VSCE_PAT: credential,
            OVSX_PAT: 'fake-open-vsx-credential'
          })
        ).toThrow('requires VSCE_PAT');
      }
    }
  );

  it.each([undefined, '', ' \t '])(
    'rejects missing Open VSX credentials only when all selects that channel: %j',
    (credential) => {
      expect(() =>
        resolveReleaseTarget('all', {
          VSCE_PAT: 'fake-marketplace-credential',
          OVSX_PAT: credential
        })
      ).toThrow('requires OVSX_PAT');
      expect(
        resolveReleaseTarget('nuget-and-marketplace', {
          VSCE_PAT: 'fake-marketplace-credential',
          OVSX_PAT: credential
        }).publish_open_vsx
      ).toBe(false);
    }
  );

  it.each([undefined, '', 'marketplace', 'nuget-and-marketplace ', 'ALL'])(
    'rejects an unrecognized target: %j',
    (target) => {
      expect(() => resolveReleaseTarget(target)).toThrow('Select RELEASE_TARGET');
    }
  );
});
