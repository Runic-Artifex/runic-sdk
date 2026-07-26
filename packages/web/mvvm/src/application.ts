import { MvvmClient } from "./client.js";
import { createMvvmProjection, type MvvmProjection } from "./projection.js";
import { ProtocolTransport, type FrameChannel } from "./transport.js";

/** Options for the shared framework-neutral MVVM application bootstrap. */
export interface MvvmApplicationOptions {
  readonly contract: string;
  readonly channel: FrameChannel;
  readonly clientId?: string;
}

/** One opened MVVM application and its exact owned lifetime. */
export interface MvvmApplication {
  readonly projection: MvvmProjection;
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
  return {
    projection,
    async dispose(reason = "MVVM application unloaded") {
      if (disposed) return;
      disposed = true;
      projection.dispose();
      await transport.close(reason);
    },
  };
}
