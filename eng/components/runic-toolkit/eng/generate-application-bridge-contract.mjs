import { createHash } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { JSONSchema } from "effect";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const positional = process.argv.slice(2).filter((argument) => !argument.startsWith("--"));
const sourcePath = resolve(repositoryRoot, positional[0] ?? "protocol/application-bridge/setup/contract.mjs");
const outputDirectory = resolve(repositoryRoot, positional[1] ?? "protocol/application-bridge/setup/generated");
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
const schemaEntries = [];
for (const declaration of declarations) {
  const jsonSchema = JSONSchema.make(declaration.schema, { topLevelReferenceStrategy: "skip" });
  const path = `schemas/${declaration.kind}/${declaration.name}.schema.json`;
  const text = serialize({
    $schema: "https://json-schema.org/draft/2020-12/schema",
    $id: `${contract.protocol.identity}/${contract.protocol.version}/${declaration.name}`,
    ...jsonSchema,
  });
  files.set(path, text);
  schemaEntries.push({
    name: declaration.name,
    kind: declaration.kind,
    ...(declaration.direction === undefined ? {} : { direction: declaration.direction }),
    file: path,
    sha256: fingerprint(text),
  });
}

const manifest = {
  generatorFormatVersion: contract.formatVersion,
  protocol: contract.protocol,
  envelope: {
    client: {
      fields: ["protocol", "version", "kind", "commandId", "sessionId", "expectedRevision", "payload"],
      kinds: ["initialize", "dispatch", "cancelOperation", "uiReady", "uiRendered"],
    },
    host: {
      fields: ["protocol", "version", "kind", "sessionId", "sequence", "revision", "commandId", "operationId", "payload"],
      kinds: ["snapshot", "receipt", "event", "error"],
    },
  },
  csharp: contract.csharp,
  limits: contract.limits,
  schemas: schemaEntries,
  commands: [...contract.commands]
    .sort((left, right) => left.tag.localeCompare(right.tag))
    .map(({ schema: _, ...item }) => item),
  receipts: contract.receipts.map(({ tag }) => tag).sort(),
  events: contract.events.map(({ tag }) => tag).sort(),
  errors: contract.errors.map(({ tag }) => tag).sort(),
};
files.set("bridge.manifest.json", serialize(manifest));

if (check) {
  const failures = [];
  for (const [path, expected] of files) {
    const absolute = join(outputDirectory, path);
    const actual = await readFile(absolute, "utf8").catch(() => undefined);
    if (actual !== expected) failures.push(relative(repositoryRoot, absolute));
  }
  if (failures.length > 0) {
    throw new Error(`Application Bridge artifacts are stale: ${failures.join(", ")}`);
  }
} else {
  await rm(outputDirectory, { recursive: true, force: true });
  for (const [path, text] of files) {
    const absolute = join(outputDirectory, path);
    await mkdir(dirname(absolute), { recursive: true });
    await writeFile(absolute, text, "utf8");
  }
}
