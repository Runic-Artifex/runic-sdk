import { Schema } from "effect";
import { defineApplicationBridgeContract } from "../../../../../packages/web/application-bridge/dist/esm/index.js";

const RefreshSnapshot = Schema.Struct({}).annotate({ identifier: "RefreshSnapshot" });
const ManifestFingerprint = Schema.String.pipe(Schema.check(Schema.isPattern(/^[a-f0-9]{64}$/)));
const AssetSourceChanged = Schema.TaggedStruct("AssetSourceChanged", {
  manifestVersion: Schema.String,
  entryPointPath: Schema.String,
  manifestFingerprint: ManifestFingerprint,
});
const TranslationLocaleChanged = Schema.TaggedStruct("TranslationLocaleChanged", {
  catalog: Schema.String,
  oldLocale: Schema.String,
  newLocale: Schema.String,
});

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.refresh", version: 1 },
  csharp: { namespace: "Runic.Application.Hosting.RefreshContract", contractName: "Refresh" },
  snapshot: RefreshSnapshot,
  commands: [],
  events: [AssetSourceChanged, TranslationLocaleChanged],
  errors: [],
});
