// The page-wide Views runtime (specs/application, "Shared globals"): rewritten for protocol 4.
/** The message of a caught value, for an error that wraps it. */
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
