import { MvvmClient } from "./client.js";
import { createMvvmProjection, type MvvmProjection } from "./projection.js";
import { ProtocolTransport, type FrameChannel } from "./transport.js";
import type { MvvmDevelopmentInspector } from "./inspector.js";

/** Options for the shared framework-neutral MVVM application bootstrap. */
export interface MvvmApplicationOptions {
  readonly contract: string;
  readonly channel: FrameChannel;
  readonly clientId?: string;
  /** Opt-in bounded, sanitized development inspection. */
  readonly inspector?: MvvmDevelopmentInspector;
}

/** One opened MVVM application and its exact owned lifetime. */
export interface MvvmApplication {
  readonly projection: MvvmProjection;
  /**
   * Rebinds an already-open logical session to a replacement physical channel
   * and recovers authoritative state before resolving.
   */
  reconnect(channel: FrameChannel): Promise<void>;
  dispose(reason?: string): Promise<void>;
}

/**
 * Opens the transport, client, contract, and immutable projection as one owned
 * application lifetime. Framework adapters can wrap only the returned projection.
 */
export async function startMvvmApplication(
  options: Readonly<MvvmApplicationOptions>,
): Promise<MvvmApplication> {
  if (options.contract.length === 0) throw new TypeError("An MVVM contract is required.");
  const transport = new ProtocolTransport(options.channel);
  const stopInspection = options.inspector?.attach(transport);
  let transportFailure: Error | undefined;
  const stopDiagnostics = transport.subscribe((event) => {
    if (event.type === "protocolError") transportFailure = event.error;
  });
  const client = new MvvmClient(transport);
  const projection = createMvvmProjection(client);
  try {
    await client.start(options.contract, options.clientId ?? crypto.randomUUID());
  } catch (error) {
    projection.dispose();
    stopInspection?.();
    await transport.close("MVVM application startup failed");
    const detail = transportFailure === undefined
      ? ""
      : ` Transport: ${transportFailure.message}` +
        (transportFailure.cause instanceof Error
          ? ` Cause: ${transportFailure.cause.message}`
          : "");
    throw new Error(
      `${error instanceof Error ? error.message : "Unknown startup failure."}${detail}`,
      { cause: error },
    );
  } finally {
    stopDiagnostics();
  }

  let disposed = false;
  let reconnecting = false;
  let reconnectCompletion: Promise<void> | undefined;
  return {
    projection,
    async reconnect(channel) {
      if (disposed) throw new Error("A disposed MVVM application cannot reconnect.");
      if (reconnecting) throw new Error("An MVVM application reconnect is already in progress.");
      if (transport.state !== "disconnected") {
        throw new Error("The current MVVM channel must disconnect before it can be replaced.");
      }

      reconnecting = true;
      reconnectCompletion = (async () => {
        try {
          transport.replaceChannel(channel);
          await client.reconnect();
        } catch (error) {
          transport.disconnect(error);
          throw error;
        } finally {
          reconnecting = false;
        }
      })();
      await reconnectCompletion;
    },
    async dispose(reason = "MVVM application unloaded") {
      if (disposed) return;
      disposed = true;
      try {
        await reconnectCompletion;
      } catch {
        // Reconnect already reported its failure to its caller. Disposal still
        // owns deterministic projection and channel cleanup.
      }
      projection.dispose();
      stopInspection?.();
      await transport.close(reason);
    },
  };
}
