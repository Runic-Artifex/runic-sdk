import {
  startMvvmApplication,
  type MvvmApplication,
} from "./application.js";
import type { MvvmProjection } from "./projection.js";
import type { FrameChannel } from "./transport.js";
import type { MvvmDevelopmentInspector } from "./inspector.js";

const defaultBridgeAsset = "vendor/webuitoolkit-mvvm-cswebui.mjs";

/** A generated contract class that can be opened over an MVVM projection. */
export interface MvvmContractConstructor<TContract> {
  readonly contractName: string;
  new(projection: MvvmProjection): TContract;
}

/** Public shape exported by the CsWebUi MVVM bridge asset. */
export interface CsWebUiMvvmBridge {
  readonly CsWebUiFrameChannel: new () => FrameChannel;
  waitForCsWebUiBinding(): Promise<void>;
}

/** Minimal page lifecycle used for automatic native application teardown. */
export interface MvvmPageLifetime {
  addEventListener(
    type: "pagehide",
    listener: () => void,
    options?: AddEventListenerOptions | boolean,
  ): void;
  removeEventListener(type: "pagehide", listener: () => void): void;
}

/** Options for the high-level native CsWebUi MVVM application owner. */
export interface NativeMvvmApplicationOptions<TContract> {
  readonly contract: MvvmContractConstructor<TContract>;
  readonly clientId?: string;
  /**
   * Development/test host seam. When supplied, the native bridge is not
   * imported and each initial connection or reconnect asks this factory for a
   * production-protocol channel. This is the supported entrypoint for
   * MvvmMockFrameChannel and semantic replay fixtures.
   */
  readonly channelFactory?: () => FrameChannel | Promise<FrameChannel>;
  /**
   * URL of the published CsWebUi bridge. The default resolves the standard
   * `vendor/webuitoolkit-mvvm-cswebui.mjs` asset against `document.baseURI`.
   */
  readonly bridgeUrl?: string | URL;
  /** Test/custom-host seam. Normal applications use the published bridge URL. */
  readonly loadBridge?: () => Promise<CsWebUiMvvmBridge>;
  /**
   * Page lifetime that owns automatic disposal. Set to `null` when another
   * framework owner deliberately manages the full application lifetime.
   */
  readonly pageLifetime?: MvvmPageLifetime | null;
  /** Opt-in sanitized inspector. Omit it from production entrypoints. */
  readonly inspector?: MvvmDevelopmentInspector;
}

/** One typed native application, including its contract and exact lifetime. */
export interface NativeMvvmApplication<TContract> {
  readonly projection: MvvmProjection;
  readonly contract: TContract;
  /**
   * Registers framework-owned cleanup under the same page/disposal lifetime.
   * Cleanups run once in reverse registration order before the transport closes.
   */
  addCleanup(cleanup: () => void | Promise<void>): () => void;
  reconnect(): Promise<void>;
  dispose(reason?: string): Promise<void>;
}

/**
 * Opens the private CsWebUi binding, binary channel, MVVM session, generated
 * contract, reconnect path, and page teardown as one typed application owner.
 */
export async function startNativeMvvmApplication<TContract>(
  options: Readonly<NativeMvvmApplicationOptions<TContract>>,
): Promise<NativeMvvmApplication<TContract>> {
  const createChannel = await resolveChannelFactory(options);
  let channel = await createChannel();
  const application = await startMvvmApplication({
    contract: options.contract.contractName,
    channel,
    ...(options.clientId === undefined ? {} : { clientId: options.clientId }),
    ...(options.inspector === undefined ? {} : { inspector: options.inspector }),
  });
  const contract = new options.contract(application.projection);
  const pageLifetime = options.pageLifetime === undefined
    ? defaultPageLifetime()
    : options.pageLifetime;
  const cleanups: Array<() => void | Promise<void>> = [];
  let disposed = false;

  const pagehide = (): void => {
    void dispose("Native MVVM page unloaded");
  };
  pageLifetime?.addEventListener("pagehide", pagehide, { once: true });

  async function dispose(reason = "Native MVVM application unloaded"): Promise<void> {
    if (disposed) return;
    disposed = true;
    pageLifetime?.removeEventListener("pagehide", pagehide);
    let cleanupFailure: unknown;
    for (const cleanup of cleanups.splice(0).reverse()) {
      try {
        await cleanup();
      } catch (error) {
        cleanupFailure ??= error;
      }
    }
    await application.dispose(reason);
    if (cleanupFailure !== undefined) {
      throw new AggregateError(
        [cleanupFailure],
        "A native MVVM framework cleanup failed.",
      );
    }
  }

  return Object.freeze({
    projection: application.projection,
    contract,
    addCleanup(cleanup: () => void | Promise<void>): () => void {
      if (disposed) throw new Error("A disposed native MVVM application cannot own cleanup.");
      if (typeof cleanup !== "function") throw new TypeError("Cleanup must be a function.");
      cleanups.push(cleanup);
      let registered = true;
      return (): void => {
        if (!registered) return;
        registered = false;
        const index = cleanups.indexOf(cleanup);
        if (index >= 0) cleanups.splice(index, 1);
      };
    },
    async reconnect(): Promise<void> {
      if (disposed) throw new Error("A disposed native MVVM application cannot reconnect.");
      await channel.close("Native MVVM reconnect");
      channel = await createChannel();
      await application.reconnect(channel);
    },
    dispose,
  });
}

async function resolveChannelFactory<TContract>(
  options: Readonly<NativeMvvmApplicationOptions<TContract>>,
): Promise<() => Promise<FrameChannel>> {
  if (options.channelFactory !== undefined) {
    if (options.loadBridge !== undefined || options.bridgeUrl !== undefined) {
      throw new TypeError(
        "channelFactory cannot be combined with loadBridge or bridgeUrl.",
      );
    }
    return async () => {
      const channel = await options.channelFactory!();
      if (
        channel === null ||
        typeof channel !== "object" ||
        typeof channel.send !== "function" ||
        typeof channel.close !== "function" ||
        typeof channel.subscribe !== "function"
      ) {
        throw new TypeError("channelFactory did not return a frame channel.");
      }
      return channel;
    };
  }

  const loadBridge = options.loadBridge ?? (() => importBridge(options.bridgeUrl));
  const bridge = await loadBridge();
  assertBridge(bridge);
  return async () => {
    await bridge.waitForCsWebUiBinding();
    return new bridge.CsWebUiFrameChannel();
  };
}

async function importBridge(
  configuredUrl: string | URL | undefined,
): Promise<CsWebUiMvvmBridge> {
  const url = configuredUrl === undefined
    ? defaultBridgeUrl()
    : configuredUrl.toString();
  return await import(/* @vite-ignore */ url) as CsWebUiMvvmBridge;
}

function defaultBridgeUrl(): string {
  if (typeof document === "undefined") {
    throw new Error(
      "A CsWebUi bridge URL or loadBridge callback is required outside a browser document.",
    );
  }
  return new URL(defaultBridgeAsset, document.baseURI).href;
}

function defaultPageLifetime(): MvvmPageLifetime | null {
  if (
    typeof globalThis.addEventListener !== "function" ||
    typeof globalThis.removeEventListener !== "function"
  ) {
    return null;
  }
  return globalThis as unknown as MvvmPageLifetime;
}

function assertBridge(bridge: CsWebUiMvvmBridge): void {
  if (
    bridge === null ||
    typeof bridge !== "object" ||
    typeof bridge.CsWebUiFrameChannel !== "function" ||
    typeof bridge.waitForCsWebUiBinding !== "function"
  ) {
    throw new TypeError("The CsWebUi MVVM bridge does not expose the required API.");
  }
}
