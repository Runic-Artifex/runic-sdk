// Page-transport profiles of the region steps (specs/desktop/host-conformance/README.md, "Page transport"). A
// `frame` or `component` step checks the HTTP and upgrade level itself: statuses, headers and the subprotocol. How a
// page then proves it is the page, so that the guest counts it, belongs to the surface's own page transport, which the
// corpus does not define (R2 DT-1). The adapter names its profile in `describe` (`pageTransport`); the driver knows
// these. An implementation whose profile the driver does not know reports the region steps that need one unsupported.
import type { WebSocketClient } from "./socket";

export type PageTransport = {
  // The surface-relative path of the page's bootstrap script, and whether a bootstrap text carries a session credential.
  bootstrap: string;
  carriesCredential: (text: string) => boolean;
  // The surface-relative path of the page's WebSocket, and its pattern in a component attach's reply.
  socket: string;
  componentUrl: RegExp;
  // After a 101 from a frame surface: admit the page as its bootstrap tells it to. `bootstrapText` is the bootstrap the
  // runner fetched first, or null when the bootstrap was not served (the step then reports that status).
  admitFrame: (socket: WebSocketClient, bootstrapText: string) => void;
  // After a 101 from a component's WebSocket.
  admitComponent: (socket: WebSocketClient) => void;
  // Whether a frame upgrade needs the bootstrap first.
  frameNeedsBootstrap: boolean;
};

// The Bridge's token check: 0xDD, the bootstrap's TOKEN (little-endian), a correlation id of 0 and the command 0xF5.
function tokenCheck(token: number): Uint8Array {
  const packet = new Uint8Array(9);
  packet[0] = 0xdd;
  new DataView(packet.buffer).setUint32(1, token >>> 0, true);
  packet[7] = 0xf5;
  return packet;
}

// webui-compat/52f9e75, Runic.Desktop's private profile (specs/desktop/wire-profile.md).
const webuiCompat: PageTransport = {
  bootstrap: "webui.js",
  carriesCredential: (text) => text.includes('const SESSION_CREDENTIAL="'),
  socket: "_webui_ws_connect",
  componentUrl: /^ws:\/\/(127\.0\.0\.1|\[::1\]):\d+(\/[A-Za-z0-9._~-]+)*\/_webui_ws_connect$/,
  frameNeedsBootstrap: true,
  admitFrame: (socket, bootstrapText) => {
    const match = /const TOKEN=(\d+);/.exec(bootstrapText);
    if (!match) throw new Error("the frame surface's bootstrap names no TOKEN");
    socket.sendBinary(tokenCheck(Number(match[1])));
  },
  admitComponent: (socket) => socket.sendBinary(tokenCheck(0)),
};

export const PAGE_TRANSPORTS: Record<string, PageTransport> = { "webui-compat/52f9e75": webuiCompat };
