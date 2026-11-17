// Absolute `sources` in the development server's source maps. Vite 8 rewrites every module map's `sources` relative to
// the module's directory after the plugins have run (`loadAndTransform`). A debugger that resolves them needs a web
// root: VS Code's js-debug has `webRoot`, but Visual Studio's JavaScript debugger attaches over CDP with none (its root
// is %TEMP%, SPIKE-VS-0), so breakpoints in `src/*.tsx` stayed unbound. With the file's absolute path as the source,
// every debugger binds them without a web root.
//
// Only `vite dev`: `configureServer` runs for the development server alone, so `vite build` and `vite preview` keep
// Vite's relative sources, and no path of the build machine reaches a bundle's map.

import { posix, win32 } from "node:path";
import type { DevEnvironment, ViteDevServer } from "vite";

interface SourceMapSources {
  sources?: (string | null)[];
  sourceRoot?: string;
}

const windowsDrive = /^[a-z]:[\\/]/iu;
// `webpack://…`, `data:…`, `vite:…`: a URL a tool chose, not a path. A drive letter is one character, so `C:/` is not.
const urlScheme = /^[a-z][a-z0-9+.-]+:/iu;

/** Whether `path` is an absolute file-system path on POSIX (`/home/…`) or Windows (`C:/…`, `C:\…`). */
function isAbsoluteFilePath(path: string): boolean {
  return path.startsWith("/") || windowsDrive.test(path);
}

/**
 * Makes a source map's relative `sources` absolute file-system paths, resolved against the directory of `file`, the
 * absolute path of the module the map belongs to. The paths take Vite's form, with forward slashes
 * (`/home/me/app/Frontend/src/App.tsx`, `C:/Users/me/app/Frontend/src/App.tsx`), which js-debug (VS Code and Visual
 * Studio), Chrome DevTools and Runic Inspect read as files. A source that already is absolute, a URL, a virtual
 * module (`\0…`) and a map with a `sourceRoot` stay as they are. Returns whether a source changed; the map is changed
 * in place.
 */
export function absoluteSourceMapSources(map: SourceMapSources, file: string): boolean {
  if (!Array.isArray(map.sources) || !isAbsoluteFilePath(file)) return false;
  if (typeof map.sourceRoot === "string" && map.sourceRoot !== "") return false;
  const windows = windowsDrive.test(file);
  const path = windows ? win32 : posix;
  const directory = path.dirname(windows ? file : file.replace(/\\/gu, "/"));
  let changed = false;
  map.sources = map.sources.map((source) => {
    if (typeof source !== "string" || source === "" || source.startsWith("\0") || isAbsoluteFilePath(source)
      || urlScheme.test(source)) return source;
    // Vite's own relative paths use the platform's separator (`path.relative`): backslashes on Windows.
    const resolved = path.resolve(directory, windows ? source : source.replace(/\\/gu, "/"));
    changed = true;
    return windows ? resolved.replace(/\\/gu, "/") : resolved;
  });
  return changed;
}

/**
 * Wraps `transformRequest` of each environment whose modules run in the browser, so that the map Vite sends with a
 * module (inline, and at `<module>.map`) has absolute sources. The wrapper changes the cached transform result's map,
 * once per map.
 */
export function serveAbsoluteSourceMapSources(server: ViteDevServer): void {
  const done = new WeakSet<object>();
  for (const environment of Object.values(server.environments ?? {}) as DevEnvironment[]) {
    if (environment.config?.consumer !== "client" || typeof environment.transformRequest !== "function") continue;
    const transformRequest = environment.transformRequest.bind(environment);
    environment.transformRequest = async (url: string) => {
      const result = await transformRequest(url);
      const map = result?.map as SourceMapSources | null | undefined;
      if (map === null || map === undefined || typeof map !== "object" || done.has(map)) return result;
      const file = (await environment.moduleGraph.getModuleByUrl(url))?.file;
      if (typeof file === "string") absoluteSourceMapSources(map, file);
      done.add(map);
      return result;
    };
  }
}
