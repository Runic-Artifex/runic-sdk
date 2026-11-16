// Who may use Runic's development endpoints and HMR events: the access list for /__runic/state, /__runic/diagnostics
// and Runic's HMR events, the plugin's own Host check when Vite's is off (server.allowedHosts: true), and the owner of a
// loopback client for the open action (local-user.ts).
import assert from "node:assert/strict";
import { request as httpRequest, createServer as createHttpServer } from "node:http";
import { connect } from "node:net";
import test from "node:test";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { AccessList, isTrustedHost, parseAllowList } from "../dist/access.js";
import { createClientGate } from "../dist/client-gate.js";
import { answerClientMessage, answerStateRequest } from "../dist/dev-session.js";
import { addressKey, createLocalUserCheck, parseProcNetTcp } from "../dist/local-user.js";

const header = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";
const row = (local, remote, uid) => `   0: ${local} ${remote} 01 00000000:00000000 00:00000000 00000000 ${String(uid).padStart(5)}        0 4242 1 0000000000000000 20 4 30 10 -1\n`;
// 127.0.0.1:41234 → 127.0.0.1:5173 as a little-endian kernel prints it, and the server's side of the same connection.
const tcp = header + row("0100007F:A0F2", "0100007F:1435", 1001) + row("0100007F:1435", "0100007F:A0F2", 1000);
// [::1]:41235 → [::1]:5173, and ::ffff:127.0.0.1:41236 → ::ffff:127.0.0.1:5173 (an IPv4 client of a dual-stack server).
const tcp6 = header +
  row("00000000000000000000000001000000:A0F3", "00000000000000000000000001000000:1435", 1000) +
  row("0000000000000000FFFF00000100007F:A0F4", "0000000000000000FFFF00000100007F:1435", 1000);

test("parses /proc/net/tcp and tcp6 in either byte order", () => {
  const [client, server] = parseProcNetTcp(tcp, true);
  assert.deepEqual(client, { localAddress: addressKey("127.0.0.1"), localPort: 41_202, remoteAddress: addressKey("127.0.0.1"), remotePort: 5173, uid: 1001 });
  assert.equal(server.uid, 1000);
  const [loopback6, mapped] = parseProcNetTcp(tcp6, true);
  assert.equal(loopback6.localAddress, addressKey("::1"));
  assert.equal(loopback6.localAddress, addressKey("0:0:0:0:0:0:0:1"));
  assert.equal(mapped.localAddress, addressKey("::ffff:127.0.0.1"));
  assert.equal(mapped.localAddress, addressKey("127.0.0.1"), "an IPv4 address and its IPv4-mapped form are one key");
  // A big-endian kernel prints the words in network order.
  assert.equal(parseProcNetTcp(header + row("7F000001:A0F2", "7F000001:1435", 7), false)[0].localAddress, addressKey("127.0.0.1"));
  assert.deepEqual(parseProcNetTcp(`${header}garbage\n   1: zz:1 00:2 01\n`, true), []);
  assert.equal(addressKey("fe80::1%eth0"), addressKey("fe80::1"));
  assert.equal(addressKey("not an address"), undefined);
});

test("a loopback client is this server's user only when its socket is found and owned by that user", async () => {
  const tables = { "/proc/net/tcp": tcp, "/proc/net/tcp6": tcp6 };
  const read = async (path) => { if (path in tables) return tables[path]; throw Object.assign(new Error("ENOENT"), { code: "ENOENT" }); };
  const check = createLocalUserCheck({ platform: "linux", uid: 1000, read, littleEndian: true });
  const socket = (remoteAddress, remotePort, localAddress = remoteAddress) => ({ remoteAddress, remotePort, localAddress, localPort: 5173 });
  assert.equal(await check(socket("127.0.0.1", 41_202)), false, "another user's client");
  assert.equal(await check(socket("::1", 41_203)), true, "this user's IPv6 client");
  assert.equal(await check(socket("::ffff:127.0.0.1", 41_204)), true, "this user's IPv4 client of a dual-stack server");
  assert.equal(await check(socket("127.0.0.1", 40_000)), false, "a socket that is not in the tables");
  assert.equal(await check({ remoteAddress: "127.0.0.1" }), false, "a socket without endpoints");
  assert.equal(await check(undefined), false);
  assert.equal(await createLocalUserCheck({ platform: "linux", uid: 1001, read, littleEndian: true })(socket("127.0.0.1", 41_202)), true);
  // Where the tables cannot be read, the check is not available: it does not refuse.
  const none = async () => { throw new Error("ENOENT"); };
  assert.equal(await createLocalUserCheck({ platform: "linux", uid: 1000, read: none })(socket("127.0.0.1", 41_202)), true);
  assert.equal(await createLocalUserCheck({ platform: "darwin", uid: 1000, read })(socket("127.0.0.1", 41_202)), true);
  assert.equal(await createLocalUserCheck({ platform: "win32", read })(socket("127.0.0.1", 41_202)), true);
});

test("the real tables name this process's user as the owner of its own loopback connection", { skip: process.platform !== "linux" }, async () => {
  const accepted = new Promise((resolve) => {
    const server = createHttpServer().on("connection", (socket) => resolve({ server, socket }));
    server.listen(0, "127.0.0.1", () => { connect(server.address().port, "127.0.0.1"); });
  });
  const { server, socket } = await accepted;
  try {
    assert.equal(await createLocalUserCheck()(socket), true);
    assert.equal(await createLocalUserCheck({ uid: process.getuid() + 1 })(socket), false, "the same socket for another user");
  } finally {
    socket.destroy();
    server.close();
  }
});

test("with Vite's host check off, Runic answers IP literals, localhost and the allow list's names only", () => {
  const access = new AccessList(parseAllowList(["laptop.tailnet.ts.net", "100.64.0.0/10"], "allow"));
  for (const host of ["127.0.0.1:5173", "127.0.0.1", "[::1]:5173", "[::1]", "100.100.1.2:5173", "localhost:5173", "LOCALHOST",
    "app.localhost:5173", "localhost.:5173", "laptop.tailnet.ts.net:5173", "Laptop.Tailnet.TS.net.", undefined]) {
    assert.equal(isTrustedHost(host, access), true, String(host));
  }
  for (const host of ["rebind.attacker.example:5173", "localhost.attacker.example", "127.0.0.1.nip.io", "tailnet.ts.net", "", ":5173",
    "[::1", "[::1]x", "[127.0.0.1]", "laptop.tailnet.ts.net.evil.example"]) {
    assert.equal(isTrustedHost(host, access), false, host);
  }
});

// A request as connect passes it: a client address, headers and an empty body.
function request({ address = "127.0.0.1", headers = {}, method = "GET", body } = {}) {
  const chunks = body === undefined ? [] : [Buffer.from(JSON.stringify(body))];
  return {
    method, headers, socket: { remoteAddress: address },
    resume() {},
    on(event, listener) {
      if (event === "data") for (const chunk of chunks) listener(chunk);
      if (event === "end") listener();
      return this;
    },
  };
}

// A response that records its status and body; `done` resolves when it ends.
function response() {
  let finish;
  const done = new Promise((resolve) => { finish = resolve; });
  return {
    statusCode: 200, headers: {}, writableEnded: false, done,
    setHeader(name, value) { this.headers[name.toLowerCase()] = value; },
    end(text) { this.writableEnded = true; finish({ status: this.statusCode, body: text }); },
  };
}

test("/__runic/state answers this machine and the allowed hosts, and no page of another origin", async () => {
  const access = new AccessList(parseAllowList(["192.0.2.0/24"], "allow"));
  const state = () => ({ views: [{ state: { customerEmail: "alice@example.com" } }], timeline: [], calls: [] });
  const ask = async (options, list = access) => {
    const answer = response();
    await answerStateRequest(request(options), answer, state, "a".repeat(32), list);
    return answer.done;
  };
  assert.equal((await ask({})).status, 200);
  assert.equal((await ask({ address: "::ffff:127.0.0.1" })).status, 200);
  assert.equal((await ask({ address: "192.0.2.5" })).status, 200, "an allowed machine");
  assert.equal((await ask({ address: "198.51.100.7" })).status, 403, "a machine outside the list");
  assert.equal((await ask({ address: "192.0.2.5" }, new AccessList())).status, 403, "another machine without a list");
  assert.equal((await ask({ headers: { "sec-fetch-site": "cross-site" } })).status, 403, "a page of another site");
  assert.equal((await ask({ headers: { origin: "http://localhost:3000", host: "127.0.0.1:5173" } })).status, 403, "a page of another origin");
  const refused = await ask({ address: "198.51.100.7" });
  assert.equal(refused.body, undefined, "a refused client gets no state");
});

test("/__runic/diagnostics takes events from this machine and the allowed hosts only", async () => {
  const access = new AccessList(parseAllowList(["192.0.2.0/24"], "allow"));
  const delivered = [];
  const post = async (address, list = access) => {
    const answer = response();
    await answerClientMessage(request({
      address, method: "POST", body: { event: "runic:view", data: { route: "customer" } },
      headers: { origin: "http://dev.example:5173", host: "dev.example:5173", "content-type": "application/json" },
    }), answer, (event, data) => delivered.push([event, data]), list);
    return (await answer.done).status;
  };
  assert.equal(await post("127.0.0.1"), 204);
  assert.equal(await post("192.0.2.5"), 204);
  assert.equal(await post("198.51.100.7"), 403);
  assert.equal(await post("192.0.2.5", new AccessList()), 403);
  assert.equal(delivered.length, 2);
});

test("Runic's HMR events reach the timeline, and the state reaches a client, only for admitted connections", async () => {
  const access = new AccessList(parseAllowList(["192.0.2.0/24"], "allow"));
  const sent = new Map();
  const connections = [];
  const gate = createClientGate(access, (upgrade) => upgrade.headers.host !== "rebind.attacker.example:5173", () => connections);
  const connect = (name, address, host = "127.0.0.1:5173") => {
    const socket = {};
    gate.connect(socket, { headers: { host }, socket: { remoteAddress: address, remotePort: 40_000, localAddress: "127.0.0.1", localPort: 5173 } });
    const client = { socket, send: (event, data) => sent.set(name, [...(sent.get(name) ?? []), [event, data]]) };
    connections.push(client);
    return socket;
  };
  const sockets = {
    page: connect("page", "127.0.0.1"),
    allowed: connect("allowed", "192.0.2.5"),
    stranger: connect("stranger", "198.51.100.7"),
    rebound: connect("rebound", "127.0.0.1", "rebind.attacker.example:5173"),
  };
  const delivered = [];
  for (const [name, socket] of Object.entries(sockets)) {
    for (const index of [1, 2, 3]) gate.whenAdmitted(socket, () => delivered.push(`${name}${index}`));
  }
  gate.whenAdmitted(undefined, () => delivered.push("unknown"));
  gate.whenAdmitted({}, () => delivered.push("unrecorded"));
  gate.broadcast("runic:state", { version: 1 });
  gate.broadcast("runic:state", { version: 2 });
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(delivered.filter((entry) => entry.startsWith("page")), ["page1", "page2", "page3"], "in the order sent");
  assert.deepEqual(delivered.filter((entry) => !entry.startsWith("page")).sort(), ["allowed1", "allowed2", "allowed3"]);
  assert.deepEqual([...sent.keys()].sort(), ["allowed", "page"]);
  assert.deepEqual(sent.get("page").at(-1), ["runic:state", { version: 2 }], "the newest state arrives last");
  assert.equal(gate.peer(sockets.rebound).trustedHost, false);
  assert.equal(await gate.admits(sockets.stranger), false);
});

// Sends a request with a chosen Host header, which fetch does not allow.
function get(port, path, host) {
  return new Promise((resolve, reject) => {
    httpRequest({ host: "127.0.0.1", port, path, headers: { host } }, (answer) => {
      answer.resume();
      answer.on("end", () => resolve(answer.statusCode));
    }).on("error", reject).end();
  });
}

test("with server.allowedHosts: true, Runic's endpoints refuse a DNS-rebinding Host", async () => {
  for (const allowedHosts of [true, undefined]) {
    const logged = [];
    const server = await createServer({
      configFile: false, logLevel: "silent", root: process.cwd(),
      plugins: [runic({ devtools: false, sourceMap: false, allow: ["laptop.example"] })],
      server: { host: "127.0.0.1", port: 0, strictPort: false, ...(allowedHosts === undefined ? {} : { allowedHosts }) },
      customLogger: { info: (message) => logged.push(message), warn() {}, warnOnce() {}, error() {}, clearScreen() {}, hasWarned: false, hasErrorLogged: () => false },
    });
    try {
      await server.listen();
      const { port } = server.httpServer.address();
      assert.equal(await get(port, "/__runic/state", `127.0.0.1:${port}`), 200);
      assert.equal(await get(port, "/__runic/state", `localhost:${port}`), 200);
      assert.equal(await get(port, "/__runic/state", `laptop.example:${port}`), allowedHosts ? 200 : 403, "a host name of the allow list");
      // Vite's own check refuses it by default; with allowedHosts: true, Runic's.
      assert.equal(await get(port, "/__runic/state", `rebind.attacker.example:${port}`), 403);
      assert.equal(await get(port, "/__runic/handlers?file=src/App.tsx", `rebind.attacker.example:${port}`), 403);
      assert.equal(logged.some((message) => message.includes("server.allowedHosts is true")), allowedHosts === true);
    } finally {
      await server.close();
    }
  }
});
