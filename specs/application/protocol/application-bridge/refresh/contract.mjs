import { Schema } from "effect";

const ManifestFingerprint = Schema.String.pipe(
  Schema.pattern(/^[a-f0-9]{64}$/),
);

const events = [
  {
    tag: "AssetSourceChanged",
    schema: Schema.TaggedStruct("AssetSourceChanged", {
      manifestVersion: Schema.String,
      entryPointPath: Schema.String,
      manifestFingerprint: ManifestFingerprint,
    }),
  },
  {
    tag: "TranslationLocaleChanged",
    schema: Schema.TaggedStruct("TranslationLocaleChanged", {
      catalog: Schema.String,
      oldLocale: Schema.String,
      newLocale: Schema.String,
    }),
  },
];

export default {
  formatVersion: 1,
  protocol: { identity: "runic.artifex.refresh", version: 1 },
  csharp: { namespace: "Runic.Application.Hosting.RefreshContract", contractName: "Refresh" },
  limits: {
    maxFrameBytes: 262144,
    maxDepth: 32,
    maxStringBytes: 65536,
    maxCollectionItems: 4096,
    maxPendingCommands: 64,
  },
  schemas: {},
  commands: [],
  receipts: [],
  events,
  errors: [],
};
