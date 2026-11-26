// String helpers of the emitter. Number and name formatting reproduces the .NET generator's, as the goldens pin it.

/**
 * A string literal of generated TypeScript (and of the declaration map's JSON): `JSON.stringify`, which escapes `"`, `\`
 * and control characters and writes every other character as itself. Until 0.24 the emitter reproduced the .NET
 * generator's escaping (`+`, `<`, `&`, non-ASCII … as `\uXXXX`) for byte identity with it (R3, Q26).
 */
export const quote = (value: string): string => JSON.stringify(value);

/**
 * The quoted specifier of a generated module beside the importing one. Built from parts, so that the package build's
 * scan for relative imports does not read the emitter's templates as imports of its own.
 */
export const sibling = (file: string) => `"${"."}/${file}"`;

/**
 * A character that continues a name in the emitter's scans (letters, nonspacing marks, decimal digits and connector
 * punctuation, as .NET's `\w`), for a pattern with the `u` flag. JavaScript's `\w` is ASCII: a scan would stop inside
 * `pageÜbersicht`.
 */
export const WORD = String.raw`\p{L}\p{Mn}\p{Nd}\p{Pc}`;
/**
 * A name in the emitter's scans, for a pattern with the `u` flag: any letter, `_` or `$`, then {@link WORD} characters
 * or `$`. A name may start with a non-ASCII letter, such as `ÜbersichtState` (#1355).
 */
export const NAME = String.raw`[\p{L}_$][${WORD}$]*`;

/**
 * A double as .NET's `Convert.ToString(double, InvariantCulture)` writes it: JavaScript's shortest round-trip digits, in
 * exponent form (`1E+20`, `1.5E-05`) when the decimal exponent is 17 or more, or less than -4, and `-0` for negative zero.
 */
export function dotnetNumber(value: number): string {
  if (Object.is(value, -0)) return "-0";
  if (!Number.isFinite(value)) return String(value);
  const [mantissa, exponentText] = value.toExponential().split("e") as [string, string];
  const exponent = Number(exponentText);
  if (exponent < 17 && exponent >= -4) return String(value);
  return `${mantissa}E${exponent < 0 ? "-" : "+"}${String(Math.abs(exponent)).padStart(2, "0")}`;
}

export const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
export const lowerFirst = (text: string) => text[0]!.toLowerCase() + text.slice(1);
export const upperFirst = (text: string) => text[0]!.toUpperCase() + text.slice(1);

// .NET char.IsLetter / IsLetterOrDigit, which test UTF-16 code units: a surrogate is neither.
const letter = /\p{L}/u;
const letterOrDigit = /[\p{L}\p{Nd}]/u;

export const isTsIdentifier = (name: string) =>
  name.length > 0 && (letter.test(name[0]!) || name[0] === "_" || name[0] === "$")
  && name.slice(1).split("").every(char => letterOrDigit.test(char) || char === "_" || char === "$");

export const tsPropertyName = (name: string) => (isTsIdentifier(name) ? name : quote(name));
export const tsLiteralKey = (name: string) => (name === "__proto__" ? `[${quote(name)}]` : tsPropertyName(name));
export const tsAccess = (target: string, name: string) =>
  isTsIdentifier(name) && name !== "__proto__" ? `${target}.${name}` : `${target}[${quote(name)}]`;

/** XmlDocumentation.Append: a TSDoc block at the indentation. */
export function appendDoc(lines: string[], indent: string, text: string | null | undefined): void {
  if (text == null || text.trim().length === 0) return;
  const parts = text.replaceAll("*/", "*\\/").split("\n");
  if (parts.length === 1) {
    lines.push(`${indent}/** ${parts[0]} */`);
    return;
  }
  lines.push(`${indent}/**`);
  for (const line of parts) lines.push(line.length === 0 ? `${indent} *` : `${indent} * ${line}`);
  lines.push(`${indent} */`);
}

/** A StringBuilder with AppendLine semantics ("\n" line endings). */
export class Builder {
  private text = "";
  line(value = ""): this { this.text += value + "\n"; return this; }
  append(value: string): this { this.text += value; return this; }
  doc(indent: string, value: string | null | undefined): this {
    const lines: string[] = [];
    appendDoc(lines, indent, value);
    for (const line of lines) this.line(line);
    return this;
  }
  toString(): string { return this.text; }
  clear(): void { this.text = ""; }
}
