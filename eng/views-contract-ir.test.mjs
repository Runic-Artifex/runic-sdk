// Checks the Views contract IR corpus (specs/application/contract-ir.md):
// - every corpus document against the JSON Schema, and the references the schema cannot express (named types, pages,
//   owners, shared clients, keys, sources);
// - that the corpus covers every example that commits generated clients, and that the contract of each View is the
//   one its example's committed clients carry, so a contract change that leaves the corpus behind fails.
// RUNIC_UPDATE_CONTRACT_IR_CORPUS=1 first copies each example's contract.ir.json from its obj directory into the corpus,
// with its sources relative to the corpus; build the examples before.
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";
import { validate } from "./json-schema.mjs";

const specs = resolve(root, "specs/application/contract-ir");
const corpus = join(specs, "corpus");
const readJson = path => JSON.parse(readFileSync(path, "utf8"));
const schema = readJson(join(specs, "contract-ir.schema.json"));
const index = readJson(join(corpus, "index.json"));

if (process.env.RUNIC_UPDATE_CONTRACT_IR_CORPUS === "1") {
  const directories = path => existsSync(path)
    ? readdirSync(path, { withFileTypes: true }).filter(item => item.isDirectory()).map(item => join(path, item.name)) : [];
  for (const entry of index.entries) {
    // obj/<configuration>/<framework>/runic-bridge/contract.ir.json
    const found = directories(join(root, dirname(entry.project), "obj")).flatMap(directories)
      .map(path => join(path, "runic-bridge", "contract.ir.json")).filter(path => existsSync(path));
    if (found.length !== 1)
      throw new Error(`${entry.project}: ${found.length === 0 ? "no contract.ir.json; build the project first" : `${found.length} contract.ir.json files; remove all but one build`}`);
    const ir = readJson(found[0]);
    for (const location of Object.values(ir.sources)) location.file = relative(corpus, resolve(location.file)).replaceAll("\\", "/");
    writeFileSync(join(corpus, entry.ir), JSON.stringify(ir, null, 2) + "\n");
  }
}

const documents = index.entries.map(entry => ({ entry, ir: readJson(join(corpus, entry.ir)) }));

// Examples whose committed clients the corpus does not cover, with the reason.
const uncovered = new Map([
  ["examples/dynamicdata/Frontend/src/generated", "builds against an external DynamicData fork checkout, which CI does not build"],
]);

function* generatedDirectories(directory) {
  for (const item of readdirSync(directory, { withFileTypes: true })) {
    if (!item.isDirectory() || ["node_modules", "obj", "bin", "dist"].includes(item.name)) continue;
    const path = join(directory, item.name);
    if (item.name === "generated" && path.endsWith(join("src", "generated"))) yield relative(root, path).replaceAll("\\", "/");
    else yield* generatedDirectories(path);
  }
}

test("the corpus index lists every document once, with an existing project and generated clients", () => {
  const listed = index.entries.map(entry => entry.ir);
  assert.deepEqual([...new Set(listed)], listed, "an IR is listed twice");
  const files = readdirSync(corpus).filter(name => name.endsWith(".ir.json")).sort();
  assert.deepEqual(files, [...listed].sort(), "corpus/*.ir.json and index.json differ");
  for (const entry of index.entries) {
    assert.ok(existsSync(join(root, entry.project)), `${entry.ir}: ${entry.project} does not exist`);
    assert.ok(existsSync(join(root, entry.generated)), `${entry.ir}: ${entry.generated} does not exist`);
  }
});

test("the corpus covers every example that commits generated clients", () => {
  const covered = new Set(index.entries.map(entry => entry.generated));
  const missing = [...generatedDirectories(join(root, "examples"))].filter(path => !covered.has(path) && !uncovered.has(path));
  assert.deepEqual(missing, [], "add the example's IR to specs/application/contract-ir/corpus");
});

test("the corpus documents match the schema", () => {
  assert.ok(documents.length > 0);
  const failures = documents.flatMap(({ entry, ir }) => validate(schema, ir).map(error => `${entry.ir}: ${error}`));
  assert.deepEqual(failures.slice(0, 20), []);
});

test("the schema rejects malformed documents", () => {
  const counter = documents.find(({ entry }) => entry.ir === "first-window.ir.json").ir;
  const cases = {
    "a lowercase name": ir => { ir.views[0].members[0].name = "count"; },
    "a value member without its value": ir => { delete ir.views[0].members[0].value; },
    "a list without its element": ir => { ir.views[0].members[0].value.type = { kind: "list" }; },
    "an unknown member": ir => { ir.views[0].unknown = true; },
    "a parameter without an input": ir => { ir.views[0].commands[0].parameter = "value"; },
    "another format": ir => { ir.format = "runic.views.contract-ir/0"; },
  };
  for (const [name, mutate] of Object.entries(cases)) {
    const ir = structuredClone(counter);
    mutate(ir);
    assert.notDeepEqual(validate(schema, ir), [], `the schema accepts ${name}`);
  }
});

test("the corpus documents' references resolve", () => {
  for (const { entry, ir } of documents) {
    const at = path => `${entry.ir}: ${path}`;
    const views = new Map(ir.views.map(view => [view.id, view]));
    const units = new Set((ir.clientModules ?? []).map(module => module.unit));
    const shared = new Map((ir.sharedClients ?? []).map(client => [client.id, client]));
    assert.equal(views.size, ir.views.length, at("duplicate View ids"));
    const typeRefs = (type, path) => {
      if (type.kind === "named") assert.ok(ir.types[type.ref], at(`${path}: no type ${type.ref}`));
      if (type.element) typeRefs(type.element, `${path}/element`);
      if (type.value) typeRefs(type.value, `${path}/value`);
    };
    const value = (position, path) => position && typeRefs(position.type, path);
    for (const [id, definition] of Object.entries(ir.types)) {
      for (const member of definition.members ?? []) typeRefs(member.type, `types/${id}/${member.wire}`);
      if (definition.kind === "union")
        for (const item of definition.cases)
          assert.ok(item.object === null || ir.types[item.object]?.kind === "object", at(`types/${id}: case ${item.tag}`));
    }
    for (const view of ir.views) {
      const path = `views/${view.id}`;
      assert.ok(view.owner === undefined || units.has(view.owner), at(`${path}: owner ${view.owner} is not in clientModules`));
      for (const id of view.sharedClients ?? []) assert.ok(shared.get(id)?.views.includes(view.id), at(`${path}: shared client ${id}`));
      for (const member of view.members) {
        value(member.value, `${path}/${member.wire}`);
        value(member.rows, `${path}/${member.wire}/rows`);
        for (const page of member.pages ?? []) assert.ok(views.has(page.view), at(`${path}/${member.wire}: no View ${page.view}`));
        if (member.collectionKey !== undefined) {
          const row = ir.types[member.value.type.element?.ref];
          assert.ok(row?.members.some(field => field.wire === member.collectionKey), at(`${path}/${member.wire}: key ${member.collectionKey}`));
        }
      }
      for (const command of view.commands)
        for (const part of ["input", "result", "failure"]) value(command[part], `${path}/${command.wire}/${part}`);
      for (const item of [...(view.interactions ?? []), ...(view.requests ?? [])]) {
        value(item.input, `${path}/${item.wire}/input`);
        value(item.output, `${path}/${item.wire}/output`);
      }
    }
    for (const client of ir.sharedClients ?? []) {
      assert.ok(client.owner === undefined || units.has(client.owner), at(`sharedClients/${client.id}: owner`));
      for (const member of client.members)
        for (const [id, published] of Object.entries(member.published)) {
          const view = views.get(id);
          assert.ok(client.views.includes(id) && view, at(`sharedClients/${client.id}/${member.name}: View ${id}`));
          assert.ok(published.member !== undefined ? view.members.some(item => item.wire === published.member)
            : view.commands.some(item => item.wire === published.command), at(`sharedClients/${client.id}/${member.name}: ${JSON.stringify(published)}`));
        }
    }
    for (const [id, location] of Object.entries(ir.sources)) {
      assert.ok(!location.file.startsWith("/") && !/^[A-Za-z]:/.test(location.file), at(`sources/${id} is absolute`));
      assert.ok(existsSync(resolve(corpus, location.file)), at(`sources/${id}: ${location.file} does not exist`));
    }
  }
});

test("each corpus View has the contract its example's committed clients carry", () => {
  for (const { entry, ir } of documents) {
    const directory = join(root, entry.generated);
    const text = readdirSync(directory).filter(name => name.endsWith(".ts")).map(name => readFileSync(join(directory, name), "utf8")).join("\n");
    for (const view of ir.views)
      assert.ok(text.includes(`"${view.id}:${view.fingerprint}"`),
        `${entry.ir}: ${entry.generated} does not carry the contract of ${view.id}; rebuild ${entry.project} and update the corpus (specs/application/contract-ir.md)`);
  }
});

test("the corpus sources point into their examples", () => {
  for (const { entry, ir } of documents) {
    const example = dirname(entry.project).split("/").slice(0, 2).join("/");
    for (const location of Object.values(ir.sources))
      assert.ok(relative(root, resolve(corpus, location.file)).replaceAll("\\", "/").startsWith(`${example}/`), `${entry.ir}: ${location.file}`);
  }
});
