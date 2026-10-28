import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { DevTools } from "@vitejs/devtools";
import { build, createServer } from "vite";
import { runic, runicDevSession, runicDevSessionProxy } from "../dist/index.js";

const execFile = promisify(execFileCallback);

test("exposes a real virtual client and bounded diagnostics endpoint", () => {
  const plugin = runic({ devtools: false });
  plugin.configResolved({ command: "serve", mode: "development", root: process.cwd() });
  assert.equal(plugin.resolveId("virtual:runic/client"), "\0virtual:runic/client");
  assert.equal(plugin.load("\0virtual:runic/client"), 'import "@runic-artifex/vite-plugin-runic/client";');

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
  assert.equal(handlers.has("runic:connection"), true);
  assert.equal(handlers.has("runic:diagnostic"), true);
  assert.equal(handlers.has("runic:state"), false, "the page no longer reports a runtime state");
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

test("builds host scripts from the web root without bundling warnings, also for nested pages", async () => {
  const root = await fixtureRoot();
  const hostScripts = [
    `<script src="webui.js"></script>`,
    `<script src="./runic-desktop-views.js"></script>`,
    `<script src="/runic-cswebui.js"></script>`,
    `<script src="runic-views.js"></script>`,
  ].join("\n");
  const page = (title) => `<!doctype html>
<html><head><title>${title}</title>
${hostScripts}
<script src="unrelated.js"></script>
</head><body><script type="module" src="/src/client-entry.js"></script></body></html>
`;
  await writeFile(join(root, "index.html"), page("Main"), "utf8");
  await mkdir(join(root, "pages", "deep"), { recursive: true });
  await writeFile(join(root, "pages", "settings.html"), page("Settings"), "utf8");
  await writeFile(join(root, "pages", "deep", "about.html"), page("About"), "utf8");
  const warnings = [];
  const logger = {
    hasWarned: false,
    info: () => undefined,
    warn: (message) => warnings.push(message),
    warnOnce: (message) => warnings.push(message),
    error: () => undefined,
    clearScreen: () => undefined,
    hasErrorLogged: () => false,
  };
  try {
    await build({
      root,
      configFile: false,
      customLogger: logger,
      plugins: [runic({ devtools: false, desktop: true })],
      build: {
        outDir: "dist",
        rolldownOptions: {
          input: [join(root, "index.html"), join(root, "pages", "settings.html"), join(root, "pages", "deep", "about.html")],
        },
      },
    });
    // Other classic scripts still warn, so the logger sees the build's warnings.
    assert.ok(warnings.some((message) => /unrelated\.js.*can't be bundled/.test(message)), warnings.join("\n"));
    assert.equal(warnings.some((message) => /(webui|runic-desktop-views|runic-cswebui|runic-views)\.js/.test(message)), false, warnings.join("\n"));

    for (const [file, prefix] of [["index.html", "./"], ["pages/settings.html", "../"], ["pages/deep/about.html", "../../"]]) {
      const html = await readFile(join(root, "dist", file), "utf8");
      for (const script of ["runic-desktop.js", "webui.js", "runic-desktop-views.js", "runic-cswebui.js", "runic-views.js"]) {
        assert.ok(html.replace(/<script\s+/g, "<script ").includes(`<script src="${prefix}${script}"></script>`), `${file} loads ${prefix}${script}:\n${html}`);
      }
      assert.doesNotMatch(html, /vite-ignore/);
      assert.match(html, /<script src="unrelated\.js"><\/script>/);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("serves host scripts from the web root for nested pages", async () => {
  const root = await fixtureRoot();
  await mkdir(join(root, "pages"), { recursive: true });
  await writeFile(join(root, "pages", "settings.html"),
    `<!doctype html><html><head><script src="webui.js"></script><script type="module" src="/src/client-entry.js"></script></head><body></body></html>`,
    "utf8");
  const server = await createServer({
    root,
    configFile: false,
    logLevel: "silent",
    plugins: [runic({ devtools: false, desktop: true })],
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const html = await fetch(`http://127.0.0.1:${serverPort(server)}/pages/settings.html`).then((response) => response.text());
    assert.match(html, /<script src="\.\.\/webui\.js"><\/script>/);
    assert.match(html, /<script src="\.\.\/runic-desktop\.js"><\/script>/);
  } finally {
    await server.close();
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
    // The development client has no exports; the virtual module only loads it.
    assert.match(await virtual.text(), /^import ".*vite-plugin-runic\/(dist\/)?client(\.js)?";?\s*$/m);

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

test("/__runic/state confirms the dotnet runic dev session", async () => {
  const previous = process.env.RUNIC_APPLICATION_DEV_SESSION;
  const session = "0123456789abcdef".repeat(4);
  try {
    for (const [value, expected] of [[session, session], [undefined, null], ["not a session value", null], ["ABCDEF".repeat(8), null]]) {
      if (value === undefined) delete process.env.RUNIC_APPLICATION_DEV_SESSION;
      else process.env.RUNIC_APPLICATION_DEV_SESSION = value;
      const server = await createServer({ configFile: false, logLevel: "silent", plugins: [runic({ devtools: false })],
        server: { host: "127.0.0.1", port: 0, strictPort: false } });
      try {
        await server.listen();
        const base = `http://127.0.0.1:${serverPort(server)}`;
        const state = await fetch(`${base}/__runic/state`);
        assert.equal(state.status, 200);
        assert.equal(state.headers.get("x-runic-dev-session"), expected, String(value));
        await state.arrayBuffer();
        // Only the state endpoint answers with it, not the pages.
        const page = await fetch(`${base}/`);
        assert.equal(page.headers.get("x-runic-dev-session"), null);
        await page.arrayBuffer();
      } finally { await server.close(); }
    }
  } finally {
    if (previous === undefined) delete process.env.RUNIC_APPLICATION_DEV_SESSION;
    else process.env.RUNIC_APPLICATION_DEV_SESSION = previous;
  }
});

test("runicDevSessionProxy and runicDevSession confirm the session without runic()", async () => {
  const previous = process.env.RUNIC_APPLICATION_DEV_SESSION;
  const session = "fedcba9876543210".repeat(4);
  try {
    delete process.env.RUNIC_APPLICATION_DEV_SESSION;
    assert.deepEqual(runicDevSessionProxy(), {}, "Without dotnet runic dev the proxy configuration is unchanged.");
    process.env.RUNIC_APPLICATION_DEV_SESSION = "not a session value";
    assert.deepEqual(runicDevSessionProxy(), {});
    process.env.RUNIC_APPLICATION_DEV_SESSION = session;
    // The Angular CLI passes its proxyConfig to Vite as server.proxy and adds no Vite plugins. SvelteKit takes
    // runicDevSession(), which has no transformIndexHtml hook for SvelteKit to warn about.
    const setups = [{ plugins: [], proxy: runicDevSessionProxy() }, { plugins: [runicDevSession()] }];
    assert.equal("transformIndexHtml" in runicDevSession(), false);
    for (const { plugins, proxy } of setups) {
      const server = await createServer({ configFile: false, logLevel: "silent", plugins,
        server: { host: "127.0.0.1", port: 0, strictPort: false, proxy } });
      try {
        await server.listen();
        const base = `http://127.0.0.1:${serverPort(server)}`;
        for (const path of ["/__runic/state", "/__runic/state?probe=1"]) {
          const state = await fetch(`${base}${path}`);
          assert.equal(state.status, 204, path);
          assert.equal(state.headers.get("x-runic-dev-session"), session, path);
          assert.equal(state.headers.get("cache-control"), "no-store", path);
          await state.arrayBuffer();
        }
        for (const path of ["/", "/__runic/statex"]) {
          const other = await fetch(`${base}${path}`);
          assert.equal(other.headers.get("x-runic-dev-session"), null, path);
          await other.arrayBuffer();
        }
      } finally { await server.close(); }
    }
  } finally {
    if (previous === undefined) delete process.env.RUNIC_APPLICATION_DEV_SESSION;
    else process.env.RUNIC_APPLICATION_DEV_SESSION = previous;
  }
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
    plugin.api.reportDiagnostic({ source: "assets", kind: "event", label: "Bun dock updated" });
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
  plugin.api.reportDiagnostic({
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
  plugin.api.reportDiagnostic({ source: "views", kind: "connection", label: "Connected" });
  plugin.api.reportDiagnostic({ source: "assets", kind: "event", label: "Asset refreshed" });
  plugin.api.reportDiagnostic({
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

test("keeps a bounded, credential-redacted failure only on error entries", () => {
  const plugin = runic({ devtools: false });
  let latest;
  plugin.configureServer({
    ws: { on: () => undefined, send: (message) => { latest = message.data; } },
    middlewares: { use: () => undefined },
    httpServer: undefined,
  });
  const stack = [
    "System.InvalidOperationException: Disk full at /home/dev/notes.db",
    ...Array.from({ length: 200 }, (_, index) => `   at Notes.Save${index}() in /home/dev/Notes/Editor.cs:line ${index}`),
  ].join("\n");
  plugin.api.reportDiagnostic({
    source: "views",
    kind: "error",
    label: "editorSave failed",
    failure: { type: "System.InvalidOperationException", message: "Disk full. Authorization: Bearer must-not-escape", stack },
  });
  plugin.api.reportDiagnostic({
    source: "views",
    kind: "operation",
    label: "Save timed-out",
    failure: { type: "Ignored", message: "Not an error entry." },
  });
  const [error, operation] = latest.timeline;
  assert.equal(error.failure.type, "System.InvalidOperationException");
  assert.equal(error.failure.message, "Disk full. Authorization: [redacted]");
  assert.match(error.failure.stack, /^System\.InvalidOperationException: Disk full at \/home\/dev\/notes\.db\n   at Notes\.Save0\(\) in \/home\/dev\/Notes\/Editor\.cs:line 0/);
  assert.ok(error.failure.stack.length <= 8_000);
  assert.ok(error.failure.stack.split("\n").length <= 80);
  assert.equal(operation.failure, undefined);
});

test("redacts credential values in failures and leaves failures out of the state endpoint", () => {
  const plugin = runic({ devtools: false });
  let latest;
  const middleware = [];
  plugin.configureServer({
    ws: { on: () => undefined, send: (message) => { latest = message.data; } },
    middlewares: { use: (path, handler) => middleware.push([path, handler]) },
    httpServer: undefined,
  });
  const secrets = [
    ["access_token=abc123", "access_token=[redacted]"],
    ["client_secret: s3cr3t", "client_secret: [redacted]"],
    ["refresh_token=r1", "refresh_token=[redacted]"],
    ["id_token=eyJhbGci", "id_token=[redacted]"],
    [String.raw`{"password":"hunter2","user":"ann"}`, String.raw`{"password":"[redacted]","user":"ann"}`],
    ["https://user:pa55@db.example/x", "https://user:[redacted]@db.example/x"],
    ["Server=db;Pwd=hunter2;", "Server=db;Pwd=[redacted];"],
    ["AccountKey=a+b/c==;EndpointSuffix=core", "AccountKey=[redacted];EndpointSuffix=core"],
    ["Authorization: Bearer tok", "Authorization: [redacted]"],
    ["sent Bearer tok", "sent Bearer [redacted]"],
  ];
  for (const [input, expected] of secrets) {
    plugin.api.reportDiagnostic({ source: "views", kind: "error", label: "editorSave failed",
      failure: { type: "System.Exception", message: input, stack: `System.Exception: ${input}\n   at Save()` } });
    const failure = latest.timeline.at(-1).failure;
    assert.equal(failure.message, expected, input);
    assert.equal(failure.stack.split("\n")[0], `System.Exception: ${expected}`, input);
  }
  let body = "";
  middleware[0][1]({}, { setHeader: () => undefined, end: (text) => { body = text; } });
  const state = JSON.parse(body);
  assert.equal(state.timeline.length, secrets.length);
  assert.ok(state.timeline.every((entry) => entry.failure === undefined), "The state endpoint exposed a failure.");
  assert.ok(latest.timeline.every((entry) => entry.failure !== undefined), "The dock state lost its failures.");
});

test("injects the Views diagnostics client only while serving with DevTools enabled", async () => {
  const root = await fixtureRoot();
  for (const [devtools, expected] of [[undefined, true], [false, false]]) {
    const server = await createServer({
      root,
      configFile: false,
      logLevel: "silent",
      plugins: [runic(devtools === undefined ? {} : { devtools })],
      server: { host: "127.0.0.1", port: 0, strictPort: false },
    });
    try {
      await server.listen();
      const html = await fetch(`http://127.0.0.1:${serverPort(server)}/`).then((response) => response.text());
      assert.equal(html.includes(`<script type="module" src="/@id/virtual:runic/client"></script>`), expected, html);
    } finally {
      await server.close();
    }
  }
  await rm(root, { recursive: true, force: true });
  const plugin = runic();
  plugin.configResolved({ command: "build", mode: "production", root: process.cwd(), plugins: [] });
  assert.deepEqual(plugin.transformIndexHtml.handler("", { path: "/index.html", filename: "index.html" }), []);
});

test("forwards a Views BridgeError with its .NET detail to the timeline", async () => {
  const sent = await executeViewsForwarding();
  assert.equal(sent.length, 3);
  assert.deepEqual(sent[0], {
    event: "runic:diagnostic",
    data: {
      source: "views",
      kind: "error",
      label: "editorSave failed",
      detail: { code: "failed", route: "editorSave" },
      failure: {
        type: "System.InvalidOperationException",
        message: "Disk full.",
        stack: "System.InvalidOperationException: Disk full.\n   at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12",
      },
    },
  });
  assert.equal(sent[1].data.label, "Bridge unavailable");
  assert.match(sent[1].data.failure.message, /window\.__runicBridge/);
  assert.match(sent[1].data.failure.message, /@runic-artifex\/views\/mock/);
  // D40 a: the page's connection state reaches the dock without application code.
  assert.deepEqual(sent[2], { event: "runic:connection", data: { state: "connected" } });
});

// I-042: the client asks @runic-artifex/views for the connection from page load, before a View
// connects, whichever of the two loads first.
for (const order of ["client-first", "views-first"]) {
  test(`forwards the connection state without a View connecting (${order})`, async () => {
    assert.deepEqual(await executeConnectionForwarding(order), [{ event: "runic:connection", data: { state: "connected" } }]);
  });
}

test("the dock shows the connection state the page forwards", () => {
  const plugin = runic({ devtools: false });
  const handlers = new Map();
  let latest;
  plugin.configureServer({
    ws: { on: (event, handler) => handlers.set(event, handler), send: (message) => { latest = message.data; } },
    middlewares: { use: () => undefined },
    httpServer: undefined,
  });
  handlers.get("runic:connection")({ state: "reconnecting" });
  assert.deepEqual(latest.connection, { state: "reconnecting" });
  handlers.get("runic:connection")({ state: "closed" });
  assert.deepEqual(latest.connection, { state: "unknown" }, "an unknown state is not shown as a Views state");
  assert.equal("operations" in latest, false);
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

// Runs the built Views runtime and this package's client in one context, as
// on a page, without either package depending on the other.
async function executeViewsForwarding() {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const sent = [];
const hot = { data: {}, send(event, data) { sent.push({ event, data }); }, on() {}, prune() {} };
const context = createContext({ setTimeout, clearTimeout });
// A host Bridge that is connected, so the Views runtime publishes "connected" on its channel.
context.__runicBridge = {
  isConnected: () => true,
  call: () => Promise.reject(new Error("not used")),
  onConnectionChange(listener) { listener(true); return () => {}; },
};
const modules = new Map();
async function load(path, identifier, meta) {
  const module = new SourceTextModule(await readFile(path, "utf8"), {
    context, identifier, ...(meta ? { initializeImportMeta: meta } : {}),
  });
  modules.set(identifier, module);
  return module;
}
const views = "../views/dist/";
const errors = await load(views + "errors.js", "views/errors");
const diagnosticsModule = await load(views + "diagnostics.js", "views/diagnostics");
const lifetime = await load(views + "lifetime.js", "views/lifetime");
const version = await load(views + "version.js", "views/version");
const transport = await load(views + "transport.js", "views/transport");
const connectionState = await load(views + "connection-state.js", "views/connection-state");
const pluginDiagnostics = await load("dist/diagnostics.js", "plugin/diagnostics");
const client = await load("dist/client.js", "plugin/client", (meta) => { meta.hot = hot; });
const link = (from) => (specifier) => {
  const name = from + "/" + specifier.replace("./", "").replace(".js", "");
  const module = modules.get(name);
  return module ?? Promise.reject(new Error(specifier));
};
await errors.link(link("views"));
await diagnosticsModule.link(link("views"));
await lifetime.link(link("views"));
await version.link(link("views"));
await transport.link(link("views"));
await connectionState.link(link("views"));
await pluginDiagnostics.link(link("plugin"));
await client.link(link("plugin"));
await client.evaluate();
await transport.evaluate();
const { BridgeError } = errors.namespace;
diagnosticsModule.namespace.emitErrorDiagnostic(new BridgeError("failed", "Save failed.", {
  route: "editorSave",
  detail: {
    type: "System.InvalidOperationException",
    message: "Disk full.",
    stack: "System.InvalidOperationException: Disk full.\n   at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12",
  },
}));
delete context.__runicBridge;
await transport.namespace.waitForBridge({ timeout: 0 }).catch(() => undefined);
context.__runicBridge = {
  isConnected: () => true,
  call: () => Promise.reject(new Error("not used")),
  onConnectionChange(listener) { listener(true); return () => {}; },
};
await connectionState.evaluate();
connectionState.namespace.publishConnectionState();
process.stdout.write(JSON.stringify(sent));
`;
  const { stdout } = await execFile(process.execPath, [
    "--experimental-vm-modules", "--input-type=module", "--eval", harness,
  ], { cwd: process.cwd() });
  return JSON.parse(stdout);
}

// Loads the built Views connection module and this package's client in one context in the given
// order, with a connected host Bridge and no View, and returns what the client sent.
async function executeConnectionForwarding(order) {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const sent = [];
const hot = { data: {}, send(event, data) { sent.push({ event, data }); }, on() {}, prune() {} };
const context = createContext({ setTimeout, clearTimeout });
context.__runicBridge = {
  isConnected: () => true,
  call: () => Promise.reject(new Error("not used")),
  onConnectionChange(listener) { listener(true); return () => {}; },
};
const modules = new Map();
async function load(path, identifier, meta) {
  const module = new SourceTextModule(await readFile(path, "utf8"), {
    context, identifier, ...(meta ? { initializeImportMeta: meta } : {}),
  });
  modules.set(identifier, module);
  return module;
}
const views = "../views/dist/";
const loaded = [
  await load(views + "errors.js", "views/errors"),
  await load(views + "diagnostics.js", "views/diagnostics"),
  await load(views + "lifetime.js", "views/lifetime"),
  await load(views + "version.js", "views/version"),
  await load(views + "transport.js", "views/transport"),
];
const connectionState = await load(views + "connection-state.js", "views/connection-state");
const pluginDiagnostics = await load("dist/diagnostics.js", "plugin/diagnostics");
const client = await load("dist/client.js", "plugin/client", (meta) => { meta.hot = hot; });
const link = (from) => (specifier) => {
  const module = modules.get(from + "/" + specifier.replace("./", "").replace(".js", ""));
  return module ?? Promise.reject(new Error(specifier));
};
for (const module of [...loaded, connectionState]) await module.link(link("views"));
await pluginDiagnostics.link(link("plugin"));
await client.link(link("plugin"));
const first = ${JSON.stringify(order)} === "client-first" ? client : connectionState;
await first.evaluate();
await (first === client ? connectionState : client).evaluate();
process.stdout.write(JSON.stringify(sent));
`;
  const { stdout } = await execFile(process.execPath, [
    "--experimental-vm-modules", "--input-type=module", "--eval", harness,
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
