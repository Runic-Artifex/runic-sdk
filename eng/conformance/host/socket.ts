// A WebSocket client over a plain TCP connection, so a runner controls every header of the upgrade (the `Host`, the
// path, `Origin`, the subprotocol) and sees the upgrade's status, headers and the peer's close code
// (specs/desktop/host-conformance/README.md, "Steps"). Messages are read continuously into a queue.
import { createHash, randomBytes } from "node:crypto";
import { createConnection, type Socket } from "node:net";

export type Message = { kind: "text"; text: string } | { kind: "binary"; data: Uint8Array };

export type UpgradeRequest = {
  address: string; // a loopback literal, with or without brackets
  port: number;
  target: string; // the request target, such as /path
  host: string; // the Host header
  headers?: [string, string][];
  timeoutMs: number;
};

export type UpgradeResult = { status: number | null; headers: [string, string][]; socket?: WebSocketClient; problem?: string };

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

const concat = (a: Uint8Array, b: Uint8Array) => {
  const out = new Uint8Array(a.length + b.length);
  out.set(a);
  out.set(b, a.length);
  return out;
};

// Opens the connection and sends the upgrade. `status` is null when the connection was refused, or closed before a
// response head arrived.
export function upgrade(request: UpgradeRequest): Promise<UpgradeResult> {
  const key = randomBytes(16).toString("base64");
  return new Promise((resolve) => {
    let buffer = new Uint8Array();
    let done = false;
    const socket = createConnection({ host: request.address.replace(/^\[|\]$/g, ""), port: request.port });
    const finish = (result: UpgradeResult) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      if (!result.socket) {
        socket.removeAllListeners("data");
        socket.destroy();
      }
      resolve(result);
    };
    const timer = setTimeout(() => finish({ status: null, headers: [], problem: `no upgrade response within ${request.timeoutMs} ms` }), request.timeoutMs);
    socket.once("error", () => finish({ status: null, headers: [] }));
    socket.once("close", () => finish({ status: null, headers: [] }));
    socket.once("connect", () => {
      const lines = [
        `GET ${request.target} HTTP/1.1`,
        `Host: ${request.host}`,
        "Upgrade: websocket",
        "Connection: Upgrade",
        "Sec-WebSocket-Version: 13",
        `Sec-WebSocket-Key: ${key}`,
        ...(request.headers ?? []).map(([name, value]) => `${name}: ${value}`),
      ];
      socket.write(lines.join("\r\n") + "\r\n\r\n");
    });
    socket.on("data", (chunk: Buffer) => {
      buffer = concat(buffer, new Uint8Array(chunk));
      const end = Buffer.from(buffer).indexOf("\r\n\r\n");
      if (end < 0) {
        if (buffer.length > 16384) finish({ status: null, headers: [], problem: "the response head is longer than 16 KiB" });
        return;
      }
      const head = new TextDecoder().decode(buffer.subarray(0, end)).split("\r\n");
      const status = Number(head[0].split(" ")[1]);
      const headers = head.slice(1).map((line): [string, string] => {
        const colon = line.indexOf(":");
        return [line.slice(0, colon).trim().toLowerCase(), line.slice(colon + 1).trim()];
      });
      if (status !== 101) return finish({ status, headers });
      const accept = headers.find(([name]) => name === "sec-websocket-accept")?.[1];
      const expected = createHash("sha1").update(key + GUID).digest("base64");
      if (accept !== expected) return finish({ status, headers, problem: `Sec-WebSocket-Accept is ${accept}, not ${expected}` });
      socket.removeAllListeners("data");
      finish({ status, headers, socket: new WebSocketClient(socket, buffer.subarray(end + 4)) });
    });
  });
}

export class WebSocketClient {
  private buffer: Uint8Array;
  private queue: (Message | null)[] = [];
  private waiters: (() => void)[] = [];
  private fragments: Uint8Array[] = [];
  private fragmentKind = 0;
  closed = false;
  closeCode: number | null = null;

  constructor(private readonly socket: Socket, rest: Uint8Array) {
    this.buffer = rest;
    socket.removeAllListeners("close");
    socket.removeAllListeners("error");
    socket.on("data", (chunk: Buffer) => {
      this.buffer = concat(this.buffer, new Uint8Array(chunk));
      this.parse();
    });
    const end = () => this.end();
    socket.on("close", end);
    socket.on("error", end);
    this.parse();
  }

  private end() {
    if (this.closed) return;
    this.closed = true;
    this.queue.push(null);
    this.wake();
  }

  private wake() {
    for (const waiter of this.waiters.splice(0)) waiter();
  }

  private parse() {
    for (;;) {
      const b = this.buffer;
      if (b.length < 2) return;
      const fin = (b[0] & 0x80) !== 0;
      const opcode = b[0] & 0x0f;
      let length = b[1] & 0x7f;
      let at = 2;
      if (length === 126) {
        if (b.length < 4) return;
        length = (b[2] << 8) | b[3];
        at = 4;
      } else if (length === 127) {
        if (b.length < 10) return;
        length = Number(new DataView(b.buffer, b.byteOffset + 2, 8).getBigUint64(0));
        at = 10;
      }
      const masked = (b[1] & 0x80) !== 0;
      const maskAt = at;
      if (masked) at += 4;
      if (b.length < at + length) return;
      let payload = b.slice(at, at + length);
      if (masked) payload = payload.map((byte, i) => byte ^ b[maskAt + (i % 4)]);
      this.buffer = b.slice(at + length);
      if (opcode === 8) {
        this.closeCode = payload.length >= 2 ? (payload[0] << 8) | payload[1] : null;
        this.write(8, payload.subarray(0, 2));
        this.end();
        this.socket.end();
        return;
      }
      if (opcode === 9) {
        this.write(10, payload);
        continue;
      }
      if (opcode === 10) continue;
      if (opcode !== 0) this.fragmentKind = opcode;
      this.fragments.push(payload);
      if (!fin) continue;
      const data = this.fragments.reduce(concat, new Uint8Array());
      this.fragments = [];
      this.queue.push(this.fragmentKind === 1 ? { kind: "text", text: new TextDecoder().decode(data) } : { kind: "binary", data });
      this.wake();
    }
  }

  // The next message; null once the peer closed or ended the connection; undefined after `timeoutMs`.
  async next(timeoutMs: number): Promise<Message | null | undefined> {
    const deadline = Date.now() + timeoutMs;
    while (!this.queue.length) {
      const left = deadline - Date.now();
      if (left <= 0) return undefined;
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, left);
        this.waiters.push(() => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
    const message = this.queue[0];
    if (message !== null) this.queue.shift();
    return message;
  }

  // A message that arrived and no step took.
  peek(): Message | undefined {
    const message = this.queue[0];
    return message ?? undefined;
  }

  private write(opcode: number, payload: Uint8Array) {
    if (this.socket.destroyed) return;
    const head = payload.length < 126 ? [0x80 | opcode, 0x80 | payload.length] : payload.length <= 0xffff ? [0x80 | opcode, 0x80 | 126, payload.length >> 8, payload.length & 0xff] : null;
    const bytes = head
      ? new Uint8Array(head)
      : (() => {
          const long = new Uint8Array(10);
          long[0] = 0x80 | opcode;
          long[1] = 0x80 | 127;
          new DataView(long.buffer).setBigUint64(2, BigInt(payload.length));
          return long;
        })();
    const mask = randomBytes(4);
    const body = payload.map((byte, i) => byte ^ mask[i % 4]);
    try {
      this.socket.write(concat(concat(bytes, new Uint8Array(mask)), body));
    } catch {}
  }

  sendText(text: string) {
    this.write(1, new TextEncoder().encode(text));
  }

  sendBinary(data: Uint8Array) {
    this.write(2, data);
  }

  close(code = 1000) {
    if (!this.closed) this.write(8, new Uint8Array([code >> 8, code & 0xff]));
    setTimeout(() => this.socket.destroy(), 200);
  }

  destroy() {
    this.socket.destroy();
  }
}
