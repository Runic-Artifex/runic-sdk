import adapterStatic from "@sveltejs/adapter-static";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { Adapter } from "@sveltejs/kit";

export type RunicToolkitSvelteKitMode = "prerendered" | "spa";

export interface RunicToolkitSvelteKitAdapterOptions {
  readonly mode?: RunicToolkitSvelteKitMode;
  readonly out?: string;
  readonly fallback?: string;
  readonly precompress?: boolean;
  readonly strict?: boolean;
}

export interface RunicToolkitSvelteKitManifest {
  readonly schema: "runic-toolkit.sveltekit/1";
  readonly mode: RunicToolkitSvelteKitMode;
  readonly entrypoint: string;
  readonly fallback: string | null;
  readonly routes: readonly string[];
}

export function runicToolkitAdapter(options: RunicToolkitSvelteKitAdapterOptions = {}): Adapter {
  const mode = options.mode ?? "prerendered";
  const out = options.out ?? "build";
  const fallback = mode === "spa" ? (options.fallback ?? "200.html") : undefined;
  const delegate = adapterStatic({
    pages: out,
    assets: out,
    ...(fallback ? { fallback } : {}),
    precompress: options.precompress ?? false,
    strict: options.strict ?? true,
  });
  return {
    name: "@runic-artifex/sveltekit",
    async adapt(builder) {
      await delegate.adapt(builder);
      const manifest: RunicToolkitSvelteKitManifest = {
        schema: "runic-toolkit.sveltekit/1",
        mode,
        entrypoint: fallback ?? "index.html",
        fallback: fallback ?? null,
        routes: builder.routes.map((route) => route.id).sort(),
      };
      await writeFile(
        resolve(out, "runic-toolkit.sveltekit.json"),
        `${JSON.stringify(manifest, null, 2)}\n`,
        "utf8",
      );
    },
  };
}

export const runicToolkitPrerenderedPageOptions = Object.freeze({
  prerender: true as const,
});

export const runicToolkitSpaPageOptions = Object.freeze({
  ssr: false as const,
  prerender: false as const,
});

