// Text splices (specs/application/state-changes.md, "Text splices"; feature `text-splice/1`) and the `text` change
// of append-only text fields. Indices count UTF-16 code units and never fall between the two halves of a surrogate
// pair.

/** The feature a client and producer both list in `hello` to change text members with splices. */
export const textSpliceFeature = "text-splice/1";

/** A splice is sent, and published, only when it keeps at least this many code units of its base. */
export const minimumKept = 64;

/** Replaces `delete` code units at `index` of a base value with `text`. */
export interface TextSplice {
  readonly index: number;
  readonly delete: number;
  readonly text: string;
}

const isHigh = (code: number) => code >= 0xd800 && code <= 0xdbff;
const isLow = (code: number) => code >= 0xdc00 && code <= 0xdfff;
const isSafe = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;

/** Whether `index` is not between the high and the low surrogate of a pair in `value`. */
export function isBoundary(value: string, index: number): boolean {
  return index <= 0 || index >= value.length || !(isHigh(value.charCodeAt(index - 1)) && isLow(value.charCodeAt(index)));
}

/**
 * The single splice from `previous` to `value`: the common prefix, then the common suffix of the rest, each shortened
 * by one code unit where it would end inside a surrogate pair.
 */
export function singleSplice(previous: string, value: string): TextSplice {
  const shorter = Math.min(previous.length, value.length);
  let prefix = 0;
  while (prefix < shorter && previous.charCodeAt(prefix) === value.charCodeAt(prefix)) prefix++;
  if (prefix > 0 && isHigh(previous.charCodeAt(prefix - 1))) prefix--;
  const rest = shorter - prefix;
  let suffix = 0;
  while (suffix < rest && previous.charCodeAt(previous.length - 1 - suffix) === value.charCodeAt(value.length - 1 - suffix)) suffix++;
  if (suffix > 0 && isLow(previous.charCodeAt(previous.length - suffix))) suffix--;
  return { index: prefix, delete: previous.length - prefix - suffix, text: value.slice(prefix, value.length - suffix) };
}

/**
 * The length of the longest suffix of `previous`, shorter than `previous`, that `value` starts with, or the whole of
 * `previous` when `value` starts with it: what an append-only text field keeps. Linear (Knuth-Morris-Pratt).
 */
export function keptSuffix(previous: string, value: string): number {
  if (value.startsWith(previous)) return previous.length;
  const pattern = value.slice(0, Math.min(value.length, previous.length - 1));
  if (pattern.length === 0) return 0;
  const failure = new Int32Array(pattern.length);
  for (let index = 1, length = 0; index < pattern.length; index++) {
    while (length > 0 && pattern.charCodeAt(index) !== pattern.charCodeAt(length)) length = failure[length - 1]!;
    if (pattern.charCodeAt(index) === pattern.charCodeAt(length)) length++;
    failure[index] = length;
  }
  let matched = 0;
  for (let index = previous.length - pattern.length; index < previous.length; index++) {
    if (matched === pattern.length) matched = failure[matched - 1]!;
    const code = previous.charCodeAt(index);
    while (matched > 0 && code !== pattern.charCodeAt(matched)) matched = failure[matched - 1]!;
    if (code === pattern.charCodeAt(matched)) matched++;
  }
  return matched;
}

/**
 * The splices a producer publishes for a text member that changed from `previous` to `value`
 * (state-changes.md, "Producing frames"): the single splice when it keeps at least half of `previous`, otherwise the
 * "drop the start, append" pair when it keeps more; `undefined`, a set of the member, when the chosen shape keeps
 * fewer than {@link minimumKept} code units.
 */
export function producerSplices(previous: string, value: string): readonly TextSplice[] | undefined {
  const single = singleSplice(previous, value);
  const kept = previous.length - single.delete;
  if (kept * 2 < previous.length) {
    const pair = keptSuffix(previous, value);
    const trim = previous.length - pair;
    if (pair > kept && isBoundary(previous, trim) && isBoundary(value, pair)) {
      if (pair < minimumKept) return undefined;
      const appended = value.slice(pair);
      return appended.length === 0 ? [{ index: 0, delete: trim, text: "" }]
        : [{ index: 0, delete: trim, text: "" }, { index: pair, delete: 0, text: appended }];
    }
  }
  return kept < minimumKept ? undefined : [single];
}

/**
 * Applies a splice `{length, index, delete, text}` to `current`. Throws when `current` is not a string of `length`
 * code units, or the range is outside it or ends inside a surrogate pair.
 */
export function applySplice(current: unknown, change: Readonly<Record<string, unknown>>): string {
  if (typeof current !== "string") throw new Error("A splice needs a string value.");
  const { length, index, delete: removed, text } = change;
  if (!isSafe(length) || !isSafe(index) || !isSafe(removed) || typeof text !== "string") throw new Error("Invalid splice.");
  if (length !== current.length) throw new Error("The text no longer has the length this splice names.");
  if (index > length || removed > length - index || !isBoundary(current, index) || !isBoundary(current, index + removed))
    throw new Error("The splice's range is outside the text or inside a surrogate pair.");
  return current.slice(0, index) + text + current.slice(index + removed);
}

/**
 * Applies a text change `{ field, kind: "text", length, trim, text }` of an append-only text field: the current value
 * must have `length` UTF-16 code units; the first `trim` are dropped and `text` is appended.
 */
export function applyText(current: unknown, change: Readonly<Record<string, unknown>>): string {
  if (typeof current !== "string") throw new Error("A text change needs a string value.");
  const { length, trim, text } = change;
  if (!isSafe(length) || !isSafe(trim) || typeof text !== "string") throw new Error("Invalid text change.");
  if (length !== current.length) throw new Error("The text no longer has the length this change names.");
  if (trim > length) throw new Error("Invalid text trim.");
  return current.slice(trim) + text;
}
