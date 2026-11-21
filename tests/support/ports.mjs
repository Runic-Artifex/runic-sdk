// Loopback ports for a child process, a server started later, or a URL handed to another program: the JavaScript side
// of tests/support/TestPorts.cs.
//
// A port taken by listening on port 0 and closing the server is free only for a moment: any later bind to port 0 or
// outgoing connection on the machine can receive it before the child binds it (#1257). A lease therefore picks a port
// outside the operating system's ephemeral range, which neither bind(0) nor connect() hands out, at random so that
// parallel tests do not collide, and holds a listener on it until release(), so no other lease takes it meanwhile.
// Release it immediately before the program that binds the port starts.
import { randomInt } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer } from "node:net";

const lowest = 10_000;
const attempts = 200;

/**
 * Leases a port on host.
 * @param {{ host?: string }} [options]
 * @returns {Promise<{ port: number, release: () => Promise<number> }>}
 */
export async function leasePort({ host = "127.0.0.1" } = {}) {
  const [low, high] = candidates(ephemeralRange());
  let last;
  for (let attempt = 0; attempt < attempts; attempt++) {
    const port = randomInt(low, high + 1);
    // A port that a connection or TIME_WAIT still uses binds for Node, which sets SO_REUSEADDR, but not for every child.
    if (socketsUse(port)) continue;
    const server = createServer();
    try {
      await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen({ port, host, exclusive: true }, resolve);
      });
    } catch (error) {
      last = error;
      continue;
    }
    let released;
    return {
      port,
      release: () => (released ??= new Promise(resolve => server.close(() => resolve(port)))),
    };
  }
  throw new Error(`No port in ${low}-${high} could be leased after ${attempts} attempts: ${last?.message ?? "every port was in use"}`);
}

/** A leased port, released at once: for a program that binds it next, or an address nothing listens on. */
export async function freePort(options) {
  const lease = await leasePort(options);
  return lease.release();
}

/** The ports a lease picks from: the larger span beside the ephemeral range. */
export function candidates([ephemeralLow, ephemeralHigh]) {
  const below = [lowest, ephemeralLow - 1];
  const above = [ephemeralHigh + 1, 65_535];
  const choice = below[1] - below[0] >= above[1] - above[0] ? below : above;
  if (choice[1] - choice[0] < 1_000)
    throw new Error(`The ephemeral port range ${ephemeralLow}-${ephemeralHigh} leaves no ports to lease.`);
  return choice;
}

// Linux reads its range from /proc; Windows and macOS use the IANA dynamic range unless reconfigured.
function ephemeralRange() {
  if (process.platform === "linux") {
    try {
      const [low, high] = readFileSync("/proc/sys/net/ipv4/ip_local_port_range", "utf8").trim().split(/\s+/).map(Number);
      if (Number.isInteger(low) && Number.isInteger(high)) return [low, high];
    } catch {}
  }
  return [49_152, 65_535];
}

/** Linux: whether any TCP socket, in any state, has port as its local port. */
export function socketsUse(port) {
  if (process.platform !== "linux") return false;
  for (const table of ["/proc/net/tcp", "/proc/net/tcp6"]) {
    let lines;
    try { lines = readFileSync(table, "utf8").split("\n").slice(1); } catch { continue; }
    for (const line of lines) {
      // sl local_address rem_address st ...: local_address is HEXADDRESS:HEXPORT.
      const local = line.trim().split(/\s+/)[1];
      if (local && Number.parseInt(local.slice(local.lastIndexOf(":") + 1), 16) === port) return true;
    }
  }
  return false;
}
