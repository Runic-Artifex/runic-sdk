// Checks the Runic host protocol conformance corpus (specs/desktop/host-conformance):
// - every scenario against its JSON Schema, and its id against its path;
// - that every scenario is self-contained: it declares exactly the identifiers it uses, creates each connection,
//   window and action before a step names it, sends a guest's item id only after a received message carried it, waits
//   only for limits it arranges, and uses only the steps of its role.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";
import { validate } from "./json-schema.mjs";

const corpus = resolve(root, "specs/desktop/host-conformance");
const readJson = path => JSON.parse(readFileSync(path, "utf8"));
const schema = readJson(join(corpus, "scenario.schema.json"));
const scenarioFiles = readdirSync(join(corpus, "scenarios"), { withFileTypes: true, recursive: true })
  .filter(entry => entry.isFile() && entry.name.endsWith(".json"))
  .map(entry => join(entry.parentPath, entry.name))
  .sort();

const secrets = new Set(["nonce", "key", "other-key", "host-proof", "host-proof-other-nonce", "guest-proof",
  "guest-proof-other-nonce", "launch-url"]);
const jsonTypes = new Set(["string", "number", "integer", "boolean", "object", "array", "null"]);
const roleSteps = {
  guest: new Set(["start", "line", "connect", "send", "sendRaw", "receive", "receiveAll", "closed", "disconnect", "closeInput",
    "advance", "guest", "expect"]),
  host: new Set(["start", "offer", "line", "accept", "send", "sendRaw", "receive", "receiveAll", "closed", "disconnect", "exit",
    "advance", "user", "expect"]),
};

// The problems of one scenario that its schema cannot express. Empty when it is self-contained.
export function scenarioProblems(scenario) {
  const problems = [];
  const declared = new Map(Object.entries(scenario.ids ?? {}).map(([name, id]) => [name, id.kind]));
  const used = new Set();
  const created = new Set();
  const use = (name, kind, where) => {
    used.add(name);
    if (!declared.has(name)) problems.push(`${where}: ${name} is not in ids`);
    else if (kind && declared.get(name) !== kind) problems.push(`${where}: ${name} is a ${declared.get(name)}, not a ${kind}`);
  };
  const create = (name, kind, where) => {
    use(name, kind, where);
    if (created.has(name)) problems.push(`${where}: ${name} is created twice`);
    created.add(name);
  };
  const exists = (name, kind, where) => {
    use(name, kind, where);
    if (!created.has(name)) problems.push(`${where}: ${name} is used before a step creates it`);
  };
  // $id, $secret, $any, $file and $directory inside a message; received is whether the runner receives it.
  const walk = (value, where, received = false) => {
    if (Array.isArray(value)) { value.forEach((item, index) => walk(item, `${where}/${index}`, received)); return; }
    if (value === null || typeof value !== "object") return;
    if ("$id" in value) {
      const kind = declared.get(value.$id);
      use(value.$id, undefined, where);
      // A guest's item id is bound from a message the runner received; a message it sends repeats a bound one.
      if (kind === "item") {
        if (received) items.add(value.$id);
        else if (!items.has(value.$id)) problems.push(`${where}: item ${value.$id} is sent before a received message carried it`);
      }
      if (kind === "window" || kind === "connection" || kind === "action") {
        if (kind !== "window") problems.push(`${where}: ${value.$id} is a ${kind}, which a message cannot carry`);
        // A guest chooses window ids; a guest runner binds them from window-open, so a message names a window it opened.
        else if (!created.has(value.$id) && !opening.has(value.$id)) problems.push(`${where}: window ${value.$id} has not been opened`);
      }
      return;
    }
    if ("$secret" in value) {
      if (!secrets.has(value.$secret)) problems.push(`${where}: unknown secret ${value.$secret}`);
      return;
    }
    if ("$file" in value || "$directory" in value) {
      // then: path segments after the file or directory, such as ".." and a name.
      const others = Object.keys(value).filter(key => key !== "$file" && key !== "$directory" && key !== "then");
      if (others.length > 0 || ("$file" in value && "$directory" in value)) problems.push(`${where}: $file or $directory has other members`);
      if ("then" in value && !(Array.isArray(value.then) && value.then.length > 0 && value.then.every(segment => typeof segment === "string" && /^(\.|\.\.|[A-Za-z0-9][A-Za-z0-9._-]*)$/.test(segment))))
        problems.push(`${where}: then is not a list of path segments`);
      return;
    }
    if ("$any" in value) {
      const types = [value.$any].flat();
      if (!types.every(type => jsonTypes.has(type))) problems.push(`${where}: unknown $any type ${JSON.stringify(value.$any)}`);
      return;
    }
    for (const [key, member] of Object.entries(value)) walk(member, `${where}/${key}`, received);
  };
  const opening = new Set();
  const items = new Set();
  const limits = new Set(Object.keys(scenario.arrange?.limits ?? {}));
  let started = false;
  scenario.steps.forEach((step, index) => {
    const where = `step ${index}`;
    const action = Object.keys(step).find(key => !["note", "connection", "as", "headers", "host", "path", "status", "refused"].includes(key));
    for (const role of scenario.roles)
      if (!roleSteps[role].has(action)) problems.push(`${where}: ${action} is not a step of the ${role} role`);
    if (action === "start") {
      if (started) problems.push(`${where}: the scenario starts twice`);
      started = true;
      if (step.start === "none" || step.start.offer || step.start.offerLine) {
        if (!scenario.roles.includes("guest")) problems.push(`${where}: only a guest runner starts a guest with an offer`);
      }
      else if (!scenario.roles.includes("host")) problems.push(`${where}: only a host runner asks a host to start`);
      if (step.start.offer) walk(step.start.offer, `${where}/offer`);
      return;
    }
    if (!started) problems.push(`${where}: ${action} before start`);
    switch (action) {
      case "connect": create(step.connect, "connection", where); break;
      case "accept": create(step.accept, "connection", where); break;
      case "disconnect": exists(step.disconnect, "connection", where); break;
      case "closed": exists(step.closed.connection, "connection", where); break;
      case "send": case "receive":
        exists(step.connection, "connection", where);
        // A window-open that a guest sends names the window its open action created.
        walk(step[action], `${where}/${action}`, action === "receive");
        break;
      case "sendRaw": exists(step.connection, "connection", where); break;
      case "receiveAll":
        exists(step.connection, "connection", where);
        step.receiveAll.forEach((message, item) => walk(message, `${where}/receiveAll/${item}`, true));
        break;
      case "offer": walk(step.offer, `${where}/offer`, true); break;
      case "line": if (typeof step.line === "object") walk(step.line, `${where}/line`); break;
      case "advance":
        if (!limits.has(step.advance)) problems.push(`${where}: advance names ${step.advance}, which arrange.limits does not set`);
        break;
      case "guest": {
        const { action: verb, window, owner } = step.guest;
        if (verb === "open") {
          create(window, "window", where);
          opening.add(window);
        }
        else if (window !== undefined) exists(window, "window", where);
        if (owner !== undefined) exists(owner, "window", where);
        if (step.as !== undefined) create(step.as, "action", where);
        break;
      }
      case "expect":
        if (step.expect.action !== undefined) exists(step.expect.action, "action", where);
        break;
    }
  });
  if (!started) problems.push("the scenario never starts");
  for (const name of declared.keys()) if (!used.has(name)) problems.push(`ids: ${name} is never used`);
  if (scenario.roles.includes("guest") && !scenario.arrange?.application)
    problems.push("arrange: a guest scenario names its application");
  if (scenario.roles.includes("host") && !scenario.arrange?.host) problems.push("arrange: a host scenario names its host");
  return problems;
}

test("the corpus has scenarios for both roles", () => {
  const roles = new Set(scenarioFiles.flatMap(file => readJson(file).roles));
  assert.ok(scenarioFiles.length >= 30, `only ${scenarioFiles.length} scenarios`);
  assert.deepEqual([...roles].sort(), ["guest", "host"]);
});

test("every scenario matches its schema and its path", () => {
  const failures = [];
  const ids = new Set();
  for (const file of scenarioFiles) {
    const scenario = readJson(file);
    const errors = validate(schema, scenario);
    if (errors.length > 0) { failures.push(`${relative(root, file)}: ${errors.slice(0, 5).join("; ")}`); continue; }
    const set = basename(dirname(file));
    if (scenario.set !== set || !scenario.id.startsWith(`${set}.${basename(file, ".json")}.v`))
      failures.push(`${relative(root, file)}: id ${scenario.id} or set ${scenario.set} does not match its path`);
    if (ids.has(scenario.id)) failures.push(`${relative(root, file)}: duplicate id ${scenario.id}`);
    ids.add(scenario.id);
  }
  assert.deepEqual(failures, []);
});

test("every scenario is self-contained", () => {
  const failures = scenarioFiles.flatMap(file =>
    scenarioProblems(readJson(file)).map(problem => `${relative(root, file)}: ${problem}`));
  assert.deepEqual(failures, []);
});

test("the self-containment check finds what it is for", () => {
  const base = {
    roles: ["guest"], ids: { c1: { kind: "connection" }, w1: { kind: "window" }, o: { kind: "action" } },
    arrange: { limits: { startTimeout: 1 }, application: { name: "a", version: "1", applicationId: null } },
  };
  const start = { start: { offer: { nonce: { $secret: "nonce" } } } };
  const open = { guest: { action: "open", window: "w1", type: "Main" }, as: "o" };
  assert.deepEqual(scenarioProblems({ ...base, steps: [start, { connect: "c1" }, open, { receive: { window: { $id: "w1" } }, connection: "c1" }, { expect: { action: "o", opened: true } }] }), []);
  assert.match(scenarioProblems({ ...base, steps: [start, { connect: "c1" }, { send: { window: { $id: "w1" } }, connection: "c1" }] }).join(), /has not been opened/);
  assert.match(scenarioProblems({ ...base, steps: [start, { send: {}, connection: "c1" }] }).join(), /used before a step creates it/);
  assert.match(scenarioProblems({ ...base, steps: [start, { advance: "stopTimeout" }] }).join(), /arrange.limits does not set/);
  assert.match(scenarioProblems({ ...base, steps: [start, { accept: "c1" }] }).join(), /not a step of the guest role/);
  assert.match(scenarioProblems({ ...base, steps: [start, { connect: "c1" }, { send: { key: { $secret: "password" } }, connection: "c1" }] }).join(), /unknown secret/);
  assert.match(scenarioProblems({ ...base, steps: [start] }).join(), /never used/);
  const items = { ...base, ids: { ...base.ids, i1: { kind: "item" } } };
  const menu = { receive: { menu: [{ id: { $id: "i1" } }] }, connection: "c1" };
  const invoke = { send: { kind: "menu-invoke", itemId: { $id: "i1" } }, connection: "c1" };
  assert.deepEqual(scenarioProblems({ ...items, steps: [start, { connect: "c1" }, open, menu, invoke, { expect: { action: "o", opened: true } }] }), []);
  assert.match(scenarioProblems({ ...items, steps: [start, { connect: "c1" }, open, invoke, menu, { expect: { action: "o", opened: true } }] }).join(), /sent before a received message/);
  const host = { roles: ["host"], ids: {}, arrange: { host: { name: "h", version: "1" } } };
  assert.deepEqual(scenarioProblems({ ...host, steps: [{ start: { host: true } }, { user: { action: "closeWindow", window: "w1" } }] }), []);
  assert.match(scenarioProblems({ ...base, steps: [start, { user: { action: "closeWindow", window: "w1" } }] }).join(), /not a step of the guest role/);
});

test("the proof example in the specification is HMAC-SHA256 of its nonce and key", async () => {
  const { createHmac } = await import("node:crypto");
  const spec = readFileSync(resolve(root, "specs/desktop/host-protocol.md"), "utf8");
  const nonce = Buffer.from(Array.from({ length: 32 }, (_, i) => i));
  const key = Buffer.from(Array.from({ length: 32 }, (_, i) => 32 + i)).toString("base64url");
  const proof = label => createHmac("sha256", nonce).update(`${label} ${key}`, "ascii").digest("base64url");
  for (const value of [nonce.toString("base64url"), key, proof("runic-host/1 host"), proof("runic-host/1 guest")])
    assert.ok(spec.includes(`\`${value}\``), `host-protocol.md does not give ${value}`);
});
