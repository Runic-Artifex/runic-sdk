import * as RunicToolkit from "@runic-artifex/mvvm";

import {
  joinFixturePath,
  loadFixtureJson,
  loadFixtureManifest,
  readFixtureText,
  splitTopLevelArray,
  validateProtocolManifest,
  validateScenarioDocument,
} from "./fixtures.js";
import {
  CONFORMANCE_FORMAT,
  type ConformanceCaseResult,
  type ConformanceDiagnostic,
  type ConformanceReport,
  type ConformanceRuntimeAdapter,
  type ConformanceScenario,
  type FixtureSource,
  type ProtocolCorpusManifest,
  type RunConformanceOptions,
  type RuntimeCaseOutcome,
} from "./types.js";
import { createSdkConformanceRuntime } from "./sdk-runtime.js";

interface MvvmSdkSurface {
  parseClientMessage(source: string | Uint8Array): unknown;
  parseHostMessage(source: string | Uint8Array): unknown;
  validateJsonFrame(source: string | Uint8Array): void;
}

interface HostileDocument {
  readonly format: "runic.toolkit.mvvm.hostile-input/1";
  readonly protocolIdentity: "runic.toolkit.mvvm/1";
  readonly cases: readonly HostileCase[];
}

interface HostileCase {
  readonly id: string;
  readonly input: Readonly<Record<string, unknown>>;
  readonly expect: Readonly<Record<string, unknown>>;
}

const sdk = RunicToolkit as unknown as MvvmSdkSurface;
const PROTOCOL_IDENTITY = "runic.toolkit.mvvm/1" as const;
const MAX_GENERATED_FRAME_BYTES = 1_048_577;
const MAX_GENERATED_JSON_DEPTH = 33;
const MAX_GENERATED_PROPERTIES = 4_097;
const MAX_GENERATED_ARRAY_ITEMS = 10_001;
const MAX_GENERATED_SNAPSHOT_MEMBERS = 4_097;
const MAX_GENERATED_PATCH_CHANGES = 1_025;
const unsupportedHostileGenerators = new Set([
  "nestedArrays",
  "spacePaddedDocument",
  "jsonStringValue",
  "singlePropertyObject",
  "numberedPropertyObject",
  "repeatedArray",
]);

export async function runConformance(options: RunConformanceOptions): Promise<ConformanceReport> {
  const runtime = options.runtime ?? createSdkConformanceRuntime();
  const manifestPath = options.manifestPath ?? "manifest.json";
  const manifest = await loadFixtureManifest(options.source, manifestPath);
  const results: ConformanceCaseResult[] = [];
  let protocolManifest: ProtocolCorpusManifest | undefined;

  for (const suite of manifest.suites) {
    const suitePath = (suite.id === "protocol-schema" || suite.id === "protocol-semantic") &&
      options.protocolManifestPath !== undefined
      ? options.protocolManifestPath
      : joinFixturePath(manifestPath, suite.file);
    const before = results.length;
    if (suite.id === "protocol-schema") {
      protocolManifest = protocolManifest ?? validateProtocolManifest(await loadFixtureJson(options.source, suitePath));
      results.push(...await runProtocolCorpus(options.source, suitePath, protocolManifest));
    } else if (suite.id === "protocol-semantic") {
      protocolManifest = protocolManifest ?? validateProtocolManifest(await loadFixtureJson(options.source, suitePath));
      results.push(...await runSemanticCorpus(options.source, suitePath, protocolManifest, runtime));
    } else if (suite.id === "hostile-input") {
      results.push(...await runHostileInputCorpus(options.source, suitePath, runtime));
    } else {
      results.push(...await runScenarioCorpus(options.source, suitePath, runtime, suite.id));
    }
    const actualCount = results.length - before;
    if (actualCount !== suite.caseCount) {
      throw new TypeError(`Suite ${suite.id} declared ${suite.caseCount} cases but supplied ${actualCount}.`);
    }
  }

  return createReport(runtime.name, results);
}

export async function runProtocolCorpus(
  source: FixtureSource,
  manifestPath = "protocol/v1/manifest.json",
  suppliedManifest?: ProtocolCorpusManifest,
): Promise<readonly ConformanceCaseResult[]> {
  const manifest = suppliedManifest ?? validateProtocolManifest(await loadFixtureJson(source, manifestPath));
  const results: ConformanceCaseResult[] = [];
  for (const item of manifest.cases) {
    const fixturePath = joinFixturePath(manifestPath, item.file);
    try {
      const text = await readFixtureText(source, fixturePath);
      const documents = item.documentMode === "eachItem" ? splitTopLevelArray(text) : [text];
      let passed = documents.length > 0;
      for (const document of documents) {
        let accepted = true;
        try {
          parseProtocolDocument(item.schema, document);
        } catch {
          accepted = false;
        }
        if (accepted !== item.valid) passed = false;
      }
      results.push(result(
        item.id,
        "protocol-schema",
        passed ? "passed" : "failed",
        passed ? [] : [{
          code: item.valid ? "unexpected-rejection" : "unexpected-acceptance",
          message: item.valid
            ? "A valid protocol fixture was rejected."
            : "An invalid protocol fixture was accepted.",
        }],
      ));
    } catch {
      results.push(result(item.id, "protocol-schema", "failed", [{
        code: "fixture-unreadable",
        message: "The protocol fixture could not be read or decoded.",
      }]));
    }
  }
  return results;
}

export async function runSemanticCorpus(
  source: FixtureSource,
  manifestPath = "protocol/v1/manifest.json",
  suppliedManifest?: ProtocolCorpusManifest,
  runtime?: ConformanceRuntimeAdapter,
): Promise<readonly ConformanceCaseResult[]> {
  const manifest = suppliedManifest ?? validateProtocolManifest(await loadFixtureJson(source, manifestPath));
  const results: ConformanceCaseResult[] = [];
  for (const item of manifest.semanticCases) {
    if (runtime?.runSemanticCase === undefined) {
      results.push(skipped(item.id, "protocol-semantic", "semantic-adapter-unavailable"));
      continue;
    }
    try {
      const document = await loadFixtureJson(source, joinFixturePath(manifestPath, item.file));
      const outcome = await runtime.runSemanticCase({ id: item.id, reason: item.reason, document });
      results.push(outcomeResult(item.id, "protocol-semantic", outcome));
    } catch {
      results.push(result(item.id, "protocol-semantic", "failed", [{
        code: "semantic-execution-failed",
        message: "The semantic case did not complete.",
      }]));
    }
  }
  return results;
}

export async function runScenarioCorpus(
  source: FixtureSource,
  documentPath: string,
  runtime?: ConformanceRuntimeAdapter,
  suiteOverride?: string,
): Promise<readonly ConformanceCaseResult[]> {
  const document = validateScenarioDocument(await loadFixtureJson(source, documentPath));
  const suite = suiteOverride ?? document.category;
  if (runtime?.createScenarioDriver === undefined) {
    return document.scenarios.map((scenario) => skipped(scenario.id, suite, "scenario-adapter-unavailable"));
  }
  const results: ConformanceCaseResult[] = [];
  for (const scenario of document.scenarios) {
    results.push(await runScenario(runtime, suite, scenario));
  }
  return results;
}

export async function runHostileInputCorpus(
  source: FixtureSource,
  documentPath = "vectors/hostile-input.json",
  _runtime?: ConformanceRuntimeAdapter,
): Promise<readonly ConformanceCaseResult[]> {
  const document = validateHostileDocument(await loadFixtureJson(source, documentPath));
  const results: ConformanceCaseResult[] = [];
  for (const item of document.cases) {
    let bytes: Uint8Array;
    try {
      bytes = generateHostileInput(item.input);
    } catch {
      results.push(result(item.id, "hostile-input", "failed", [{
        code: "hostile-input-generation-failed",
        message: "The hostile input recipe exceeded conformance generator bounds or was invalid.",
      }]));
      continue;
    }

    try {
      const schema = hostileParserSchema(item);
      const parsed = schema === "client"
        ? sdk.parseClientMessage(bytes)
        : schema === "host"
          ? sdk.parseHostMessage(bytes)
          : sdk.validateJsonFrame(bytes);
      const actual = acceptanceObservation(parsed, item.id);
      const diagnostics = compareExpected(item.expect, actual);
      results.push(result(item.id, "hostile-input", diagnostics.length === 0 ? "passed" : "failed", diagnostics));
    } catch (error) {
      const actual = rejectionObservation(error);
      const diagnostics = compareExpected(item.expect, actual);
      results.push(result(item.id, "hostile-input", diagnostics.length === 0 ? "passed" : "failed", diagnostics));
    }
  }
  return results;
}

export function createReport(runtime: string, cases: readonly ConformanceCaseResult[]): ConformanceReport {
  const frozenCases = Object.freeze([...cases]);
  const passed = frozenCases.filter((item) => item.status === "passed").length;
  const failed = frozenCases.filter((item) => item.status === "failed").length;
  const skippedCount = frozenCases.length - passed - failed;
  return Object.freeze({
    format: CONFORMANCE_FORMAT,
    protocolIdentity: PROTOCOL_IDENTITY,
    runtime,
    success: failed === 0 && skippedCount === 0,
    totals: Object.freeze({ total: frozenCases.length, passed, failed, skipped: skippedCount }),
    cases: frozenCases,
  });
}

async function runScenario(
  runtime: ConformanceRuntimeAdapter,
  suite: string,
  scenario: ConformanceScenario,
): Promise<ConformanceCaseResult> {
  const diagnostics: ConformanceDiagnostic[] = [];
  let driver;
  try {
    driver = await runtime.createScenarioDriver!({ suite, scenario });
    for (let index = 0; index < scenario.steps.length; index += 1) {
      const step = scenario.steps[index];
      if (step === undefined) continue;
      const actual = await driver.perform(step, index);
      diagnostics.push(...compareExpected(step.expect, actual, index));
    }
  } catch {
    diagnostics.push({ code: "scenario-execution-failed", message: "The scenario did not complete." });
  } finally {
    if (driver?.close !== undefined) {
      try {
        await driver.close();
      } catch {
        diagnostics.push({ code: "scenario-close-failed", message: "The scenario driver did not close cleanly." });
      }
    }
  }
  return result(scenario.id, suite, diagnostics.length === 0 ? "passed" : "failed", diagnostics);
}

function parseProtocolDocument(schema: "client" | "host", document: string): unknown {
  if (schema === "client") return sdk.parseClientMessage(document);
  return sdk.parseHostMessage(document);
}

function validateHostileDocument(value: unknown): HostileDocument {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError("Hostile input document must be an object.");
  }
  const document = value as Record<string, unknown>;
  if (document.format !== "runic.toolkit.mvvm.hostile-input/1" || document.protocolIdentity !== PROTOCOL_IDENTITY) {
    throw new TypeError("Unsupported hostile input document.");
  }
  if (!Array.isArray(document.cases)) throw new TypeError("Hostile input cases must be an array.");
  const ids = new Set<string>();
  for (const item of document.cases) {
    if (typeof item !== "object" || item === null || Array.isArray(item)) throw new TypeError("Hostile case must be an object.");
    const record = item as Record<string, unknown>;
    if (typeof record.id !== "string" || record.id.length === 0) throw new TypeError("Hostile case id is invalid.");
    if (ids.has(record.id)) throw new TypeError(`Duplicate hostile case id: ${record.id}.`);
    ids.add(record.id);
    if (!isRecord(record.input) || !isRecord(record.expect)) throw new TypeError("Hostile case input and expect are required.");
  }
  return value as HostileDocument;
}

function generateHostileInput(input: Readonly<Record<string, unknown>>): Uint8Array {
  if (input.kind === "hex") {
    if (typeof input.value !== "string" || input.value.length % 2 !== 0 || !/^[0-9a-f]*$/u.test(input.value)) {
      throw new TypeError("Invalid hostile hexadecimal input.");
    }
    if (input.value.length > MAX_GENERATED_FRAME_BYTES * 2) throw new TypeError("Hostile hexadecimal input exceeds generator bounds.");
    const bytes = new Uint8Array(input.value.length / 2);
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Number.parseInt(input.value.slice(index * 2, index * 2 + 2), 16);
    }
    return bytes;
  }
  if (input.kind === "utf8") {
    if (typeof input.value !== "string") throw new TypeError("Invalid hostile UTF-8 input.");
    if (input.value.length > MAX_GENERATED_FRAME_BYTES) throw new TypeError("Hostile UTF-8 input exceeds generator bounds.");
    const bytes = new TextEncoder().encode(input.value);
    if (bytes.byteLength > MAX_GENERATED_FRAME_BYTES) throw new TypeError("Hostile UTF-8 input exceeds generator bounds.");
    return bytes;
  }
  if (input.kind !== "generated" || typeof input.generator !== "string" || !isRecord(input.parameters)) {
    throw new TypeError("Invalid hostile input recipe.");
  }
  const generated = runGenerator(input.generator, input.parameters);
  if (generated.length > MAX_GENERATED_FRAME_BYTES) throw new TypeError("Generated hostile input exceeds generator bounds.");
  const bytes = new TextEncoder().encode(generated);
  if (bytes.byteLength > MAX_GENERATED_FRAME_BYTES) throw new TypeError("Generated hostile input exceeds generator bounds.");
  return bytes;
}

function runGenerator(name: string, parameters: Readonly<Record<string, unknown>>): string {
  switch (name) {
    case "nestedArrays": {
      const depth = boundedIntegerParameter(parameters, "depth", MAX_GENERATED_JSON_DEPTH);
      return `${"[".repeat(depth)}${JSON.stringify(parameters.leaf)}${"]".repeat(depth)}`;
    }
    case "spacePaddedDocument": {
      const document = stringParameter(parameters, "document");
      const total = boundedIntegerParameter(parameters, "totalUtf8Bytes", MAX_GENERATED_FRAME_BYTES);
      const documentBytes = utf8ByteLength(document);
      if (documentBytes > total) throw new TypeError("Generated document exceeds requested size.");
      return document + " ".repeat(total - documentBytes);
    }
    case "jsonStringValue": {
      const value = boundedRepeatedString(parameters);
      return JSON.stringify(value);
    }
    case "singlePropertyObject": {
      const property = boundedRepeatedString(parameters);
      return JSON.stringify({ [property]: parameters.value });
    }
    case "numberedPropertyObject": {
      const count = boundedIntegerParameter(parameters, "count", MAX_GENERATED_PROPERTIES);
      const prefix = stringParameter(parameters, "prefix");
      if (prefix.length > 128) throw new TypeError("Generator parameter prefix exceeds generator bounds.");
      const valueJson = serializedGeneratorValue(parameters.value);
      const maximumKeyJson = JSON.stringify(`${prefix}${Math.max(0, count - 1)}`);
      assertGeneratedSize(2 + count * (maximumKeyJson.length + 1 + valueJson.length) + Math.max(0, count - 1));
      const entries: [string, unknown][] = [];
      for (let index = 0; index < count; index += 1) entries.push([`${prefix}${index}`, parameters.value]);
      return JSON.stringify(Object.fromEntries(entries));
    }
    case "repeatedArray": {
      const count = boundedIntegerParameter(parameters, "count", MAX_GENERATED_ARRAY_ITEMS);
      const valueJson = serializedGeneratorValue(parameters.value);
      assertGeneratedSize(2 + count * valueJson.length + Math.max(0, count - 1));
      return JSON.stringify(Array.from({ length: count }, () => parameters.value));
    }
    case "hostSnapshot": {
      const members = Array.from({ length: boundedIntegerParameter(parameters, "memberCount", MAX_GENERATED_SNAPSHOT_MEMBERS) }, (_, index) => ({ type: "property", member: index + 1, value: null }));
      return hostFrame("snapshot", `"revision":${decimalParameter(parameters, "revision")},"members":${JSON.stringify(members)}`);
    }
    case "hostPatch": {
      const changes = Array.from({ length: boundedIntegerParameter(parameters, "changeCount", MAX_GENERATED_PATCH_CHANGES) }, () => ({ type: "property", member: 1, value: null }));
      return hostFrame("patch", `"fromRevision":${decimalParameter(parameters, "fromRevision")},"toRevision":${decimalParameter(parameters, "toRevision")},"changes":${JSON.stringify(changes)}`);
    }
    case "hostCollectionInsertPatch": {
      const itemCount = boundedIntegerParameter(parameters, "itemCount", MAX_GENERATED_ARRAY_ITEMS);
      const firstCount = Math.min(itemCount, MAX_GENERATED_ARRAY_ITEMS - 1);
      const changes = JSON.stringify([
        { type: "collection", member: 1, operation: "insert", index: 0, items: Array.from({ length: firstCount }, () => null) },
        ...(itemCount > firstCount
          ? [{ type: "collection", member: 1, operation: "insert", index: 0, items: Array.from({ length: itemCount - firstCount }, () => null) }]
          : []),
      ]);
      return hostFrame("patch", `"fromRevision":${decimalParameter(parameters, "fromRevision")},"toRevision":${decimalParameter(parameters, "toRevision")},"changes":${changes}`);
    }
    default:
      throw new TypeError(`Unknown hostile input generator: ${name}.`);
  }
}

function hostFrame(kind: string, payloadMembers: string): string {
  const request = kind === "snapshot" ? `,"request":"00000000-0000-4000-8000-000000000005"` : "";
  return `{"v":1,"kind":"${kind}","session":"00000000-0000-4000-8000-000000000004","view":"00000000-0000-4000-8000-000000000002"${request},"payload":{${payloadMembers}}}`;
}

function integerParameter(parameters: Readonly<Record<string, unknown>>, name: string): number {
  const value = parameters[name];
  if (!Number.isSafeInteger(value) || (value as number) < 0) throw new TypeError(`Generator parameter ${name} must be a non-negative safe integer.`);
  return value as number;
}

function boundedIntegerParameter(parameters: Readonly<Record<string, unknown>>, name: string, maximum: number): number {
  const value = integerParameter(parameters, name);
  if (value > maximum) throw new TypeError(`Generator parameter ${name} exceeds generator bounds.`);
  return value;
}

function boundedRepeatedString(parameters: Readonly<Record<string, unknown>>): string {
  const value = stringParameter(parameters, "unicodeScalar");
  const repeat = boundedIntegerParameter(parameters, "repeat", MAX_GENERATED_FRAME_BYTES);
  if (Array.from(value).length !== 1) throw new TypeError("Generator parameter unicodeScalar must contain one Unicode scalar.");
  const jsonUnitLength = JSON.stringify(value).length - 2;
  const unitBytes = Math.max(utf8ByteLength(value), jsonUnitLength);
  if (unitBytes * repeat + 2 > MAX_GENERATED_FRAME_BYTES) {
    throw new TypeError("Repeated string exceeds generator bounds.");
  }
  return value.repeat(repeat);
}

function utf8ByteLength(value: string): number {
  if (value.length > MAX_GENERATED_FRAME_BYTES) throw new TypeError("String exceeds generator bounds.");
  return new TextEncoder().encode(value).byteLength;
}

function serializedGeneratorValue(value: unknown): string {
  const serialized = JSON.stringify(value);
  if (serialized === undefined) throw new TypeError("Generator value must be JSON serializable.");
  assertGeneratedSize(serialized.length);
  return serialized;
}

function assertGeneratedSize(size: number): void {
  if (!Number.isSafeInteger(size) || size > MAX_GENERATED_FRAME_BYTES) {
    throw new TypeError("Generated hostile input exceeds generator bounds.");
  }
}

function stringParameter(parameters: Readonly<Record<string, unknown>>, name: string): string {
  const value = parameters[name];
  if (typeof value !== "string") throw new TypeError(`Generator parameter ${name} must be a string.`);
  return value;
}

function decimalParameter(parameters: Readonly<Record<string, unknown>>, name: string): string {
  const value = stringParameter(parameters, name);
  if (!/^(?:0|[1-9][0-9]*)$/u.test(value)) throw new TypeError(`Generator parameter ${name} must be canonical decimal text.`);
  return value;
}

function compareExpected(expected: unknown, actual: unknown, step?: number): ConformanceDiagnostic[] {
  const mismatch = firstMismatch(expected, normalize(actual), "$expected");
  if (mismatch === undefined) return [];
  const diagnostic: ConformanceDiagnostic = {
    code: "expectation-mismatch",
    message: `Observed value did not match ${mismatch.path}.`,
    expected: mismatch.expected,
    actual: mismatch.actual,
    ...(step === undefined ? {} : { step }),
  };
  return [diagnostic];
}

function firstMismatch(expected: unknown, actual: unknown, path: string): { path: string; expected: unknown; actual: unknown } | undefined {
  const normalizedExpected = normalize(expected);
  if (Array.isArray(normalizedExpected)) {
    if (!Array.isArray(actual) || actual.length !== normalizedExpected.length) return { path, expected: normalizedExpected, actual };
    for (let index = 0; index < normalizedExpected.length; index += 1) {
      const mismatch = firstMismatch(normalizedExpected[index], actual[index], `${path}[${index}]`);
      if (mismatch !== undefined) return mismatch;
    }
    return undefined;
  }
  if (isRecord(normalizedExpected)) {
    if (!isRecord(actual)) return { path, expected: normalizedExpected, actual };
    for (const key of Object.keys(normalizedExpected)) {
      if (!Object.hasOwn(actual, key)) return { path: `${path}.${key}`, expected: normalizedExpected[key], actual: undefined };
      const mismatch = firstMismatch(normalizedExpected[key], actual[key], `${path}.${key}`);
      if (mismatch !== undefined) return mismatch;
    }
    return undefined;
  }
  return Object.is(normalizedExpected, actual) ? undefined : { path, expected: normalizedExpected, actual };
}

function normalize(value: unknown): unknown {
  if (typeof value === "bigint") return value.toString(10);
  if (Array.isArray(value)) return value.map(normalize);
  if (isRecord(value)) return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, normalize(item)]));
  return value;
}

function rejectionObservation(error: unknown): Readonly<Record<string, unknown>> {
  const code = isRecord(error) && typeof error.code === "string" ? error.code : undefined;
  return { accepted: false, dispatchCount: 0, reason: code };
}

function hostileParserSchema(item: HostileCase): "client" | "host" | "generic" {
  const generator = item.input.generator;
  if (typeof generator === "string") {
    if (generator === "hostSnapshot" || generator === "hostPatch" || generator === "hostCollectionInsertPatch") {
      return "host";
    }
    if (unsupportedHostileGenerators.has(generator)) return "generic";
  }
  return "client";
}

function acceptanceObservation(parsed: unknown, id: string): Readonly<Record<string, unknown>> {
  const observation: Record<string, unknown> = { accepted: true, reason: null };
  if (isRecord(parsed) && typeof parsed.baseRevision === "bigint") {
    observation.baseRevision = parsed.baseRevision.toString(10);
  }
  if (id === "prototype-pollution-payload-key") {
    observation.prototypePolluted = Object.hasOwn(Object.prototype, "polluted");
  }
  return observation;
}

function outcomeResult(id: string, suite: string, outcome: RuntimeCaseOutcome): ConformanceCaseResult {
  return result(id, suite, outcome.passed ? "passed" : "failed", outcome.diagnostics ?? []);
}

function skipped(id: string, suite: string, code: string): ConformanceCaseResult {
  return result(id, suite, "skipped", [{ code, message: "The runtime adapter does not implement this conformance facet." }]);
}

function result(
  id: string,
  suite: string,
  status: "passed" | "failed" | "skipped",
  diagnostics: readonly ConformanceDiagnostic[],
): ConformanceCaseResult {
  return Object.freeze({ id, suite, status, diagnostics: Object.freeze([...diagnostics]) });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
