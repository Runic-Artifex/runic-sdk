import { createHash } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { JSONSchema } from "effect";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourcePath = resolve(repositoryRoot, "samples/03-SetupApplication/Contract/contract.mjs");
const outputDirectory = resolve(repositoryRoot, "samples/03-SetupApplication/Contract/generated");
const frontendContractPath = resolve(repositoryRoot, "samples/03-SetupApplication/Frontend/src/setup-bridge-contract.generated.ts");
const check = process.argv.includes("--check");
const contract = (await import(`${pathToFileURL(sourcePath).href}?v=${Date.now()}`)).default;

const canonical = (value) => {
  if (Array.isArray(value)) return value.map(canonical);
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])]));
  }
  return value;
};
const serialize = (value) => `${JSON.stringify(canonical(value), null, 2)}\n`;
const fingerprint = (text) => createHash("sha256").update(text).digest("hex");
const declarations = [
  ...Object.entries(contract.schemas).map(([name, schema]) => ({ name, schema, kind: "type" })),
  ...contract.commands.map((item) => ({ ...item, name: item.tag, kind: "command", direction: "client-to-host" })),
  ...contract.receipts.map((item) => ({ ...item, name: item.tag, kind: "receipt", direction: "host-to-client" })),
  ...contract.events.map((item) => ({ ...item, name: item.tag, kind: "event", direction: "host-to-client" })),
  ...contract.errors.map((item) => ({ ...item, name: item.tag, kind: "error", direction: "host-to-client" })),
].sort((left, right) => left.name.localeCompare(right.name));

const files = new Map();
const projections = new Map();
const schemas = [];
for (const declaration of declarations) {
  const schema = JSONSchema.make(declaration.schema, { topLevelReferenceStrategy: "skip" });
  const path = `schemas/${declaration.kind}/${declaration.name}.schema.json`;
  const text = serialize({
    $schema: "https://json-schema.org/draft/2020-12/schema",
    $id: `${contract.protocol.identity}/${contract.protocol.version}/${declaration.name}`,
    ...schema,
  });
  files.set(path, text);
  schemas.push({
    name: declaration.name,
    kind: declaration.kind,
    ...(declaration.direction === undefined ? {} : { direction: declaration.direction }),
    file: path,
    sha256: fingerprint(text),
  });
}

const manifestBody = {
  generatorFormatVersion: contract.formatVersion,
  protocol: contract.protocol,
  envelope: {
    client: {
      fields: ["protocol", "version", "contractFingerprint", "connectionEpoch", "kind", "commandId", "sessionId", "expectedRevision", "payload"],
      kinds: ["initialize", "dispatch", "cancelOperation", "uiReady", "uiRendered"],
    },
    host: {
      fields: ["protocol", "version", "contractFingerprint", "connectionEpoch", "kind", "sessionId", "sequence", "revision", "commandId", "operationId", "payload"],
      kinds: ["snapshot", "receipt", "event", "error"],
    },
  },
  csharp: contract.csharp,
  limits: contract.limits,
  schemas,
  commands: [...contract.commands]
    .sort((left, right) => left.tag.localeCompare(right.tag))
    .map(({ schema: _, ...item }) => item),
  receipts: contract.receipts.map(({ tag }) => tag).sort(),
  events: contract.events.map(({ tag }) => tag).sort(),
  errors: contract.errors.map(({ tag }) => tag).sort(),
};
const contractFingerprint = fingerprint(serialize(manifestBody));
files.set("bridge.manifest.json", serialize({
  ...manifestBody,
  contractFingerprint,
}));
files.set("SetupBridgeContract.cs", `namespace ${contract.csharp.namespace};

internal static class SetupBridgeContract
{
    internal const string ProtocolIdentity = "${contract.protocol.identity}";
    internal const int ProtocolVersion = ${contract.protocol.version};
    internal const string ProtocolArtifactIdentity = "${contract.protocol.identity}/${contract.protocol.version}";
    internal const string Fingerprint = "${contractFingerprint}";
}
`);
projections.set(frontendContractPath, `export const SetupBridgeContract = {
  identity: "${contract.protocol.identity}",
  version: ${contract.protocol.version},
  fingerprint: "${contractFingerprint}",
} as const;
`);

if (check) {
  const stale = [];
  for (const [path, expected] of files) {
    const absolute = join(outputDirectory, path);
    const actual = await readFile(absolute, "utf8").catch(() => undefined);
    if (actual !== expected) stale.push(relative(repositoryRoot, absolute));
  }
  for (const [absolute, expected] of projections) {
    const actual = await readFile(absolute, "utf8").catch(() => undefined);
    if (actual !== expected) stale.push(relative(repositoryRoot, absolute));
  }
  if (stale.length > 0) throw new Error(`Setup contract artifacts are stale: ${stale.join(", ")}`);
} else {
  await rm(outputDirectory, { recursive: true, force: true });
  for (const [path, text] of files) {
    const absolute = join(outputDirectory, path);
    await mkdir(dirname(absolute), { recursive: true });
    await writeFile(absolute, text, "utf8");
  }
  for (const [absolute, text] of projections) {
    await mkdir(dirname(absolute), { recursive: true });
    await writeFile(absolute, text, "utf8");
  }
}
