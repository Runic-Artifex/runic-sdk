import { realpathSync } from "node:fs";
import { isAbsolute, relative, sep } from "node:path";

// Resolve both sides: macOS temporary directories commonly have a /private alias.
export function isWithinDirectory(directory, path) {
  const suffix = relative(realpathSync(directory), realpathSync(path));
  return suffix !== ".." && !suffix.startsWith(`..${sep}`) && !isAbsolute(suffix);
}
