import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contractId = "runic.desktop.presentation";
const contractVersion = 1;
const contractIdentity = `${contractId}/${contractVersion}`;
const dottedId = /^[a-z][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)+$/;
const name = /^[A-Za-z][A-Za-z0-9._/-]*$/;
const errorCategories = new Set([
  "invalidArgument", "invalidState", "invalidFrame", "limitExceeded",
  "authenticationDenied", "originDenied", "capabilityDenied", "notFound",
  "conflict", "cancelled", "timedOut", "transportClosed", "hostStopping",
  "unavailable", "operationFailed",
]);
const expectedScenarios = new Map([
  ["cancellation.request-disconnect-stops-stream.v1", "cancellation.request-disconnect-stops-stream.json"],
  ["errors.handler-failure-redaction.v1", "errors.handler-failure-redaction.json"],
  ["lifecycle.shared-listener-surface-close.v1", "lifecycle.shared-listener-surface-close.json"],
  ["protocol.reconnect-new-session.v1", "protocol.reconnect-new-session.json"],
  ["requests.head-stream-metadata.v1", "requests.head-stream-metadata.json"],
  ["security.default-admission.v1", "security.default-admission.json"],
  ["security.surface-credential-isolation.v1", "security.surface-credential-isolation.json"],
]);
const expectedVectors = new Map([
  ["protocol.webui-compat-check-token.v1", "protocol.webui-compat-check-token.json"],
  ["protocol.webui-compat-multi-header.v1", "protocol.webui-compat-multi-header.json"],
  ["serialization.duplicate-key-rejected.v1", "serialization.duplicate-key-rejected.json"],
]);
const expectedParityEntries = new Set([
  "cs-webui.basic-sample",
  "cs-webui.high-level-sample",
  "cs-webui.native-aot-smoke",
  "webui.minimal",
  "webui.call-native-from-javascript",
  "webui.call-javascript-from-native",
  "webui.serve-a-folder",
  "webui.test-index-redirect",
  "webui.virtual-file-system",
  "webui.chatgpt-api",
  "webui.custom-web-server",
  "webui.frameless",
  "webui.public-network-access",
  "webui.react",
  "webui.stress-test",
  "webui.text-editor",
  "webui.web-app-multi-client",
  "webui.starter-kit",
]);
const parityDispositions = new Set([
  "desktop-sample",
  "automated-behavioral-equivalent",
  "suite-level-replacement",
  "documented-non-goal",
]);

export function verifyContract(root = repositoryRoot) {
  const errors = [];
  const report = (message) => errors.push(message);
  const contractRoot = join(root, "contract");
  const conformanceRoot = join(contractRoot, "conformance");
  const scenarioSchema = readJson(join(conformanceRoot, "scenario.schema.json"), report);
  const vectorSchema = readJson(join(conformanceRoot, "codec-vector.schema.json"), report);
  const parity = readJson(join(conformanceRoot, "cs-webui-example-parity.json"), report);

  validateSchemaMetadata(
    scenarioSchema,
    "https://runic-artifex.org/schemas/runic-desktop/conformance-scenario-1.schema.json",
    "runic.desktop.conformance-scenario/1",
    report,
  );
  validateSchemaMetadata(
    vectorSchema,
    "https://runic-artifex.org/schemas/runic-desktop/codec-vector-1.schema.json",
    "runic.desktop.codec-vector/1",
    report,
  );
  validateMarkdownClosure(contractRoot, report);
  validateParityCatalog(parity, root, report);

  validateFixtureDirectory(
    join(conformanceRoot, "scenarios"),
    expectedScenarios,
    "scenario",
    (fixture, path) => validateScenario(fixture, path, report),
    report,
  );
  validateFixtureDirectory(
    join(conformanceRoot, "vectors"),
    expectedVectors,
    "vector",
    (fixture, path) => validateVector(fixture, path, report),
    report,
  );

  if (errors.length > 0) {
    throw new Error(`Runic Desktop contract verification failed:\n${errors.map((error) => `- ${error}`).join("\n")}`);
  }

  return {
    identity: contractIdentity,
    scenarios: expectedScenarios.size,
    vectors: expectedVectors.size,
    parityEntries: expectedParityEntries.size,
  };
}

function validateParityCatalog(catalog, root, report) {
  const label = "contract/conformance/cs-webui-example-parity.json";
  if (!hasOnlyKeys(catalog, ["format", "oracles", "entries"], label, report)) return;
  requireKeys(catalog, ["format", "oracles", "entries"], label, report);
  if (catalog.format !== "runic.desktop.cs-webui-example-parity/1") report(`${label} has an invalid format.`);
  if (!hasOnlyKeys(catalog.oracles, ["webUiExamplesRevision", "csWebUiRevision"], `${label}.oracles`, report)) return;
  requireKeys(catalog.oracles, ["webUiExamplesRevision", "csWebUiRevision"], `${label}.oracles`, report);
  for (const [key, value] of Object.entries(catalog.oracles)) {
    if (typeof value !== "string" || !/^[a-f0-9]{40}$/.test(value)) report(`${label}.oracles.${key} must be a full Git revision.`);
  }
  if (!Array.isArray(catalog.entries)) {
    report(`${label}.entries must be an array.`);
    return;
  }
  const actual = new Set();
  for (const [index, entry] of catalog.entries.entries()) {
    const item = `${label}.entries[${index}]`;
    if (!hasOnlyKeys(entry, ["id", "disposition", "target", "rationale"], item, report)) continue;
    requireKeys(entry, ["id", "disposition", "target", "rationale"], item, report);
    if (typeof entry.id !== "string" || !dottedId.test(entry.id)) report(`${item}.id is invalid.`);
    if (actual.has(entry.id)) report(`${item}.id duplicates ${entry.id}.`);
    actual.add(entry.id);
    if (!parityDispositions.has(entry.disposition)) report(`${item}.disposition is invalid.`);
    if (typeof entry.target !== "string" || entry.target.startsWith("/") || entry.target.includes("..") || !existsSync(resolve(root, entry.target))) {
      report(`${item}.target must name an existing repository-relative artifact.`);
    }
    if (typeof entry.rationale !== "string" || entry.rationale.length < 20) report(`${item}.rationale must explain the mapping.`);
  }
  const missing = [...expectedParityEntries].filter((id) => !actual.has(id));
  const unexpected = [...actual].filter((id) => !expectedParityEntries.has(id));
  if (missing.length > 0 || unexpected.length > 0) {
    report(`${label} is not closed: missing ${missing.join(", ") || "none"}; unexpected ${unexpected.join(", ") || "none"}.`);
  }
}

function validateSchemaMetadata(schema, expectedId, expectedFormat, report) {
  if (!isObject(schema)) return;
  if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") {
    report(`${expectedFormat} schema must declare JSON Schema draft 2020-12.`);
  }
  if (schema.$id !== expectedId) report(`${expectedFormat} schema has an unexpected $id.`);
  if (schema.properties?.format?.const !== expectedFormat) {
    report(`${expectedFormat} schema must fix its format identifier.`);
  }
  if (schema.$defs?.contract?.properties?.id?.const !== contractId ||
      schema.$defs?.contract?.properties?.version?.const !== contractVersion) {
    report(`${expectedFormat} schema must fix ${contractIdentity}.`);
  }
}

function validateMarkdownClosure(contractRoot, report) {
  const required = [
    "README.md",
    "presentation-host.md",
    "ownership.md",
    "language-mappings.md",
    "milestones.md",
    "wire-profile.md",
    "conformance/README.md",
    "conformance/scenario.schema.json",
    "conformance/codec-vector.schema.json",
    "conformance/cs-webui-example-parity.json",
  ];
  for (const entry of required) {
    if (!existsSync(join(contractRoot, entry))) report(`Contract closure is missing ${entry}.`);
  }

  const readme = readText(join(contractRoot, "README.md"), report);
  if (!readme.includes(`Contract identity: \`${contractIdentity}\``)) {
    report(`contract/README.md must declare ${contractIdentity}.`);
  }
  for (const document of ["presentation-host.md", "ownership.md", "language-mappings.md", "milestones.md", "wire-profile.md"]) {
    if (!readme.includes(`](${document})`)) report(`contract/README.md must link ${document}.`);
  }

  for (const path of findFiles(contractRoot, (entry) => extname(entry) === ".md")) {
    const text = readText(path, report);
    for (const target of markdownTargets(text)) {
      if (target.startsWith("#") || /^[a-z][a-z0-9+.-]*:/i.test(target)) continue;
      const local = target.split("#", 1)[0];
      if (local.length > 0 && !existsSync(resolve(dirname(path), local))) {
        report(`${relative(contractRoot, path)} links missing contract artifact ${target}.`);
      }
    }
  }
}

function validateFixtureDirectory(directory, expected, kind, validate, report) {
  const actual = readdirSync(directory)
    .filter((entry) => extname(entry) === ".json")
    .sort();
  const wanted = [...expected.values()].sort();
  if (actual.join("\n") !== wanted.join("\n")) {
    report(`${kind} fixture set is not closed: expected ${wanted.join(", ")}; found ${actual.join(", ")}.`);
  }

  const seen = new Set();
  for (const file of actual) {
    const path = join(directory, file);
    const fixture = readJson(path, report);
    validate(fixture, path);
    if (isObject(fixture) && typeof fixture.id === "string") {
      if (seen.has(fixture.id)) report(`${kind} id ${fixture.id} is duplicated.`);
      seen.add(fixture.id);
      if (expected.get(fixture.id) !== file) {
        report(`${kind} ${file} does not match an authorized fixture id.`);
      }
    }
  }
}

function validateScenario(fixture, path, report) {
  const label = relative(repositoryRoot, path);
  if (!hasOnlyKeys(fixture, ["format", "id", "contract", "area", "requires", "clock", "ids", "policy", "limits", "arrange", "steps", "expect"], label, report)) return;
  requireKeys(fixture, ["format", "id", "contract", "area", "ids", "arrange", "steps", "expect"], label, report);
  if (fixture.format !== "runic.desktop.conformance-scenario/1") report(`${label} has an invalid format.`);
  validateIdentity(fixture.contract, label, report);
  if (typeof fixture.id !== "string" || !dottedId.test(fixture.id)) report(`${label} has an invalid id.`);
  if (!new Set(["lifecycle", "requests", "protocol", "serialization", "cancellation-streaming", "security", "errors"]).has(fixture.area)) {
    report(`${label} has an invalid area.`);
  }
  validateNameArray(fixture.requires, `${label}.requires`, report, true);
  validateIds(fixture.ids, `${label}.ids`, report);
  validateOperations(fixture.arrange, `${label}.arrange`, false, report);
  validateOperations(fixture.steps, `${label}.steps`, true, report);
  validateExpect(fixture.expect, `${label}.expect`, report);
}

function validateVector(fixture, path, report) {
  const label = relative(repositoryRoot, path);
  if (!hasOnlyKeys(fixture, ["format", "id", "contract", "area", "profile", "operation", "limits", "input", "expect"], label, report)) return;
  requireKeys(fixture, ["format", "id", "contract", "area", "profile", "operation", "input", "expect"], label, report);
  if (fixture.format !== "runic.desktop.codec-vector/1") report(`${label} has an invalid format.`);
  validateIdentity(fixture.contract, label, report);
  if (typeof fixture.id !== "string" || !dottedId.test(fixture.id)) report(`${label} has an invalid id.`);
  if (!new Set(["protocol", "serialization"]).has(fixture.area)) report(`${label} has an invalid area.`);
  if (typeof fixture.profile !== "string" || !/^[a-z][a-z0-9.-]+\/[A-Za-z0-9._-]+$/.test(fixture.profile)) {
    report(`${label} has an invalid profile.`);
  }
  if (!new Set(["encode", "decode", "roundTrip"]).has(fixture.operation)) report(`${label} has an invalid operation.`);
  validateValue(fixture.input, `${label}.input`, report);
  if (!isObject(fixture.expect) || ("value" in fixture.expect) === ("error" in fixture.expect)) {
    report(`${label}.expect must contain exactly one value or error.`);
  } else if ("value" in fixture.expect) {
    validateValue(fixture.expect.value, `${label}.expect.value`, report);
  } else {
    validateError(fixture.expect.error, `${label}.expect.error`, report);
  }
}

function validateIdentity(value, label, report) {
  if (!isObject(value) || value.id !== contractId || value.version !== contractVersion || Object.keys(value).length !== 2) {
    report(`${label} must declare exactly ${contractIdentity}.`);
  }
}

function validateNameArray(value, label, report, optional) {
  if (value === undefined && optional) return;
  if (!Array.isArray(value) || value.some((entry) => typeof entry !== "string" || !name.test(entry)) || new Set(value).size !== value.length) {
    report(`${label} must be a unique array of contract names.`);
  }
}

function validateIds(value, label, report) {
  if (!isObject(value) || Object.keys(value).length === 0 || Object.entries(value).some(([key, entry]) => !name.test(key) || typeof entry !== "string" || !name.test(entry))) {
    report(`${label} must be a non-empty map of contract names.`);
  }
}

function validateOperations(value, label, required, report) {
  if (!Array.isArray(value) || (required && value.length === 0)) {
    report(`${label} must be ${required ? "a non-empty" : "an"} array.`);
    return;
  }
  for (const [index, operation] of value.entries()) {
    const item = `${label}[${index}]`;
    if (!hasOnlyKeys(operation, ["op", "target", "arguments"], item, report)) continue;
    requireKeys(operation, ["op", "target"], item, report);
    if (typeof operation.op !== "string" || !name.test(operation.op) || typeof operation.target !== "string" || !name.test(operation.target)) {
      report(`${item} must contain contract operation and target names.`);
    }
    if (operation.arguments !== undefined && !isObject(operation.arguments)) report(`${item}.arguments must be an object.`);
  }
}

function validateExpect(value, label, report) {
  if (!hasOnlyKeys(value, ["events", "states", "outcomes", "absentEvents"], label, report)) return;
  requireKeys(value, ["events", "states", "outcomes"], label, report);
  validateEvents(value.events, `${label}.events`, report);
  validateEvents(value.absentEvents, `${label}.absentEvents`, report, true);
  if (!Array.isArray(value.states) || !Array.isArray(value.outcomes)) {
    report(`${label}.states and ${label}.outcomes must be arrays.`);
    return;
  }
  for (const [index, state] of value.states.entries()) {
    const item = `${label}.states[${index}]`;
    if (!hasOnlyKeys(state, ["subject", "state"], item, report)) continue;
    if (typeof state.subject !== "string" || !name.test(state.subject) || typeof state.state !== "string" || !name.test(state.state)) {
      report(`${item} must contain contract subject and state names.`);
    }
  }
  const terminalSubjects = new Set();
  for (const [index, outcome] of value.outcomes.entries()) {
    const item = `${label}.outcomes[${index}]`;
    if (!isObject(outcome) || typeof outcome.subject !== "string" || !name.test(outcome.subject) || typeof outcome.terminal !== "string") {
      report(`${item} must contain a terminal contract outcome.`);
      continue;
    }
    if (terminalSubjects.has(outcome.subject)) report(`${label} has more than one terminal outcome for ${outcome.subject}.`);
    terminalSubjects.add(outcome.subject);
    if (outcome.terminal === "succeeded") {
      if (!hasOnlyKeys(outcome, ["subject", "terminal"], item, report)) continue;
      continue;
    }
    if (!new Set(["failed", "cancelled", "timedOut", "unavailable"]).has(outcome.terminal)) {
      report(`${item} has an invalid terminal outcome.`);
      continue;
    }
    validateError(outcome, item, report, true);
    if (outcome.reason !== undefined && (typeof outcome.reason !== "string" || !name.test(outcome.reason))) {
      report(`${item}.reason must be a contract name.`);
    }
  }
}

function validateEvents(value, label, report, optional = false) {
  if (value === undefined && optional) return;
  if (!Array.isArray(value)) {
    report(`${label} must be an array.`);
    return;
  }
  for (const [index, event] of value.entries()) {
    const item = `${label}[${index}]`;
    if (!hasOnlyKeys(event, ["type", "subject", "data"], item, report)) continue;
    if (typeof event.type !== "string" || !name.test(event.type) || typeof event.subject !== "string" || !name.test(event.subject)) {
      report(`${item} must contain contract event and subject names.`);
    }
    if (event.data !== undefined && !isObject(event.data)) report(`${item}.data must be an object.`);
  }
}

function validateValue(value, label, report) {
  if (!hasOnlyKeys(value, ["encoding", "value", "schemaId"], label, report)) return;
  requireKeys(value, ["encoding", "value"], label, report);
  if (!new Set(["json", "utf8", "base64"]).has(value.encoding)) report(`${label} has an invalid encoding.`);
  if ((value.encoding === "utf8" || value.encoding === "base64") && typeof value.value !== "string") {
    report(`${label}.value must be a string for ${value.encoding}.`);
  }
  if (value.schemaId !== undefined && (typeof value.schemaId !== "string" || value.schemaId.length === 0)) {
    report(`${label}.schemaId must be a non-empty string.`);
  }
}

function validateError(value, label, report, outcome = false) {
  const allowed = outcome
    ? ["subject", "terminal", "category", "code", "message", "reason", "retryable"]
    : ["category", "code", "message", "retryable"];
  if (!hasOnlyKeys(value, allowed, label, report)) return;
  requireKeys(value, ["category", "message", "retryable"], label, report);
  if (!errorCategories.has(value.category) || typeof value.message !== "string" || value.message.length === 0 || typeof value.retryable !== "boolean") {
    report(`${label} must contain a stable category, safe message, and retryability.`);
  }
  if (value.code !== undefined && (typeof value.code !== "string" || value.code.length === 0)) report(`${label}.code must be non-empty when present.`);
  if (outcome && (typeof value.code !== "string" || value.code.length === 0)) report(`${label}.code is required for terminal errors.`);
}

function requireKeys(value, keys, label, report) {
  if (!isObject(value)) {
    report(`${label} must be an object.`);
    return;
  }
  for (const key of keys) if (!(key in value)) report(`${label} is missing ${key}.`);
}

function hasOnlyKeys(value, allowed, label, report) {
  if (!isObject(value)) {
    report(`${label} must be an object.`);
    return false;
  }
  const unexpected = Object.keys(value).filter((key) => !allowed.includes(key));
  if (unexpected.length > 0) report(`${label} has unexpected properties: ${unexpected.join(", ")}.`);
  return unexpected.length === 0;
}

function readJson(path, report) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    report(`Cannot parse ${relative(repositoryRoot, path)}: ${error instanceof Error ? error.message : String(error)}.`);
    return undefined;
  }
}

function readText(path, report) {
  try {
    return readFileSync(path, "utf8");
  } catch (error) {
    report(`Cannot read ${relative(repositoryRoot, path)}: ${error instanceof Error ? error.message : String(error)}.`);
    return "";
  }
}

function findFiles(directory, predicate) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? findFiles(path, predicate) : predicate(path) ? [path] : [];
  });
}

function markdownTargets(text) {
  return [...text.matchAll(/\]\(([^)\s]+)(?:\s+[^)]*)?\)/g)].map((match) => match[1]);
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = verifyContract();
  process.stdout.write(`${result.identity}: ${result.scenarios} scenarios, ${result.vectors} vectors, ${result.parityEntries} parity mappings verified.\n`);
}
