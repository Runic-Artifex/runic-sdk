import {
  CAPABILITIES,
  FAULT_CODES,
  PROTOCOL_LIMITS,
  type CapabilityName,
  type ClientMessage,
  type FaultCode,
  type FaultPayload,
  type HostMessage,
  type JsonValue,
  type PatchChange,
  type ProtocolLimits,
  type ProtocolParseLimits,
  type Revision,
  type SnapshotMember,
  type SnapshotState,
} from "./protocol.js";

type MutableRecord = Record<string, unknown>;

class RawNumber {
  public constructor(public readonly text: string) {}
}

export class ProtocolValidationError extends Error {
  public override readonly name = "ProtocolValidationError";

  public constructor(
    public readonly code: string,
    public readonly path = "",
  ) {
    super(`MVVM protocol validation failed (${code})${path === "" ? "" : ` at ${path}`}.`);
  }
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const CAPABILITY_TOKEN = /^[A-Za-z0-9_-]{43}$/;
const CONTROL = /[\u0000-\u001f\u007f]/;
const REVISION = /^(?:0|[1-9][0-9]*)$/;
const MAX_REVISION = 9_223_372_036_854_775_807n;
const capabilityNames = new Set<string>(CAPABILITIES);
const faultCodes = new Set<string>(FAULT_CODES);

const defaultParseLimits: ProtocolParseLimits = {
  maxFrameBytes: PROTOCOL_LIMITS.maxFrameBytes,
  maxJsonDepth: PROTOCOL_LIMITS.maxJsonDepth,
  maxStringBytes: PROTOCOL_LIMITS.maxStringBytes,
  maxPropertyNameBytes: PROTOCOL_LIMITS.maxPropertyNameBytes,
  maxPropertiesPerObject: PROTOCOL_LIMITS.maxPropertiesPerObject,
  maxArrayItems: PROTOCOL_LIMITS.maxArrayItems,
};

function fail(code: string, path = ""): never {
  throw new ProtocolValidationError(code, path);
}

function pointer(parent: string, property: string | number): string {
  return `${parent}/${String(property).replaceAll("~", "~0").replaceAll("/", "~1")}`;
}

function utf8Bytes(value: string): number {
  let count = 0;
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code <= 0x7f) count += 1;
    else if (code <= 0x7ff) count += 2;
    else if (code >= 0xd800 && code <= 0xdbff && index + 1 < value.length) {
      const next = value.charCodeAt(index + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        count += 4;
        index += 1;
      } else count += 3;
    } else count += 3;
  }
  return count;
}

function resolveParseLimits(options?: Partial<ProtocolParseLimits>): ProtocolParseLimits {
  const limits = { ...defaultParseLimits, ...options };
  for (const [name, hard] of Object.entries(defaultParseLimits)) {
    const value = limits[name as keyof ProtocolParseLimits];
    if (!Number.isSafeInteger(value) || value < 1 || value > hard) fail("invalid-parser-limit", `/${name}`);
  }
  return limits;
}

function decodeFrame(input: string | Uint8Array, maxFrameBytes: number): string {
  if (typeof input === "string") {
    if (utf8Bytes(input) > maxFrameBytes) fail("max-frame-bytes");
    if (input.charCodeAt(0) === 0xfeff) fail("byte-order-mark");
    return input;
  }
  if (!(input instanceof Uint8Array)) fail("invalid-frame");
  if (input.byteLength > maxFrameBytes) fail("max-frame-bytes");
  if (input.length >= 3 && input[0] === 0xef && input[1] === 0xbb && input[2] === 0xbf) {
    fail("byte-order-mark");
  }
  try {
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(input);
  } catch {
    return fail("invalid-utf8");
  }
}

class JsonReader {
  private index = 0;

  public constructor(
    private readonly text: string,
    private readonly limits: ProtocolParseLimits,
  ) {}

  public read(): unknown {
    this.white();
    const value = this.value(0);
    this.white();
    if (this.index !== this.text.length) fail("trailing-data");
    return value;
  }

  private value(depth: number): unknown {
    const current = this.text[this.index];
    if (current === "{") return this.object(depth + 1);
    if (current === "[") return this.array(depth + 1);
    if (current === '"') return this.string(false);
    if (current === "t" && this.consume("true")) return true;
    if (current === "f" && this.consume("false")) return false;
    if (current === "n" && this.consume("null")) return null;
    if (current === "N" || current === "I" || this.text.startsWith("-Infinity", this.index)) {
      fail("non-finite-number");
    }
    if (current === "-" || (current !== undefined && current >= "0" && current <= "9")) {
      return this.number();
    }
    if (current === "/") fail("comment");
    return fail("invalid-json");
  }

  private object(depth: number): MutableRecord {
    this.checkDepth(depth);
    this.index += 1;
    const result = Object.create(null) as MutableRecord;
    const keys = new Set<string>();
    this.white();
    if (this.text[this.index] === "}") {
      this.index += 1;
      return result;
    }
    for (;;) {
      if (this.text[this.index] !== '"') fail("invalid-json");
      const key = this.string(true);
      if (keys.has(key)) fail("duplicate-key");
      keys.add(key);
      if (keys.size > this.limits.maxPropertiesPerObject) fail("max-object-properties");
      this.white();
      if (this.text[this.index] !== ":") fail("invalid-json");
      this.index += 1;
      this.white();
      Object.defineProperty(result, key, {
        value: this.value(depth),
        enumerable: true,
        configurable: true,
        writable: true,
      });
      this.white();
      const separator = this.text[this.index];
      if (separator === "}") {
        this.index += 1;
        return result;
      }
      if (separator !== ",") fail("invalid-json");
      this.index += 1;
      this.white();
      if (this.text[this.index] === "}") fail("trailing-comma");
    }
  }

  private array(depth: number): unknown[] {
    this.checkDepth(depth);
    this.index += 1;
    const result: unknown[] = [];
    this.white();
    if (this.text[this.index] === "]") {
      this.index += 1;
      return result;
    }
    for (;;) {
      if (result.length >= this.limits.maxArrayItems) fail("max-array-items");
      result.push(this.value(depth));
      this.white();
      const separator = this.text[this.index];
      if (separator === "]") {
        this.index += 1;
        return result;
      }
      if (separator !== ",") fail("invalid-json");
      this.index += 1;
      this.white();
      if (this.text[this.index] === "]") fail("trailing-comma");
    }
  }

  private string(propertyName: boolean): string {
    this.index += 1;
    let value = "";
    for (;;) {
      if (this.index >= this.text.length) fail("invalid-json");
      const current = this.text[this.index++];
      if (current === '"') break;
      if (current === undefined || current.charCodeAt(0) < 0x20) fail("invalid-json");
      if (current !== "\\") {
        value += current;
        continue;
      }
      const escaped = this.text[this.index++];
      const simple: Record<string, string> = {
        '"': '"',
        "\\": "\\",
        "/": "/",
        b: "\b",
        f: "\f",
        n: "\n",
        r: "\r",
        t: "\t",
      };
      if (escaped === "u") {
        const hex = this.text.slice(this.index, this.index + 4);
        if (!/^[0-9a-fA-F]{4}$/.test(hex)) fail("invalid-json");
        value += String.fromCharCode(Number.parseInt(hex, 16));
        this.index += 4;
      } else if (escaped !== undefined && Object.hasOwn(simple, escaped)) value += simple[escaped];
      else fail("invalid-json");
    }
    const bytes = utf8Bytes(value);
    if (propertyName && bytes > this.limits.maxPropertyNameBytes) fail("max-property-name-utf8-bytes");
    if (!propertyName && bytes > this.limits.maxStringBytes) fail("max-general-string-utf8-bytes");
    return value;
  }

  private number(): RawNumber {
    const start = this.index;
    if (this.text[this.index] === "-") this.index += 1;
    if (this.text[this.index] === "0") this.index += 1;
    else {
      const first = this.text[this.index];
      if (first === undefined || first < "1" || first > "9") fail("invalid-json");
      while (this.digit(this.text[this.index])) this.index += 1;
    }
    if (this.text[this.index] === ".") {
      this.index += 1;
      if (!this.digit(this.text[this.index])) fail("invalid-json");
      while (this.digit(this.text[this.index])) this.index += 1;
    }
    if (this.text[this.index] === "e" || this.text[this.index] === "E") {
      this.index += 1;
      if (this.text[this.index] === "+" || this.text[this.index] === "-") this.index += 1;
      if (!this.digit(this.text[this.index])) fail("invalid-json");
      while (this.digit(this.text[this.index])) this.index += 1;
    }
    return new RawNumber(this.text.slice(start, this.index));
  }

  private white(): void {
    while (true) {
      const current = this.text.charCodeAt(this.index);
      if (current === 0x20 || current === 0x09 || current === 0x0a || current === 0x0d) this.index += 1;
      else {
        if (this.text[this.index] === "/") fail("comment");
        return;
      }
    }
  }

  private consume(expected: string): boolean {
    if (!this.text.startsWith(expected, this.index)) return false;
    this.index += expected.length;
    return true;
  }

  private digit(value: string | undefined): boolean {
    return value !== undefined && value >= "0" && value <= "9";
  }

  private checkDepth(depth: number): void {
    if (depth > this.limits.maxJsonDepth) fail("max-json-depth");
  }
}

function parseJson(input: string | Uint8Array, options?: Partial<ProtocolParseLimits>): unknown {
  const limits = resolveParseLimits(options);
  return new JsonReader(decodeFrame(input, limits.maxFrameBytes), limits).read();
}

function record(value: unknown, path: string): MutableRecord {
  if (value === null || typeof value !== "object" || Array.isArray(value)) fail("expected-object", path);
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) fail("expected-plain-object", path);
  const descriptors = Object.getOwnPropertyDescriptors(value);
  for (const descriptor of Object.values(descriptors)) {
    if (!("value" in descriptor)) fail("accessor-not-allowed", path);
  }
  if (Reflect.ownKeys(value).some((key) => typeof key !== "string")) fail("unknown-property", path);
  const keys = Object.keys(value);
  if (keys.length > PROTOCOL_LIMITS.maxPropertiesPerObject) fail("max-object-properties", path);
  return value as MutableRecord;
}

function shape(value: unknown, path: string, required: readonly string[], optional: readonly string[] = []): MutableRecord {
  const result = record(value, path);
  const allowed = new Set([...required, ...optional]);
  for (const key of Object.keys(result)) if (!allowed.has(key)) fail("unknown-property", pointer(path, key));
  for (const key of required) if (!Object.hasOwn(result, key)) fail("missing-property", pointer(path, key));
  return result;
}

function literal<T extends string | number>(value: unknown, expected: T, path: string): T {
  const actual = value instanceof RawNumber ? Number(value.text) : value;
  if (actual !== expected) fail("unexpected-value", path);
  return expected;
}

function string(value: unknown, path: string): string {
  if (typeof value !== "string") fail("expected-string", path);
  if (utf8Bytes(value) > PROTOCOL_LIMITS.maxStringBytes) fail("max-general-string-utf8-bytes", path);
  return value;
}

function bool(value: unknown, path: string): boolean {
  if (typeof value !== "boolean") fail("expected-boolean", path);
  return value;
}

function integer(value: unknown, minimum: number, maximum: number, path: string): number {
  const result = value instanceof RawNumber ? Number(value.text) : value;
  if (typeof result !== "number" || !Number.isSafeInteger(result) || result < minimum || result > maximum) {
    fail("integer-out-of-range", path);
  }
  return result;
}

function revision(value: unknown, path: string): Revision {
  if (typeof value === "bigint") {
    if (value < 0n || value > MAX_REVISION) fail(value > MAX_REVISION ? "revision-overflow" : "revision-out-of-range", path);
    return value;
  }
  if (value instanceof RawNumber) {
    if (value.text === "-0") return 0n;
    if (!REVISION.test(value.text)) {
      if (value.text.includes(".") || /e/i.test(value.text)) fail("fractional-revision", path);
      fail("revision-out-of-range", path);
    }
    const parsed = BigInt(value.text);
    if (parsed > MAX_REVISION) fail("revision-overflow", path);
    return parsed;
  }
  if (typeof value === "number" && Number.isSafeInteger(value) && value >= 0) return BigInt(value);
  fail(typeof value === "number" && !Number.isInteger(value) ? "fractional-revision" : "unsafe-revision", path);
}

function checkDecodedDepth(value: unknown, path = "", depth = 0, seen = new Set<object>()): void {
  if (value === null || typeof value !== "object" || value instanceof RawNumber) return;
  const currentDepth = depth + 1;
  if (currentDepth > PROTOCOL_LIMITS.maxJsonDepth) fail("max-json-depth", path);
  if (seen.has(value)) fail("cyclic-value", path);
  seen.add(value);
  for (const key of Reflect.ownKeys(value)) {
    if (typeof key !== "string") continue;
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    if (descriptor !== undefined && "value" in descriptor) {
      checkDecodedDepth(descriptor.value, pointer(path, key), currentDepth, seen);
    }
  }
  seen.delete(value);
}

function uuid(value: unknown, path: string): string {
  const result = string(value, path);
  if (!UUID.test(result)) fail("invalid-uuid", path);
  return result;
}

function contract(value: unknown, path: string): string {
  const result = string(value, path);
  if (result.length === 0 || CONTROL.test(result)) fail("control-character", path);
  if (utf8Bytes(result) > PROTOCOL_LIMITS.maxContractBytes) fail("max-contract-utf8-bytes", path);
  return result;
}

function capabilityToken(value: unknown, path: string): string {
  const result = string(value, path);
  if (!CAPABILITY_TOKEN.test(result)) fail("invalid-capability-token", path);
  return result;
}

function sanitized(value: unknown, path: string): string {
  const result = string(value, path);
  if (result.length === 0 || CONTROL.test(result)) fail("invalid-sanitized-message", path);
  if (utf8Bytes(result) > PROTOCOL_LIMITS.maxSanitizedMessageBytes) fail("max-sanitized-message-bytes", path);
  return result;
}

function array(
  value: unknown,
  path: string,
  maximum: number = PROTOCOL_LIMITS.maxArrayItems,
  limitCode = "max-array-items",
): readonly unknown[] {
  if (!Array.isArray(value)) fail("expected-array", path);
  if (value.length > maximum) fail(limitCode, path);
  return value;
}

function jsonValue(value: unknown, path: string, depth = 0, seen = new Set<object>()): JsonValue {
  if (value === null || typeof value === "boolean" || typeof value === "string") {
    if (typeof value === "string" && utf8Bytes(value) > PROTOCOL_LIMITS.maxStringBytes) fail("max-general-string-utf8-bytes", path);
    return value;
  }
  if (value instanceof RawNumber || typeof value === "number") {
    const number = value instanceof RawNumber ? Number(value.text) : value;
    if (!Number.isFinite(number)) fail("non-finite-number", path);
    return number;
  }
  if (typeof value !== "object" || value === null || typeof value === "bigint") fail("invalid-json-value", path);
  if (depth >= PROTOCOL_LIMITS.maxJsonDepth) fail("max-json-depth", path);
  if (seen.has(value)) fail("cyclic-value", path);
  seen.add(value);
  if (Array.isArray(value)) {
    if (value.length > PROTOCOL_LIMITS.maxArrayItems) fail("max-array-items", path);
    const result = value.map((item, index) => jsonValue(item, pointer(path, index), depth + 1, seen));
    seen.delete(value);
    return result;
  }
  const source = record(value, path);
  const result = Object.create(null) as Record<string, JsonValue>;
  for (const key of Object.keys(source)) {
    if (key.length === 0) fail("empty-property-name", path);
    if (utf8Bytes(key) > PROTOCOL_LIMITS.maxPropertyNameBytes) fail("max-property-name-utf8-bytes", path);
    Object.defineProperty(result, key, {
      value: jsonValue(source[key], pointer(path, key), depth + 1, seen),
      enumerable: true,
      configurable: true,
      writable: false,
    });
  }
  seen.delete(value);
  return result;
}

function capabilities(value: unknown, path: string): readonly CapabilityName[] {
  const source = array(value, path, PROTOCOL_LIMITS.maxCapabilities);
  const result = source.map((item, index) => {
    const name = string(item, pointer(path, index));
    if (!capabilityNames.has(name)) fail("unknown-capability", pointer(path, index));
    return name as CapabilityName;
  });
  if (new Set(result).size !== result.length) fail("duplicate-capability", path);
  for (let index = 1; index < result.length; index += 1) {
    if ((result[index - 1] as string) > (result[index] as string)) fail("unsorted-capabilities", path);
  }
  return result;
}

function emptyPayload(value: unknown, path: string): Record<string, never> {
  shape(value, path, []);
  return {};
}

function member(value: unknown, path: string): number {
  return integer(value, 1, 2_147_483_647, path);
}

function jsonItems(value: unknown, path: string, maximum: number = PROTOCOL_LIMITS.maxCollectionItems): readonly JsonValue[] {
  return array(value, path, maximum).map((item, index) => jsonValue(item, pointer(path, index)));
}

function errors(value: unknown, path: string): readonly string[] {
  return array(value, path, PROTOCOL_LIMITS.maxValidationErrors).map((item, index) =>
    sanitized(item, pointer(path, index)),
  );
}

function snapshotMember(value: unknown, path: string): SnapshotMember {
  const source = record(value, path);
  const type = string(source.type, pointer(path, "type"));
  switch (type) {
    case "property": {
      const item = shape(source, path, ["type", "member", "value"]);
      return { type, member: member(item.member, pointer(path, "member")), value: jsonValue(item.value, pointer(path, "value")) };
    }
    case "collection": {
      const item = shape(source, path, ["type", "member", "items"]);
      return { type, member: member(item.member, pointer(path, "member")), items: jsonItems(item.items, pointer(path, "items")) };
    }
    case "command": {
      const item = shape(source, path, ["type", "member", "canExecute", "isExecuting"]);
      return {
        type,
        member: member(item.member, pointer(path, "member")),
        canExecute: bool(item.canExecute, pointer(path, "canExecute")),
        isExecuting: bool(item.isExecuting, pointer(path, "isExecuting")),
      };
    }
    case "validation": {
      const item = shape(source, path, ["type", "member", "errors"]);
      return { type, member: member(item.member, pointer(path, "member")), errors: errors(item.errors, pointer(path, "errors")) };
    }
    default:
      return fail("unknown-snapshot-member", pointer(path, "type"));
  }
}

function snapshot(value: unknown, path: string, requireZero = false): SnapshotState {
  const source = shape(value, path, ["revision", "members"]);
  const parsedRevision = revision(source.revision, pointer(path, "revision"));
  if (requireZero && parsedRevision !== 0n) fail("opened-revision-not-zero", pointer(path, "revision"));
  const sourceMembers = array(
    source.members,
    pointer(path, "members"),
    PROTOCOL_LIMITS.maxSnapshotMembers,
    "max-snapshot-members",
  );
  const members = sourceMembers.map((item, index) => snapshotMember(item, pointer(pointer(path, "members"), index)));
  const identities = new Set<string>();
  for (const item of members) {
    const identity = `${item.type}:${item.member}`;
    if (identities.has(identity)) fail("duplicate-snapshot-member", pointer(path, "members"));
    identities.add(identity);
  }
  return { revision: parsedRevision, members };
}

function patchChange(value: unknown, path: string): PatchChange {
  const source = record(value, path);
  const type = string(source.type, pointer(path, "type"));
  switch (type) {
    case "property": {
      const item = shape(source, path, ["type", "member", "value"]);
      return { type, member: member(item.member, pointer(path, "member")), value: jsonValue(item.value, pointer(path, "value")) };
    }
    case "collection": {
      const item = shape(source, path, ["type", "member", "operation", "index", "items"]);
      const operation = string(item.operation, pointer(path, "operation"));
      if (operation !== "insert" && operation !== "remove" && operation !== "replace" && operation !== "reset") {
        fail("unknown-collection-operation", pointer(path, "operation"));
      }
      const index = integer(item.index, 0, 9_999, pointer(path, "index"));
      if (operation === "reset" && index !== 0) fail("reset-index-not-zero", pointer(path, "index"));
      return {
        type,
        member: member(item.member, pointer(path, "member")),
        operation,
        index,
        items: jsonItems(item.items, pointer(path, "items")),
      };
    }
    case "collectionMove": {
      const item = shape(source, path, ["type", "member", "from", "to", "count"]);
      return {
        type,
        member: member(item.member, pointer(path, "member")),
        from: integer(item.from, 0, 9_999, pointer(path, "from")),
        to: integer(item.to, 0, 9_999, pointer(path, "to")),
        count: integer(item.count, 1, PROTOCOL_LIMITS.maxCollectionItems, pointer(path, "count")),
      };
    }
    case "command": {
      const item = shape(source, path, ["type", "member", "canExecute", "isExecuting"]);
      return {
        type,
        member: member(item.member, pointer(path, "member")),
        canExecute: bool(item.canExecute, pointer(path, "canExecute")),
        isExecuting: bool(item.isExecuting, pointer(path, "isExecuting")),
      };
    }
    case "validation": {
      const item = shape(source, path, ["type", "member", "errors"]);
      return { type, member: member(item.member, pointer(path, "member")), errors: errors(item.errors, pointer(path, "errors")) };
    }
    default:
      return fail("unknown-patch-change", pointer(path, "type"));
  }
}

function limits(value: unknown, path: string): ProtocolLimits {
  const source = shape(value, path, [
    "maxFrameBytes",
    "maxJsonDepth",
    "maxSessions",
    "maxPendingRequests",
    "maxSnapshotMembers",
    "maxPatchChanges",
    "maxCollectionItems",
    "commandTimeoutMilliseconds",
  ]);
  return {
    maxFrameBytes: integer(source.maxFrameBytes, 1_024, PROTOCOL_LIMITS.maxFrameBytes, pointer(path, "maxFrameBytes")),
    maxJsonDepth: integer(source.maxJsonDepth, 1, PROTOCOL_LIMITS.maxJsonDepth, pointer(path, "maxJsonDepth")),
    maxSessions: integer(source.maxSessions, 1, PROTOCOL_LIMITS.maxSessions, pointer(path, "maxSessions")),
    maxPendingRequests: integer(source.maxPendingRequests, 1, PROTOCOL_LIMITS.maxPendingRequests, pointer(path, "maxPendingRequests")),
    maxSnapshotMembers: integer(source.maxSnapshotMembers, 1, PROTOCOL_LIMITS.maxSnapshotMembers, pointer(path, "maxSnapshotMembers")),
    maxPatchChanges: integer(source.maxPatchChanges, 1, PROTOCOL_LIMITS.maxPatchChanges, pointer(path, "maxPatchChanges")),
    maxCollectionItems: integer(source.maxCollectionItems, 1, PROTOCOL_LIMITS.maxCollectionItems, pointer(path, "maxCollectionItems")),
    commandTimeoutMilliseconds: integer(
      source.commandTimeoutMilliseconds,
      1,
      PROTOCOL_LIMITS.maxCommandTimeoutMilliseconds,
      pointer(path, "commandTimeoutMilliseconds"),
    ),
  };
}

function faultPayload(value: unknown, path: string): FaultPayload {
  const source = shape(value, path, ["code", "message", "retryable"], ["currentRevision", "snapshotRequired"]);
  const code = string(source.code, pointer(path, "code"));
  if (!faultCodes.has(code)) fail("unknown-fault-code", pointer(path, "code"));
  const retryable = bool(source.retryable, pointer(path, "retryable"));
  const expectedRetryable = code === "revision.stale" || code === "limit.exceeded" || code === "request.timeout";
  if (retryable !== expectedRetryable) fail("invalid-fault-retryable", pointer(path, "retryable"));
  const result: {
    code: FaultCode;
    message: string;
    retryable: boolean;
    currentRevision?: Revision;
    snapshotRequired?: boolean;
  } = { code: code as FaultCode, message: sanitized(source.message, pointer(path, "message")), retryable };
  if (Object.hasOwn(source, "currentRevision")) result.currentRevision = revision(source.currentRevision, pointer(path, "currentRevision"));
  if (Object.hasOwn(source, "snapshotRequired")) result.snapshotRequired = bool(source.snapshotRequired, pointer(path, "snapshotRequired"));
  if (code === "revision.stale") {
    if (result.currentRevision === undefined) fail("missing-property", pointer(path, "currentRevision"));
    if (result.snapshotRequired !== true) fail("stale-snapshot-required", pointer(path, "snapshotRequired"));
  }
  return result;
}

function baseEnvelope(source: MutableRecord, path: string): { v: 1; kind: string } {
  return { v: literal(source.v, 1, pointer(path, "v")), kind: string(source.kind, pointer(path, "kind")) };
}

export function validateClientMessage(value: unknown): ClientMessage {
  checkDecodedDepth(value);
  const source = record(value, "");
  const base = baseEnvelope(source, "");
  switch (base.kind) {
    case "handshake": {
      const message = shape(source, "", ["v", "kind", "request", "payload"]);
      const payload = shape(message.payload, "/payload", ["supportedVersions", "capabilities"]);
      const versions = array(payload.supportedVersions, "/payload/supportedVersions", 1);
      if (versions.length !== 1) fail("unsupported-version", "/payload/supportedVersions");
      literal(versions[0], 1, "/payload/supportedVersions/0");
      return {
        v: 1,
        kind: "handshake",
        request: uuid(message.request, "/request"),
        payload: { supportedVersions: [1], capabilities: capabilities(payload.capabilities, "/payload/capabilities") },
      };
    }
    case "open": {
      const message = shape(source, "", ["v", "kind", "contract", "view", "request", "payload"]);
      return {
        v: 1,
        kind: "open",
        contract: contract(message.contract, "/contract"),
        view: uuid(message.view, "/view"),
        request: uuid(message.request, "/request"),
        payload: emptyPayload(message.payload, "/payload"),
      };
    }
    case "setProperty": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "baseRevision", "capability", "payload"]);
      const payload = shape(message.payload, "/payload", ["member", "value"]);
      return {
        v: 1,
        kind: "setProperty",
        session: uuid(message.session, "/session"),
        view: uuid(message.view, "/view"),
        request: uuid(message.request, "/request"),
        baseRevision: revision(message.baseRevision, "/baseRevision"),
        capability: capabilityToken(message.capability, "/capability"),
        payload: { member: member(payload.member, "/payload/member"), value: jsonValue(payload.value, "/payload/value") },
      };
    }
    case "execute": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "baseRevision", "capability", "payload"]);
      const payload = shape(message.payload, "/payload", ["member"], ["argument"]);
      const result: Extract<ClientMessage, { kind: "execute" }> = {
        v: 1,
        kind: "execute",
        session: uuid(message.session, "/session"),
        view: uuid(message.view, "/view"),
        request: uuid(message.request, "/request"),
        baseRevision: revision(message.baseRevision, "/baseRevision"),
        capability: capabilityToken(message.capability, "/capability"),
        payload: { member: member(payload.member, "/payload/member") },
      };
      if (Object.hasOwn(payload, "argument")) {
        return { ...result, payload: { ...result.payload, argument: jsonValue(payload.argument, "/payload/argument") } };
      }
      return result;
    }
    case "cancel": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "capability", "payload"]);
      const payload = shape(message.payload, "/payload", ["targetRequest"]);
      return {
        v: 1,
        kind: "cancel",
        session: uuid(message.session, "/session"), view: uuid(message.view, "/view"), request: uuid(message.request, "/request"),
        capability: capabilityToken(message.capability, "/capability"), payload: { targetRequest: uuid(payload.targetRequest, "/payload/targetRequest") },
      };
    }
    case "ack": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "capability", "payload"]);
      const payload = shape(message.payload, "/payload", ["revision"]);
      return {
        v: 1, kind: "ack", session: uuid(message.session, "/session"), view: uuid(message.view, "/view"), request: uuid(message.request, "/request"),
        capability: capabilityToken(message.capability, "/capability"), payload: { revision: revision(payload.revision, "/payload/revision") },
      };
    }
    case "requestSnapshot":
    case "close": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "capability", "payload"]);
      const identity = {
        v: 1 as const, session: uuid(message.session, "/session"), view: uuid(message.view, "/view"), request: uuid(message.request, "/request"),
        capability: capabilityToken(message.capability, "/capability"),
      };
      if (base.kind === "requestSnapshot") return { ...identity, kind: "requestSnapshot", payload: emptyPayload(message.payload, "/payload") };
      const payload = shape(message.payload, "/payload", [], ["reason"]);
      return {
        ...identity,
        kind: "close",
        payload: Object.hasOwn(payload, "reason") ? { reason: sanitized(payload.reason, "/payload/reason") } : {},
      };
    }
    default:
      return fail("unknown-kind", "/kind");
  }
}

export function validateHostMessage(value: unknown): HostMessage {
  checkDecodedDepth(value);
  const source = record(value, "");
  const base = baseEnvelope(source, "");
  switch (base.kind) {
    case "handshakeResult": {
      const message = shape(source, "", ["v", "kind", "request", "payload"]);
      const payload = shape(message.payload, "/payload", ["selectedVersion", "capabilities", "limits"]);
      return {
        v: 1,
        kind: "handshakeResult",
        request: uuid(message.request, "/request"),
        payload: {
          selectedVersion: literal(payload.selectedVersion, 1, "/payload/selectedVersion"),
          capabilities: capabilities(payload.capabilities, "/payload/capabilities"),
          limits: limits(payload.limits, "/payload/limits"),
        },
      };
    }
    case "opened": {
      const message = shape(source, "", ["v", "kind", "contract", "session", "view", "request", "capability", "payload"]);
      const payload = shape(message.payload, "/payload", ["snapshot"]);
      return {
        v: 1, kind: "opened", contract: contract(message.contract, "/contract"), session: uuid(message.session, "/session"),
        view: uuid(message.view, "/view"), request: uuid(message.request, "/request"), capability: capabilityToken(message.capability, "/capability"),
        payload: { snapshot: snapshot(payload.snapshot, "/payload/snapshot", true) },
      };
    }
    case "result": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "payload"]);
      const payloadSource = record(message.payload, "/payload");
      const operation = string(payloadSource.operation, "/payload/operation");
      let payload: Extract<HostMessage, { kind: "result" }>["payload"];
      if (operation === "setProperty" || operation === "ack") {
        const item = shape(payloadSource, "/payload", ["operation", "revision"]);
        payload = { operation, revision: revision(item.revision, "/payload/revision") };
      } else if (operation === "execute") {
        const item = shape(payloadSource, "/payload", ["operation", "revision"], ["value"]);
        payload = Object.hasOwn(item, "value")
          ? { operation, revision: revision(item.revision, "/payload/revision"), value: jsonValue(item.value, "/payload/value") }
          : { operation, revision: revision(item.revision, "/payload/revision") };
      } else if (operation === "cancel") {
        const item = shape(payloadSource, "/payload", ["operation", "revision", "targetRequest", "accepted"]);
        payload = {
          operation, revision: revision(item.revision, "/payload/revision"), targetRequest: uuid(item.targetRequest, "/payload/targetRequest"),
          accepted: bool(item.accepted, "/payload/accepted"),
        };
      } else return fail("unknown-result-operation", "/payload/operation");
      return {
        v: 1, kind: "result", session: uuid(message.session, "/session"), view: uuid(message.view, "/view"),
        request: uuid(message.request, "/request"), payload,
      };
    }
    case "snapshot": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "payload"]);
      return {
        v: 1, kind: "snapshot", session: uuid(message.session, "/session"), view: uuid(message.view, "/view"),
        request: uuid(message.request, "/request"), payload: snapshot(message.payload, "/payload"),
      };
    }
    case "patch": {
      const message = shape(source, "", ["v", "kind", "session", "view", "payload"]);
      const payload = shape(message.payload, "/payload", ["fromRevision", "toRevision", "changes"]);
      const fromRevision = revision(payload.fromRevision, "/payload/fromRevision");
      const toRevision = revision(payload.toRevision, "/payload/toRevision");
      if (toRevision !== fromRevision + 1n) fail("non-consecutive-patch", "/payload/toRevision");
      const sourceChanges = array(
        payload.changes,
        "/payload/changes",
        PROTOCOL_LIMITS.maxPatchChanges,
        "max-patch-changes",
      );
      if (sourceChanges.length === 0) fail("empty-patch", "/payload/changes");
      const changes = sourceChanges.map((item, index) => patchChange(item, `/payload/changes/${index}`));
      let insertedOrReplaced = 0;
      for (const change of changes) {
        if (change.type === "collection" && change.operation !== "remove") insertedOrReplaced += change.items.length;
        if (insertedOrReplaced > PROTOCOL_LIMITS.maxInsertedOrReplacedItems) fail("max-inserted-items", "/payload/changes");
      }
      return {
        v: 1, kind: "patch", session: uuid(message.session, "/session"), view: uuid(message.view, "/view"),
        payload: { fromRevision, toRevision, changes },
      };
    }
    case "fault": {
      const hasSession = Object.hasOwn(source, "session") || Object.hasOwn(source, "view");
      const message = hasSession
        ? shape(source, "", ["v", "kind", "session", "view", "request", "payload"])
        : shape(source, "", ["v", "kind", "request", "payload"]);
      const common = { v: 1 as const, kind: "fault" as const, request: uuid(message.request, "/request"), payload: faultPayload(message.payload, "/payload") };
      return hasSession
        ? { ...common, session: uuid(message.session, "/session"), view: uuid(message.view, "/view") }
        : common;
    }
    case "closed": {
      const message = shape(source, "", ["v", "kind", "session", "view", "request", "payload"]);
      const payload = shape(message.payload, "/payload", ["revision", "reason"]);
      return {
        v: 1, kind: "closed", session: uuid(message.session, "/session"), view: uuid(message.view, "/view"), request: uuid(message.request, "/request"),
        payload: { revision: revision(payload.revision, "/payload/revision"), reason: sanitized(payload.reason, "/payload/reason") },
      };
    }
    default:
      return fail("unknown-kind", "/kind");
  }
}

export function assertClientMessage(value: unknown): asserts value is ClientMessage {
  validateClientMessage(value);
}

export function assertHostMessage(value: unknown): asserts value is HostMessage {
  validateHostMessage(value);
}

export function parseClientMessage(input: string | Uint8Array, options?: Partial<ProtocolParseLimits>): ClientMessage {
  return validateClientMessage(parseJson(input, options));
}

export function parseHostMessage(input: string | Uint8Array, options?: Partial<ProtocolParseLimits>): HostMessage {
  return validateHostMessage(parseJson(input, options));
}

function quote(value: string): string {
  return JSON.stringify(value);
}

function serializeJson(value: unknown, path: string, seen: Set<object>): string {
  if (value === null) return "null";
  if (typeof value === "string") return quote(value);
  if (typeof value === "boolean") return value ? "true" : "false";
  if (typeof value === "number") {
    if (!Number.isFinite(value)) fail("non-finite-number", path);
    return JSON.stringify(value);
  }
  if (typeof value === "bigint") {
    if (value < 0n || value > MAX_REVISION) fail("revision-out-of-range", path);
    return value.toString(10);
  }
  if (typeof value !== "object" || value === null) fail("invalid-json-value", path);
  if (seen.has(value)) fail("cyclic-value", path);
  seen.add(value);
  if (Array.isArray(value)) {
    const serialized = `[${value.map((item, index) => serializeJson(item, pointer(path, index), seen)).join(",")}]`;
    seen.delete(value);
    return serialized;
  }
  const source = record(value, path);
  const serialized = `{${Object.keys(source)
    .map((key) => `${quote(key)}:${serializeJson(source[key], pointer(path, key), seen)}`)
    .join(",")}}`;
  seen.delete(value);
  return serialized;
}

function serializeMessage(value: ClientMessage | HostMessage): string {
  const serialized = serializeJson(value, "", new Set());
  if (utf8Bytes(serialized) > PROTOCOL_LIMITS.maxFrameBytes) fail("max-frame-bytes");
  return serialized;
}

/** Validates and emits a compact frame with bigint revisions as JSON integer tokens. */
export function stringifyClientMessage(value: unknown): string {
  return serializeMessage(validateClientMessage(value));
}

/** Validates and emits a compact frame with bigint revisions as JSON integer tokens. */
export function stringifyHostMessage(value: unknown): string {
  return serializeMessage(validateHostMessage(value));
}
