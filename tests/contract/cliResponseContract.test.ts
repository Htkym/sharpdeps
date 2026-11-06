import * as fs from 'node:fs';
import * as path from 'node:path';
import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';

type JsonObject = Record<string, unknown>;
type QueryItem = JsonObject & { edge?: JsonObject };
type QueryFixture = {
  snapshot: JsonObject;
  items: QueryItem[];
  candidates: QueryItem[];
  unresolved: QueryItem[];
};

const schema = JSON.parse(
  fs.readFileSync(path.join(process.cwd(), 'docs/schemas/cli-response-v1.schema.json'), 'utf8')
) as object;
const fixture = JSON.parse(
  fs.readFileSync(
    path.join(process.cwd(), 'tests/fixtures/cli-response/enriched-query.json'),
    'utf8'
  )
) as QueryFixture;
const validate = new Ajv2020({ allErrors: true, strict: false, validateFormats: false }).compile(
  schema
);

function metadata(
  reply: QueryFixture,
  key: 'extensionBinding' | 'httpGetDeclaration',
  state: 'Saved' | 'Omitted'
): JsonObject {
  const edge = [...reply.items, ...reply.candidates, ...reply.unresolved]
    .map((item) => item.edge)
    .find((candidate) => (candidate?.[key] as JsonObject | undefined)?.state === state);
  if (!edge) {
    throw new Error(`Fixture lacks ${key}/${state}`);
  }
  return edge[key] as JsonObject;
}

const invalidCases: [string, (reply: QueryFixture) => void][] = [
  [
    'unknown edge field',
    (reply) => {
      reply.items[0].edge!.unexpected = true;
    }
  ],
  [
    'unknown metadata field',
    (reply) => {
      metadata(reply, 'extensionBinding', 'Saved').unexpected = true;
    }
  ],
  [
    'unknown metadata state',
    (reply) => {
      metadata(reply, 'httpGetDeclaration', 'Saved').state = 'Unknown';
    }
  ],
  [
    'missing saved field',
    (reply) => {
      delete metadata(reply, 'extensionBinding', 'Saved').constructedMethodDisplay;
    }
  ],
  [
    'saved field on omitted metadata',
    (reply) => {
      metadata(reply, 'httpGetDeclaration', 'Omitted').template = '';
    }
  ],
  [
    'unknown omission reason',
    (reply) => {
      metadata(reply, 'extensionBinding', 'Omitted').omissionReason = 'unknown';
    }
  ],
  [
    'overlong binding display',
    (reply) => {
      metadata(reply, 'extensionBinding', 'Saved').receiverTypeDisplay = 'x'.repeat(1025);
    }
  ],
  [
    'overlong HttpGet template',
    (reply) => {
      metadata(reply, 'httpGetDeclaration', 'Saved').template = 'x'.repeat(1025);
    }
  ]
];

describe('CLI response schema', () => {
  it('accepts the actual C#-serialized enriched query fixture', () => {
    expect(metadata(fixture, 'httpGetDeclaration', 'Saved').template).toBe('');
    expect(validate(fixture), JSON.stringify(validate.errors)).toBe(true);
  });

  it('accepts legacy replies without optional metadata', () => {
    const reply = structuredClone(fixture);
    delete reply.snapshot.namespaceBrowseVersion;
    for (const item of [...reply.items, ...reply.candidates, ...reply.unresolved]) {
      delete item.resultKind;
      if (item.edge) {
        delete item.edge.extensionBinding;
        delete item.edge.httpGetDeclaration;
      }
    }
    expect(validate(reply), JSON.stringify(validate.errors)).toBe(true);
  });

  it.each(invalidCases)('rejects %s', (_, mutate) => {
    const reply = structuredClone(fixture);
    mutate(reply);
    expect(validate(reply), JSON.stringify(validate.errors)).toBe(false);
  });
});
