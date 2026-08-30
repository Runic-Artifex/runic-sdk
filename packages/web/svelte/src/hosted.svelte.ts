export interface RunicHostedBrowserBootstrap {
  readonly schema: "runic.hosted-bootstrap/1";
  readonly fingerprint: string;
}

export interface RunicHostedBrowserBridge {
  start(): Promise<unknown>;
  dispose(): Promise<void>;
}

/**
 * Starts a browser bridge only after the SSR bootstrap marker has been checked.
 * The marker is a hydration-consistency guard, not an authorization credential.
 */
export function startRunicHostedBridgeAfterBootstrap(
  bootstrap: RunicHostedBrowserBootstrap,
  bridge: RunicHostedBrowserBridge,
): boolean {
  if (bootstrap?.schema !== "runic.hosted-bootstrap/1" || !isFingerprint(bootstrap.fingerprint)) {
    throw new TypeError("The Runic hosted browser bootstrap is malformed.");
  }
  if (!bridge || typeof bridge.start !== "function" || typeof bridge.dispose !== "function") {
    throw new TypeError("The Runic hosted browser bridge is malformed.");
  }
  const marker = document.querySelector('meta[name="runic-hosted-bootstrap"]');
  if (!(marker instanceof HTMLMetaElement) || marker.content !== bootstrap.fingerprint) {
    document.documentElement.dataset.runicHostedHydration = "mismatch";
    return false;
  }
  delete document.documentElement.dataset.runicHostedHydration;
  void bridge.start().catch(() => undefined);
  return true;
}

function isFingerprint(value: unknown): value is string {
  return typeof value === "string" && /^runic-hosted-[a-f0-9]{8}$/.test(value);
}
