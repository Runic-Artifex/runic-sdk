// The development source map (#951): `/__runic/sources` serves the Bridge generator's sources.json in `vite dev`,
// `/__runic/open` opens only a location the map lists, or the source of a Runic Translations message that the
// Translations dev server names, and `vite build` output does not depend on the map.
import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, readFile, readdir, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import test from "node:test";
import { build, createServer } from "vite";
import { runic } from "../dist/index.js";
// An editor that records its arguments, as LAUNCH_EDITOR, and the wait for a complete record (#1010).
import { readRecord, recordingEditor } from "../../../../tests/support/recording-editor.mjs";
import { answerClientRequest, findOpenLocation, parseSourceMap } from "../dist/sources.js";

const environment = ["RUNIC_APPLICATION_DEV_SOURCE_MAP", "RUNIC_APPLICATION_DEV_PROJECT", "LAUNCH_EDITOR", "RUNIC_TEST_EDITOR_RECORD"];

// A project as `dotnet build` leaves it: CounterViewModel.cs, obj/Debug/net10.0/runic-bridge/sources.json and the
// Vite root in Frontend/.
async function project() {
  const directory = await mkdtemp(join(tmpdir(), "runic-vite-sources-"));
  const frontend = join(directory, "Frontend");
  await cp(join(process.cwd(), "test", "fixtures", "vite"), frontend, { recursive: true });
  await mkdir(join(frontend, "node_modules", "@runic-artifex"), { recursive: true });
  await symlink(process.cwd(), join(frontend, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
  const source = join(directory, "CounterViewModel.cs");
  await writeFile(source, "namespace Demo;\n\npublic sealed partial class CounterViewModel\n{\n    [RelayCommand]\n    private void Increment() => Count++;\n}\n");
  const map = sourceMap(source);
  const mapPath = join(directory, "obj", "Debug", "net10.0", "runic-bridge", "sources.json");
  await mkdir(join(mapPath, ".."), { recursive: true });
  await writeFile(mapPath, JSON.stringify(map, null, 2));
  return { directory, frontend, source, map, mapPath };
}

function sourceMap(source, line = 6) {
  return {
    format: "runic-bridge-sources/1",
    viewModels: [{
      id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:00", pageKinds: ["counter"],
      symbol: "Demo.CounterViewModel", location: { file: source, line: 3, column: 29 },
    }],
    members: [
      {
        id: "Demo.CounterViewModel/command/increment", viewModel: "Demo.CounterViewModel", member: "increment", kind: "command",
        symbol: "Demo.CounterViewModel.Increment()", property: "Demo.CounterViewModel.IncrementCommand",
        location: { file: source, line, column: 5 },
      },
      // Built without a PDB: no location, nothing to open.
      { id: "Demo.CounterViewModel/state/count", viewModel: "Demo.CounterViewModel", member: "count", kind: "state", symbol: "Demo.CounterViewModel.Count" },
    ],
  };
}

async function withEnvironment(values, run) {
  const previous = Object.fromEntries(environment.map((name) => [name, process.env[name]]));
  for (const name of environment) delete process.env[name];
  Object.assign(process.env, values);
  try { return await run(); }
  finally {
    for (const name of environment) {
      if (previous[name] === undefined) delete process.env[name];
      else process.env[name] = previous[name];
    }
  }
}

async function withServer(root, options, run, plugins = []) {
  const server = await createServer({ root, configFile: false, logLevel: "silent", plugins: [...plugins, runic({ devtools: false, ...options })],
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    const address = server.httpServer.address();
    await run(`http://127.0.0.1:${address.port}`);
  } finally { await server.close(); }
}

const exists = (path) => stat(path).then(() => true, () => false);

test("parses only well-formed runic-bridge-sources/1 entries and finds only locations in listed files", () => {
  assert.equal(parseSourceMap({ format: "other", viewModels: [], members: [] }), undefined);
  assert.equal(parseSourceMap(null), undefined);
  const map = parseSourceMap({
    format: "runic-bridge-sources/1",
    viewModels: [{ id: "A", name: "A", route: "a", contract: "A:0", pageKinds: ["a"], symbol: "A", location: { file: "relative.cs", line: 1, column: 1 } }],
    members: [
      { id: "A/command/go", viewModel: "A", member: "go", kind: "command", symbol: "A.Go()", location: { file: "/src/A.cs", line: 4, column: 9 } },
      { id: "A/state/x", viewModel: "A", member: "x", kind: "state", symbol: "A.X", location: { file: "/src/A.cs", line: 0, column: 1 } },
      { id: "A/odd/y", viewModel: "A", member: "y", kind: "odd", symbol: "A.Y" },
      "not an entry",
    ],
  });
  assert.deepEqual(map.members.map((entry) => entry.id), ["A/command/go", "A/state/x"], "an unknown kind is dropped");
  assert.equal(map.viewModels[0].location, undefined, "a relative path is no location");
  assert.equal(map.members[1].location, undefined, "line 0 is no location");
  assert.deepEqual(findOpenLocation(map, { id: "A/command/go" }), { file: "/src/A.cs", line: 4, column: 9 });
  assert.deepEqual(findOpenLocation(map, { file: "/src/A.cs", line: 4, column: 9 }), { file: "/src/A.cs", line: 4, column: 9 });
  // A .NET stack frame in a listed file: another line, the column defaulting to 1.
  assert.deepEqual(findOpenLocation(map, { file: "/src/A.cs", line: 17 }), { file: "/src/A.cs", line: 17, column: 1 });
  for (const refused of [
    { id: "A/state/x" }, { id: "B" }, { file: "/etc/passwd", line: 1, column: 1 }, { file: "/src/A.cs", line: 0 },
    { file: "/src/A.cs", line: 1.5 }, { file: "/src/A.cs", line: 4, column: -1 }, { file: "/src/A.cs", line: "4" },
    { file: "/src/../src/A.cs", line: 4, column: 9 }, { file: "/src/A.cs:4:9" }, [], "A/command/go", null,
  ]) {
    assert.equal(findOpenLocation(map, refused), undefined, JSON.stringify(refused));
  }
});

test("vite dev serves the map it finds under the project's obj, and reads it again after a rebuild", async () => {
  const fixture = await project();
  try {
    await withEnvironment({}, () => withServer(fixture.frontend, {}, async (base) => {
      const response = await fetch(`${base}/__runic/sources`);
      assert.equal(response.status, 200);
      assert.equal(response.headers.get("cache-control"), "no-store");
      assert.deepEqual(await response.json(), fixture.map);
      // A cross-origin page cannot read it.
      for (const headers of [{ "Sec-Fetch-Site": "cross-site" }, { "Sec-Fetch-Site": "same-site" }, { Origin: "http://evil.test" }]) {
        const refused = await fetch(`${base}/__runic/sources`, { headers });
        assert.equal(refused.status, 403, JSON.stringify(headers));
        await refused.arrayBuffer();
      }
      const posted = await fetch(`${base}/__runic/sources`, { method: "POST", headers: { Origin: base } });
      assert.equal(posted.status, 405);
      await posted.arrayBuffer();
      // A rebuild moved the handler.
      await writeFile(fixture.mapPath, JSON.stringify(sourceMap(fixture.source, 7)));
      const later = new Date(Date.now() + 5000);
      await utimes(fixture.mapPath, later, later);
      const rebuilt = await fetch(`${base}/__runic/sources`).then((answer) => answer.json());
      assert.equal(rebuilt.members[0].location.line, 7);
    }));
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

test("vite dev takes the map dotnet runic dev or the sourceMap option names, and sourceMap: false serves none", async () => {
  const fixture = await project();
  const elsewhere = join(fixture.directory, "elsewhere.json");
  try {
    const other = { ...fixture.map, viewModels: [] };
    await writeFile(elsewhere, JSON.stringify(other));
    await withEnvironment({ RUNIC_APPLICATION_DEV_SOURCE_MAP: elsewhere }, () => withServer(fixture.frontend, {}, async (base) => {
      assert.deepEqual(await fetch(`${base}/__runic/sources`).then((answer) => answer.json()), other);
    }));
    // A passed path that does not exist (a build property moved obj) falls back to the search.
    await withEnvironment({ RUNIC_APPLICATION_DEV_SOURCE_MAP: join(fixture.directory, "missing.json"),
      RUNIC_APPLICATION_DEV_PROJECT: join(fixture.directory, "Demo.csproj") },
    () => withServer(fixture.frontend, {}, async (base) => {
      assert.deepEqual(await fetch(`${base}/__runic/sources`).then((answer) => answer.json()), fixture.map);
    }));
    // No map anywhere: 404.
    await withEnvironment({}, () => withServer(join(fixture.directory, "Frontend", "src"), { sourceMap: "missing.json" }, async (base) => {
      const response = await fetch(`${base}/__runic/sources`);
      assert.equal(response.status, 404);
      await response.arrayBuffer();
    }));
    await withEnvironment({}, () => withServer(fixture.frontend, { sourceMap: "../elsewhere.json" }, async (base) => {
      assert.deepEqual(await fetch(`${base}/__runic/sources`).then((answer) => answer.json()), other);
    }));
    await withEnvironment({}, () => withServer(fixture.frontend, { sourceMap: false }, async (base) => {
      const response = await fetch(`${base}/__runic/sources`, { headers: { Accept: "application/json" } });
      assert.notEqual(response.headers.get("content-type"), "application/json; charset=utf-8");
      await response.arrayBuffer();
    }));
    assert.throws(() => runic({ sourceMap: "" }), /sourceMap must be a path or false/u);
    assert.throws(() => runic({ sourceMap: true }), /sourceMap must be a path or false/u);
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

test("/__runic/open refuses everything but a location in a listed file or a served frontend file from the server's own origin", async () => {
  const fixture = await project();
  const { editor, record } = await recordingEditor(fixture.directory);
  // A file of the Vite root that Vite's server.fs.deny keeps from the page.
  await writeFile(join(fixture.frontend, ".env"), "SECRET=1\n");
  try {
    await withEnvironment({ LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record }, () => withServer(fixture.frontend, {}, async (base) => {
      const open = (body, headers = {}, method = "POST") => fetch(`${base}/__runic/open`, {
        method, headers: { "Content-Type": "application/json", Origin: base, ...headers },
        ...(method === "GET" ? {} : { body: typeof body === "string" ? body : JSON.stringify(body) }),
      }).then(async (response) => { await response.arrayBuffer(); return response.status; });
      const location = fixture.map.members[0].location;
      assert.equal(await open(undefined, {}, "GET"), 405);
      assert.equal(await open({ id: fixture.map.members[0].id }, { Origin: "http://evil.test" }), 403, "another origin");
      assert.equal(await open({ id: fixture.map.members[0].id }, { Origin: "" }), 403, "no origin");
      assert.equal(await open({ id: fixture.map.members[0].id }, { "Sec-Fetch-Site": "same-site" }), 403, "a same-site page");
      assert.equal(await open({ id: fixture.map.members[0].id }, { "Content-Type": "text/plain" }), 415, "a simple request");
      assert.equal(await open(JSON.stringify({ id: "x".repeat(5000) })), 413);
      assert.equal(await open("{"), 400);
      assert.equal(await open({ id: "Demo.CounterViewModel/command/missing" }), 404, "an unknown id");
      assert.equal(await open({ id: "Demo.CounterViewModel/state/count" }), 404, "an entry without a location");
      assert.equal(await open({ file: join(fixture.directory, "record-editor.sh"), line: 1, column: 1 }), 404, "a file the map does not list");
      assert.equal(await open({ file: "/etc/passwd", line: 1, column: 1 }), 404);
      assert.equal(await open({ file: `${location.file}:${location.line}:${location.column}` }), 404);
      // Frontend files (Runic Inspect's component links): only what Vite serves, by a normalized absolute path.
      assert.equal(await open({ file: join(fixture.frontend, ".env"), line: 1 }), 404, "a file server.fs.deny keeps from the page");
      assert.equal(await open({ file: `${fixture.frontend}/src/../index.html`, line: 1 }), 404, "a path that is not normalized");
      assert.equal(await open({ file: "index.html", line: 1 }), 404, "a relative path");
      assert.equal(await open({ file: join(fixture.frontend, "missing.ts"), line: 1 }), 404, "a file that does not exist");
      assert.equal(await exists(record), false, "no refused request launched the editor");

      if (process.platform !== "win32") {
        assert.equal(await open({ id: fixture.map.members[0].id }), 200);
        assert.deepEqual(await readRecord(record), [location.file, String(location.line), String(location.column)]);
        assert.equal(await open(location), 200, "the same location by file, line and column");
        assert.deepEqual(await readRecord(record), [location.file, String(location.line), String(location.column)]);
        assert.equal(await open({ file: location.file, line: 2 }), 200, "another line of a listed file, as a stack frame names it");
        assert.deepEqual(await readRecord(record), [location.file, "2", "1"]);
        const page = join(fixture.frontend, "index.html");
        assert.equal(await open({ file: page, line: 3, column: 2 }), 200, "a frontend file Vite serves");
        assert.deepEqual(await readRecord(record), [page, "3", "2"]);
      }
      // A listed file that a later edit removed opens nothing.
      await rm(fixture.source);
      assert.equal(await open({ id: fixture.map.members[0].id }), 404);
      assert.equal(await exists(record), false);
    }));
    await withEnvironment({ LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record }, () => withServer(fixture.frontend, { sourceMap: false }, async (base) => {
      const response = await fetch(`${base}/__runic/open`, {
        method: "POST", headers: { "Content-Type": "application/json", Origin: base }, body: JSON.stringify({ id: fixture.map.members[0].id }),
      });
      await response.arrayBuffer();
      assert.equal(response.status, 404);
      assert.equal(await exists(record), false, "sourceMap: false opens no map entry");
      if (process.platform !== "win32") {
        // It still opens a frontend file Vite serves, such as Runic Inspect's component links.
        const page = join(fixture.frontend, "index.html");
        const frontend = await fetch(`${base}/__runic/open`, {
          method: "POST", headers: { "Content-Type": "application/json", Origin: base }, body: JSON.stringify({ file: page, line: 1 }),
        });
        await frontend.arrayBuffer();
        assert.equal(frontend.status, 200);
        assert.deepEqual(await readRecord(record), [page, "1", "1"]);
      }
    }));
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

// The dock's open buttons call the runic:open-in-editor DevTools action, which takes the same decision as /__runic/open
// and launches the editor itself: a location in a file the map lists opens, anything else is refused and launches nothing.
test("the dock's open action opens only a location in a file the map lists", { skip: process.platform === "win32" }, async () => {
  const fixture = await project();
  const { editor, record } = await recordingEditor(fixture.directory);
  try {
    await withEnvironment({ LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record }, async () => {
      for (const sourceMap of [undefined, false]) {
        const plugin = runic(sourceMap === undefined ? {} : { sourceMap });
        const actions = new Map();
        const server = await createServer({ root: fixture.frontend, configFile: false, logLevel: "silent", plugins: [plugin],
          server: { host: "127.0.0.1", port: 0, strictPort: false } });
        try {
          await plugin.devtools.setup({
            rpc: {
              sharedState: { get: async (_key, options) => ({ value: () => options.initialValue, mutate: () => undefined }) },
              register: (definition) => actions.set(definition.name, definition.setup({}).handler),
            },
            createJsonRenderer: () => ({ view: {}, updateSpec: () => undefined }),
            docks: { register: () => undefined },
            commands: { register: () => undefined },
          });
          const open = actions.get("runic:open-in-editor");
          const location = fixture.map.members[0].location;
          for (const refused of [
            { file: join(fixture.directory, "record-editor.sh"), line: 1, column: 1 }, { file: "/etc/passwd", line: 1 },
            { id: "Demo.CounterViewModel/state/count" }, { id: "Demo.CounterViewModel/command/missing" },
            { file: location.file, line: 0 }, "Demo.CounterViewModel/command/increment", null,
            // A frontend file: /__runic/open opens it for Runic Inspect, the dock links only the map's files.
            { file: join(fixture.frontend, "index.html"), line: 1 },
            ...(sourceMap === false ? [{ id: fixture.map.members[0].id }, { file: location.file, line: 2 }] : []),
          ]) {
            await assert.rejects(open(refused), /lists no such location/u, JSON.stringify(refused));
          }
          await new Promise((resolve) => setTimeout(resolve, 200));
          assert.equal(await exists(record), false, "a refused location launched the editor");
          if (sourceMap === false) continue;
          await open({ id: fixture.map.members[0].id });
          assert.deepEqual(await readRecord(record), [location.file, String(location.line), String(location.column)]);
          await open({ file: location.file, line: 2 });
          assert.deepEqual(await readRecord(record), [location.file, "2", "1"]);
        } finally { await server.close(); }
      }
    });
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

// Runic Inspect's requests over Vite's HMR connection (#1012), which under dotnet runic dev reach the Vite server from the
// .NET host's page: the same answers as /__runic/sources, /__runic/handlers, the Translations message API and
// /__runic/open, the same decision for what opens, and like those endpoints only for a connection from this machine.
test("requests over Vite's connection answer like the HTTP endpoints and open only what /__runic/open opens", async () => {
  const fixture = await project();
  try {
    const map = parseSourceMap(fixture.map);
    const location = fixture.map.members[0].location;
    const page = join(fixture.frontend, "index.html");
    const launched = [];
    const ask = (message, { read = () => map, local = true } = {}) => answerClientRequest(message, {
      read, local, launch: (target) => launched.push(target),
      resolution: { frontendFile: (file) => file.startsWith(`${fixture.frontend}/`) },
      analyze: async (file) => file === "src/App.tsx" ? { format: "runic-handlers/1", bindings: [] } : undefined,
      message: async (reference) => reference.key === "heading" ? { message: { catalog: reference.catalog, key: reference.key, locales: [] } } : undefined,
    });
    assert.deepEqual(await ask({ id: 1, kind: "sources" }), { id: 1, status: 200, body: map });
    assert.deepEqual(await ask({ id: 2, kind: "sources" }, { read: () => undefined }), { id: 2, status: 404 }, "no map (sourceMap: false)");
    assert.deepEqual(await ask({ id: 3, kind: "open", target: { id: fixture.map.members[0].id } }), { id: 3, status: 200 });
    assert.deepEqual(await ask({ id: 4, kind: "open", target: { file: page, line: 2 } }), { id: 4, status: 200 }, "a served frontend file");
    assert.deepEqual(launched, [location, { file: page, line: 2, column: 1 }]);
    launched.length = 0;
    assert.deepEqual(await ask({ id: 7, kind: "handlers", file: "src/App.tsx" }), { id: 7, status: 200, body: { format: "runic-handlers/1", bindings: [] } });
    assert.deepEqual(await ask({ id: 8, kind: "handlers", file: "src/Missing.tsx" }), { id: 8, status: 404 });
    assert.deepEqual(await ask({ id: 9, kind: "handlers" }), { id: 9, status: 400 });
    assert.deepEqual(await ask({ id: 10, kind: "message", target: { catalog: "app", key: "heading" } }),
      { id: 10, status: 200, body: { message: { catalog: "app", key: "heading", locales: [] } } });
    assert.deepEqual(await ask({ id: 11, kind: "message", target: { catalog: "app", key: "../heading" } }), { id: 11, status: 400 });
    assert.deepEqual(await ask({ id: 12, kind: "open", target: { message: "app:heading" } }), { id: 12, status: 400 }, "a malformed message reference");
    for (const target of [
      { file: join(fixture.directory, "record-editor.sh"), line: 1 }, { file: "/etc/passwd", line: 1 },
      { id: "Demo.CounterViewModel/state/count" }, { file: `${fixture.frontend}/src/../index.html`, line: 1 },
      { file: join(fixture.frontend, "missing.ts"), line: 1 }, { file: location.file, line: 0 }, "index.html", null, undefined,
      // A message the resolution cannot locate (no Translations dev server here) opens nothing.
      { message: { catalog: "app", key: "heading", locale: "en" } },
    ]) {
      assert.equal((await ask({ id: 5, kind: "open", target }))?.status, 404, JSON.stringify(target));
    }
    // A connection from another machine gets nothing, whatever it asks: the endpoints are loopback-only.
    for (const kind of ["sources", "handlers", "message", "open"]) {
      assert.deepEqual(await ask({ id: 13, kind, file: "src/App.tsx", target: { id: fixture.map.members[0].id } }, { local: false }), { id: 13, status: 403 }, kind);
    }
    assert.deepEqual(launched, [], "a refused request launched the editor");
    assert.deepEqual(await ask({ id: 6, kind: "delete", target: { id: fixture.map.members[0].id } }), { id: 6, status: 400 });
    for (const malformed of [null, [], "sources", { kind: "sources" }, { id: -1, kind: "sources" }, { id: 1.5, kind: "sources" }, { id: "1", kind: "open" }]) {
      assert.equal(await ask(malformed), undefined, JSON.stringify(malformed));
    }
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

// The Translations dev server's message API as vite-plugin-runic-translations 0.12 answers it (runic-translations-sdk#182),
// recording each request's client address.
function translationsApi(sources, requests) {
  return {
    name: "translations-message-api",
    configureServer(server) {
      server.middlewares.use("/__runic-translations/message", (request, response) => {
        const url = new URL(request.url, "http://localhost");
        const key = url.searchParams.get("key"), catalog = url.searchParams.get("catalog");
        requests.push({ key, catalog, address: request.socket.remoteAddress, method: request.method });
        const source = sources[`${catalog}:${key}`];
        response.setHeader("Content-Type", "application/json");
        if (source === undefined) {
          response.statusCode = 404;
          response.end(JSON.stringify({ error: "not-found", detail: "Unknown message." }));
          return;
        }
        response.end(JSON.stringify({ message: { catalog, key, baseLocale: "en", locales: [
          { locale: "en", origin: "catalog", text: "Counter", source: { path: source, line: 2, column: 1 } },
          { locale: "de", origin: "missing", text: null, source: null },
        ] } }));
      });
    },
  };
}

test("/__runic/open opens a Translations message's source that the Translations dev server names, never a path from the page", async () => {
  const fixture = await project();
  const { editor, record } = await recordingEditor(fixture.directory);
  const rmf2 = join(fixture.frontend, "translations", "en.rmf2");
  await mkdir(join(rmf2, ".."), { recursive: true });
  await writeFile(rmf2, "# App\nheading = Counter\n");
  const requests = [];
  const api = translationsApi({ "app:heading": rmf2, "app:passwd": "/etc/passwd", "app:gone": join(fixture.frontend, "translations", "fr.rmf2") }, requests);
  try {
    for (const options of [{}, { sourceMap: false }]) {
      await withEnvironment({ LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record }, () => withServer(fixture.frontend, options, async (base) => {
        const open = body => fetch(`${base}/__runic/open`, {
          method: "POST", headers: { "Content-Type": "application/json", Origin: base }, body: JSON.stringify(body),
        }).then(async (response) => { await response.arrayBuffer(); return response.status; });
        assert.equal(await open({ message: { catalog: "app", key: "missing", locale: "en" } }), 404, "a message the API does not know");
        assert.equal(await open({ message: { catalog: "app", key: "heading", locale: "de" } }), 404, "a locale without a source");
        assert.equal(await open({ message: { catalog: "app", key: "passwd", locale: "en" } }), 404, "not a translation source");
        assert.equal(await open({ message: { catalog: "app", key: "gone", locale: "en" } }), 404, "a file that does not exist");
        assert.equal(await open({ message: { catalog: "app", key: "../heading", locale: "en" } }), 400);
        assert.equal(await open({ message: "app:heading" }), 400);
        assert.equal(await open({ message: { catalog: "app", key: "missing", locale: "en" }, file: rmf2, line: 2, column: 1 }), 404,
          "a message request never opens a path it carries");
        assert.equal(await exists(record), false, "no refused request launched the editor");
        // The request went to this server from loopback, as the API requires.
        assert.ok(requests.length > 0 && requests.every(request => request.method === "GET" && /^(?:::ffff:)?127\.0\.0\.1$|^::1$/u.test(request.address)), JSON.stringify(requests));
        if (process.platform !== "win32") {
          assert.equal(await open({ message: { catalog: "app", key: "heading", locale: "en" } }), 200);
          assert.deepEqual(await readRecord(record), [rmf2, "2", "1"]);
        }
        requests.length = 0;
      }, [api]));
    }
    // Without Runic Inspect and the map there is no open action; without the Translations API a message opens nothing.
    await withEnvironment({ LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record }, () => withServer(fixture.frontend, {}, async (base) => {
      const response = await fetch(`${base}/__runic/open`, {
        method: "POST", headers: { "Content-Type": "application/json", Origin: base },
        body: JSON.stringify({ message: { catalog: "app", key: "heading", locale: "en" } }),
      });
      await response.arrayBuffer();
      assert.equal(response.status, 404);
      assert.equal(await exists(record), false);
    }));
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

test("vite build output is byte-identical with and without the source map", async () => {
  const fixture = await project();
  const builds = {};
  try {
    for (const [name, options, values] of [
      ["with-map", {}, { RUNIC_APPLICATION_DEV_SOURCE_MAP: fixture.mapPath }],
      ["explicit-map", { sourceMap: "../obj" }, {}],
      ["without-map", { sourceMap: false }, {}],
    ]) {
      if (name === "without-map") await rm(join(fixture.directory, "obj"), { recursive: true, force: true });
      await withEnvironment(values, () => build({
        root: fixture.frontend, configFile: false, logLevel: "silent", plugins: [runic(options)],
        build: { outDir: join(fixture.directory, name) },
      }));
      builds[name] = await files(join(fixture.directory, name));
    }
    assert.ok(Object.keys(builds["without-map"]).length > 0);
    assert.deepEqual(builds["with-map"], builds["without-map"]);
    assert.deepEqual(builds["explicit-map"], builds["without-map"]);
    const text = Object.values(builds["with-map"]).map((content) => content.toString("utf8")).join("\n");
    assert.doesNotMatch(text, /__runic\/(?:sources|open)|runic-bridge-sources|CounterViewModel/u);
  } finally { await rm(fixture.directory, { recursive: true, force: true }); }
});

// Every file of a directory, by relative path, as bytes.
async function files(directory) {
  const result = {};
  for (const entry of await readdir(directory, { recursive: true, withFileTypes: true })) {
    if (!entry.isFile()) continue;
    const path = join(entry.parentPath ?? entry.path, entry.name);
    result[relative(directory, path).replaceAll("\\", "/")] = await readFile(path);
  }
  return result;
}
