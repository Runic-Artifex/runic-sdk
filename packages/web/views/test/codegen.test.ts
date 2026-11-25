import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import ts from "typescript";
import { ContractIrDiagnostic, generate, type ContractIr, type View } from "../dist/codegen/index.js";

// The TypeScript emitter (R7 G4, @runic-artifex/views/codegen) writes the generated clients from the Views contract IR
// (specs/application/contract-ir.md). Until the build runs it (G6), the .NET generator writes them, and the emitter must
// reproduce that output byte for byte.
const root = fileURLToPath(new URL("../../../../", import.meta.url));
const corpus = join(root, "specs/application/contract-ir/corpus");
const readJson = (path: string) => JSON.parse(readFileSync(path, "utf8")) as unknown;
const index = readJson(join(corpus, "index.json")) as { entries: { ir: string; generated: string }[] };

// The first line that differs, for a readable failure.
function difference(name: string, actual: string | undefined, expected: string | undefined): string | null {
  if (actual === expected) return null;
  if (actual === undefined || expected === undefined) return `${name}: ${actual === undefined ? "not generated" : "not expected"}`;
  const a = actual.split("\n"), e = expected.split("\n");
  const line = a.findIndex((text, at) => text !== e[at]);
  return `${name}:${line + 1}\n  expected: ${e[line]}\n  actual:   ${a[line]}`;
}

test("the emitter reproduces the committed clients of every corpus IR byte for byte", () => {
  assert.ok(index.entries.length > 0);
  for (const entry of index.entries) {
    const directory = join(root, entry.generated);
    const files = generate(readJson(join(corpus, entry.ir)) as ContractIr, { outputDirectory: directory, irDirectory: corpus });
    const names = [...new Set([...files.keys(), ...readdirSync(directory)])].sort();
    const differences = names.map(name => difference(name, files.get(name),
      existsSync(join(directory, name)) ? readFileSync(join(directory, name), "utf8") : undefined)).filter(item => item !== null);
    assert.deepEqual(differences, [], `${entry.ir} → ${entry.generated}`);
  }
});

test("the emitter reproduces the TypeScript goldens of the generator tests from their IR goldens", () => {
  // tests/dotnet/Runic.Application.Testing.Tests/golden/<set>: the IR golden pins the set's Views without source
  // locations, so the client goldens are compared without their sourceMappingURL line. Both write the placeholder
  // <fingerprint> for the contract fingerprint, which the mock quotes. types.ts holds the named types of every View
  // of the test assembly, more than one set's IR has, so it is not compared.
  const goldens = join(root, "tests/dotnet/Runic.Application.Testing.Tests/golden");
  let compared = 0;
  for (const set of readdirSync(goldens)) {
    const path = join(goldens, set, "contract.ir.json.golden");
    if (!existsSync(path)) continue;
    const ir = readJson(path) as ContractIr & { sourceFiles?: unknown };
    delete ir.sourceFiles;
    const files = generate({ ...ir, sources: {} }, { outputDirectory: "/generated" });
    for (const name of readdirSync(join(goldens, set)).filter(name => /\.ts\.golden$/.test(name) && name !== "types.ts.golden")) {
      const file = name.slice(0, -".golden".length);
      const expected = readFileSync(join(goldens, set, name), "utf8").replace(/^\/\/# sourceMappingURL=.*\n/m, "");
      assert.equal(difference(`${set}/${file}`, files.get(file)?.replaceAll("\\u003Cfingerprint\\u003E", "<fingerprint>"), expected), null);
      compared++;
    }
  }
  assert.ok(compared >= 40, `${compared} goldens compared`);
});

// A minimal IR around Views; the tests below cover what no corpus IR has.
function document(views: View[], extra: Partial<ContractIr> = {}): ContractIr {
  return {
    format: "runic.views.contract-ir/1",
    producer: { language: "rust", tool: "test", unit: "app" },
    views, types: {}, sources: {}, ...extra,
  };
}

const view = (name: string, extra: Partial<View> = {}): View => ({
  id: `app::${name}`, name, route: name[0]!.toLowerCase() + name.slice(1), fingerprint: "F1", kinds: [name[0]!.toLowerCase() + name.slice(1)],
  members: [], commands: [], ...extra,
});

// The IR of SharedClientCodegenTests' IResultPage, implemented by Orders and Invoices.
function resultPages(): ContractIr {
  const rows = { name: "Rows", wire: "rows", kind: "value" as const,
    value: { direction: "to-page" as const, type: { kind: "list" as const, element: { kind: "named" as const, ref: "app::ResultRow" } } } };
  const command = { name: "RunAction", wire: "runAction", parameter: "input", input: { direction: "from-page" as const, type: { kind: "string" as const } } };
  const orders = view("Orders", {
    sharedClients: ["app::IResultPage"],
    members: [rows, { name: "Title", wire: "title", kind: "value", value: { direction: "both", type: { kind: "string" } }, settable: true, declaringType: "Orders", checked: true },
      { name: "Open", wire: "open", kind: "value", value: { direction: "to-page", type: { kind: "int32" } } }],
    commands: [command],
  });
  const invoices = view("Invoices", {
    sharedClients: ["app::IResultPage"],
    members: [rows, { name: "Title", wire: "title", kind: "value", value: { direction: "to-page", type: { kind: "string" } } }],
    commands: [command],
  });
  return document([invoices, orders], {
    sharedClients: [{
      id: "app::IResultPage", name: "ResultPage", interface: "IResultPage", views: ["app::Invoices", "app::Orders"],
      members: [
        { name: "Rows", published: { "app::Invoices": { member: "rows" }, "app::Orders": { member: "rows" } } },
        { name: "Title", published: { "app::Invoices": { member: "title" }, "app::Orders": { member: "title" } } },
        { name: "RunActionCommand", published: { "app::Invoices": { command: "runAction" }, "app::Orders": { command: "runAction" } } },
      ],
    }],
    types: {
      "app::ResultRow": { kind: "object", name: { source: "app::ResultRow", base: "ResultRow", qualifiers: ["App"] },
        members: [{ wire: "id", type: { kind: "string" } }, { wire: "label", type: { kind: "string" } }] },
    },
  });
}

test("a shared client has the members its implementing Views publish alike", () => {
  const files = generate(resultPages(), { outputDirectory: "/generated" });
  const shared = files.get("resultPage.ts")!;
  for (const expected of [
    "export interface ResultPageState {", "  readonly rows: readonly ResultRow[];", "  readonly title: string;",
    "export interface ResultPageClient extends ViewClient<ResultPageState> {", "  runAction(input: string): Promise<ResultPageState>;",
    "  canRunAction(input: string): Promise<boolean>;", "  readonly kind: \"invoices\" | \"orders\";", "  connect(): Promise<ResultPageClient>;",
    "export const resultPageKinds: readonly ResultPagePageReference[\"kind\"][] = [\"invoices\", \"orders\"];",
    "import type { ResultRow } from \"./types.js\";",
  ]) assert.ok(shared.includes(expected), `${expected}\n${shared}`);
  assert.ok(!shared.includes("setTitle") && !shared.includes("readonly open:"), shared);
  const orders = files.get("orders.contract.d.ts")!;
  for (const expected of ["export interface OrdersPageReference extends ResultPagePageReference {",
    "  readonly [sharedPageReferences]?: ResultPagePageReference;", "import type { ResultPagePageReference } from \"./resultPage.js\";",
    "setTitle(value: string): Promise<OrdersState>;"]) assert.ok(orders.includes(expected), `${expected}\n${orders}`);
});

test("RUNICBRIDGE036: the Views of a shared client publish a member differently", () => {
  const ir = resultPages();
  const invoices = ir.views[0]!;
  const changed = { ...ir, views: [{ ...invoices, members: invoices.members.map(member => member.wire !== "title" ? member
    : { ...member, value: { direction: "to-page" as const, type: { kind: "string" as const, nullable: true as const } } }) }, ir.views[1]!] };
  assert.throws(() => generate(changed, { outputDirectory: "/generated" }),
    (error: unknown) => error instanceof ContractIrDiagnostic && error.code === "RUNICBRIDGE036" && /Title/.test(error.message));
});

test("RUNICBRIDGE071: a region collection's method collides with another client member", () => {
  const ir = document([view("Tabs", {
    members: [{ name: "Documents", wire: "documents", kind: "region-collection", pages: [{ view: "app::Tabs", name: "Tabs", kind: "tabs" }], pageCanClose: true }],
    commands: [{ name: "CloseDocuments", wire: "closeDocuments" }],
  })]);
  assert.throws(() => generate(ir, { outputDirectory: "/generated" }),
    (error: unknown) => error instanceof ContractIrDiagnostic && error.code === "RUNICBRIDGE071" && error.source === "app::Tabs/state/documents");
});

test("RUNICBRIDGE004: a generated module declares a name twice", () => {
  // A View that presents its own kind imports its own page function.
  const ir = document([view("Node", {
    members: [{ name: "Child", wire: "child", kind: "content", pages: [{ view: "app::Node", name: "Node", kind: "node" }], nullable: true }],
  })]);
  assert.throws(() => generate(ir, { outputDirectory: "/generated" }),
    (error: unknown) => error instanceof ContractIrDiagnostic && error.code === "RUNICBRIDGE004" && /pageNode/.test(error.message));
});

test("an IR of another format is rejected", () => {
  assert.throws(() => generate({ ...document([view("Counter")]), format: "runic.views.contract-ir/0" }, { outputDirectory: "/generated" }), /format/);
});

// Development-only members, keyed and custom-codec commands, view requests, streams and named-type collisions, which no
// example has: the output compiles against the package's declarations.
function surface(): ContractIr {
  const custom = { kind: "custom" as const, codec: "app::Money", typescript: { type: "number", decode: "bridgeWire.finiteNumber($value)", encode: "$value" } };
  return document([view("Panel", {
    doc: "A panel.",
    members: [
      { name: "Count", wire: "count", kind: "value", value: { direction: "to-page", type: { kind: "int64" } }, developmentOnly: true },
      { name: "Price", wire: "price", kind: "value", value: { direction: "both", type: custom }, settable: true, declaringType: "Panel", checked: true },
      { name: "Note", wire: "note", kind: "value", value: { direction: "to-page", type: { kind: "named", ref: "b::Note", nullable: true } } },
      { name: "Other", wire: "other", kind: "value", value: { direction: "to-page", type: { kind: "named", ref: "a::Note" } } },
      { name: "Since", wire: "since", kind: "value", value: { direction: "both", type: { kind: "local-date" } }, settable: true, declaringType: "Panel", checked: true,
        constraints: { required: true, min: "2020-01-01" }, developmentOnly: true },
    ],
    commands: [
      { name: "Open", wire: "open", parameter: "id", input: { direction: "from-page", type: { kind: "uuid" } }, keyed: true, async: true, executionState: true },
      { name: "Watch", wire: "watch", async: true, stream: true, result: { direction: "to-page", type: { kind: "named", ref: "a::Note" } }, developmentOnly: true },
      { name: "Legacy", wire: "legacy", parameter: "text", input: { direction: "from-page", type: { kind: "string" }, untyped: true } },
    ],
    requests: [{ name: "Show", wire: "show", contract: "F1:request:Show", input: { direction: "to-page", type: { kind: "string" } } }],
  })], {
    types: {
      "a::Note": { kind: "object", name: { source: "a.Note", base: "Note", qualifiers: ["A"] }, members: [{ wire: "text", type: { kind: "string" } }] },
      "b::Note": { kind: "object", name: { source: "b.Note", base: "Note", qualifiers: ["B"] }, members: [{ wire: "size", type: { kind: "int32" } }] },
    },
  });
}

test("the output of the remaining surface compiles against the package's declarations", () => {
  const files = generate(surface(), { outputDirectory: "/generated" });
  const contract = files.get("panel.contract.d.ts")!;
  assert.ok(contract.includes("  readonly count?: bigint;"), contract);
  assert.ok(contract.includes("Development-only (`[RunicDevelopmentOnly]`)"), contract);
  assert.ok(contract.includes("export type PanelCommandName = \"open\" | \"watch\" | \"legacy\";"), contract);
  assert.ok(contract.includes("reason `unresolved-key`"), contract);
  assert.ok(files.get("types.ts")!.includes("export interface ANote {") && files.get("types.ts")!.includes("export interface BNote {"));
  const client = files.get("panel.ts")!;
  assert.ok(client.includes("count: (wire: unknown) => wire === undefined ? undefined : bridgeWire.bigint("), client);
  assert.ok(!client.includes("readonly since:"), "a development-only member has no field descriptor");

  const directory = mkdtempSync(join(tmpdir(), "runic-views-codegen-"));
  try {
    // Generated modules are ES modules, as in a frontend package.
    writeFileSync(join(directory, "package.json"), '{ "type": "module" }\n');
    for (const [name, text] of files) writeFileSync(join(directory, name), text);
    const dist = resolve(fileURLToPath(new URL("../dist", import.meta.url)));
    const program = ts.createProgram([...files.keys()].filter(name => name.endsWith(".ts")).map(name => join(directory, name)), {
      strict: true, noEmit: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.NodeNext,
      moduleResolution: ts.ModuleResolutionKind.NodeNext, lib: ["lib.es2022.d.ts", "lib.dom.d.ts", "lib.esnext.disposable.d.ts"], types: [],
      paths: {
        "@runic-artifex/views": [join(dist, "index.d.ts")],
        "@runic-artifex/views/generated": [join(dist, "generated.d.ts")],
        "@runic-artifex/views/generated/wire": [join(dist, "wire.d.ts")],
        "@runic-artifex/views/generated/mock": [join(dist, "generated-mock.d.ts")],
        "@runic-artifex/views/mock": [join(dist, "mock.d.ts")],
      },
    });
    const diagnostics = ts.getPreEmitDiagnostics(program).filter(item => item.file?.fileName.startsWith(directory) ?? true)
      .map(item => `${item.file?.fileName.slice(directory.length + 1) ?? ""}: ${ts.flattenDiagnosticMessageText(item.messageText, "\n")}`);
    assert.deepEqual(diagnostics, []);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
