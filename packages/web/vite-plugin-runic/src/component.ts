// The `component` build target (trusted component mode, `region-component/1`): next to the page, `vite build` builds
// an ES module that defines the guest's custom element (`defineRunicComponent` in `@runic-artifex/views/component`). It
// bundles the framework runtime and its own copy of `@runic-artifex/views`, takes its CSS as constructable stylesheets
// instead of document stylesheets, and addresses its chunks and assets relative to its own URL, so that they load from
// the guest's origin in the host page's document.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";
import type { EnvironmentOptions, Rollup, ViteBuilder } from "vite";

type OutputBundle = Rollup.OutputBundle;

/** The directory of the component under the page's build output, which the guest's surface serves with CORS. */
export const componentDirectory = "_runic_component";
/** The component's module, in {@link componentDirectory}. */
export const componentModule = "component.js";
/** The manifest the guest reads to answer a component attach: `{ version, name, module, tag }`. */
export const componentManifest = "runic-component.json";
/** The name of the Vite environment that builds the component. */
export const componentEnvironment = "runic_component";

const entryId = "virtual:runic/component-entry";
const resolvedEntryId = `\0${entryId}`;
const metaId = "virtual:runic/component-meta";
// The generated module that holds the tag and the stylesheets, which are known only once the bundle is written.
const metaFile = "runic-component-meta.js";

export interface RunicComponentOptions {
  /**
   * The component's entry module, relative to the Vite root. Its default export is the mount function
   * (`RunicComponentMount` of `@runic-artifex/views/component`), which renders the guest's Views into the element.
   */
  readonly entry: string;
  /**
   * The guest's tag prefix: a lowercase letter, then lowercase letters, digits and hyphens. The element's tag is
   * `<name>-<hash>`, with a hash of the build, so two guests, and two builds of one guest, never define the same tag.
   * Defaults to the `name` of the root's `package.json`, made a valid prefix.
   */
  readonly name?: string;
}

interface ResolvedComponent {
  readonly entry: string;
  readonly name: string | undefined;
}

const namePattern = /^[a-z][a-z0-9-]*$/;

export function resolveComponentOptions(options: RunicComponentOptions | undefined): ResolvedComponent | undefined {
  if (options === undefined) return undefined;
  if (options === null || typeof options !== "object" || typeof options.entry !== "string" || options.entry === "") {
    throw new TypeError("component.entry must name the component's entry module.");
  }
  if (options.name !== undefined && (typeof options.name !== "string" || !namePattern.test(options.name) || options.name.length > 64)) {
    throw new TypeError("component.name must be a lowercase letter followed by lowercase letters, digits and hyphens, at most 64 characters.");
  }
  return { entry: options.entry, name: options.name };
}

/** The tag prefix of a package name: `@acme/check-tool` becomes `acme-check-tool`. */
export function componentPrefix(packageName: string | undefined): string {
  const words = (packageName ?? "").toLowerCase().split(/[^a-z0-9]+/).filter((word) => word !== "");
  const prefix = words.join("-").replace(/^[^a-z]+/, "").slice(0, 64).replace(/-+$/, "");
  return prefix === "" ? "runic-component" : prefix;
}

function packageName(root: string): string | undefined {
  try {
    const name = (JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { name?: unknown }).name;
    return typeof name === "string" ? name : undefined;
  } catch {
    return undefined;
  }
}

/** The Vite environment that builds the component into `<outDir>/_runic_component`. */
export function componentEnvironmentOptions(outDir: string): EnvironmentOptions {
  return {
    consumer: "client",
    build: {
      outDir: join(outDir, componentDirectory),
      emptyOutDir: true,
      copyPublicDir: false,
      // One stylesheet, which the bundle adopts in each element's shadow root; no chunk adds a <link> to the document.
      cssCodeSplit: false,
      modulePreload: false,
      manifest: false,
      rolldownOptions: {
        input: { component: entryId },
        preserveEntrySignatures: "strict",
        output: { format: "es", entryFileNames: componentModule },
      },
    },
  };
}

/** Builds the component after the environments the configuration builds, unless its own `buildApp` built it. */
export async function buildComponent(builder: ViteBuilder): Promise<void> {
  const component = builder.environments[componentEnvironment];
  if (component === undefined || component.isBuilt) return;
  const others = Object.values(builder.environments).filter((environment) => environment !== component);
  // Vite builds every environment when no buildApp built one; this hook runs after the configuration's, so it does so.
  if (others.every((environment) => !environment.isBuilt)) {
    for (const environment of others) await builder.build(environment);
  }
  await builder.build(component);
}

/** The hooks of the component environment, which the `runic` plugin calls for that environment only. */
export interface ComponentHooks {
  resolveId(id: string): string | { id: string; external: true } | undefined;
  load(id: string): string | undefined;
  generateBundle(context: { emitFile: (file: { type: "asset"; fileName: string; source: string }) => unknown }, bundle: OutputBundle): void;
}

export function componentHooks(component: ResolvedComponent, root: () => string): ComponentHooks {
  let prefix = component.name;
  return {
    resolveId(id) {
      if (id === entryId) return resolvedEntryId;
      // Written in generateBundle, next to the module.
      if (id === metaId) return { id: `./${metaFile}`, external: true };
      return undefined;
    },
    load(id) {
      if (id !== resolvedEntryId) return undefined;
      const entry = isAbsolute(component.entry) ? component.entry : resolve(root(), component.entry);
      return [
        `import mount from ${JSON.stringify(entry)};`,
        `import meta from ${JSON.stringify(metaId)};`,
        `import { defineRunicComponent } from "@runic-artifex/views/component";`,
        `const definition = defineRunicComponent({ tag: meta.tag, styles: meta.styles, mount });`,
        `export const tag = definition.tag;`,
        `export default definition.element;`,
      ].join("\n");
    },
    generateBundle(context, bundle) {
      prefix ??= componentPrefix(packageName(root()));
      const styles: { text: string; file: string }[] = [];
      const hash = createHash("sha256");
      for (const fileName of Object.keys(bundle).sort()) {
        const item = bundle[fileName]!;
        if (item.type === "asset" && fileName.endsWith(".css")) {
          // Vite marks the end of its CSS with a comment of its own.
          styles.push({ text: String(item.source).replace(/\/\*\$vite\$:\d+\*\/\s*$/, ""), file: fileName });
          delete bundle[fileName];
        }
        hash.update(fileName).update("\0");
        hash.update(item.type === "chunk" ? item.code : item.source).update("\0");
      }
      const tag = `${prefix}-${hash.digest("hex").slice(0, 8)}`;
      // Relative to the module, which loads from the guest's origin: a constructed stylesheet resolves its relative
      // url() against the host page otherwise.
      const styleList = styles.map((style) =>
        `{ text: ${JSON.stringify(style.text)}, href: new URL(${JSON.stringify(style.file)}, import.meta.url).href }`);
      context.emitFile({
        type: "asset",
        fileName: metaFile,
        source: `export default { tag: ${JSON.stringify(tag)}, styles: [${styleList.join(", ")}] };\n`,
      });
      context.emitFile({
        type: "asset",
        fileName: componentManifest,
        source: `${JSON.stringify({ version: 1, name: prefix, module: componentModule, tag }, null, 2)}\n`,
      });
    },
  };
}
