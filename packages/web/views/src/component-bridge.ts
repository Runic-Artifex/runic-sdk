// Trusted component mode (host-protocol.md, `region-component/1`): the Bridge of a component, a WebSocket from the host
// page's document to the guest's surface. The guest admits the upgrade only with the component token as its subprotocol
// (`runic-desktop.<token>`) and with the host page's origin as `Origin`; no cookie takes part. Over the socket it speaks
// the same wire as the guest's own page (Runic Desktop's webui.js): binary packets with an 8-byte header, Views
// requests as calls of `__runicViews`, and Views pushes as raw data for `__runicViewsPush`. A component never runs
// script that the guest sends and never navigates the host page.
import type { RunicBridgeClient } from "./transport.js";

/** Where a component's Bridge connects: the guest's surface and the component token (`region-component/1`). */
export interface RunicComponentEndpoint {
  /** The WebSocket address of the guest's surface, `ws://` on a loopback address. */
  readonly url: string;
  /** The component token, a bearer credential of the guest. */
  readonly token: string;
}

/** The Bridge of a component: a {@link RunicBridgeClient} that the element closes when it ends. */
export interface RunicComponentBridge extends RunicBridgeClient {
  /** Closes the connection for good: pending requests reject, and it does not connect again. */
  close(): void;
}

const signature = 0xDD;
const commandScript = 0xFE;
const commandCall = 0xF9;
const commandRaw = 0xF8;
const commandClose = 0xFA;
const commandMulti = 0xF6;
const commandCheckToken = 0xF5;
const headerSize = 8;
const multiChunkSize = 65_500;
const viewsFunction = "__runicViews";
const viewsPush = "__runicViewsPush";
const pingInterval = 20_000;
const firstRetry = 500;
const lastRetry = 5_000;

/**
 * Connects a component to its guest with the endpoint that the host page received for it (`hosted-region-content` with
 * the embedding `component`). The connection opens at once and opens again after a loss, until {@link
 * RunicComponentBridge.close} or until the guest refuses the token, as it does once the region was detached.
 *
 * @throws TypeError when `url` is not a `ws:` or `wss:` address or `token` is empty.
 */
export function connectComponentBridge(endpoint: RunicComponentEndpoint): RunicComponentBridge {
  const url = new URL(endpoint?.url ?? "");
  if (url.protocol !== "ws:" && url.protocol !== "wss:") throw new TypeError("A component connects to a ws: or wss: address.");
  const token = endpoint.token;
  if (typeof token !== "string" || !/^[A-Za-z0-9_-]{1,256}$/.test(token))
    throw new TypeError("A component connects with its component token.");
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const messageListeners = new Set<(message: string) => void>();
  const connectionListeners = new Set<(connected: boolean) => void>();
  const pending = new Map<number, { resolve(value: string): void; reject(error: unknown): void }>();
  let socket: WebSocket | undefined;
  let connected = false;
  let closed = false;
  let nextId = 0;
  let retry = firstRetry;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  let ping: ReturnType<typeof setInterval> | undefined;

  const report = (now: boolean) => {
    if (now === connected) return;
    connected = now;
    for (const listener of [...connectionListeners]) {
      try { listener(now); } catch (error) { console.error("Runic connection listener failed", error); }
    }
  };

  // The packet header: signature, a window token of 0 (the component token admitted the socket), id and command.
  const packet = (length: number, id: number, command: number): Uint8Array<ArrayBuffer> => {
    const bytes = new Uint8Array(headerSize + length);
    bytes[0] = signature;
    new DataView(bytes.buffer).setUint16(5, id, true);
    bytes[7] = command;
    return bytes;
  };

  const send = (bytes: Uint8Array<ArrayBuffer>) => {
    if (socket?.readyState !== WebSocket.OPEN) throw new Error("The component's connection is not open.");
    if (bytes.length < multiChunkSize) { socket.send(bytes); return; }
    const length = encoder.encode(String(bytes.length));
    const header = packet(length.length + 1, 0, commandMulti);
    header.set(length, headerSize);
    socket.send(header);
    for (let offset = 0; offset < bytes.length; offset += multiChunkSize)
      socket.send(bytes.subarray(offset, Math.min(offset + multiChunkSize, bytes.length)));
  };

  const readText = (bytes: Uint8Array, offset: number) => {
    let end = bytes.indexOf(0, offset);
    if (end < 0) end = bytes.length;
    return decoder.decode(bytes.subarray(offset, end));
  };

  const failPending = (error: Error) => {
    for (const call of pending.values()) call.reject(error);
    pending.clear();
  };

  const receive = (event: MessageEvent) => {
    if (!(event.data instanceof ArrayBuffer)) return;
    const bytes = new Uint8Array(event.data);
    if (bytes.length < headerSize || bytes[0] !== signature) return;
    const id = new DataView(bytes.buffer).getUint16(5, true);
    switch (bytes[7]) {
      case commandCheckToken:
        // A refused token is refused for good: the region was detached, or the token is not this attachment's.
        if (bytes[headerSize] !== 1) stop("The guest refused the component token.");
        return;
      case commandCall: {
        const call = pending.get(id);
        if (!call) return;
        pending.delete(id);
        call.resolve(readText(bytes, headerSize));
        return;
      }
      case commandRaw: {
        let separator = bytes.indexOf(0, headerSize);
        if (separator < 0) separator = bytes.length;
        if (decoder.decode(bytes.subarray(headerSize, separator)) !== viewsPush) return;
        const message = decoder.decode(bytes.subarray(separator + 1, Math.max(separator + 1, bytes.length - 1)));
        for (const listener of [...messageListeners]) {
          try { listener(message); } catch (error) { console.error("Runic Views message listener failed", error); }
        }
        return;
      }
      case commandScript: {
        // The guest's script never runs in the host page's realm: the call fails.
        const text = encoder.encode("A component does not run scripts of its guest.");
        const reply = packet(text.length + 2, id, commandScript);
        reply[headerSize] = 1;
        reply.set(text, headerSize + 1);
        try { send(reply); } catch { /* closed */ }
        return;
      }
      case commandClose:
        stop("The guest closed the component's connection.");
        return;
      default:
        // Navigation, bindings, clicks and the rest of the page wire do not apply to a component.
        return;
    }
  };

  const connect = () => {
    retryTimer = undefined;
    if (closed) return;
    const current = new WebSocket(url, `runic-desktop.${token}`);
    current.binaryType = "arraybuffer";
    socket = current;
    current.addEventListener("open", () => {
      if (socket !== current) return;
      retry = firstRetry;
      // The token check goes first; requests may follow it at once, since the guest reads a socket's packets in order.
      try { send(packet(1, 0, commandCheckToken)); } catch { return; }
      report(true);
    });
    current.addEventListener("message", receive);
    current.addEventListener("close", () => {
      if (socket !== current) return;
      socket = undefined;
      failPending(new Error("The component's connection was closed."));
      report(false);
      if (closed) return;
      retryTimer = setTimeout(connect, retry);
      retry = Math.min(retry * 2, lastRetry);
    });
  };

  const stop = (reason: string) => {
    if (closed) return;
    closed = true;
    clearTimeout(retryTimer);
    clearInterval(ping);
    const current = socket;
    socket = undefined;
    failPending(new Error(reason));
    report(false);
    current?.close();
  };

  ping = setInterval(() => {
    if (socket?.readyState === WebSocket.OPEN) socket.send(encoder.encode("ping"));
  }, pingInterval);
  connect();

  return {
    isConnected: () => connected,
    request(message: string): Promise<string> {
      if (typeof message !== "string") return Promise.reject(new TypeError("A Bridge request is JSON text."));
      const name = encoder.encode(viewsFunction);
      const value = encoder.encode(message);
      const lengths = encoder.encode(String(value.length));
      const id = nextId = (nextId - 1) & 0xFFFF;
      const bytes = packet(name.length + 1 + lengths.length + 1 + value.length + 1, id, commandCall);
      bytes.set(name, headerSize);
      bytes.set(lengths, headerSize + name.length + 1);
      bytes.set(value, headerSize + name.length + 1 + lengths.length + 1);
      return new Promise<string>((resolve, reject) => {
        try { send(bytes); }
        catch (error) { reject(error); return; }
        pending.set(id, { resolve, reject });
      });
    },
    onMessage(listener) {
      const entry = (message: string) => listener(message);
      messageListeners.add(entry);
      return () => { messageListeners.delete(entry); };
    },
    onConnectionChange(listener) {
      let reported: boolean | undefined;
      let active = true;
      const entry = (now: boolean) => {
        if (!active || now === reported) return;
        reported = now;
        listener(now);
      };
      connectionListeners.add(entry);
      try { entry(connected); } catch (error) { console.error("Runic connection listener failed", error); }
      return () => { active = false; connectionListeners.delete(entry); };
    },
    close: () => stop("The component's connection was closed."),
  };
}
