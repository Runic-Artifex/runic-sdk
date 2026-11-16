// Runic's events over Vite's HMR connection under the access rules of the HTTP endpoints. Vite's WebSocket server asks
// for its token only when the upgrade request carries an Origin, so any client that reaches the server, a program of
// another machine included, can connect. The gate remembers each connection's socket and whether the upgrade request's
// Host passed, and admits a connection when its peer address is loopback or one the access list admits.

import type { IncomingMessage } from "node:http";
import type { AccessList } from "./access.js";
import type { PeerSocket } from "./local-user.js";

/** A connection as the gate saw its upgrade request. */
export interface GatedPeer {
  /** The TCP socket of the upgrade request: its peer address and endpoints. */
  readonly socket: PeerSocket;
  /** Whether the upgrade request's Host passed the plugin's host check. */
  readonly trustedHost: boolean;
}

/** A client that the gate can send to: Vite's `WebSocketClient`. */
export interface GatedClient {
  readonly socket?: object;
  send(event: string, payload?: unknown): void;
}

export interface ClientGate {
  /** Records a connection: Vite's `connection` event, with the raw WebSocket and its upgrade request. */
  connect(socket: object, request: IncomingMessage): void;
  /** The recorded connection of a raw WebSocket, or undefined. */
  peer(socket: object | undefined): GatedPeer | undefined;
  /** Whether a connection is admitted now. An unknown connection is not. */
  admits(socket: object | undefined): Promise<boolean>;
  /**
   * Runs `action` once the connection is admitted, and never when it is not. Actions of one connection run in the order
   * they were given, so the page's events reach the timeline in the order it sent them.
   */
  whenAdmitted(socket: object | undefined, action: () => void): void;
  /**
   * Sends an event to every admitted client. The admission is asynchronous, so a client receives the event's newest
   * payload at that point: an older state never arrives after a newer one.
   */
  broadcast(event: string, payload: unknown): void;
}

/**
 * The gate of one development server. `clients` lists the connected clients (Vite's `server.ws.clients`), and
 * `trustedHost` is the server's host check of an upgrade request.
 */
export function createClientGate(
  access: AccessList,
  trustedHost: (request: IncomingMessage) => boolean,
  clients: () => Iterable<GatedClient> = () => [],
): ClientGate {
  const peers = new WeakMap<object, GatedPeer>();
  const queues = new WeakMap<object, Promise<void>>();
  const latest = new Map<string, unknown>();
  const gate: ClientGate = {
    connect(socket: object, request: IncomingMessage) {
      const raw = request.socket;
      peers.set(socket, {
        socket: { remoteAddress: raw?.remoteAddress, remotePort: raw?.remotePort, localAddress: raw?.localAddress, localPort: raw?.localPort },
        trustedHost: trustedHost(request),
      });
    },
    peer: (socket: object | undefined) => socket === undefined ? undefined : peers.get(socket),
    async admits(socket: object | undefined): Promise<boolean> {
      const peer = gate.peer(socket);
      return peer !== undefined && peer.trustedHost && await access.allows(peer.socket.remoteAddress);
    },
    whenAdmitted(socket: object | undefined, action: () => void) {
      if (socket === undefined) return;
      const next = (queues.get(socket) ?? Promise.resolve())
        .then(() => gate.admits(socket))
        .then((admitted) => { if (admitted) action(); })
        .catch(() => undefined);
      queues.set(socket, next);
    },
    broadcast(event: string, payload: unknown) {
      latest.set(event, payload);
      for (const client of clients()) {
        gate.whenAdmitted(client.socket, () => client.send(event, latest.get(event)));
      }
    },
  };
  return gate;
}
