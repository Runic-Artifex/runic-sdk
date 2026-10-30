// Checks the Views protocol 4 conformance corpus (specs/application/conformance):
// - every vector and scenario file against its JSON Schema;
// - the canonical vectors against a reference RFC 8785 reader and writer, and the valid
//   ones against JSON.stringify with sorted keys, which is the canonical form in JavaScript;
// - the value and message vectors against reference readers written from values.md and
//   README.md;
// - that every scenario is self-contained: it creates every connection, request, call and
//   identifier it uses, and lists exactly those in `ids` (C1, #667).
// The reference readers here are spec checks, not an implementation the runtime uses.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";

const corpus = resolve(root, "specs/application/conformance");
const readJson = path => JSON.parse(readFileSync(path, "utf8"));
function* files(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* files(path);
    else if (entry.name.endsWith(".json")) yield path;
  }
}
const vectorFiles = [...files(join(corpus, "vectors"))];
const scenarioFiles = [...files(join(corpus, "scenarios"))];
const name = path => relative(root, path);

// ---------------------------------------------------------------------------
// A JSON Schema 2020-12 validator for the keywords the corpus schemas use.

function typeOf(value) {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  if (typeof value === "number") return Number.isInteger(value) ? "integer" : "number";
  return typeof value;
}
function hasType(value, type) {
  const actual = typeOf(value);
  return actual === type || (type === "number" && actual === "integer");
}
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((item, index) => deepEqual(item, b[index]));
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every(key => Object.hasOwn(b, key) && deepEqual(a[key], b[key]));
}
const known = new Set(["$schema", "$id", "$defs", "title", "description", "$ref", "type", "const", "enum", "required",
  "properties", "patternProperties", "additionalProperties", "propertyNames", "items", "minItems", "maxItems",
  "uniqueItems", "minLength", "maxLength", "pattern", "minimum", "maximum", "minProperties", "maxProperties", "oneOf",
  "anyOf", "allOf", "not", "if", "then", "else"]);

export function validate(rootSchema, value) {
  const errors = [];
  const check = (schema, value, path) => {
    if (schema === true) return true;
    if (schema === false) return fail(path, "is not allowed");
    for (const keyword of Object.keys(schema)) if (!known.has(keyword)) throw new Error(`Unsupported schema keyword ${keyword}`);
    let ok = true;
    function fail(at, message) { errors.push(`${at || "/"} ${message}`); return false; }
    const sub = (s, v, p) => check(s, v, p);
    const quiet = (s, v) => { const before = errors.length; const result = check(s, v, ""); errors.length = before; return result; };
    if (schema.$ref) {
      const target = schema.$ref.replace(/^#\/\$defs\//, "");
      if (!schema.$ref.startsWith("#/$defs/") || !rootSchema.$defs?.[target]) throw new Error(`Unknown $ref ${schema.$ref}`);
      ok = sub(rootSchema.$defs[target], value, path) && ok;
    }
    if (schema.type !== undefined && ![schema.type].flat().some(type => hasType(value, type))) return fail(path, `is not ${schema.type}`);
    if ("const" in schema && !deepEqual(schema.const, value)) ok = fail(path, `is not ${JSON.stringify(schema.const)}`);
    if (schema.enum && !schema.enum.some(item => deepEqual(item, value))) ok = fail(path, `is not one of ${JSON.stringify(schema.enum)}`);
    if (typeof value === "string") {
      const length = [...value].length;
      if (schema.minLength !== undefined && length < schema.minLength) ok = fail(path, "is too short");
      if (schema.maxLength !== undefined && length > schema.maxLength) ok = fail(path, "is too long");
      if (schema.pattern !== undefined && !new RegExp(schema.pattern, "u").test(value)) ok = fail(path, `does not match ${schema.pattern}`);
    }
    if (typeof value === "number") {
      if (schema.minimum !== undefined && value < schema.minimum) ok = fail(path, "is below the minimum");
      if (schema.maximum !== undefined && value > schema.maximum) ok = fail(path, "is above the maximum");
    }
    if (Array.isArray(value)) {
      if (schema.minItems !== undefined && value.length < schema.minItems) ok = fail(path, "has too few items");
      if (schema.maxItems !== undefined && value.length > schema.maxItems) ok = fail(path, "has too many items");
      if (schema.uniqueItems && value.some((item, index) => value.findIndex(other => deepEqual(other, item)) !== index)) ok = fail(path, "has duplicate items");
      if (schema.items !== undefined) value.forEach((item, index) => { ok = sub(schema.items, item, `${path}/${index}`) && ok; });
    }
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      const keys = Object.keys(value);
      if (schema.minProperties !== undefined && keys.length < schema.minProperties) ok = fail(path, "has too few members");
      if (schema.maxProperties !== undefined && keys.length > schema.maxProperties) ok = fail(path, "has too many members");
      for (const key of schema.required ?? []) if (!Object.hasOwn(value, key)) ok = fail(path, `lacks ${key}`);
      for (const key of keys) {
        let matched = false;
        if (schema.properties && Object.hasOwn(schema.properties, key)) { matched = true; ok = sub(schema.properties[key], value[key], `${path}/${key}`) && ok; }
        for (const [pattern, patternSchema] of Object.entries(schema.patternProperties ?? {}))
          if (new RegExp(pattern, "u").test(key)) { matched = true; ok = sub(patternSchema, value[key], `${path}/${key}`) && ok; }
        if (!matched && schema.additionalProperties !== undefined) ok = sub(schema.additionalProperties, value[key], `${path}/${key}`) && ok;
        if (schema.propertyNames !== undefined) ok = sub(schema.propertyNames, key, `${path}/${key} (name)`) && ok;
      }
    }
    if (schema.allOf) for (const item of schema.allOf) ok = sub(item, value, path) && ok;
    if (schema.anyOf && !schema.anyOf.some(item => quiet(item, value))) ok = fail(path, "matches no anyOf branch");
    if (schema.oneOf) {
      const matches = schema.oneOf.filter(item => quiet(item, value)).length;
      if (matches !== 1) {
        if (matches === 0) schema.oneOf.forEach((item, index) => { const before = errors.length; check(item, value, `${path} (oneOf ${index})`); if (errors.length > before + 3) errors.length = before + 3; });
        ok = fail(path, `matches ${matches} oneOf branches`);
      }
    }
    if (schema.not && quiet(schema.not, value)) ok = fail(path, "matches a schema it must not");
    if (schema.if !== undefined) {
      if (quiet(schema.if, value)) { if (schema.then !== undefined) ok = sub(schema.then, value, path) && ok; }
      else if (schema.else !== undefined) ok = sub(schema.else, value, path) && ok;
    }
    return ok;
  };
  check(rootSchema, value, "");
  return errors;
}

test("the schema validator accepts and rejects as JSON Schema does", () => {
  const schema = { $defs: { n: { type: "integer", minimum: 1 } }, type: "object", required: ["a"], additionalProperties: false,
    properties: { a: { $ref: "#/$defs/n" }, b: { oneOf: [{ const: 1 }, { type: "string" }] } } };
  assert.deepEqual(validate(schema, { a: 1, b: "x" }), []);
  assert.notDeepEqual(validate(schema, { a: 0 }), []);
  assert.notDeepEqual(validate(schema, { a: 1, c: 1 }), []);
  assert.notDeepEqual(validate(schema, { b: 1 }), []);
  assert.notDeepEqual(validate(schema, { a: 1, b: true }), []);
});

// ---------------------------------------------------------------------------
// Reference canonical form (values.md, Canonical form).

class CanonicalError extends Error { constructor(code, message) { super(message); this.code = code; } }
const maximumDepth = 64;

function decodeUtf8(bytes) {
  try { return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes); }
  catch { throw new CanonicalError("invalid-utf8", "The input is not well-formed UTF-8."); }
}

function parseStrict(text) {
  let i = 0;
  const fail = (code, message) => { throw new CanonicalError(code, `${message} (at ${i})`); };
  const ws = () => { while (i < text.length && " \t\n\r".includes(text[i])) i++; };
  const value = depth => {
    ws();
    const c = text[i];
    if (c === "{" || c === "[") {
      if (depth + 1 > maximumDepth) fail("depth-exceeded", "Nested deeper than 64.");
      return c === "{" ? object(depth + 1) : array(depth + 1);
    }
    if (c === '"') return string();
    if (c === "-" || (c >= "0" && c <= "9")) return number();
    for (const [word, result] of [["true", true], ["false", false], ["null", null]])
      if (text.startsWith(word, i)) { i += word.length; return result; }
    return fail("invalid-json", "Unexpected character.");
  };
  const object = depth => {
    i++;
    const result = Object.create(null);
    ws();
    if (text[i] === "}") { i++; return result; }
    for (;;) {
      ws();
      if (text[i] !== '"') fail("invalid-json", "Expected a member name.");
      const key = string();
      if (Object.hasOwn(result, key)) fail("duplicate-member", "Duplicate member name.");
      ws();
      if (text[i] !== ":") fail("invalid-json", "Expected ':'.");
      i++;
      result[key] = value(depth);
      ws();
      if (text[i] === ",") { i++; continue; }
      if (text[i] === "}") { i++; return result; }
      fail("invalid-json", "Expected ',' or '}'.");
    }
  };
  const array = depth => {
    i++;
    const result = [];
    ws();
    if (text[i] === "]") { i++; return result; }
    for (;;) {
      result.push(value(depth));
      ws();
      if (text[i] === ",") { i++; continue; }
      if (text[i] === "]") { i++; return result; }
      fail("invalid-json", "Expected ',' or ']'.");
    }
  };
  const string = () => {
    i++;
    let units = "";
    for (;;) {
      if (i >= text.length) fail("invalid-json", "Unterminated string.");
      const c = text[i];
      if (c === '"') { i++; break; }
      if (c.charCodeAt(0) < 0x20) fail("invalid-json", "Unescaped control character.");
      if (c !== "\\") { units += c; i++; continue; }
      const simple = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" }[text[i + 1]];
      if (simple !== undefined) { units += simple; i += 2; continue; }
      if (text[i + 1] === "u" && /^[0-9A-Fa-f]{4}$/.test(text.slice(i + 2, i + 6))) {
        units += String.fromCharCode(parseInt(text.slice(i + 2, i + 6), 16));
        i += 6;
        continue;
      }
      fail("invalid-json", "Invalid escape.");
    }
    if (!units.isWellFormed()) fail("lone-surrogate", "A string contains a lone surrogate.");
    return units;
  };
  const number = () => {
    const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(text.slice(i));
    if (!match) fail("invalid-json", "Invalid number.");
    i += match[0].length;
    const result = Number(match[0]);
    if (!Number.isFinite(result)) fail("number-out-of-range", "The number is outside the range of doubles.");
    return result;
  };
  const result = value(0);
  ws();
  if (i !== text.length) fail("invalid-json", "Trailing characters.");
  return result;
}

function serialize(value) {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(serialize).join(",")}]`;
  return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${serialize(value[key])}`).join(",")}}`;
}
const bytesOf = input => input.encoding === "base64" ? Buffer.from(input.value, "base64") : new TextEncoder().encode(input.value);
const textOf = input => input.encoding === "json" ? JSON.stringify(input.value) : decodeUtf8(bytesOf(input));
// JSON.stringify of each scalar and member name, with the members of each object sorted. Sorting
// the keys of a plain object is not enough: JavaScript orders integer-like keys ("1", "10") first.
function sortedStringify(value) {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(sortedStringify).join(",")}]`;
  return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${sortedStringify(value[key])}`).join(",")}}`;
}

// ---------------------------------------------------------------------------
// Reference value readers (values.md, Wire value types). Each returns the normal form.

class InvalidValue extends Error {}
const invalid = message => { throw new InvalidValue(message); };
const integerRanges = { int8: [-128, 127], uint8: [0, 255], int16: [-32768, 32767], uint16: [0, 65535], int32: [-2147483648, 2147483647], uint32: [0, 4294967295] };
const bigRanges = { int64: [-(2n ** 63n), 2n ** 63n - 1n], uint64: [0n, 2n ** 64n - 1n] };
const ticksPerDay = 864000000000n, ticksPerHour = 36000000000n, ticksPerMinute = 600000000n, ticksPerSecond = 10000000n;

function date(text) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text) ?? invalid("date");
  const [year, month, day] = match.slice(1).map(Number);
  const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > days) invalid("date range");
  return text;
}
function time(text) {
  const match = /^(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(text) ?? invalid("time");
  if (+match[1] > 23 || +match[2] > 59 || +match[3] > 59) invalid("time range");
  const fraction = (match[4] ?? "").replace(/0+$/, "");
  return `${match[1]}:${match[2]}:${match[3]}${fraction ? `.${fraction}` : ""}`;
}
function dateTime(text, offsetRequired) {
  const match = /^(\d{4}-\d{2}-\d{2})T([^Z+-]+)(Z|[+-]\d{2}:\d{2})?$/.exec(text) ?? invalid("date-time");
  const offset = match[3] ?? "";
  if (offsetRequired && !offset) invalid("offset required");
  if (offset && offset !== "Z") {
    const [hours, minutes] = offset.slice(1).split(":").map(Number);
    if (minutes > 59 || hours * 60 + minutes > 14 * 60) invalid("offset range");
  }
  const zero = offset === "Z" || offset === "+00:00" || offset === "-00:00";
  return `${date(match[1])}T${time(match[2])}${offsetRequired && zero ? "Z" : offset}`;
}
function duration(text) {
  const match = /^(-)?P(?:(\d+)D)?(?:(T)(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)(?:\.(\d{1,7}))?S)?)?$/.exec(text) ?? invalid("duration");
  const [, sign, days, t, hours, minutes, seconds, fraction] = match;
  if (days === undefined && t === undefined) invalid("no component");
  if (t !== undefined && hours === undefined && minutes === undefined && seconds === undefined) invalid("empty time");
  let ticks = BigInt(days ?? 0) * ticksPerDay + BigInt(hours ?? 0) * ticksPerHour + BigInt(minutes ?? 0) * ticksPerMinute
    + BigInt(seconds ?? 0) * ticksPerSecond + BigInt((fraction ?? "").padEnd(7, "0") || "0");
  if (sign) ticks = -ticks;
  if (ticks < -(2n ** 63n) || ticks > 2n ** 63n - 1n) invalid("duration range");
  if (ticks === 0n) return "PT0S";
  let rest = ticks < 0n ? -ticks : ticks;
  const part = unit => { const count = rest / unit; rest %= unit; return count; };
  const d = part(ticksPerDay), h = part(ticksPerHour), m = part(ticksPerMinute), s = part(ticksPerSecond);
  const f = rest.toString().padStart(7, "0").replace(/0+$/, "");
  const timePart = `${h ? `${h}H` : ""}${m ? `${m}M` : ""}${s || f ? `${s}${f ? `.${f}` : ""}S` : ""}`;
  return `${ticks < 0n ? "-" : ""}P${d ? `${d}D` : ""}${timePart ? `T${timePart}` : ""}`;
}
function float32(value) {
  const single = Math.fround(value);
  if (!Number.isFinite(single)) invalid("float32 range");
  for (let precision = 1; precision <= 9; precision++) {
    const candidate = Number(single.toPrecision(precision));
    if (Math.fround(candidate) === single) return candidate + 0;
  }
  return single;
}

export function decodeValue(type, value, parameters = {}) {
  if (value === null && parameters.nullable === true) return null;
  if (type === "json") {
    if (value === null && parameters.nullable === false) invalid("null");
    return value;
  }
  if (value === null) invalid("null");
  if (type === "boolean") return typeof value === "boolean" ? value : invalid("boolean");
  if (integerRanges[type]) {
    const [minimum, maximum] = integerRanges[type];
    if (typeof value !== "number" || !Number.isInteger(value) || value < minimum || value > maximum) invalid(type);
    return value + 0;
  }
  if (type === "float64") return typeof value === "number" ? value + 0 : invalid("float64");
  if (type === "float32") return typeof value === "number" ? float32(value) : invalid("float32");
  if (type === "string") return typeof value === "string" ? value : invalid("string");
  if (typeof value !== "string") invalid(`${type} must be a string`);
  switch (type) {
    case "int64": case "uint64": case "big-integer": {
      if (value.length > 4096 || !/^-?(0|[1-9][0-9]*)$/.test(value)) invalid(type);
      const number = BigInt(value);
      if (bigRanges[type] && (number < bigRanges[type][0] || number > bigRanges[type][1])) invalid(`${type} range`);
      return number.toString();
    }
    case "decimal": {
      const match = /^(-?)(0|[1-9][0-9]*)(?:\.([0-9]+))?$/.exec(value);
      if (value.length > 4096 || !match) invalid("decimal");
      const fraction = match[3] ?? "";
      if (fraction.length > 28 || BigInt(match[2] + fraction) >= 2n ** 96n) invalid("decimal range");
      return /^-0(\.0+)?$/.test(value) ? value.slice(1) : value;
    }
    case "uuid":
      return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) ? value.toLowerCase() : invalid("uuid");
    case "local-date": return date(value);
    case "local-time": return time(value);
    case "date-time": return dateTime(value, false);
    case "offset-date-time": return dateTime(value, true);
    case "duration": return duration(value);
    case "enum": return parameters.names.includes(value) ? value : invalid("enum");
    default: throw new Error(`No reference reader for ${type}`);
  }
}

// ---------------------------------------------------------------------------
// Reference message readers (README.md, the message catalogue; state-changes.md).

const isObject = value => value !== null && typeof value === "object" && !Array.isArray(value);
const isSafe = value => Number.isSafeInteger(value) && value >= 0;
const isIdentifier = value => typeof value === "string" && value.length >= 1 && value.length <= 128 && /^[!-~]+$/.test(value);
const isRoute = value => typeof value === "string" && /^(?:[A-Za-z0-9_-]{1,128}|runic\.[a-z0-9.-]{1,122})$/.test(value);
const isMember = value => typeof value === "string" && value.length > 0;
const isContract = value => typeof value === "string" && value.lastIndexOf(":") > 0 && /^[!-9;-~]{1,128}$/.test(value.slice(value.lastIndexOf(":") + 1));
const isName = (value, maximum) => typeof value === "string" && value.length >= 1 && value.length <= maximum;
const isHandlers = value => Array.isArray(value) && value.every(item => isObject(item) && isMember(item.name) && typeof item.contract === "string");
const identity = message => isRoute(message.route) && isMember(message.member) && isIdentifier(message.operation) && isContract(message.contract);
// README.md, "Features": absent, or at most 64 strings of at most 64 characters; unknown names are ignored.
const isFeatures = value => value === undefined || (Array.isArray(value) && value.length <= 64 && value.every(item => typeof item === "string" && item.length <= 64));
// README.md, "Edits": a set carries its value, or a text splice against an edit or a revision (text-splice/1).
const isSplice = value => isObject(value) && (("baseEdit" in value) !== ("baseRevision" in value))
  && ("baseEdit" in value ? Number.isSafeInteger(value.baseEdit) && value.baseEdit > 0 : isSafe(value.baseRevision))
  && isSafe(value.length) && isSafe(value.index) && isSafe(value.delete) && typeof value.text === "string";
// README.md, "Text splices in write": a checked write's splice has no base; it applies to the field's value at
// expectedVersion.
const isWriteSplice = value => isObject(value) && !("baseEdit" in value) && !("baseRevision" in value)
  && isSafe(value.length) && isSafe(value.index) && isSafe(value.delete) && typeof value.text === "string";

const requestRules = {
  hello: m => isSafe(m.protocol) && isIdentifier(m.session) && isObject(m.client) && isName(m.client.name, 128) && isName(m.client.version, 64)
    && isFeatures(m.features),
  open: m => isRoute(m.route) && isIdentifier(m.presentation) && (m.handlers === undefined || isHandlers(m.handlers)),
  snapshot: m => isRoute(m.route),
  close: m => isRoute(m.route) && isIdentifier(m.presentation),
  set: m => isRoute(m.route) && isMember(m.member) && ("value" in m ? !("splice" in m) : isSplice(m.splice))
    && Number.isSafeInteger(m.edit) && m.edit > 0,
  write: m => isRoute(m.route) && isMember(m.member) && isIdentifier(m.requestId) && isSafe(m.expectedVersion)
    && ("splice" in m ? !("value" in m) && !("expectedValue" in m) && isWriteSplice(m.splice) : "expectedValue" in m && "value" in m),
  // README.md, "Presentation epochs": on a connection that uses presentation-epoch/1, epoch is a safe integer.
  command: m => isRoute(m.route) && isMember(m.member) && (m.epoch === undefined || isSafe(m.epoch)),
  can: m => isRoute(m.route) && isMember(m.member),
  start: identity, status: identity, watch: identity, unwatch: identity, cancel: identity,
  stream: m => identity(m) && isSafe(m.cursor),
  interactions: m => isRoute(m.route) && isIdentifier(m.presentation) && isSafe(m.generation) && isHandlers(m.handlers),
  "interaction-reply": m => isRoute(m.route) && isIdentifier(m.presentation) && isIdentifier(m.requestId) && isSafe(m.ownerEpoch)
    && isMember(m.name) && typeof m.contract === "string" && ["answered", "cancelled", "failed"].includes(m.outcome)
    && ((m.outcome === "answered") === ("output" in m)),
  receive: () => true,
  // README.md, "Presented states and window close" (presented/1).
  presented: m => isRoute(m.route) && isSafe(m.revision),
  "closing-reply": () => true,
};

export function readRequest(text, limits = {}) {
  if (limits.inputBytes !== undefined && new TextEncoder().encode(text).length > limits.inputBytes)
    return { result: "invalid", error: { kind: "rejected", reason: "invalid-input" } };
  let message;
  try { message = parseStrict(text); } catch { return { result: "invalid", error: { kind: "rejected", reason: "invalid-request" } }; }
  const rule = isObject(message) && typeof message.kind === "string" ? requestRules[message.kind] : undefined;
  return rule?.(message) ? { result: "valid" } : { result: "invalid", error: { kind: "rejected", reason: "invalid-request" } };
}

function validChange(change) {
  if (!isObject(change) || !isMember(change.field) || typeof change.kind !== "string") return false;
  switch (change.kind) {
    case "set": return "value" in change && (change.version === undefined || isSafe(change.version));
    case "add": case "remove": case "replace": case "move": {
      const { index, oldIndex, keys, items } = change;
      if (!isSafe(index) || !Array.isArray(keys) || keys.length === 0 || !keys.every(key => typeof key === "string" && key)) return false;
      if (!Array.isArray(items) || !items.every(isObject)) return false;
      if (change.kind === "add") return oldIndex === -1 && items.length === keys.length;
      if (change.kind === "replace") return oldIndex === index && items.length === keys.length;
      if (change.kind === "remove") return oldIndex === index && items.length === 0;
      return isSafe(oldIndex) && items.length === 0;
    }
    case "text": return isSafe(change.length) && isSafe(change.trim) && change.trim <= change.length && typeof change.text === "string";
    case "splice": return isSafe(change.length) && isSafe(change.index) && isSafe(change.delete) && change.index <= change.length
      && change.delete <= change.length - change.index && typeof change.text === "string" && (change.version === undefined || isSafe(change.version));
    default: return false;
  }
}
function validFrame(m) {
  if (!isRoute(m.route) || !isSafe(m.baseRevision) || !isSafe(m.revision) || m.revision <= m.baseRevision) return false;
  if (!Array.isArray(m.changes) || m.changes.length === 0 || m.changes.length > 4096 || !m.changes.every(validChange)) return false;
  const sets = new Set(), touched = new Set();
  for (const change of m.changes) {
    if (change.kind === "set" ? sets.has(change.field) || touched.has(change.field) : sets.has(change.field)) return false;
    (change.kind === "set" ? sets : touched).add(change.field);
  }
  return true;
}
const terminalKinds = new Set(["succeeded", "failed", "domain-failed", "cancelled"]);
function validStatus(status) {
  if (!isObject(status) || typeof status.kind !== "string") return false;
  if (status.kind === "running") return status.progress === undefined || (typeof status.progress === "number" && status.progress >= 0 && status.progress <= 1);
  if (terminalKinds.has(status.kind)) return isSafe(status.revision);
  return true;
}
const validFields = fields => fields === undefined || (isObject(fields) && Object.values(fields).every(field => isObject(field) && isSafe(field.version)));
const isOffsetDateTime = value => { try { decodeValue("offset-date-time", value); return true; } catch { return false; } };
// A client takes `epoch` of a state push or an open or snapshot reply only when it is a safe integer, and otherwise
// ignores it (README.md, "Presentation epochs", Client), so the readers do not check it.
const pushRules = {
  state: m => isRoute(m.route) && isSafe(m.revision) && isObject(m.state) && validFields(m.fields),
  frame: validFrame,
  failure: m => isRoute(m.route) && isSafe(m.revision) && isObject(m.error) && typeof m.error.kind === "string" && typeof m.error.message === "string",
  operation: m => identity(m) && validStatus(m.status),
  interaction: m => isRoute(m.route) && isIdentifier(m.presentation) && isIdentifier(m.requestId) && isSafe(m.ownerEpoch)
    && isMember(m.name) && typeof m.contract === "string" && isOffsetDateTime(m.expiresAt) && "input" in m,
  "interaction-cancelled": m => isRoute(m.route) && isIdentifier(m.presentation) && isIdentifier(m.requestId) && isSafe(m.ownerEpoch) && typeof m.message === "string",
  // presented/1. closing is the first push without a route.
  present: m => isRoute(m.route) && isSafe(m.revision),
  closing: () => true,
};
export function readPush(text) {
  let message;
  try { message = parseStrict(text); } catch { return "invalid"; }
  if (!isObject(message) || typeof message.kind !== "string") return "invalid";
  const rule = pushRules[message.kind];
  return rule === undefined ? "ignored" : rule(message) ? "valid" : "invalid";
}

const withRevision = m => m.released === true || isSafe(m.revision);
const replyRules = {
  hello: m => isSafe(m.protocol) && isObject(m.implementation) && isName(m.implementation.name, 128) && isName(m.implementation.version, 64)
    && (m.features === undefined || (Array.isArray(m.features) && m.features.every(item => typeof item === "string"))),
  open: m => isSafe(m.revision) && isObject(m.state) && validFields(m.fields) && ["ok", "ignored"].includes(m.presentation),
  snapshot: m => isSafe(m.revision) && isObject(m.state) && validFields(m.fields),
  close: m => ["ok", "ignored"].includes(m.presentation),
  set: m => withRevision(m) && (m.superseded === undefined || typeof m.superseded === "boolean"),
  write: m => m.released === true || (isSafe(m.revision) && isObject(m.receipt) && ["applied", "committed-with-error", "conflict", "rejected"].includes(m.receipt.kind)),
  command: m => withRevision(m) && (m.superseded === undefined || typeof m.superseded === "boolean"),
  can: m => typeof m.value === "boolean",
  start: m => isSafe(m.revision) && ["accepted", "duplicate", "expired", "rejected"].includes(m.admission)
    && (!["accepted", "duplicate"].includes(m.admission) || validStatus(m.status)) && (m.admission !== "rejected" || typeof m.reason === "string"),
  status: m => validStatus(m.status), watch: m => validStatus(m.status), unwatch: m => validStatus(m.status),
  cancel: m => typeof m.cancellation === "string",
  stream: m => isObject(m.page) && typeof m.page.kind === "string",
  interactions: m => typeof m.registration === "string",
  "interaction-reply": m => typeof m.result === "string",
  receive: m => Array.isArray(m.messages) && m.messages.every(isObject),
  presented: () => true,
  "closing-reply": () => true,
};
export function readReply(request, text) {
  let message;
  try { message = parseStrict(text); } catch { return "invalid"; }
  if (!isObject(message) || typeof message.ok !== "boolean") return "invalid";
  if (!message.ok) {
    const { error } = message;
    return isObject(error) && typeof error.kind === "string" && typeof error.message === "string"
      && (error.reason === undefined || typeof error.reason === "string") && (message.revision === undefined || isSafe(message.revision)) ? "valid" : "invalid";
  }
  return replyRules[request](message) ? "valid" : "invalid";
}

// ---------------------------------------------------------------------------

test("the corpus files match their schemas", () => {
  const vectorSchema = readJson(join(corpus, "vector.schema.json"));
  const scenarioSchema = readJson(join(corpus, "scenario.schema.json"));
  assert.ok(vectorFiles.length > 0 && scenarioFiles.length > 0);
  const failures = [];
  for (const path of vectorFiles) for (const error of validate(vectorSchema, readJson(path))) failures.push(`${name(path)}: ${error}`);
  for (const path of scenarioFiles) for (const error of validate(scenarioSchema, readJson(path))) failures.push(`${name(path)}: ${error}`);
  assert.deepEqual(failures.slice(0, 20), []);
});

test("vector files and cases have unique ids that name their area and file", () => {
  const ids = new Set();
  for (const path of vectorFiles) {
    const set = readJson(path);
    const [area, file] = relative(join(corpus, "vectors"), path).replace(/\.json$/, "").split("/");
    assert.equal(set.area, area, name(path));
    assert.equal(set.id, `${area}.${file}`, name(path));
    for (const item of set.cases) {
      assert.ok(item.id.startsWith(`${set.id}.`), item.id);
      assert.ok(!ids.has(item.id), `duplicate ${item.id}`);
      ids.add(item.id);
    }
  }
});

const vectorCases = area => vectorFiles.map(readJson).filter(set => set.area === area).flatMap(set => set.cases);

test("the canonical vectors match the reference RFC 8785 implementation and JSON.stringify with sorted keys", () => {
  const cases = vectorCases("canonical");
  assert.ok(cases.length >= 40, `${cases.length} canonical vectors`);
  for (const item of cases) {
    let actual;
    try { actual = { output: { encoding: "utf8", value: serialize(parseStrict(decodeUtf8(bytesOf(item.input)))) } }; }
    catch (error) { if (!(error instanceof CanonicalError)) throw error; actual = { error: { code: error.code } }; }
    assert.deepEqual(actual, item.expect, item.id);
    if (item.expect.output) {
      assert.equal(sortedStringify(JSON.parse(decodeUtf8(bytesOf(item.input)))), item.expect.output.value, `${item.id} (JSON.stringify)`);
      assert.ok(Buffer.from(item.expect.output.value, "utf8").equals(Buffer.from(serialize(parseStrict(item.expect.output.value)), "utf8")), `${item.id} is idempotent`);
    }
  }
});

test("the value vectors match the reference readers of values.md", () => {
  const cases = vectorCases("values");
  const types = new Set(cases.map(item => item.type));
  for (const type of ["boolean", "int8", "uint8", "int16", "uint16", "int32", "uint32", "float32", "float64", "int64", "uint64",
    "big-integer", "decimal", "string", "uuid", "local-date", "local-time", "date-time", "offset-date-time", "duration", "enum", "json"])
    assert.ok(types.has(type), `no vector for ${type}`);
  for (const item of cases) {
    let actual;
    try {
      const input = item.input.encoding === "json" ? item.input.value : parseStrict(textOf(item.input));
      actual = decodeValue(item.type, input, item.parameters);
    } catch (error) {
      if (!(error instanceof InvalidValue || error instanceof CanonicalError)) throw error;
      actual = undefined;
    }
    if (item.expect.error) assert.equal(actual, undefined, `${item.id} should be invalid`);
    else assert.ok(deepEqual(actual, "value" in item.expect ? item.expect.value : JSON.parse(item.expect.text)), `${item.id}: ${JSON.stringify(actual)}`);
  }
});

test("the message vectors match the reference readers of the message catalogue", () => {
  for (const item of vectorCases("messages")) {
    const text = textOf(item.input);
    if (item.operation === "read-request") assert.deepEqual(readRequest(text, item.limits), item.expect, item.id);
    else if (item.operation === "read-push") assert.equal(readPush(text), item.expect.result, item.id);
    else assert.equal(readReply(item.request, text), item.expect.result, item.id);
  }
});

// ---------------------------------------------------------------------------
// Scenarios are self-contained.

const clientExpectations = new Set(["state", "call", "operation", "diagnostics", "notifications", "connection"]);
const producerExpectations = new Set(["model", "presented", "closed"]);
function visit(value, callback) {
  callback(value);
  if (Array.isArray(value)) value.forEach(item => visit(item, callback));
  else if (isObject(value)) Object.values(value).forEach(item => visit(item, callback));
}

// The groups of receive steps with unordered (README "Steps"), as step indexes: a run of such steps, separated only by
// steps a producer runner skips (calls, client steps and client expectations).
function unorderedGroups(steps) {
  const groups = [];
  let group;
  steps.forEach((step, index) => {
    if (step.role === "client") return;
    if ("receive" in step && step.unordered) {
      if (!group) groups.push(group = []);
      group.push(index);
    } else if (!(step.call || (step.expect && clientExpectations.has(Object.keys(step.expect)[0])))) group = undefined;
  });
  return groups;
}

export function scenarioProblems(scenario) {
  const problems = [];
  const problem = message => problems.push(message);
  const declared = scenario.ids ?? {};
  const used = new Map();
  const use = (id, kind, where) => {
    if (!declared[id]) problem(`${where}: ${id} is not in ids`);
    else if (declared[id].kind !== kind) problem(`${where}: ${id} is a ${declared[id].kind}, used as ${kind}`);
    used.set(id, kind);
  };
  const roles = new Set(scenario.roles);
  const connected = new Set(), created = new Set(), requests = new Map(), replied = new Set(), calls = new Set();
  const started = { wait: new Set(), close: new Set() };
  const start = (kind, id, where) => {
    if (started[kind].has(id)) problem(`${where}: ${kind} ${id} is started twice`);
    use(id, kind, where);
    started[kind].add(id);
  };
  let first;
  const steps = scenario.steps ?? [];
  steps.forEach((step, index) => {
    const where = `step ${index + 1}`;
    const role = step.role;
    if (role && !roles.has(role)) problem(`${where}: role ${role} is not one of the scenario's roles`);
    const forProducer = roles.has("producer") && role !== "client";
    const connection = step.connection ?? first;
    if (step.connect !== undefined) {
      if (created.has(step.connect)) problem(`${where}: ${step.connect} is created twice`);
      use(step.connect, "connection", where);
      created.add(step.connect); connected.add(step.connect); first ??= step.connect;
      return;
    }
    if (step.disconnect !== undefined) {
      if (!connected.has(step.disconnect)) problem(`${where}: ${step.disconnect} is not connected`);
      connected.delete(step.disconnect);
      return;
    }
    if ("send" in step || "receive" in step) {
      if (connection === undefined || !connected.has(connection)) problem(`${where}: connection ${connection} is not connected`);
      if (step.connection) use(step.connection, "connection", where);
    }
    visit(step, value => {
      if (isObject(value) && typeof value.$id === "string") use(value.$id, declared[value.$id]?.kind === "provided" ? "provided" : "wire", where);
      if (isObject(value) && Object.keys(value).length === 1 && ("$rows" in value || "$adds" in value)) {
        const args = value.$rows ?? value.$adds;
        if (!Array.isArray(args) || args.length < 2 || args.length > 3 || !args.every(isSafe)) problem(`${where}: invalid generator`);
      }
    });
    if ("send" in step) {
      if (requests.has(step.id)) problem(`${where}: request ${step.id} is sent twice`);
      use(step.id, "request", where);
      requests.set(step.id, { connection, role, index });
    }
    if ("receive" in step && step.replyTo !== undefined) {
      const request = requests.get(step.replyTo);
      if (!request) problem(`${where}: replyTo ${step.replyTo} names no earlier request`);
      else {
        if (request.connection !== connection) problem(`${where}: the reply to ${step.replyTo} is on another connection`);
        if (replied.has(step.replyTo)) problem(`${where}: ${step.replyTo} is answered twice`);
        if (request.role && request.role !== role) problem(`${where}: the reply to ${step.replyTo} has another role than its request`);
        replied.add(step.replyTo);
      }
    }
    if (step.mutate || step.delivery || step.closeWindow) {
      if (!roles.has("producer")) problem(`${where}: a producer step in a client-only scenario`);
    }
    for (const mutation of step.mutate ?? []) if (mutation.op === "waitPresented") start("wait", mutation.id, where);
    if (step.closeWindow) start("close", step.closeWindow.id, where);
    if (step.delivery && !(scenario.requires ?? []).includes("held-delivery")) problem(`${where}: delivery needs held-delivery`);
    if (step.advance && !(scenario.requires ?? []).includes("virtual-clock")) problem(`${where}: advance needs virtual-clock`);
    if (step.call) {
      if (!roles.has("client")) problem(`${where}: a call in a producer-only scenario`);
      if (calls.has(step.call.id)) problem(`${where}: call ${step.call.id} is made twice`);
      use(step.call.id, "call", where);
      if (step.call.operation) use(step.call.operation, "wire", where);
      calls.add(step.call.id);
    }
    if (step.expect) {
      const [kind, value] = Object.entries(step.expect)[0];
      if (clientExpectations.has(kind) && !roles.has("client")) problem(`${where}: a client expectation in a producer-only scenario`);
      if (producerExpectations.has(kind) && !roles.has("producer")) problem(`${where}: a ${kind} expectation in a client-only scenario`);
      if (kind === "presented" || kind === "closed") {
        const startKind = kind === "presented" ? "wait" : "close";
        if (!started[startKind].has(value.id)) problem(`${where}: ${startKind} ${value.id} was not started`);
        used.set(value.id, startKind);
        if (declared[value.id]?.kind !== startKind) problem(`${where}: ${value.id} is not a ${startKind} id`);
      }
      if (kind === "call" && !calls.has(value.id)) problem(`${where}: call ${value.id} was not made`);
      if (kind === "operation") {
        if (declared[value.id]?.kind !== "wire") problem(`${where}: operation ${value.id} is not a wire id`);
        used.set(value.id, "wire");
      }
    }
    void forProducer;
  });
  for (const [id, request] of requests) {
    const producerSees = roles.has("producer") && request.role !== "client";
    if (producerSees && !replied.has(id)) problem(`request ${id} has no reply for the producer`);
  }
  for (const id of Object.keys(declared)) if (!used.has(id)) problem(`ids lists ${id}, which no step uses`);
  for (const group of unorderedGroups(steps)) {
    const where = `the unordered steps ${group.map(index => index + 1).join(", ")}`;
    if (group.length < 2) problem(`${where}: a group needs two receive steps`);
    if (new Set(group.map(index => steps[index].connection ?? first)).size > 1) problem(`${where}: the group spans connections`);
    if (group.some(index => steps[index].drop)) problem(`${where}: a dropped message in a group`);
  }
  if ((scenario.requires ?? []).includes("held-delivery") && !steps.some(step => step.delivery)) problem("requires held-delivery without a delivery step");
  if ((scenario.requires ?? []).includes("virtual-clock") && !steps.some(step => step.advance)) problem("requires virtual-clock without an advance step");
  return problems;
}

test("every scenario is self-contained and names its file", () => {
  const ids = new Set();
  const failures = [];
  for (const path of scenarioFiles) {
    const scenario = readJson(path);
    const [set, file] = relative(join(corpus, "scenarios"), path).replace(/\.json$/, "").split("/");
    if (scenario.set !== set || scenario.id !== `${set}.${file}.v1`) failures.push(`${name(path)}: id ${scenario.id}`);
    if (ids.has(scenario.id)) failures.push(`${name(path)}: duplicate id`);
    ids.add(scenario.id);
    for (const problem of scenarioProblems(scenario)) failures.push(`${name(path)}: ${problem}`);
  }
  assert.deepEqual(failures, []);
  for (const set of ["handshake", "state-changes", "replies", "edits", "operations", "presentations", "interactions", "errors"])
    assert.ok([...ids].some(id => id.startsWith(`${set}.`)), `no ${set} scenario`);
});

test("the scenario check rejects references to resources that were not created", () => {
  const base = { roles: ["producer", "client"], ids: { c1: { kind: "connection" }, r: { kind: "request" } } };
  assert.deepEqual(scenarioProblems({ ...base, steps: [{ connect: "c1" }, { send: { kind: "receive" }, id: "r" }, { receive: { ok: true, messages: [] }, replyTo: "r" }] }), []);
  assert.ok(scenarioProblems({ ...base, steps: [{ send: { kind: "receive" }, id: "r" }, { receive: { ok: true }, replyTo: "r" }] }).some(p => p.includes("not connected")));
  assert.ok(scenarioProblems({ ...base, steps: [{ connect: "c1" }, { send: { kind: "receive" }, id: "r" }] }).some(p => p.includes("no reply")));
  assert.ok(scenarioProblems({ ...base, steps: [{ connect: "c1" }, { receive: { ok: true }, replyTo: "x" }] }).some(p => p.includes("names no earlier request")));
  assert.ok(scenarioProblems({ ...base, steps: [{ connect: "c1" }, { send: { kind: "open", presentation: { $id: "p" } }, id: "r" }, { receive: { ok: true }, replyTo: "r" }] }).some(p => p.includes("p is not in ids")));
  assert.ok(scenarioProblems({ ...base, ids: { ...base.ids, unused: { kind: "call" } }, steps: [{ connect: "c1" }, { send: {}, id: "r" }, { receive: {}, replyTo: "r" }] }).some(p => p.includes("unused")));
  assert.ok(scenarioProblems({ ...base, steps: [{ connect: "c1" }, { expect: { call: { id: "nope", pending: true } } }, { send: {}, id: "r" }, { receive: {}, replyTo: "r" }] }).some(p => p.includes("was not made")));
  const waits = { ...base, ids: { ...base.ids, w: { kind: "wait" }, x: { kind: "close" } } };
  const exchange = [{ connect: "c1" }, { send: {}, id: "r" }, { receive: {}, replyTo: "r" }];
  assert.deepEqual(scenarioProblems({ ...waits, steps: [...exchange, { mutate: [{ op: "waitPresented", id: "w", timeout: 1 }] }, { expect: { presented: { id: "w", result: true } } },
    { closeWindow: { id: "x", timeout: 1 } }, { expect: { closed: { id: "x", acknowledged: true } } }] }), []);
  assert.ok(scenarioProblems({ ...waits, steps: [...exchange, { expect: { presented: { id: "w", result: true } } }, { closeWindow: { id: "x", timeout: 1 } }] }).some(p => p.includes("w was not started")));
  assert.ok(scenarioProblems({ ...waits, steps: [...exchange, { mutate: [{ op: "waitPresented", id: "x", timeout: 1 }] }, { closeWindow: { id: "w", timeout: 1 } }] }).some(p => p.includes("used as")));
});

test("the scenario check accepts unordered groups of two receives and rejects lone ones", () => {
  const base = { roles: ["producer", "client"], ids: { c1: { kind: "connection" }, r: { kind: "request" } } };
  const steps = [{ connect: "c1" }, { send: {}, id: "r" }];
  assert.deepEqual(scenarioProblems({ ...base, steps: [...steps, { receive: {}, replyTo: "r", unordered: true }, { expect: { state: {} } }, { receive: {}, unordered: true }] }), []);
  assert.ok(scenarioProblems({ ...base, steps: [...steps, { receive: {}, replyTo: "r", unordered: true }, { receive: {} }] }).some(p => p.includes("needs two")));
  assert.ok(scenarioProblems({ ...base, steps: [...steps, { receive: {}, replyTo: "r", unordered: true }, { mutate: [] }, { receive: {}, unordered: true }] }).some(p => p.includes("needs two")));
  assert.ok(scenarioProblems({ ...base, steps: [...steps, { receive: {}, replyTo: "r", unordered: true }, { expect: { model: {} } }, { receive: {}, unordered: true }] }).some(p => p.includes("needs two")));
  assert.ok(scenarioProblems({ ...base, steps: [...steps, { receive: {}, replyTo: "r", unordered: true }, { receive: {}, unordered: true, drop: true }] }).some(p => p.includes("dropped")));
});
