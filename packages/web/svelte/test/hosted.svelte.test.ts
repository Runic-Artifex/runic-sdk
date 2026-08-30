// @vitest-environment happy-dom

import { hydrate, tick, unmount } from "svelte";
import { resolve } from "node:path";
import { createServer } from "vite";
import { describe, expect, test, vi } from "vitest";
import HostedBootstrapFixture from "./HostedBootstrapFixture.svelte";

const bootstrap = { schema: "runic.hosted-bootstrap/1" as const, fingerprint: "runic-hosted-1234abcd" };

describe("Runic hosted browser bootstrap", () => {
  test("does not start a bridge during SSR and starts only after the matching hydration marker", async () => {
    const bridge = { start: vi.fn(async () => undefined), dispose: vi.fn(async () => undefined) };
    const vite = await createServer({
      root: resolve("."),
      configFile: resolve("vite.config.ts"),
      appType: "custom",
      server: { middlewareMode: true },
    });
    const serverModule = await vite.ssrLoadModule("/test/HostedBootstrapFixture.svelte");
    const { render } = await vite.ssrLoadModule("svelte/server");
    const ssr = render(serverModule.default, { props: { bootstrap, bridge } });
    await vite.close();
    expect(bridge.start).not.toHaveBeenCalled();

    document.head.innerHTML = ssr.head;
    document.body.innerHTML = ssr.body;
    const component = hydrate(HostedBootstrapFixture, { target: document.body, props: { bootstrap, bridge } });
    await vi.waitFor(() => expect(bridge.start).toHaveBeenCalledOnce());
    await unmount(component);
    expect(bridge.dispose).toHaveBeenCalledOnce();
  });

  test("fails closed before bridge start when the hydration marker changes", async () => {
    const bridge = { start: vi.fn(async () => undefined), dispose: vi.fn(async () => undefined) };
    document.head.innerHTML = '<meta name="runic-hosted-bootstrap" content="runic-hosted-deadbeef">';
    document.body.innerHTML = '<output data-hosted-bootstrap>runic-hosted-1234abcd</output>';
    hydrate(HostedBootstrapFixture, { target: document.body, props: { bootstrap, bridge } });
    await tick();
    expect(bridge.start).not.toHaveBeenCalled();
    expect(document.documentElement.dataset.runicHostedHydration).toBe("mismatch");
  });
});
