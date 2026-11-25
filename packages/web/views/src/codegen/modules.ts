// The modules of one generation: TypeScriptModules of the .NET generator. Each module's own bindings are resolved when it
// is added; a module that a client library owns (application mode) is checked the same way, but not written.
import { resolveBindings } from "./bindings.js";
import { DeclarationMarks } from "./declmap.js";

/** A line the module writer replaces with the named-type imports. */
export const NAMED_TYPE_IMPORTS = "\u0002named-type-imports\u0002";

export class Modules {
  readonly marks = new DeclarationMarks();
  readonly pending: { path: string; text: string }[] = [];
  /** True while the emitter writes a module that a client library owns. */
  discarding = false;

  /** @param owners The module specifier of each generated module that a client library owns, by module name. */
  constructor(private readonly owners: ReadonlyMap<string, string>) {}

  /** Adds a module; `source` is the `sources` id that a RUNICBRIDGE004 about it names. */
  add(path: string, text: string, source?: string): void {
    const resolved = resolveBindings(text, path, source);
    if (!this.discarding) this.pending.push({ path, text: resolved });
  }

  /** The specifier that imports the generated module `module`: the owning library's, or `./{module}{suffix}`. */
  specifier(module: string, suffix: string): string {
    return this.owners.get(module) ?? `./${module}${suffix}`;
  }

  /** True when a module text imports from a client library. */
  importsLibrary(text: string): boolean {
    return [...new Set(this.owners.values())].some(specifier => text.includes(` from "${specifier}";`));
  }
}
