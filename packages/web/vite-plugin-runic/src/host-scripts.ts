// The Runic hosts serve these classic scripts from the web root: CS-WebUI and
// Runic Desktop serve webui.js and runic-desktop.js, and the host packages copy
// their client scripts into www/, the referenced host's also as the
// host-neutral runic-views.js. Vite cannot bundle them.
const hostScriptNames = new Set([
  "webui.js",
  "runic-desktop.js",
  "runic-cswebui.js",
  "runic-desktop-views.js",
  "runic-views.js",
]);

const scriptTag = /<script\b([^>]*)>/gi;
const sourceAttribute = /(\ssrc\s*=\s*)(["'])([^"']*)\2/i;
const moduleType = /\stype\s*=\s*(["']?)module\1(?=[\s>/]|$)/i;
const ignoreAttribute = /\svite-ignore(?=[\s=>/]|$)/i;

/**
 * The relative prefix from an HTML document to the web root: `./` for a page
 * at the root, `../` for `pages/settings.html`. Runic Desktop serves each
 * window below its own path, so the prefix is relative rather than `/`.
 */
export function webRootPrefix(documentPath: string): string {
  const depth = documentPath.replaceAll("\\", "/").split("/").filter((segment) => segment !== "").length - 1;
  return depth <= 0 ? "./" : "../".repeat(depth);
}

/**
 * Points the host scripts of one page at the web root, whatever folder the
 * page is in. `webui.js`, `./webui.js`, `/webui.js` and `../webui.js` all name
 * the root file. With `ignore`, the tags also get `vite-ignore`, so the Vite
 * build leaves them alone instead of warning that they cannot be bundled.
 */
export function rewriteHostScripts(html: string, documentPath: string, ignore: boolean): string {
  const prefix = webRootPrefix(documentPath);
  return html.replace(scriptTag, (tag: string, attributes: string) => {
    if (moduleType.test(attributes)) return tag;
    const source = sourceAttribute.exec(attributes);
    if (!source) return tag;
    const name = hostScriptName(source[3] ?? "");
    if (name === undefined) return tag;
    let rewritten = attributes.replace(sourceAttribute, (_match, before: string, quote: string) =>
      `${before}${quote}${prefix}${name}${quote}`);
    if (ignore && !ignoreAttribute.test(rewritten)) rewritten = ` vite-ignore${rewritten}`;
    return `<script${rewritten}>`;
  });
}

function hostScriptName(url: string): string | undefined {
  const segments = url.split("/");
  const name = segments.pop() ?? "";
  if (!hostScriptNames.has(name)) return undefined;
  return segments.every((segment) => segment === "" || segment === "." || segment === "..") ? name : undefined;
}
