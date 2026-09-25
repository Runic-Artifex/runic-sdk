import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { DevTools } from "@vitejs/devtools";
import { build, createServer } from "vite";
import { runic } from "../dist/index.js";

const execFile = promisify(execFileCallback);

test("exposes a real virtual client and bounded diagnostics endpoint", () => {
  const plugin = runic({ devtools: false });
  plugin.configResolved({ command: "serve", mode: "development", root: process.cwd() });
  assert.equal(plugin.resolveId("virtual:runic/client"), "\0virtual:runic/client");
  assert.match(plugin.load("\0virtual:runic/client"), /vite-plugin-runic\/client/);

  const handlers = new Map();
  const middleware = [];
  plugin.configureServer({
    ws: {
      on: (event, handler) => handlers.set(event, handler),
      send: () => undefined,
    },
    middlewares: { use: (path, handler) => middleware.push([path, handler]) },
    httpServer: undefined,
  });
  assert.equal(handlers.has("runic:state"), true);
  assert.equal(handlers.has("runic:diagnostic"), true);
  assert.equal(middleware[0][0], "/__runic/state");
});

test("injects the Desktop bootstrap while leaving Vite and HMR ownership intact", async () => {
  const root = await fixtureRoot();
  const plugin = runic({
    devtools: false,
    desktop: { bootstrapUrl: "http://127.0.0.1:43123/runic-desktop.js" },
  });
  const server = await createServer({
    root,
    configFile: false,
    logLevel: "silent",
    plugins: [plugin],
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const html = await fetch(`http://127.0.0.1:${serverPort(server)}/`).then((response) => response.text());
    assert.match(html, /<script src="http:\/\/127\.0\.0\.1:43123\/runic-desktop\.js"><\/script>/);
    assert.match(html, /@vite\/client/);
    assert.ok(html.indexOf("runic-desktop.js") < html.indexOf("@vite/client"));
  } finally {
    await server.close();
    await rm(root, { recursive: true, force: true });
  }

  assert.throws(
    () => runic({ desktop: { bootstrapUrl: "https://user@example.test/runic-desktop.js" } }),
    /desktop\.bootstrapUrl/,
  );
  assert.throws(
    () => runic({ desktop: { bootstrapUrl: "/runic-desktop.js" } }),
    /desktop\.bootstrapUrl/,
  );
});

test("emits relocatable Desktop bootstrap and asset URLs for production", async () => {
  const root = await fixtureRoot();
  try {
    await assert.rejects(
      build({
        root,
        configFile: false,
        logLevel: "silent",
        base: "/",
        plugins: [runic({ devtools: false, desktop: true })],
        build: { outDir: "invalid-dist" },
      }),
      /requires Vite base to be '\.\/'/,
    );
    await assert.rejects(
      build({
        root,
        configFile: false,
        logLevel: "silent",
        plugins: [
          runic({ devtools: false, desktop: true }),
          { name: "invalid-desktop-base", config: () => ({ base: "/later/" }) },
        ],
        build: { outDir: "overridden-dist" },
      }),
      /resolved Vite base to be '\.\/'/,
    );
    await build({
      root,
      configFile: false,
      logLevel: "silent",
      plugins: [runic({ devtools: false, desktop: true })],
      build: { outDir: "dist" },
    });
    const html = await readFile(join(root, "dist", "index.html"), "utf8");
    assert.match(html, /<script src="\.\/runic-desktop\.js"><\/script>/);
    assert.match(html, /(?:src|href)="\.\/assets\//);
    assert.doesNotMatch(html, /(?:src|href)="\/(?:runic-desktop\.js|assets\/)/);

    await build({
      root,
      configFile: false,
      logLevel: "silent",
      plugins: [runic({
        devtools: false,
        desktop: { bootstrapUrl: "http://127.0.0.1:43123/runic-desktop.js" },
      })],
      build: { outDir: "custom-development-url-dist" },
    });
    const customDevelopmentUrlHtml = await readFile(
      join(root, "custom-development-url-dist", "index.html"),
      "utf8",
    );
    assert.match(customDevelopmentUrlHtml, /<script src="\.\/runic-desktop\.js"><\/script>/);
    assert.match(customDevelopmentUrlHtml, /(?:src|href)="\.\/assets\//);
    assert.doesNotMatch(customDevelopmentUrlHtml, /127\.0\.0\.1:43123/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("runs the Vite, HMR, and SSR fixtures without browser-only state", async () => {
  const root = await fixtureRoot();
  const plugin = runic({ devtools: false });
  const server = await createServer({
    root,
    configFile: false,
    logLevel: "silent",
    plugins: [plugin],
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const client = await fetch(`http://127.0.0.1:${serverPort(server)}/src/client-entry.js`);
    assert.equal(client.status, 200);
    assert.ok((await client.text()).length > 0);

    const virtual = await fetch(
      `http://127.0.0.1:${serverPort(server)}/@id/virtual:runic/client`,
    );
    assert.equal(virtual.status, 200);
    assert.deepEqual(await executeVirtualReporter(await virtual.text()), [{
      event: "runic:diagnostic",
      data: {
        source: "assets",
        kind: "event",
        label: "[redacted]",
        detail: { status: "ready", message: "[redacted]" },
      },
    }]);

    assert.equal((await server.ssrLoadModule("/src/ssr-entry.js")).revision, 1);
    const changed = new Promise((resolve) => server.watcher.once("change", resolve));
    await writeFile(join(root, "src", "hmr-value.js"), "export const revision = 2;\n", "utf8");
    await changed;
    assert.equal(await ssrRevisionAfterUpdate(server), 2);
  } finally {
    await server.close();
    await rm(root, { recursive: true, force: true });
  }
});

test("registers the official Vite DevTools dock, shared state, and command", async () => {
  const plugin = runic({
    contract: { identity: "sample", version: "1", fingerprint: "abc" },
  });
  const docks = [];
  const commands = [];
  const specs = [];
  const view = { stateKey: "runic:test-view" };
  await plugin.devtools.setup({
    rpc: {
      sharedState: {
        get: async (_key, options) => ({
          value: () => options.initialValue,
          mutate: () => undefined,
        }),
      },
    },
    createJsonRenderer: (spec) => {
      specs.push(spec);
      return { view, updateSpec: (next) => specs.push(next) };
    },
    docks: { register: (dock) => docks.push(dock) },
    commands: { register: (command) => commands.push(command) },
  });
  assert.equal(docks[0].id, "runic:overview");
  assert.equal(docks[0].title, "Runic");
  assert.equal(docks[0].type, "json-render");
  assert.equal(docks[0].view, view);
  assert.equal(commands[0].id, "runic:copy-diagnostic-state");
  assert.equal(specs[0].elements.heading.props.text, "Runic");
  assert.equal(specs.length, 1);
});

test("keeps core diagnostics available without the optional dock", async () => {
  const required = runic({ devtools: true });
  assert.throws(() => required.configResolved({ command: "serve", mode: "development", root: process.cwd(), plugins: [] }), /RUNICP007.*DevTools/);
  const plugin = runic();
  const server = await createServer({ configFile: false, logLevel: "silent", plugins: [plugin],
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    assert.doesNotMatch(plugin.load("\0virtual:runic/client"), /@vitejs\/devtools\/client/);
    const address = server.httpServer.address();
    assert.equal((await fetch(`http://127.0.0.1:${address.port}/__runic/state`)).status, 200);
  } finally { await server.close(); }
});

test("official DevTools registers a live Runic dock under Bun", { timeout: 30_000 }, async () => {
  const root = await fixtureRoot();
  const plugin = runic({ devtools: true, contract: { identity: "bun-dock" } });
  let context;
  const setup = plugin.devtools.setup;
  plugin.devtools.setup = async (value) => { context = value; await setup(value); };
  const server = await createServer({ root, configFile: false, logLevel: "silent",
    plugins: [DevTools({ embeddedVisibility: "normal" }), plugin],
    server: { host: "127.0.0.1", port: 0 } });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${serverPort(server)}`;
    const html = await fetch(base).then((response) => response.text());
    const proxy = html.match(/src="([^"]*html-proxy[^"]*)"/);
    const injection = proxy ? await fetch(`${base}${proxy[1]}`).then((response) => response.text()) : html;
    assert.match(injection, /__devtools\/embedded\.js/);
    assert.equal((await fetch(`${base}/__devtools/embedded.js`)).status, 200);
    const dock = context.docks.views.get("runic:overview");
    assert.equal(dock.type, "json-render");
    assert.ok(dock.view);
    plugin.diagnostics.report({ source: "assets", kind: "event", label: "Bun dock updated" });
    const shared = await context.rpc.sharedState.get("runic:state");
    assert.equal(shared.value().timeline.at(-1).label, "Bun dock updated");
    const copied = JSON.parse(await context.commands.execute("runic:copy-diagnostic-state"));
    assert.equal(copied.contract.identity, "bun-dock");
    assert.equal(copied.timeline.at(-1).label, "Bun dock updated");
  } finally { await server.close(); await rm(root, { recursive: true, force: true }); }
});

test("excludes the official DevTools client from production output", async () => {
  const plugin = runic();
  plugin.configResolved({ command: "build", mode: "production", root: process.cwd() });
  assert.doesNotMatch(
    plugin.load("\0virtual:runic/client"),
    /@vitejs\/devtools\/client/,
  );

  const root = await mkdtemp(join(tmpdir(), "runic-vite-production-"));
  try {
    await writeFile(
      join(root, "index.html"),
      '<!doctype html><script type="module" src="/src.js"></script>',
      "utf8",
    );
    await writeFile(join(root, "src.js"), 'import "virtual:runic/client";', "utf8");
    await build({
      root,
      configFile: false,
      logLevel: "silent",
      plugins: [DevTools({ embeddedVisibility: "passive" }), runic()],
      build: { outDir: "dist", minify: false },
    });
    const assets = await readdir(join(root, "dist", "assets"));
    const scripts = await Promise.all(
      assets.filter((file) => file.endsWith(".js"))
        .map((file) => readFile(join(root, "dist", "assets", file), "utf8")),
    );
    assert.doesNotMatch(scripts.join("\n"), /vite-devtools|@vitejs\/devtools/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("fails closed on hostile summaries and bounds a million-key detail object", () => {
  const plugin = runic({ devtools: false });
  let latest;
  plugin.configureServer({
    ws: {
      on: () => undefined,
      send: (message) => { latest = message.data; },
    },
    middlewares: { use: () => undefined },
    httpServer: undefined,
  });
  const detail = Object.create(null);
  detail.nested = { secret: "must-not-escape" };
  detail.authorization = "must-not-escape";
  detail.localPath = "/private/customer/project";
  detail.fileName = "customer.json";
  detail.cwd = "./private/customer";
  detail.message = "vscode://private/customer/project";
  detail.errorMessage = "failed: ../private/customer.json";
  for (let index = 0; index < 1_000_000; index += 1) detail[`key${index}`] = index;
  plugin.diagnostics.report({
    source: "assets",
    id: "Authorization: Bearer must-not-escape",
    timestamp: "file:///private/timestamp",
    kind: "event",
    label: "Asset at /private/customer/project refreshed",
    detail,
  });
  const entry = latest.timeline[0];
  assert.equal(entry.id, "diagnostic-1");
  assert.match(entry.timestamp, /^\d{4}-\d{2}-\d{2}T/);
  assert.equal(entry.label, "[redacted]");
  assert.equal(Object.keys(entry.detail).length, 12);
  assert.equal(entry.detail.authorization, undefined);
  assert.equal(entry.detail.localPath, undefined);
  assert.equal(entry.detail.nested, undefined);
  assert.equal(entry.detail.fileName, undefined);
  assert.equal(entry.detail.cwd, undefined);
  assert.equal(entry.detail.message, "[redacted]");
  assert.equal(entry.detail.errorMessage, "[redacted]");
  assert.ok(JSON.stringify(entry).length <= 2_048);
});

test("accepts view, asset, and translation summaries through one bounded timeline", () => {
  const plugin = runic({ devtools: false, maxTimelineEntries: 2 });
  let latest;
  plugin.configureServer({
    ws: {
      on: () => undefined,
      send: (message) => { latest = message.data; },
    },
    middlewares: { use: () => undefined },
    httpServer: undefined,
  });
  plugin.diagnostics.report({ source: "views", kind: "connection", label: "Connected" });
  plugin.diagnostics.report({ source: "assets", kind: "event", label: "Asset refreshed" });
  plugin.diagnostics.report({
    source: "translations",
    kind: "error",
    label: "Catalog rejected",
    detail: { key: "home.title", authorization: "must-not-escape" },
  });
  assert.deepEqual(
    latest.timeline.map((entry) => [entry.source, entry.id, entry.detail]),
    [
      ["assets", "diagnostic-2", {}],
      ["translations", "diagnostic-3", { key: "home.title" }],
    ],
  );
});

async function fixtureRoot() {
  const root = await mkdtemp(join(tmpdir(), "runic-vite-fixture-"));
  await cp(join(process.cwd(), "test", "fixtures", "vite"), root, { recursive: true });
  const packageDirectory = join(root, "node_modules", "@runic-artifex");
  await mkdir(packageDirectory, { recursive: true });
  await symlink(process.cwd(), join(packageDirectory, "vite-plugin-runic"), "dir");
  return root;
}

function serverPort(server) {
  const address = server.httpServer?.address();
  assert.ok(address && typeof address === "object");
  return address.port;
}

async function executeVirtualReporter(virtualCode) {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const sent = [];
const hot = {
  data: {},
  send(event, data) { sent.push({ event, data }); },
  on() {},
  prune() {},
};
const context = createContext({});
const diagnostics = new SourceTextModule(await readFile("dist/diagnostics.js", "utf8"), {
  context,
  identifier: "diagnostics",
});
const client = new SourceTextModule(await readFile("dist/client.js", "utf8"), {
  context,
  identifier: "client",
  initializeImportMeta(meta) { meta.hot = hot; },
});
const virtual = new SourceTextModule(Buffer.from(process.argv[1], "base64").toString("utf8"), {
  context,
  identifier: "virtual:runic/client",
});
await diagnostics.link(() => { throw new Error("diagnostics has no imports"); });
await client.link((specifier) => specifier === "./diagnostics.js" ? diagnostics : Promise.reject(new Error(specifier)));
await virtual.link((specifier) => specifier.endsWith("/dist/client.js") ? client : Promise.reject(new Error(specifier)));
await virtual.evaluate();
virtual.namespace.reportRunicDiagnostic({
  source: "assets",
  id: "Authorization: Bearer must-not-escape",
  timestamp: "file:///private/timestamp",
  kind: "event",
  label: "Asset ./private/customer/project refreshed",
  detail: {
    status: "ready",
    fileName: "customer.json",
    message: "error: ../private/customer.json",
    nested: { secret: "must-not-escape" },
  },
});
process.stdout.write(JSON.stringify(sent));
`;
  const { stdout } = await execFile(process.execPath, [
    "--experimental-vm-modules",
    "--input-type=module",
    "--eval",
    harness,
    Buffer.from(virtualCode).toString("base64"),
  ], { cwd: process.cwd() });
  return JSON.parse(stdout);
}

async function ssrRevisionAfterUpdate(server) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const revision = (await server.ssrLoadModule("/src/ssr-entry.js")).revision;
    if (revision === 2) return revision;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  return (await server.ssrLoadModule("/src/ssr-entry.js")).revision;
}
