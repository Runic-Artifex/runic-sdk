import { expect, test } from "bun:test";
import { createServer, request } from "node:http";
import { coordinatorHandler, isLoopbackHost, maximumEventClients } from "../../tools/Runic.Application.Views.Dev/runic-dev.mjs";

// The examples' development coordinator (SEC-33): it answers only loopback Host names, which the Vite and Angular
// dev proxies forward unchanged or rewrite to 127.0.0.1, and it caps the restart event streams.

test("the coordinator accepts only loopback Host names", () => {
  for (const host of ["127.0.0.1:5173", "127.0.0.1", "localhost:4200", "LOCALHOST", "[::1]:8080"])
    expect(isLoopbackHost(host)).toBe(true);
  for (const host of [undefined, "", "evil.example:5173", "127.0.0.1.evil.example", "localhost.evil.example:80", "[::2]:1", "127.0.0.2:5173"])
    expect(isLoopbackHost(host)).toBe(false);
});

function get(port, path, host, keep) {
  return new Promise((accept, reject) => {
    const outgoing = request({ port, host: "127.0.0.1", path, headers: { host } }, (response) => {
      if (keep) { accept(response); return; }
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => { body += chunk; });
      response.on("end", () => accept({ status: response.statusCode, body }));
    });
    outgoing.on("error", reject);
    outgoing.end();
  });
}

test("the coordinator rejects other hosts and caps event streams", async () => {
  const clients = new Set();
  const server = createServer(coordinatorHandler({
    state: () => ({ generation: 3, backendPort: 1, frontendPort: 2 }),
    clients,
    maximumClients: 2,
  }));
  await new Promise((accept) => server.listen(0, "127.0.0.1", accept));
  const { port } = server.address();
  const streams = [];
  try {
    expect(maximumEventClients).toBeGreaterThan(0);
    const status = await get(port, "/__runic_dev/status", `localhost:${port}`);
    expect(status.status).toBe(200);
    expect(JSON.parse(status.body)).toEqual({ generation: 3, backendPort: 1, frontendPort: 2 });
    // A rebinding page sends its own name as Host.
    expect((await get(port, "/__runic_dev/status", `rebind.example:${port}`)).status).toBe(403);
    expect((await get(port, "/__runic_dev/events", "rebind.example")).status).toBe(403);
    for (let index = 0; index < 2; index++) {
      const stream = await get(port, "/__runic_dev/events", "127.0.0.1:5173", true);
      expect(stream.statusCode).toBe(200);
      streams.push(stream);
    }
    expect(clients.size).toBe(2);
    expect((await get(port, "/__runic_dev/events", "127.0.0.1:5173")).status).toBe(503);
    // A closed stream frees its place.
    streams.shift().destroy();
    for (let attempt = 0; clients.size !== 1 && attempt < 100; attempt++) await new Promise((accept) => setTimeout(accept, 10));
    expect(clients.size).toBe(1);
    const again = await get(port, "/__runic_dev/events", "127.0.0.1", true);
    expect(again.statusCode).toBe(200);
    streams.push(again);
    expect((await get(port, "/elsewhere", "127.0.0.1")).status).toBe(404);
  } finally {
    for (const stream of streams) stream.destroy();
    for (const client of clients) client.end();
    await new Promise((accept) => server.close(accept));
  }
});
