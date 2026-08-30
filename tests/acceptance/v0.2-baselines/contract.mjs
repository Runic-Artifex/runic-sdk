import * as Either from 'effect/Either';
import * as JSONSchema from 'effect/JSONSchema';
import * as S from 'effect/Schema';

export const SCHEMA_ID = 'runic.v0.2-baseline/2';
const Status = S.Literal('passed', 'failed', 'blocked');
const NullableString = S.Union(S.String, S.Null);
const NullableObject = S.Union(S.Record({ key: S.String, value: S.Unknown }), S.Null);
const GitRevision = S.String.pipe(S.pattern(/^[0-9a-f]{40}$/));
const Sha256 = S.String.pipe(S.pattern(/^[0-9a-f]{64}$/));
const NonNegativeInt = S.Int.pipe(S.nonNegative());
const CleanSnapshot = S.Struct({ revision: GitRevision, tree: GitRevision, status: S.Literal('') });

// This is deliberately the one receipt contract. `details` is metric-specific
// evidence, while the envelope and every common metric field remain closed.
export const Metric = S.Struct({
  id: S.String,
  category: S.String,
  status: Status,
  required: S.Boolean,
  unit: S.String,
  argv: S.Array(S.String),
  cwd: S.String,
  clock: S.String,
  warmups: S.Int,
  samples: S.Int,
  observations: S.Array(S.Int),
  summary: NullableObject,
  details: S.Record({ key: S.String, value: S.Unknown }),
  reasonCode: NullableString
});

export const Receipt = S.Struct({
  schema: S.Literal(SCHEMA_ID),
  source: S.Struct({
    examples: S.Struct({ revision: S.String, tree: S.String, before: S.Unknown, after: S.Unknown }),
    editor: S.Struct({ revision: S.String, tree: S.String, before: S.Unknown, after: S.Unknown }),
    releaseManifest: S.Struct({
      path: S.Literal('runic.release.json'),
      revision: GitRevision,
      tree: GitRevision,
      digest: Sha256,
      publicPackageCounts: S.Struct({ nuget: NonNegativeInt, npm: NonNegativeInt }),
      before: CleanSnapshot,
      after: CleanSnapshot
    })
  }),
  environment: S.Struct({
    os: S.String,
    arch: S.String,
    tools: S.Record({ key: S.String, value: S.Struct({ path: NullableString, version: NullableString }) }),
    packageIdentities: S.Struct({ npm: S.Array(S.String), nuget: S.Array(S.String) }),
    hashes: S.Record({ key: S.String, value: S.String })
  }),
  methodology: S.Struct({
    version: S.Literal(2),
    clock: S.Literal('process.hrtime.bigint-monotonic-nanoseconds'),
    volatilePaths: S.Array(S.String)
  }),
  measurements: S.Array(Metric)
});

export function decodeReceipt(value) {
  const result = S.decodeUnknownEither(Receipt)(value, { errors: 'all', onExcessProperty: 'error' });
  return Either.isRight(result)
    ? { ok: true, value: result.right }
    : { ok: false, errors: [String(result.left)] };
}

export function generatedSchema() {
  const schema = JSONSchema.make(Receipt, { target: 'jsonSchema2020-12' });
  schema.$id = 'https://runic-artifex.eu/schemas/runic.v0.2-baseline-2.json';
  return schema;
}

export function stableJson(value) {
  if (Array.isArray(value)) return value.map(stableJson);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, stableJson(value[key])]));
  return value;
}
