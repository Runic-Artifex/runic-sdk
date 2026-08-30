import { readFile, rm } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { afterEach, describe, expect, test, vi } from "vitest";
import { runicToolkitAdapter } from "../src/index.js";

const out = resolve(".tmp-adapter-test");

afterEach(async () => rm(out, { recursive: true, force: true }));

describe("runicToolkitAdapter", () => {
  test("delegates static output and writes a deterministic native manifest", async () => {
    const adapter = runicToolkitAdapter({ mode: "spa", desktop: true, out });
    const log = Object.assign(vi.fn(), {
      minor: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn(), success: vi.fn(),
    });
    const builder = desktopSpaBuilder({
      html: '<html><head><link href="/_app/client.js"><script>import("/_app/start.js")</script></head></html>',
      log,
    });
    await adapter.adapt(builder as never);
    const entrypoint = await readFile(resolve(out, "200.html"), "utf8");
    const manifest = JSON.parse(await readFile(resolve(out, "runic-toolkit.sveltekit.json"), "utf8"));
    expect(entrypoint).toContain('href="./_app/client.js"');
    expect(entrypoint).toContain('import("./_app/start.js")');
    expect(entrypoint).toContain('<script src="./runic-desktop.js"></script>');
    expect(entrypoint.indexOf("runic-desktop.js")).toBeLessThan(entrypoint.indexOf("./_app/start.js"));
    expect(manifest).toEqual({
      schema: "runic-toolkit.sveltekit/1",
      mode: "spa",
      entrypoint: "200.html",
      fallback: "200.html",
      routes: ["/"],
    });
  });

  test("relocates a nested Desktop fallback and a nested SvelteKit app directory", async () => {
    const adapter = runicToolkitAdapter({
      mode: "spa",
      desktop: true,
      fallback: "nested/200.html",
      out,
    });
    const builder = desktopSpaBuilder({
      appDir: "client/_app",
      html: '<html><head><img src="./runic-desktop.js"><script>import("/client/_app/start.js")</script></head></html>',
    });

    await adapter.adapt(builder as never);

    const entrypoint = await readFile(resolve(out, "nested/200.html"), "utf8");
    expect(entrypoint).toContain('<script src="../runic-desktop.js"></script>');
    expect(entrypoint).toContain('import("../client/_app/start.js")');
  });

  test("rejects a manually inserted Desktop bootstrap with flexible HTML spacing", async () => {
    const adapter = runicToolkitAdapter({ mode: "spa", desktop: true, out });
    const builder = desktopSpaBuilder({
      html: '<html><head><script src = "./runic-desktop.js"></script></head></html>',
    });

    await expect(adapter.adapt(builder as never)).rejects.toThrow("owns bootstrap insertion");
  });

  test("rejects a pathname-routed SPA that cannot move under a native surface namespace", async () => {
    const adapter = runicToolkitAdapter({ mode: "spa", desktop: true, out });
    const builder = {
      config: { kit: { router: { type: "pathname" } } },
    };

    await expect(adapter.adapt(builder as never)).rejects.toThrow("requires kit.router.type to be 'hash'");
  });

  test("fails closed when a multi-page prerendered app has no deterministic entrypoint", async () => {
    const adapter = runicToolkitAdapter({ mode: "prerendered", out });
    const log = Object.assign(vi.fn(), {
      minor: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn(), success: vi.fn(),
    });
    const builder = {
      routes: [],
      prerendered: {
        pages: new Map([
          ["/de/setup", { file: "de/setup.html" }],
          ["/setup", { file: "setup.html" }],
        ]),
        assets: new Map(),
        redirects: new Map(),
        paths: ["/de/setup", "/setup"],
      },
      rimraf: vi.fn(),
      mkdirp: vi.fn(),
      generateEnvModule: vi.fn(),
      writeClient: vi.fn(),
      writePrerendered: vi.fn(),
      log,
      config: {
        kit: {
          paths: { base: "" },
          appDir: "_app",
          router: { type: "pathname" },
          files: { routes: "src/routes" },
          prerender: { entries: ["*"] },
        },
      },
    };
    await expect(adapter.adapt(builder as never)).rejects.toThrow("needs an explicit entrypoint");
  });
});

function desktopSpaBuilder(options: Readonly<{
  appDir?: string;
  html: string;
  log?: ReturnType<typeof vi.fn>;
}>) {
  const log = options.log ?? Object.assign(vi.fn(), {
    minor: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn(), success: vi.fn(),
  });
  return {
    routes: [{ id: "/settings" }, { id: "/" }],
    prerendered: {
      pages: new Map([["/", { file: "index.html" }]]),
      assets: new Map(),
      redirects: new Map(),
      paths: ["/"],
    },
    rimraf: vi.fn(),
    mkdirp: vi.fn(),
    generateEnvModule: vi.fn(),
    writeClient: vi.fn(),
    writePrerendered: vi.fn(),
    generateFallback: vi.fn(async (path: string) => {
      const { mkdir, writeFile } = await import("node:fs/promises");
      const file = resolve(out, path);
      await mkdir(dirname(file), { recursive: true });
      await writeFile(file, options.html, "utf8");
    }),
    log,
    config: {
      kit: {
        paths: { base: "" },
        appDir: options.appDir ?? "_app",
        router: { type: "hash" },
        files: { routes: "src/routes" },
        prerender: { entries: ["*"] },
      },
    },
  };
}
