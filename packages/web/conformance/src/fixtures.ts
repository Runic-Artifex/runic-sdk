import type {
  ConformanceFixtureManifest,
  FixtureData,
  FixtureSource,
  ProtocolCorpusManifest,
  ScenarioDocument,
} from "./types.js";

const decoder = new TextDecoder("utf-8", { fatal: true });
const encoder = new TextEncoder();
const MAX_FIXTURE_BYTES = 2_097_152;
const MAX_FIXTURE_PATH_LENGTH = 1_024;
const MAX_FIXTURE_ARRAY_ITEMS = 10_000;
const MAX_FIXTURE_JSON_DEPTH = 64;
const expectedSuites = Object.freeze([
  ["protocol-schema", 33],
  ["protocol-semantic", 12],
  ["state-lifecycle", 8],
  ["command-lifecycle", 6],
  ["reconnect-lifecycle", 5],
  ["hostile-input", 28],
  ["flow-projection", 2],
] as const);

export function createFixtureSource(
  read: (path: string) => Promise<FixtureData> | FixtureData,
): FixtureSource {
  return Object.freeze({
    read: async (path: string) => {
      assertFixturePath(path);
      return read(path);
    },
  });
}

export function createFetchFixtureSource(
  baseUrl: string | URL,
  fetcher: typeof globalThis.fetch = globalThis.fetch,
): FixtureSource {
  const suppliedBase = new URL(String(baseUrl), globalThis.location?.href);
  const base = new URL(suppliedBase.href.endsWith("/") ? suppliedBase.href : `${suppliedBase.href}/`);
  return createFixtureSource(async (path) => {
    // Validate here as well as in the public read helpers. A FixtureSource is a
    // public object, so callers can invoke source.read() directly.
    assertFixturePath(path);
    const resolved = new URL(path, base);
    if (resolved.origin !== base.origin || !resolved.href.startsWith(base.href)) {
      throw new TypeError("Fixture URL must remain within the configured base URL.");
    }
    const response = await fetcher(resolved);
    if (!response.ok) {
      throw new Error(`Fixture request failed with status ${response.status}.`);
    }
    const declaredLength = response.headers.get("content-length");
    if (declaredLength !== null && Number(declaredLength) > MAX_FIXTURE_BYTES) {
      throw new TypeError("Fixture response exceeds the maximum byte length.");
    }
    const bytes = new Uint8Array(await response.arrayBuffer());
    assertFixtureByteLength(bytes.byteLength);
    return bytes;
  });
}

export async function readFixtureBytes(source: FixtureSource, path: string): Promise<Uint8Array> {
  assertFixturePath(path);
  const value = await source.read(path);
  if (typeof value === "string") {
    if (value.length > MAX_FIXTURE_BYTES) throw new TypeError("Fixture exceeds the maximum byte length.");
    const bytes = encoder.encode(value);
    assertFixtureByteLength(bytes.byteLength);
    return bytes;
  }
  assertFixtureByteLength(value.byteLength);
  return new Uint8Array(value);
}

export async function readFixtureText(source: FixtureSource, path: string): Promise<string> {
  assertFixturePath(path);
  const value = await source.read(path);
  if (typeof value === "string") {
    if (value.length > MAX_FIXTURE_BYTES) throw new TypeError("Fixture exceeds the maximum byte length.");
    assertFixtureByteLength(encoder.encode(value).byteLength);
    return value;
  }
  assertFixtureByteLength(value.byteLength);
  return decoder.decode(value);
}

export async function loadFixtureJson(source: FixtureSource, path: string): Promise<unknown> {
  return JSON.parse(await readFixtureText(source, path)) as unknown;
}

export async function loadFixtureManifest(
  source: FixtureSource,
  path = "manifest.json",
): Promise<ConformanceFixtureManifest> {
  const value = await loadFixtureJson(source, path);
  assertRecord(value, "fixture manifest");
  if (value.formatVersion !== 1) {
    throw new TypeError("Fixture manifest formatVersion must be 1.");
  }
  assertProtocolIdentity(value.protocolIdentity);
  if (!Array.isArray(value.suites)) throw new TypeError("Fixture manifest suites must be an array.");
  const ids = new Set<string>();
  for (const suite of value.suites) {
    assertRecord(suite, "fixture suite");
    assertNonemptyString(suite.id, "fixture suite id");
    assertNonemptyString(suite.file, "fixture suite file");
    assertNonemptyString(suite.caseProperty, "fixture suite caseProperty");
    if (!Number.isInteger(suite.caseCount) || (suite.caseCount as number) < 0) {
      throw new TypeError("Fixture suite caseCount must be a non-negative integer.");
    }
    if (ids.has(suite.id as string)) throw new TypeError(`Duplicate fixture suite id: ${String(suite.id)}.`);
    ids.add(suite.id as string);
  }
  if (value.suites.length !== expectedSuites.length) throw new TypeError("Fixture manifest must contain the canonical seven suites.");
  let totalCases = 0;
  for (let index = 0; index < expectedSuites.length; index += 1) {
    const expected = expectedSuites[index];
    const actual = value.suites[index] as Record<string, unknown> | undefined;
    if (expected === undefined || actual?.id !== expected[0] || actual.caseCount !== expected[1]) {
      throw new TypeError("Fixture manifest suites must use canonical ids, order, and case counts.");
    }
    totalCases += expected[1];
  }
  if (totalCases !== 94) throw new TypeError("Fixture manifest must declare exactly 94 cases.");
  return value as unknown as ConformanceFixtureManifest;
}

export function validateProtocolManifest(value: unknown): ProtocolCorpusManifest {
  assertRecord(value, "protocol manifest");
  assertProtocolIdentity(value.protocolIdentity);
  if (!Array.isArray(value.cases) || !Array.isArray(value.semanticCases)) {
    throw new TypeError("Protocol manifest cases and semanticCases must be arrays.");
  }
  if (value.cases.length > MAX_FIXTURE_ARRAY_ITEMS || value.semanticCases.length > MAX_FIXTURE_ARRAY_ITEMS) {
    throw new TypeError("Protocol manifest contains too many cases.");
  }
  const ids = new Set<string>();
  for (const item of [...value.cases, ...value.semanticCases]) {
    assertRecord(item, "protocol case");
    assertNonemptyString(item.id, "protocol case id");
    assertNonemptyString(item.file, "protocol case file");
    assertNonemptyString(item.reason, "protocol case reason");
    if (ids.has(item.id as string)) throw new TypeError(`Duplicate protocol case id: ${String(item.id)}.`);
    ids.add(item.id as string);
  }
  for (const item of value.cases) {
    const record = item as Record<string, unknown>;
    if (record.schema !== "client" && record.schema !== "host") {
      throw new TypeError("Protocol case schema must be client or host.");
    }
    if (record.documentMode !== "single" && record.documentMode !== "eachItem") {
      throw new TypeError("Protocol case documentMode must be single or eachItem.");
    }
    if (typeof record.valid !== "boolean") throw new TypeError("Protocol case valid must be boolean.");
  }
  return value as unknown as ProtocolCorpusManifest;
}

export function validateScenarioDocument(value: unknown): ScenarioDocument {
  assertRecord(value, "scenario document");
  if (value.format !== "runic.toolkit.mvvm.conformance-scenarios/1") {
    throw new TypeError("Unsupported scenario document format.");
  }
  assertProtocolIdentity(value.protocolIdentity);
  assertNonemptyString(value.category, "scenario category");
  if (!Array.isArray(value.scenarios)) throw new TypeError("Scenario document scenarios must be an array.");
  if (value.scenarios.length > MAX_FIXTURE_ARRAY_ITEMS) throw new TypeError("Scenario document contains too many scenarios.");
  const ids = new Set<string>();
  for (const scenario of value.scenarios) {
    assertRecord(scenario, "scenario");
    assertNonemptyString(scenario.id, "scenario id");
    assertRecord(scenario.initial, "scenario initial state");
    if (!Array.isArray(scenario.steps) || scenario.steps.length === 0) {
      throw new TypeError("Scenario steps must be a non-empty array.");
    }
    if (scenario.steps.length > MAX_FIXTURE_ARRAY_ITEMS) throw new TypeError("Scenario contains too many steps.");
    if (ids.has(scenario.id as string)) throw new TypeError(`Duplicate scenario id: ${String(scenario.id)}.`);
    ids.add(scenario.id as string);
    for (const step of scenario.steps) {
      assertRecord(step, "scenario step");
      assertNonemptyString(step.action, "scenario action");
      assertRecord(step.expect, "scenario expectation");
    }
  }
  return value as unknown as ScenarioDocument;
}

export function joinFixturePath(parentFile: string, childFile: string): string {
  assertFixturePath(parentFile);
  assertFixturePath(childFile);
  const slash = parentFile.lastIndexOf("/");
  return slash < 0 ? childFile : `${parentFile.slice(0, slash + 1)}${childFile}`;
}

export function assertFixturePath(path: string): void {
  if (
    path.length === 0 ||
    path.length > MAX_FIXTURE_PATH_LENGTH ||
    !/^[A-Za-z0-9._/-]+$/u.test(path) ||
    path.startsWith("/")
  ) {
    throw new TypeError("Fixture path must be a normalized relative path.");
  }
  const segments = path.split("/");
  if (segments.some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    throw new TypeError("Fixture path must not contain empty or traversal segments.");
  }
}

export function splitTopLevelArray(source: string): readonly string[] {
  if (source.length > MAX_FIXTURE_BYTES || encoder.encode(source).byteLength > MAX_FIXTURE_BYTES) {
    throw new TypeError("Fixture array exceeds the maximum byte length.");
  }
  let index = skipWhitespace(source, 0);
  if (source[index] !== "[") throw new TypeError("eachItem fixture must contain a JSON array.");
  index = skipWhitespace(source, index + 1);
  const items: string[] = [];
  if (source[index] === "]") {
    index = skipWhitespace(source, index + 1);
    if (index !== source.length) throw new TypeError("Unexpected data after fixture array.");
    return items;
  }
  while (index < source.length) {
    const start = index;
    index = scanJsonValue(source, index);
    items.push(source.slice(start, index));
    if (items.length > MAX_FIXTURE_ARRAY_ITEMS) throw new TypeError("Fixture array contains too many items.");
    index = skipWhitespace(source, index);
    if (source[index] === "]") {
      index = skipWhitespace(source, index + 1);
      if (index !== source.length) throw new TypeError("Unexpected data after fixture array.");
      return items;
    }
    if (source[index] !== ",") throw new TypeError("Invalid fixture array separator.");
    index = skipWhitespace(source, index + 1);
  }
  throw new TypeError("Unterminated fixture array.");
}

function scanJsonValue(source: string, start: number): number {
  const first = source[start];
  if (first === undefined) throw new TypeError("Missing fixture array value.");
  if (first !== "{" && first !== "[") {
    let index = start;
    if (first === '"') {
      index += 1;
      let escaped = false;
      while (index < source.length) {
        const char = source[index++];
        if (escaped) escaped = false;
        else if (char === "\\") escaped = true;
        else if (char === '"') return index;
      }
      throw new TypeError("Unterminated string in fixture array.");
    }
    while (index < source.length && source[index] !== "," && source[index] !== "]") index += 1;
    return index;
  }
  const stack = [first];
  let string = false;
  let escaped = false;
  for (let index = start + 1; index < source.length; index += 1) {
    const char = source[index];
    if (string) {
      if (escaped) escaped = false;
      else if (char === "\\") escaped = true;
      else if (char === '"') string = false;
      continue;
    }
    if (char === '"') string = true;
    else if (char === "{" || char === "[") {
      if (stack.length >= MAX_FIXTURE_JSON_DEPTH) throw new TypeError("Fixture JSON exceeds the maximum depth.");
      stack.push(char);
    }
    else if (char === "}" || char === "]") {
      const opening = stack.pop();
      if ((opening === "{" && char !== "}") || (opening === "[" && char !== "]")) {
        throw new TypeError("Mismatched JSON delimiters in fixture array.");
      }
      if (stack.length === 0) return index + 1;
    }
  }
  throw new TypeError("Unterminated value in fixture array.");
}

function skipWhitespace(source: string, start: number): number {
  let index = start;
  while (index < source.length && /[\t\n\r ]/u.test(source[index] ?? "")) index += 1;
  return index;
}

function assertRecord(value: unknown, label: string): asserts value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError(`${label} must be an object.`);
  }
}

function assertNonemptyString(value: unknown, label: string): asserts value is string {
  if (typeof value !== "string" || value.length === 0) throw new TypeError(`${label} must be a non-empty string.`);
}


function assertProtocolIdentity(value: unknown): asserts value is "runic.toolkit.mvvm/1" {
  if (value !== "runic.toolkit.mvvm/1") throw new TypeError("Fixture protocol identity is not runic.toolkit.mvvm/1.");
}

function assertFixtureByteLength(length: number): void {
  if (length > MAX_FIXTURE_BYTES) throw new TypeError("Fixture exceeds the maximum byte length.");
}
