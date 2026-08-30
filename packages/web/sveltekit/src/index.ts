import adapterStatic from "@sveltejs/adapter-static";
import { access, readFile, writeFile } from "node:fs/promises";
import { isAbsolute, posix, relative, resolve, sep } from "node:path";
import type { Adapter } from "@sveltejs/kit";

export type RunicToolkitSvelteKitMode = "prerendered" | "spa";

export interface RunicToolkitSvelteKitAdapterOptions {
  readonly mode?: RunicToolkitSvelteKitMode;
  /** Emits an entrypoint relocatable under a generated Runic Desktop surface namespace. */
  readonly desktop?: boolean;
  readonly out?: string;
  readonly fallback?: string;
  /** Output-relative HTML file for a multi-page prerendered application. */
  readonly entrypoint?: string;
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
  const desktop = options.desktop ?? false;
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
      if (desktop && mode === "spa" && builder.config.kit.router.type !== "hash") {
        throw new Error(
          "The Runic SvelteKit SPA adapter requires kit.router.type to be 'hash' so its output remains relocatable under a Desktop surface namespace.",
        );
      }
      await delegate.adapt(builder);
      const pages = [...builder.prerendered.pages.entries()]
        .map(([route, page]) => ({ route, file: page.file }))
        .sort((left, right) => left.route.localeCompare(right.route));
      const entrypoint = mode === "spa"
        ? fallback!
        : selectPrerenderedEntrypoint(pages, options.entrypoint);
      await assertOutputFile(out, entrypoint);
      if (desktop) {
        await makeDesktopEntrypointRelocatable(
          out,
          entrypoint,
          mode === "spa" ? builder.config.kit.appDir : undefined,
        );
      }
      const manifest: RunicToolkitSvelteKitManifest = {
        schema: "runic-toolkit.sveltekit/1",
        mode,
        entrypoint,
        fallback: fallback ?? null,
        routes: pages.map((page) => page.route),
      };
      await writeFile(
        resolve(out, "runic-toolkit.sveltekit.json"),
        `${JSON.stringify(manifest, null, 2)}\n`,
        "utf8",
      );
    },
  };
}

async function makeDesktopEntrypointRelocatable(
  out: string,
  entrypoint: string,
  appDir: string | undefined,
): Promise<void> {
  const file = resolve(out, entrypoint);
  const html = await readFile(file, "utf8");
  if (/<script\b[^>]*\bsrc\s*=\s*["'][^"']*runic-desktop\.js["'][^>]*>/i.test(html)) {
    throw new Error(
      "The Runic SvelteKit Desktop adapter owns bootstrap insertion; remove the manual runic-desktop.js script.",
    );
  }
  const normalizedEntrypoint = entrypoint.replaceAll("\\", "/");
  const entryDirectory = posix.dirname(normalizedEntrypoint);
  const bootstrapPath = toDocumentRelativePath(posix.relative(entryDirectory, "runic-desktop.js"));
  let relocatable = html;
  if (appDir !== undefined) {
    const absolutePrefix = `/${appDir}/`;
    const relativePrefix = `${toDocumentRelativePath(posix.relative(entryDirectory, appDir))}/`;
    relocatable = relocatable.replaceAll(absolutePrefix, relativePrefix);
  }
  const openingHead = /<head(?:\s[^>]*)?>/i.exec(relocatable);
  if (openingHead?.index === undefined) {
    throw new Error("The Runic SvelteKit Desktop entrypoint must contain a head element.");
  }
  const insertion = openingHead.index + openingHead[0].length;
  relocatable = `${relocatable.slice(0, insertion)}\n<script src="${bootstrapPath}"></script>${relocatable.slice(insertion)}`;
  await writeFile(file, relocatable, "utf8");
}

function toDocumentRelativePath(path: string): string {
  return path.startsWith(".") ? path : `./${path}`;
}

function selectPrerenderedEntrypoint(
  pages: readonly Readonly<{ route: string; file: string }>[],
  requested: string | undefined,
): string {
  if (requested !== undefined) {
    if (!isOutputRelative(requested)) {
      throw new Error("The Runic SvelteKit adapter entrypoint must be an output-relative HTML file.");
    }
    if (!pages.some((page) => page.file === requested)) {
      throw new Error(`The Runic SvelteKit adapter entrypoint \"${requested}\" is not a prerendered page.`);
    }
    return requested;
  }
  const root = pages.find((page) => page.route === "/");
  if (root !== undefined) return root.file;
  if (pages.length === 1) return pages[0]!.file;
  throw new Error(
    "The Runic SvelteKit adapter needs an explicit entrypoint for a prerendered app without exactly one page or '/'.",
  );
}

async function assertOutputFile(out: string, entrypoint: string): Promise<void> {
  if (!isOutputRelative(entrypoint)) {
    throw new Error("The Runic SvelteKit adapter entrypoint must be an output-relative HTML file.");
  }
  const output = resolve(out);
  const file = resolve(output, entrypoint);
  if (relative(output, file).startsWith(`..${sep}`) || relative(output, file) === "..") {
    throw new Error("The Runic SvelteKit adapter entrypoint escapes the output directory.");
  }
  try {
    await access(file);
  } catch {
    throw new Error(`The Runic SvelteKit adapter entrypoint \"${entrypoint}\" was not emitted to \"${out}\".`);
  }
}

function isOutputRelative(file: string): boolean {
  return file.length > 0 && !isAbsolute(file) && !file.split(/[\\/]/).includes("..");
}

export {
  runicToolkitPrerenderedPageOptions,
  runicToolkitSpaPageOptions,
} from "./page-options.js";
