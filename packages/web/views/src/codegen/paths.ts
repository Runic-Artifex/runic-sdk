// Path arithmetic for declaration maps, without node:path, so the emitter runs in any JavaScript host. Paths use `/`;
// `\` is read as `/`. A path is absolute when it starts with `/` or a drive (`C:/`).

const root = (path: string) => /^[A-Za-z]:\//.exec(path)?.[0] ?? (path.startsWith("/") ? "/" : "");

export const isAbsolutePath = (path: string) => root(path.replaceAll("\\", "/")) !== "";

function normalize(path: string): { root: string; parts: string[] } {
  const text = path.replaceAll("\\", "/");
  const prefix = root(text);
  const parts: string[] = [];
  for (const part of text.slice(prefix.length).split("/")) {
    if (part === "" || part === ".") continue;
    if (part === ".." && parts.length > 0 && parts[parts.length - 1] !== "..") parts.pop();
    else if (part !== ".." || prefix === "") parts.push(part);
  }
  return { root: prefix, parts };
}

/** `path` against the absolute `base`, normalized. */
export function resolvePath(base: string, path: string): string {
  const joined = isAbsolutePath(path) ? path : `${base.replaceAll("\\", "/")}/${path}`;
  const { root: prefix, parts } = normalize(joined);
  return prefix + parts.join("/");
}

export function directoryOf(path: string): string {
  const { root: prefix, parts } = normalize(path);
  return prefix + parts.slice(0, -1).join("/");
}

/** The relative path from the directory `from` to `to`, both absolute; `to` itself when their roots differ. */
export function relativePath(from: string, to: string): string {
  const a = normalize(from), b = normalize(to);
  if (a.root.toLowerCase() !== b.root.toLowerCase()) return b.root + b.parts.join("/");
  let common = 0;
  while (common < a.parts.length && common < b.parts.length && a.parts[common] === b.parts[common]) common++;
  return [...a.parts.slice(common).map(() => ".."), ...b.parts.slice(common)].join("/");
}
