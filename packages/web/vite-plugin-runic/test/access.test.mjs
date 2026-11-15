// Runic Inspect from another machine (#957, D066.27 #14): `/__runic/sources` and `/__runic/handlers` answer loopback,
// and the hosts and networks `runic({ allow })`, `runicDevSessionProxy({ allow })` and `RUNIC_DEV_ALLOW` name;
// `/__runic/open` answers loopback only. The allow list parses entries as Runic Translations' in-page editing API does.
// The remote client is this machine's own LAN address, which a dev server listening on every address sees as a
// non-loopback peer.
import assert from "node:assert/strict";
import { mkdir, mkdtemp, realpath, rm, symlink, writeFile } from "node:fs/promises";
import { networkInterfaces, tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { createServer, createLogger } from "vite";
import { runic, runicDevSessionProxy } from "../dist/index.js";
import { AccessList, allowListFromEnvironment, isLoopbackAddress, parseAllowList, resolveAccessList } from "../dist/access.js";

const here = fileURLToPath(new URL("..", import.meta.url));
const lan = Object.values(networkInterfaces()).flat().find((entry) => entry?.family === "IPv4" && !entry.internal)?.address;

test("the allow list: addresses, CIDR networks, host names; loopback always; invalid entries name themselves", async () => {
  const rules = parseAllowList(["100.64.0.0/10", "fd7a:115c:a1e0::/48", "192.168.1.20", "[::2]", "laptop.tailnet.ts.net"], "allow");
  assert.deepEqual(rules.map((rule) => rule.kind), ["network", "network", "address", "address", "host"]);
  const lookups = [];
  const access = new AccessList(rules, async (name) => { lookups.push(name); return ["100.101.102.103"]; });
  for (const address of ["127.0.0.1", "127.8.9.10", "::1", "::ffff:127.0.0.1"]) assert.equal(await access.allows(address), true, address);
  for (const address of ["100.100.1.2", "::ffff:100.64.0.1", "fd7a:115c:a1e0::9", "192.168.1.20", "::2", "100.101.102.103"]) {
    assert.equal(await access.allows(address), true, address);
  }
  for (const address of ["100.128.0.1", "192.168.1.21", "fd7a:115c:a1e1::1", "10.0.0.1", "", undefined, "not-an-address"]) {
    assert.equal(await access.allows(address), false, String(address));
  }
  assert.deepEqual(lookups, ["laptop.tailnet.ts.net"], "a host name is resolved once and cached");
  assert.equal(access.remote, true);
  assert.equal(new AccessList().remote, false);
  assert.equal(await new AccessList().allows("192.168.1.20"), false, "loopback only by default");
  assert.equal(isLoopbackAddress("::ffff:127.0.0.1"), true);
  assert.equal(isLoopbackAddress("128.0.0.1"), false);

  assert.throws(() => parseAllowList(["100.64.0.0/40"], "allow"), /allow: '100\.64\.0\.0\/40' is not a network/u);
  assert.throws(() => parseAllowList(["1.2.3"], "allow"), /'1\.2\.3' is not an IP address, a CIDR network or a host name/u);
  assert.throws(() => parseAllowList([""], "allow"), /every entry must be a non-empty string/u);
  assert.throws(() => parseAllowList("100.64.0.0/10", "allow"), /allow must be an array/u);
  assert.throws(() => runic({ allow: ["nope/1"] }), /allow: 'nope\/1' is not a network/u);
  assert.throws(() => runicDevSessionProxy({ allow: ["nope/1"] }), /allow: 'nope\/1' is not a network/u);

  // RUNIC_DEV_ALLOW: separated by commas or spaces, adding to the option, validated under its name.
  assert.deepEqual(allowListFromEnvironment(" 10.0.0.0/8,192.168.1.20  laptop "), ["10.0.0.0/8", "192.168.1.20", "laptop"]);
  const resolved = resolveAccessList(parseAllowList(["192.168.1.20"], "allow"), { RUNIC_DEV_ALLOW: "10.0.0.0/8" });
  assert.deepEqual(resolved.rules.map((rule) => rule.text), ["192.168.1.20", "10.0.0.0/8"]);
  assert.throws(() => resolveAccessList([], { RUNIC_DEV_ALLOW: "10.0.0.0/8 nope/1" }), /RUNIC_DEV_ALLOW: 'nope\/1' is not a network/u);
});

async function project() {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-access-")));
  await mkdir(join(root, "src"));
  await writeFile(join(root, "src", "runic.js"), "export const useCommand = (command) => ({ run: command });\n");
  await writeFile(join(root, "src", "Page.jsx"), `import { useCommand } from "./runic.js";
export function Page({ client }) {
  const save = useCommand(() => client.save());
  return <button id="save" onClick={() => void save.run()}>Save</button>;
}
`);
  await writeFile(join(root, "index.html"), "<!doctype html><html><body></body></html>");
  await writeFile(join(root, "Counter.cs"), "namespace Demo;\n");
  await mkdir(join(root, "node_modules"));
  await symlink(await realpath(join(here, "node_modules", "react")), join(root, "node_modules", "react"), "dir");
  await mkdir(join(root, "node_modules", "@runic-artifex"));
  await symlink(here, join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
  await writeFile(join(root, "sources.json"), JSON.stringify({
    format: "runic-bridge-sources/1",
    viewModels: [{ id: "Demo.Counter", name: "Counter", route: "counter", contract: "Demo.Counter:00", pageKinds: ["counter"], symbol: "Demo.Counter" }],
    members: [{ id: "Demo.Counter/command/save", viewModel: "Demo.Counter", member: "save", kind: "command", symbol: "Demo.Counter.Save()",
      location: { file: join(root, "Counter.cs"), line: 1, column: 1 } }],
  }));
  return root;
}

async function withEnvironment(value, run) {
  const previous = process.env.RUNIC_DEV_ALLOW;
  if (value === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = value;
  try { return await run(); }
  finally { if (previous === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = previous; }
}

test("from another machine: the data endpoints answer only when allowed, /__runic/open never", { skip: lan === undefined && "no non-loopback IPv4 address" }, async () => {
  const root = await project();
  const cwd = process.cwd();
  try {
    // runic() and the Angular CLI's proxy, each listening on every address, as with `vite --host`.
    const serve = async (kind, options) => {
      const infos = [];
      const logger = createLogger("info", { allowClearScreen: false });
      logger.info = (message) => infos.push(message);
      if (kind === "proxy") process.chdir(root);
      const server = await createServer({
        root, configFile: false, customLogger: logger, logLevel: "info",
        plugins: kind === "plugin" ? [runic({ sourceMap: "sources.json", ...options })] : [],
        server: { host: "0.0.0.0", port: 0, ...(kind === "proxy" ? { proxy: runicDevSessionProxy({ sourceMap: "sources.json", ...options }) } : {}) },
      });
      process.chdir(cwd);
      await server.listen();
      return { server, infos, port: server.httpServer.address().port };
    };
    const endpoints = async (port, host) => {
      const origin = `http://${host}:${port}`;
      const sources = await fetch(`${origin}/__runic/sources`);
      const handlers = await fetch(`${origin}/__runic/handlers?file=${encodeURIComponent(join(root, "src", "Page.jsx"))}`);
      const open = await fetch(`${origin}/__runic/open`, {
        method: "POST", headers: { "Content-Type": "application/json", Origin: origin }, body: JSON.stringify({ id: "Demo.Counter/command/missing" }),
      });
      return { sources: sources.status, handlers: handlers.status, open: open.status, openBody: await open.json().catch(() => null) };
    };
    for (const kind of ["plugin", "proxy"]) {
      // Without configuration: loopback reads, the LAN address is refused.
      let { server, infos, port } = await withEnvironment(undefined, () => serve(kind, {}));
      try {
        assert.deepEqual(await endpoints(port, "127.0.0.1"), { sources: 200, handlers: 200, open: 404, openBody: null }, `${kind}: loopback`);
        const refused = await endpoints(port, lan);
        assert.equal(refused.sources, 403, `${kind}: sources from ${lan}`);
        assert.equal(refused.handlers, 403, `${kind}: handlers from ${lan}`);
        assert.equal(refused.open, 403);
        assert.equal(refused.openBody?.error, "loopback-only");
        assert.equal(infos.some((line) => line.includes("RUNIC_DEV_ALLOW")), false, "nothing logged without a list");
      } finally { await server.close(); }

      // RUNIC_DEV_ALLOW, and the option: the LAN address reads the data; the open action stays loopback-only.
      for (const [environment, options] of [[lan, {}], [undefined, { allow: [`${lan}/32`] }]]) {
        ({ server, infos, port } = await withEnvironment(environment, () => serve(kind, options)));
        try {
          const allowed = await endpoints(port, lan);
          assert.equal(allowed.sources, 200, `${kind}: sources from ${lan} with ${environment ? "RUNIC_DEV_ALLOW" : "allow"}`);
          assert.equal(allowed.handlers, 200, `${kind}: handlers from ${lan}`);
          assert.equal(allowed.open, 403, "/__runic/open launches the editor here: loopback only");
          assert.match(allowed.openBody.detail, /development machine/u);
          // A page of another origin is still refused.
          assert.equal((await fetch(`http://${lan}:${port}/__runic/sources`, { headers: { "Sec-Fetch-Site": "cross-site" } })).status, 403);
          if (kind === "plugin") assert.ok(infos.some((line) => line.includes(`accepts requests from loopback and ${environment ?? `${lan}/32`}`)), infos.join("\n"));
        } finally { await server.close(); }
      }
    }
    // An invalid RUNIC_DEV_ALLOW stops the dev server with its name.
    await withEnvironment("nope/1", async () => {
      await assert.rejects(createServer({ root, configFile: false, logLevel: "silent", plugins: [runic({ sourceMap: "sources.json" })], server: { port: 0 } }),
        /RUNIC_DEV_ALLOW: 'nope\/1' is not a network/u);
    });
  } finally {
    process.chdir(cwd);
    await rm(root, { recursive: true, force: true });
  }
});

// Runic Inspect's requests over Vite's HMR connection (#1013), which reach the dev server from the .NET host's page under
// dotnet runic dev, follow the same rules, judged per request from the connection's address: reads (sources, handlers,
// message) for loopback and allowed machines, open for loopback only. The connection is Vite's own, with its token.
async function hmrRequests(host, port, token, requests) {
  const socket = new WebSocket(`ws://${host}:${port}/?token=${encodeURIComponent(token)}`, "vite-hmr");
  const answers = new Map();
  const done = new Promise((resolve, reject) => {
    socket.addEventListener("message", (event) => {
      const payload = JSON.parse(String(event.data));
      if (payload.type === "custom" && payload.event === "runic:response") answers.set(payload.data.id, payload.data.status);
      if (answers.size === requests.length) resolve();
    });
    socket.addEventListener("error", () => reject(new Error(`The HMR connection to ${host} failed.`)));
  });
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  for (const request of requests) socket.send(JSON.stringify({ type: "custom", event: "runic:request", data: request }));
  await Promise.race([done, new Promise((_, reject) => setTimeout(() => reject(new Error("No answer over the HMR connection.")), 10_000))]);
  socket.close();
  return Object.fromEntries(requests.map((request) => [request.kind, answers.get(request.id)]));
}

test("over Vite's HMR connection: reads answer only when allowed, open never from another machine", { skip: lan === undefined && "no non-loopback IPv4 address" }, async () => {
  const root = await project();
  try {
    const serve = async (options) => {
      const server = await createServer({
        root, configFile: false, logLevel: "silent", plugins: [runic({ sourceMap: "sources.json", ...options })],
        server: { host: "0.0.0.0", port: 0 },
      });
      await server.listen();
      return server;
    };
    // An unknown id: an admitted open answers 404 and launches nothing; a refused one answers 403.
    const requests = [
      { id: 1, kind: "sources" },
      { id: 2, kind: "handlers", file: join(root, "src", "Page.jsx") },
      { id: 3, kind: "open", target: { id: "Demo.Counter/command/missing" } },
    ];
    const ask = (server, host) => hmrRequests(host, server.httpServer.address().port, server.config.webSocketToken, requests);
    let server = await withEnvironment(undefined, () => serve({}));
    try {
      assert.deepEqual(await ask(server, "127.0.0.1"), { sources: 200, handlers: 200, open: 404 }, "loopback");
      assert.deepEqual(await ask(server, lan), { sources: 403, handlers: 403, open: 403 }, `${lan} without a list`);
    } finally { await server.close(); }
    for (const [environment, options] of [[lan, {}], [undefined, { allow: [`${lan}/32`] }]]) {
      server = await withEnvironment(environment, () => serve(options));
      try {
        assert.deepEqual(await ask(server, lan), { sources: 200, handlers: 200, open: 403 },
          `${lan} with ${environment ? "RUNIC_DEV_ALLOW" : "allow"}: reads, but open is loopback-only`);
        assert.deepEqual(await ask(server, "127.0.0.1"), { sources: 200, handlers: 200, open: 404 }, "loopback");
      } finally { await server.close(); }
    }
    // A list that names another network admits nothing more.
    server = await withEnvironment(undefined, () => serve({ allow: ["198.51.100.0/24"] }));
    try {
      assert.deepEqual(await ask(server, lan), { sources: 403, handlers: 403, open: 403 }, `${lan} outside the list`);
    } finally { await server.close(); }
  } finally { await rm(root, { recursive: true, force: true }); }
});
