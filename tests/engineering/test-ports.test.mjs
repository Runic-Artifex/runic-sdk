// The shared port lease for scripts (tests/support/ports.mjs), the counterpart of tests/support/TestPorts.cs.
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { createServer } from "node:net";
import { candidates, freePort, leasePort, socketsUse } from "../support/ports.mjs";

const binds = port => new Promise(resolve => {
  const server = createServer();
  server.once("error", () => resolve(false));
  server.listen({ port, host: "127.0.0.1", exclusive: true }, () => server.close(() => resolve(true)));
});

test("a lease holds its port until it is released", async () => {
  const lease = await leasePort();
  expect(await binds(lease.port)).toBe(false);
  expect(await lease.release()).toBe(lease.port);
  expect(await lease.release()).toBe(lease.port);
  expect(await binds(lease.port)).toBe(true);
});

test("leases come from outside the ephemeral range and do not repeat while held", async () => {
  const leases = await Promise.all(Array.from({ length: 20 }, () => leasePort()));
  try {
    expect(new Set(leases.map(lease => lease.port)).size).toBe(leases.length);
    if (process.platform === "linux") {
      const [low, high] = readFileSync("/proc/sys/net/ipv4/ip_local_port_range", "utf8").trim().split(/\s+/).map(Number);
      for (const { port } of leases) expect(port < low || port > high).toBe(true);
    }
  } finally {
    await Promise.all(leases.map(lease => lease.release()));
  }
  expect(await binds(await freePort())).toBe(true);
});

test("the candidates are the larger span beside the ephemeral range", () => {
  expect(candidates([32_768, 60_999])).toEqual([10_000, 32_767]);
  expect(candidates([49_152, 65_535])).toEqual([10_000, 49_151]);
  expect(candidates([1_024, 40_000])).toEqual([40_001, 65_535]);
  expect(() => candidates([1_024, 65_000])).toThrow("leaves no ports");
});

test("a port that a socket uses is reported on Linux", async () => {
  const server = createServer();
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    expect(socketsUse(server.address().port)).toBe(process.platform === "linux");
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});
