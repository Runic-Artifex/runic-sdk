// A stand-in for a guest's surface in component mode (host-protocol.md, region-component/1), for the component tests:
// it admits a WebSocket upgrade only with `runic-desktop.<token>` as its subprotocol and, when `origin` is given, with
// that Origin, as Runic Desktop's guest does, and speaks the guest's page wire (webui.js). Views requests are answered
// by `answer`; `push` sends a Views push to every admitted socket.
// The part of Bun's server that these tests use; the package's type check has no Bun types.
interface GuestSocket { sendBinary(data: Uint8Array): void; close(): void }
interface LoopbackServer { readonly port: number; upgrade(request: Request, options: { headers: Record<string, string>; data: undefined }): boolean; stop(force?: boolean): Promise<void> }
declare const Bun: {
  serve(options: {
    hostname: string;
    port: number;
    fetch(request: Request, server: LoopbackServer): Response | Promise<Response> | undefined;
    websocket?: { open(socket: GuestSocket): void; close(socket: GuestSocket): void; message(socket: GuestSocket, data: string | Uint8Array): void };
  }): LoopbackServer;
};

/** An HTTP server on 127.0.0.1 with a free port; `stop` ends it. */
export function serveLoopback(fetch: (request: Request) => Response | Promise<Response>): { readonly origin: string; stop(): void } {
  const server = Bun.serve({ hostname: "127.0.0.1", port: 0, fetch });
  return { origin: `http://127.0.0.1:${server.port}`, stop: () => { void server.stop(true); } };
}

const signature = 0xDD;
const commandScript = 0xFE;
const commandCall = 0xF9;
const commandRaw = 0xF8;
const commandMulti = 0xF6;
const commandCheckToken = 0xF5;
const headerSize = 8;

export interface ComponentGuest {
  readonly url: string;
  readonly origin: string;
  readonly token: string;
  /** Every upgrade: its Origin and offered subprotocols, and whether it was admitted. */
  readonly upgrades: { origin: string | null; protocols: string | null; admitted: boolean }[];
  /** The Views requests the admitted sockets sent. */
  readonly requests: string[];
  /** The replies to script the guest asked a socket to run: [failed, text]. */
  readonly scriptReplies: [boolean, string][];
  /** The packet tokens of the admitted sockets' token checks. */
  readonly checks: number[];
  open(): number;
  push(message: string): void;
  runScript(script: string): void;
  /** Refuses the token from now on, as a detach does, and closes the admitted sockets. */
  revoke(): void;
  stop(): void;
}

function packet(length: number, id: number, command: number): Uint8Array {
  const bytes = new Uint8Array(headerSize + length);
  bytes[0] = signature;
  new DataView(bytes.buffer).setUint16(5, id, true);
  bytes[7] = command;
  return bytes;
}

export function startComponentGuest(options: {
  readonly token?: string;
  readonly origin?: string;
  readonly answer?: (request: string) => string;
  readonly files?: (path: string, origin: string | null) => Response | Promise<Response> | undefined;
} = {}): ComponentGuest {
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const token = options.token ?? "c".repeat(43);
  let revoked = false;
  const upgrades: ComponentGuest["upgrades"] = [];
  const requests: string[] = [];
  const scriptReplies: [boolean, string][] = [];
  const checks: number[] = [];
  const sockets = new Set<GuestSocket>();
  const multi = new Map<GuestSocket, { length: number; received: number; chunks: Uint8Array[] }>();
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port: 0,
    fetch(request, bun) {
      const url = new URL(request.url);
      const origin = request.headers.get("origin");
      if (url.pathname === "/_webui_ws_connect") {
        const protocols = request.headers.get("sec-websocket-protocol");
        const offered = (protocols ?? "").split(",").map(value => value.trim());
        const admitted = !revoked && offered.includes(`runic-desktop.${token}`) &&
          (options.origin === undefined || origin === options.origin);
        upgrades.push({ origin, protocols, admitted });
        if (!admitted) return new Response("Forbidden", { status: 403 });
        if (bun.upgrade(request, { headers: { "Sec-WebSocket-Protocol": `runic-desktop.${token}` }, data: undefined })) return undefined;
        return new Response("Upgrade failed", { status: 400 });
      }
      return options.files?.(url.pathname, origin) ?? new Response("Not found", { status: 404 });
    },
    websocket: {
      open(socket) { sockets.add(socket); },
      close(socket) { sockets.delete(socket); },
      message(socket, data) {
        if (typeof data === "string") return;
        let bytes: Uint8Array = new Uint8Array(data);
        // A packet of 65,500 bytes or more comes as a CMD_MULTI header with its length, then its bytes in chunks.
        const pending = multi.get(socket);
        if (pending) {
          pending.chunks.push(bytes);
          pending.received += bytes.length;
          if (pending.received < pending.length) return;
          multi.delete(socket);
          bytes = new Uint8Array(pending.received);
          let offset = 0;
          for (const chunk of pending.chunks) { bytes.set(chunk, offset); offset += chunk.length; }
        }
        if (bytes.length === 4 && decoder.decode(bytes) === "ping") return;
        if (bytes.length < headerSize || bytes[0] !== signature) return;
        if (bytes[7] === commandMulti) {
          const length = Number(decoder.decode(bytes.subarray(headerSize, bytes.indexOf(0, headerSize))));
          multi.set(socket, { length, received: 0, chunks: [] });
          return;
        }
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        const id = view.getUint16(5, true);
        switch (bytes[7]) {
          case commandCheckToken: {
            checks.push(view.getUint32(1, true));
            const reply = packet(1, id, commandCheckToken);
            reply[headerSize] = 1;
            socket.sendBinary(reply);
            return;
          }
          case commandCall: {
            // name \0 lengths \0 value \0
            const nameEnd = bytes.indexOf(0, headerSize);
            const lengthsEnd = bytes.indexOf(0, nameEnd + 1);
            const length = Number(decoder.decode(bytes.subarray(nameEnd + 1, lengthsEnd)));
            const value = decoder.decode(bytes.subarray(lengthsEnd + 1, lengthsEnd + 1 + length));
            requests.push(value);
            const text = encoder.encode(options.answer?.(value) ?? '{"ok":true}');
            const reply = packet(text.length + 1, id, commandCall);
            reply.set(text, headerSize);
            socket.sendBinary(reply);
            return;
          }
          case commandScript:
            scriptReplies.push([bytes[headerSize] === 1, decoder.decode(bytes.subarray(headerSize + 1))]);
            return;
        }
      },
    },
  });
  const url = `ws://127.0.0.1:${server.port}/_webui_ws_connect`;
  return {
    url,
    origin: `http://127.0.0.1:${server.port}`,
    token,
    upgrades,
    requests,
    scriptReplies,
    checks,
    open: () => sockets.size,
    push(message) {
      const name = encoder.encode("__runicViewsPush");
      const value = encoder.encode(message);
      const bytes = packet(name.length + 1 + value.length + 1, 0, commandRaw);
      bytes.set(name, headerSize);
      bytes.set(value, headerSize + name.length + 1);
      for (const socket of sockets) socket.sendBinary(bytes);
    },
    runScript(script) {
      const text = encoder.encode(script);
      const bytes = packet(text.length + 1, 7, commandScript);
      bytes.set(text, headerSize);
      for (const socket of sockets) socket.sendBinary(bytes);
    },
    revoke() {
      revoked = true;
      for (const socket of sockets) socket.close();
    },
    stop: () => { void server.stop(true); },
  };
}
