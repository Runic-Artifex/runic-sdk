// Svelte's bundlers (Vite, in development and in builds) replace process.env.NODE_ENV, as React's and Vue's do.
declare const process: { readonly env: { readonly NODE_ENV?: string } };

/** Whether the page is a development build. A page whose bundler does not replace NODE_ENV counts as production. */
export function isDevelopment(): boolean {
  try {
    return process.env.NODE_ENV !== "production";
  } catch {
    return false;
  }
}
