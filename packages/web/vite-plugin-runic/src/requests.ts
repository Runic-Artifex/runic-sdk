// Guards of the development server's endpoints: same-origin pages of this machine, and for the data Runic Inspect reads,
// of the hosts and networks the allow list admits (access.ts).

import type { IncomingMessage } from "node:http";
import { isLoopbackAddress, loopbackOnly, type AccessList } from "./access.js";

/**
 * Whether a request comes from a page of this server: a browser sends `Origin` with every POST, and a cross-site
 * page that posts JSON needs a CORS preflight, which the endpoint never grants.
 */
export function sameOrigin(request: IncomingMessage): boolean {
  const site = request.headers["sec-fetch-site"];
  if (site !== undefined && site !== "same-origin") return false;
  const origin = request.headers.origin;
  const host = request.headers.host;
  if (typeof origin !== "string" || typeof host !== "string") return false;
  try {
    const url = new URL(origin);
    return (url.protocol === "http:" || url.protocol === "https:") && url.host === host.toLowerCase();
  } catch {
    return false;
  }
}

/** Whether a request comes from this machine: a loopback peer address. */
export function isLoopbackRequest(request: IncomingMessage): boolean {
  return isLoopbackAddress(request.socket?.remoteAddress);
}

/** The body as UTF-8, or undefined once it exceeds `limit` bytes; the rest is drained so that the answer reaches the client. */
export function readLimitedBody(request: IncomingMessage, limit: number): Promise<string | undefined> {
  const declared = Number(request.headers["content-length"]);
  if (Number.isFinite(declared) && declared > limit) {
    request.resume();
    return Promise.resolve(undefined);
  }
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let exceeded = false;
    request.on("data", (chunk: Buffer) => {
      if (exceeded) return;
      size += chunk.byteLength;
      if (size > limit) {
        exceeded = true;
        chunks.length = 0;
        return;
      }
      chunks.push(chunk);
    });
    request.on("end", () => resolve(exceeded ? undefined : Buffer.concat(chunks).toString("utf8")));
    request.on("error", () => resolve(undefined));
  });
}

/**
 * Whether a GET may read development data (the source map, the handler analysis): not a page of another origin, and
 * a client of this machine or one the allow list admits (`runic({ allow })`, `RUNIC_DEV_ALLOW`). A browser marks a
 * cross-origin fetch with `Sec-Fetch-Site` and `Origin`; neither is required, so that a tool such as `curl` reads it.
 */
export async function mayRead(request: IncomingMessage, access: AccessList = loopbackOnly): Promise<boolean> {
  const site = request.headers["sec-fetch-site"];
  if (site !== undefined && site !== "same-origin" && site !== "none") return false;
  const origin = request.headers.origin;
  if (origin !== undefined && !sameOrigin(request)) return false;
  return access.allowsRequest(request);
}
