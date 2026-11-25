// <view>.contract.d.ts.map (#952): leads each marked member of a client contract to its source declaration, as the .NET
// generator's ClientDeclarationMap does.
import type { Location } from "./ir.js";
import { directoryOf, isAbsolutePath, relativePath } from "./paths.js";
import { ordinal, relaxedQuote } from "./text.js";

export const CONTRACT_SUFFIX = ".contract.d.ts";

const MARK = /\u0006(\d+)\u0006/g;

/** The members marked in the contracts of one generation: a source id and the declared name the mark follows. */
export class DeclarationMarks {
  private readonly targets: { id: string; name: string }[] = [];

  /** Marks the declaration of `name` on the current line as the one of source `id`. */
  mark(id: string, name: string): string {
    this.targets.push({ id, name });
    return `\u0006${this.targets.length - 1}\u0006`;
  }

  /**
   * Removes the marks of the contract written at `path` (absolute); returns its text, with a sourceMappingURL comment
   * when a name is mapped, and its map, or null.
   */
  complete(path: string, text: string, sources: Readonly<Record<string, Location>>): { text: string; map: string | null } {
    const lines = text.replaceAll("\r\n", "\n").split("\n");
    const directory = directoryOf(path);
    const names: MappedName[] = [];
    for (let index = 0; index < lines.length; index++) {
      const marks = [...lines[index]!.matchAll(MARK)];
      if (marks.length === 0) continue;
      lines[index] = lines[index]!.replace(MARK, "");
      for (const found of marks) {
        const target = this.targets[Number(found[1])]!;
        const at = column(lines[index]!, target.name);
        const location = sources[target.id];
        if (at === null || !location) continue;
        const source = relativePath(directory, location.file);
        if (isAbsolutePath(source)) continue;
        names.push({ line: index, column: at, length: target.name.length, source, sourceLine: location.line - 1, sourceColumn: location.column - 1 });
      }
    }
    let body = lines.join("\n");
    if (names.length === 0) return { text: body, map: null };
    if (!body.endsWith("\n")) body += "\n";
    const file = path.split("/").pop()!;
    body += `//# sourceMappingURL=${file}.map\n`;
    return { text: body, map: render(file, body, names) };
  }
}

interface MappedName { line: number; column: number; length: number; source: string; sourceLine: number; sourceColumn: number }

// The column of a declared name: after the indentation (spaces) and an `export interface ` or `readonly ` prefix.
function column(line: string, name: string): number | null {
  let at = line.length - line.replace(/^ +/, "").length;
  for (const prefix of ["export interface ", "readonly "])
    if (line.startsWith(prefix, at)) { at += prefix.length; break; }
  return line.startsWith(name, at) ? at : null;
}

const BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function vlq(value: number): string {
  let remaining = value < 0 ? (-value * 2) + 1 : value * 2;
  let out = "";
  do {
    let digit = remaining & 31;
    remaining = Math.floor(remaining / 32);
    if (remaining > 0) digit |= 32;
    out += BASE64[digit];
  } while (remaining > 0);
  return out;
}

// Source map v3: every line starts with a generated-only segment unless a name starts at column 0; a mapped name is
// followed by a generated-only segment where it ends.
function render(file: string, body: string, names: MappedName[]): string {
  const sources = [...new Set(names.map(name => name.source))].sort(ordinal);
  const lines = body.split("\n");
  const lineCount = body.endsWith("\n") ? lines.length - 1 : lines.length;
  let mappings = "";
  let previousSource = 0, previousSourceLine = 0, previousSourceColumn = 0;
  for (let line = 0; line < lineCount; line++) {
    if (line !== 0) mappings += ";";
    let previousColumn = 0;
    let first = true;
    const segment = (at: number) => {
      if (!first) mappings += ",";
      first = false;
      mappings += vlq(at - previousColumn);
      previousColumn = at;
    };
    const named = names.filter(name => name.line === line).sort((a, b) => a.column - b.column);
    if (named.length === 0 || named[0]!.column !== 0) segment(0);
    for (const name of named) {
      segment(name.column);
      const index = sources.indexOf(name.source);
      mappings += vlq(index - previousSource); previousSource = index;
      mappings += vlq(name.sourceLine - previousSourceLine); previousSourceLine = name.sourceLine;
      mappings += vlq(name.sourceColumn - previousSourceColumn); previousSourceColumn = name.sourceColumn;
      const end = name.column + name.length;
      if (end < lines[line]!.length) segment(end);
    }
  }
  return `{"version":3,"file":${relaxedQuote(file)},"sourceRoot":"","sources":[${sources.map(relaxedQuote).join(",")}],"names":[],"mappings":${relaxedQuote(mappings)}}\n`;
}
