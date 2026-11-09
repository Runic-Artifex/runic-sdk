// A JSON Schema 2020-12 validator for the keywords the conformance corpora's schemas use
// (specs/application/conformance, specs/desktop/host-conformance). A spec check, not a general validator: an unknown
// keyword throws, so a schema cannot rely on one silently.

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
export function deepEqual(a, b) {
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
