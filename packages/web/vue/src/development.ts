// Vue's bundler builds require bundlers to define process.env.NODE_ENV (Vite does, in development and in builds).
declare const process: { readonly env: { readonly NODE_ENV?: string } };

/**
 * Whether the page is a development build. A page whose bundler does not replace NODE_ENV, such as Vue's esm-browser
 * build through an import map, counts as production instead of throwing `process is not defined`.
 */
export function isDevelopment(): boolean {
  try {
    return process.env.NODE_ENV !== "production";
  } catch {
    return false;
  }
}
