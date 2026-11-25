// TypeScriptModuleBindings: the emitter's own top-level bindings (runtime imports, decoders, reference caches), kept apart
// from the names a module derives from the IR (#258). Emitters write each own binding through Ref, Import or Shorthand;
// resolveBindings keeps the name unless a derived binding of the same module uses it, and then renames it `_name`.
import { ContractIrDiagnostic, NAME_COLLISION } from "./diagnostics.js";
import { NAME, WORD } from "./text.js";

export const Ref = (name: string) => `\u0003${name}\u0003`;
export const Import = (name: string) => `\u0004${name}\u0004`;
export const Shorthand = (name: string) => `\u0005${name}\u0005`;

const marked = new RegExp(String.raw`([\u0003\u0004\u0005])(${NAME})\1`, "gu");
const declaration = new RegExp(String.raw`^(?:export\s+)?(?:async\s+)?(?:interface|type|function|const|let|var|class|enum)\s+(${NAME})`, "gmu");
const namespaceImport = new RegExp(String.raw`^import\s+(?:type\s+)?\*\s+as\s+(${NAME})`, "gmu");

/** The names a module's top-level declarations and imports bind; generated modules start each statement in column 0. */
export function* declaredTopLevel(module: string): Generator<string> {
  for (const match of module.matchAll(declaration)) yield match[1]!;
  for (const match of module.matchAll(namespaceImport)) yield match[1]!;
  for (const match of module.matchAll(/^import\s+(?:type\s+)?\{([^}]*)\}/gm))
    for (const raw of match[1]!.split(",").map(part => part.trim()).filter(part => part.length > 0)) {
      const name = raw.startsWith("type ") ? raw.slice(5).trim() : raw;
      const alias = name.indexOf(" as ");
      yield alias >= 0 ? name.slice(alias + 4).trim() : name;
    }
}

/** Assigns the own bindings of a module; RUNICBRIDGE004 when a top-level name is still bound twice. */
export function resolveBindings(module: string, path: string, source?: string): string {
  const taken = new Set(declaredTopLevel(module.replace(marked, "")));
  const resolved = module.replace(marked, (_, marker: string, name: string) => {
    let local = name;
    while (taken.has(local)) local = "_" + local;
    if (marker === "\u0004" && local !== name) return `${name} as ${local}`;
    if (marker === "\u0005" && local !== name) return `${name}: ${local}`;
    return local;
  });
  const seen = new Set<string>();
  for (const name of declaredTopLevel(resolved)) {
    if (seen.has(name))
      throw new ContractIrDiagnostic(NAME_COLLISION,
        `the generated TypeScript module ${path} declares '${name}' more than once. Rename the ViewModel, View, command or type it is derived from.`, source);
    seen.add(name);
  }
  return resolved;
}

/** Own bindings a module references through Ref. */
export const references = (module: string) =>
  [...new Set([...module.matchAll(marked)].filter(match => match[1] === "\u0003").map(match => match[2]!))];

export const withoutComments = (module: string) => module.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/^[ \t]*\/\/.*$/gm, "");

export const mentions = (module: string, name: string) =>
  new RegExp(`(?<![${WORD}$])${name.replace(/[$]/g, "\\$")}(?![${WORD}$])`, "u").test(module);
