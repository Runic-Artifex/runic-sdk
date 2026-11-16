import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

// A source map comment as source-map tools find it: js-debug takes the last "sourceMappingURL" anywhere in a file
// that follows "//#", "//@", "/*#" or "/*@" and a space or tab, not only one that starts the last line (#1050).
const comment = /\/[/*][#@][ \t]sourceMappingURL=/g;

/**
 * The 1-based lines of a JavaScript file's source map comments that are not its own trailing source map comment: the
 * last non-blank line may be one, as a bundler writes it; any other, in a doc comment, string or pattern, is what a
 * debugger reads as the file's source map when the real one is absent.
 */
export function straySourceMapComments(text) {
  const lines = text.split("\n");
  let last = lines.length - 1;
  while (last > 0 && lines[last].trim() === "") last--;
  const stray = [];
  for (const match of text.matchAll(comment)) {
    const line = text.slice(0, match.index).split("\n").length - 1;
    const indent = text.slice(text.lastIndexOf("\n", match.index - 1) + 1, match.index);
    if (line !== last || indent.trim() !== "") stray.push(line + 1);
  }
  return stray;
}

/** The shipped JavaScript files of an installed or unpacked package directory with stray source map comments. */
export function packageSourceMapProblems(directory) {
  const problems = [];
  for (const entry of readdirSync(directory, { recursive: true, withFileTypes: true })) {
    if (!entry.isFile() || !/\.(?:m|c)?js$/.test(entry.name)) continue;
    const path = join(entry.parentPath, entry.name);
    const lines = straySourceMapComments(readFileSync(path, "utf8"));
    if (lines.length > 0) problems.push(`${path}:${lines.join(",")}`);
  }
  return problems;
}
