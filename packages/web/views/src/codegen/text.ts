// String helpers that reproduce the .NET generator's formatting byte for byte.

const hex = (code: number) => "\\u" + code.toString(16).toUpperCase().padStart(4, "0");

/**
 * `System.Text.Json.JsonSerializer.Serialize(string)` with the default encoder: HTML-sensitive characters
 * (`"`, `&`, `'`, `+`, `<`, `>`, backtick), controls and everything outside Basic Latin become `\uXXXX`.
 */
export function csQuote(value: string): string {
  let out = '"';
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    const char = value[index]!;
    if (char === "\\") out += "\\\\";
    else if (char === "\n") out += "\\n";
    else if (char === "\r") out += "\\r";
    else if (char === "\t") out += "\\t";
    else if (char === "\b") out += "\\b";
    else if (char === "\f") out += "\\f";
    else if (code < 0x20 || code >= 0x7f || "\"&'+<>`".includes(char)) out += hex(code);
    else out += char;
  }
  return out + '"';
}

/** `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`: only `"`, `\` and controls are escaped (non-ASCII kept; inference for the BMP). */
export function relaxedQuote(value: string): string {
  let out = '"';
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    const char = value[index]!;
    if (char === "\\") out += "\\\\";
    else if (char === '"') out += '\\"';
    else if (char === "\n") out += "\\n";
    else if (char === "\r") out += "\\r";
    else if (char === "\t") out += "\\t";
    else if (char === "\b") out += "\\b";
    else if (char === "\f") out += "\\f";
    else if (code < 0x20 || code === 0x7f) out += hex(code);
    else out += char;
  }
  return out + '"';
}

/**
 * The quoted specifier of a generated module beside the importing one. Built from parts, so that the package build's
 * scan for relative imports does not read the emitter's templates as imports of its own.
 */
export const sibling = (file: string) => `"${"."}/${file}"`;

export const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
export const lowerFirst = (text: string) => text[0]!.toLowerCase() + text.slice(1);
export const upperFirst = (text: string) => text[0]!.toUpperCase() + text.slice(1);

// .NET char.IsLetter / IsLetterOrDigit, approximated with Unicode properties.
const letter = /\p{L}/u;
const letterOrDigit = /[\p{L}\p{Nd}]/u;

export const isTsIdentifier = (name: string) =>
  name.length > 0 && (letter.test(name[0]!) || name[0] === "_" || name[0] === "$")
  && [...name.slice(1)].every(char => letterOrDigit.test(char) || char === "_" || char === "$");

export const tsPropertyName = (name: string) => (isTsIdentifier(name) ? name : csQuote(name));
export const tsLiteralKey = (name: string) => (name === "__proto__" ? `[${csQuote(name)}]` : tsPropertyName(name));
export const tsAccess = (target: string, name: string) =>
  isTsIdentifier(name) && name !== "__proto__" ? `${target}.${name}` : `${target}[${csQuote(name)}]`;

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
