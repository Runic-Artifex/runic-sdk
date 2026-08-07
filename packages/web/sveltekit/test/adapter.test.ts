import { readFile, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { afterEach, describe, expect, test, vi } from "vitest";
import { runicToolkitAdapter } from "../src/index.js";

const out = resolve(".tmp-adapter-test");

afterEach(async () => rm(out, { recursive: true, force: true }));

describe("runicToolkitAdapter", () => {
  test("delegates static output and writes a deterministic native manifest", async () => {
    const adapter = runicToolkitAdapter({ mode: "spa", out });
    const log = Object.assign(vi.fn(), {
      minor: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn(), success: vi.fn(),
    });
    const builder = {
      routes: [{ id: "/settings" }, { id: "/" }],
      rimraf: vi.fn(),
      mkdirp: vi.fn(),
      generateEnvModule: vi.fn(),
      writeClient: vi.fn(),
      writePrerendered: vi.fn(),
      generateFallback: vi.fn(async (path: string) => {
        const { mkdir, writeFile } = await import("node:fs/promises");
        await mkdir(out, { recursive: true });
        await writeFile(resolve(out, path), "fallback", "utf8");
      }),
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
    await adapter.adapt(builder as never);
    const manifest = JSON.parse(await readFile(resolve(out, "runic-toolkit.sveltekit.json"), "utf8"));
    expect(manifest).toEqual({
      schema: "runic-toolkit.sveltekit/1",
      mode: "spa",
      entrypoint: "200.html",
      fallback: "200.html",
      routes: ["/", "/settings"],
    });
  });
});
