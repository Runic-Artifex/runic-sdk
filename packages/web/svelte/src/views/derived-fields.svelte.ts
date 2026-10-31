/**
 * Derives each member of a record that a controller replaces as a whole, so a reader of one member, such as an
 * `$effect` that reads only `pending`, runs again only when that member changes and not with every new record.
 * Call it where `$derived` may be created: during component initialization or in an `$effect.root`.
 */
export function derivedFields<T extends object, K extends keyof T>(record: () => T, keys: readonly K[]): { readonly [P in K]: () => T[P] } {
  const readers = {} as { [P in K]: () => T[P] };
  for (const key of keys) {
    const value = $derived(record()[key]);
    readers[key] = () => value;
  }
  return readers;
}
