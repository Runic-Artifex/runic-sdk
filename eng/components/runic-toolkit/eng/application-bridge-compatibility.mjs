import { readFile } from "node:fs/promises";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

/**
 * Compares generated manifests and their JSON Schemas without inventing an IDL.
 * Each finding names the committed JSON source and JSON pointer that produced it.
 */
export async function compareApplicationBridgeContracts(baselinePath, candidatePath) {
  const baselineAbsolute = resolve(baselinePath);
  const candidateAbsolute = resolve(candidatePath);
  const baseline = JSON.parse(await readFile(baselineAbsolute, "utf8"));
  const candidate = JSON.parse(await readFile(candidateAbsolute, "utf8"));
  const findings = [];
  const add = (classification, code, message, source) => findings.push({ classification, code, message, source });
  const baselineName = relative(repositoryRoot, baselineAbsolute);
  const candidateName = relative(repositoryRoot, candidateAbsolute);

  if (baseline.protocol.identity !== candidate.protocol.identity) {
    add("breaking", "protocol-identity-changed", "Protocol identity changed.", `${candidateName}#/protocol/identity`);
  }
  if (baseline.protocol.version !== candidate.protocol.version) {
    add("breaking", "protocol-version-changed", "Protocol version changed.", `${candidateName}#/protocol/version`);
  }
  compareStrings("client envelope field", baseline.envelope.client.fields, candidate.envelope.client.fields, baselineName, candidateName, add);
  compareStrings("host envelope field", baseline.envelope.host.fields, candidate.envelope.host.fields, baselineName, candidateName, add);
  compareStrings("command", baseline.commands.map((item) => item.tag), candidate.commands.map((item) => item.tag), baselineName, candidateName, add);
  compareCommandMetadata(baseline.commands, candidate.commands, baselineName, candidateName, add);
  compareStrings("receipt", baseline.receipts, candidate.receipts, baselineName, candidateName, add);
  compareStrings("event", baseline.events, candidate.events, baselineName, candidateName, add);
  compareStrings("error", baseline.errors, candidate.errors, baselineName, candidateName, add);

  const baselineSchemas = schemaMap(baseline);
  const candidateSchemas = schemaMap(candidate);
  for (const key of [...new Set([...baselineSchemas.keys(), ...candidateSchemas.keys()])].sort(compareCodeUnits)) {
    const before = baselineSchemas.get(key);
    const after = candidateSchemas.get(key);
    if (before === undefined) {
      add("additive", "schema-added", `Schema '${key}' was added.`, schemaSource(candidateName, after, ""));
      continue;
    }
    if (after === undefined) {
      add("breaking", "schema-removed", `Schema '${key}' was removed.`, schemaSource(baselineName, before, ""));
      continue;
    }
    const beforeJson = JSON.parse(await readFile(resolve(dirname(baselineAbsolute), before.file), "utf8"));
    const afterJson = JSON.parse(await readFile(resolve(dirname(candidateAbsolute), after.file), "utf8"));
    compareSchema(beforeJson, afterJson, key, "", before, after, baselineName, candidateName, add);
  }

  findings.sort((left, right) => rank(left.classification) - rank(right.classification) ||
    compareCodeUnits(left.source, right.source) || compareCodeUnits(left.code, right.code));
  return {
    classification: findings.some((item) => item.classification === "breaking") ? "breaking"
      : findings.some((item) => item.classification === "additive") ? "additive" : "compatible",
    baseline: baselineName,
    candidate: candidateName,
    diagnostics: findings,
  };
}

function schemaMap(manifest) {
  return new Map(manifest.schemas.map((entry) => [`${entry.kind}:${entry.name}`, entry]));
}

function compareStrings(label, before, after, baselineName, candidateName, add) {
  const left = new Set(before);
  const right = new Set(after);
  for (const value of [...left].filter((item) => !right.has(item)).sort(compareCodeUnits)) {
    add("breaking", `${label.replaceAll(" ", "-")}-removed`, `${capitalize(label)} '${value}' was removed.`, listSource(baselineName, label, before.indexOf(value)));
  }
  for (const value of [...right].filter((item) => !left.has(item)).sort(compareCodeUnits)) {
    // Envelope decoders are strict, so an envelope field is wire-breaking.
    add(label.includes("envelope") || label === "enum value" ? "breaking" : "additive", `${label.replaceAll(" ", "-")}-added`, `${capitalize(label)} '${value}' was added.`, listSource(candidateName, label, after.indexOf(value)));
  }
}

function compareSchema(before, after, name, pointer, beforeEntry, afterEntry, baselineName, candidateName, add) {
  if (before.type !== after.type || before.$ref !== after.$ref) {
    add("breaking", "schema-shape-changed", `Schema '${name}' changed shape at '${pointer || "/"}'.`, schemaSource(candidateName, afterEntry, pointer));
    return;
  }
  if (Array.isArray(before.enum) || Array.isArray(after.enum)) {
    compareStrings("enum value", before.enum ?? [], after.enum ?? [], schemaSource(baselineName, beforeEntry, pointer), schemaSource(candidateName, afterEntry, pointer), add);
  }
  if (JSON.stringify(before.const) !== JSON.stringify(after.const)) {
    add("breaking", "const-changed", `Schema '${name}' changed its constant at '${pointer || "/"}'.`, schemaSource(candidateName, afterEntry, `${pointer}/const`));
  }
  for (const keyword of ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties"]) {
    if (JSON.stringify(before[keyword]) !== JSON.stringify(after[keyword])) {
      add("breaking", "constraint-changed", `Schema '${name}' changed '${keyword}' at '${pointer || "/"}'.`, schemaSource(candidateName, afterEntry, `${pointer}/${keyword}`));
    }
  }
  if (JSON.stringify(before.additionalProperties) !== JSON.stringify(after.additionalProperties)) {
    add("breaking", "additional-properties-changed", `Schema '${name}' changed additional-properties policy at '${pointer || "/"}'.`, schemaSource(candidateName, afterEntry, `${pointer}/additionalProperties`));
  }
  if (before.type === "object") {
    const beforeProperties = before.properties ?? {};
    const afterProperties = after.properties ?? {};
    const beforeRequired = new Set(before.required ?? []);
    const afterRequired = new Set(after.required ?? []);
    if (compareCodeUnits([...beforeRequired].sort(compareCodeUnits).join("\u0000"), [...afterRequired].sort(compareCodeUnits).join("\u0000")) !== 0) {
      add("breaking", "required-set-changed", `Schema '${name}' changed its required property set.`, schemaSource(candidateName, afterEntry, `${pointer}/required`));
    }
    for (const property of [...new Set([...Object.keys(beforeProperties), ...Object.keys(afterProperties)])].sort(compareCodeUnits)) {
      const child = `${pointer}/properties/${escapePointer(property)}`;
      if (!(property in afterProperties)) {
        add("breaking", "property-removed", `Property '${property}' was removed from '${name}'.`, schemaSource(baselineName, beforeEntry, child));
      } else if (!(property in beforeProperties)) {
        // Generated readers reject unknown fields. Even an optional emitted
        // property breaks an older strict receiver.
        add("breaking", afterRequired.has(property) ? "required-property-added" : "optional-property-added", `Property '${property}' was added to '${name}'.`, schemaSource(candidateName, afterEntry, child));
      } else {
        if (!beforeRequired.has(property) && afterRequired.has(property)) {
          add("breaking", "property-became-required", `Property '${property}' became required in '${name}'.`, schemaSource(candidateName, afterEntry, child));
        }
        if (beforeRequired.has(property) && !afterRequired.has(property)) {
          // Contract direction has not been independently proven. Strict
          // readers make either required-set direction wire-breaking.
          add("breaking", "property-became-optional", `Property '${property}' became optional in '${name}'.`, schemaSource(candidateName, afterEntry, child));
        }
        compareSchema(beforeProperties[property], afterProperties[property], name, child, beforeEntry, afterEntry, baselineName, candidateName, add);
      }
    }
  }
  if (before.type === "array" && before.items && after.items) {
    compareSchema(before.items, after.items, name, `${pointer}/items`, beforeEntry, afterEntry, baselineName, candidateName, add);
  }
  const beforeDefs = before.$defs ?? {};
  const afterDefs = after.$defs ?? {};
  for (const definition of [...new Set([...Object.keys(beforeDefs), ...Object.keys(afterDefs)])].sort(compareCodeUnits)) {
    const child = `${pointer}/$defs/${escapePointer(definition)}`;
    if (!(definition in afterDefs)) add("breaking", "definition-removed", `Definition '${definition}' was removed from '${name}'.`, schemaSource(baselineName, beforeEntry, child));
    else if (!(definition in beforeDefs)) add("additive", "definition-added", `Definition '${definition}' was added to '${name}'.`, schemaSource(candidateName, afterEntry, child));
    else compareSchema(beforeDefs[definition], afterDefs[definition], name, child, beforeEntry, afterEntry, baselineName, candidateName, add);
  }
}

function compareCommandMetadata(before, after, baselineName, candidateName, add) {
  const left = new Map(before.map((item) => [item.tag, item]));
  const right = new Map(after.map((item) => [item.tag, item]));
  for (const tag of [...left.keys()].filter((item) => right.has(item)).sort(compareCodeUnits)) {
    const candidateIndex = after.findIndex((item) => item.tag === tag);
    for (const key of ["receipt", "advancesRevision", "startsOperation", "cancellable"]) {
      if (JSON.stringify(left.get(tag)[key]) !== JSON.stringify(right.get(tag)[key])) {
        add("breaking", "command-metadata-changed", `Command '${tag}' changed '${key}'.`, `${candidateName}#/commands/${candidateIndex}/${key}`);
      }
    }
  }
}

function schemaSource(manifestPath, entry, pointer) {
  const manifestAbsolute = resolve(repositoryRoot, manifestPath);
  const schemaAbsolute = resolve(dirname(manifestAbsolute), entry.file);
  return `${relative(repositoryRoot, schemaAbsolute).replaceAll("\\", "/")}#${pointer}`;
}
function listSource(manifestPath, label, index) {
  if (label === "enum value") return `${sourceRoot(manifestPath)}/enum/${index}`;
  const pointer = label === "client envelope field" ? "/envelope/client/fields"
    : label === "host envelope field" ? "/envelope/host/fields"
      : label === "command" ? "/commands"
        : label === "receipt" ? "/receipts"
          : label === "event" ? "/events"
            : label === "error" ? "/errors" : "";
  return `${manifestPath}#${pointer}/${index}`;
}
function sourceRoot(source) { return source.includes("#") ? source : `${source}#`; }
function escapePointer(value) { return value.replaceAll("~", "~0").replaceAll("/", "~1"); }
function rank(value) { return value === "breaking" ? 0 : value === "additive" ? 1 : 2; }
function compareCodeUnits(left, right) { return left < right ? -1 : left > right ? 1 : 0; }
function capitalize(value) { return value[0].toUpperCase() + value.slice(1); }

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [baseline, candidate] = process.argv.slice(2);
  if (baseline === undefined || candidate === undefined) {
    throw new Error("Usage: node eng/application-bridge-compatibility.mjs <baseline-manifest> <candidate-manifest>");
  }
  process.stdout.write(`${JSON.stringify(await compareApplicationBridgeContracts(baseline, candidate), null, 2)}\n`);
}
