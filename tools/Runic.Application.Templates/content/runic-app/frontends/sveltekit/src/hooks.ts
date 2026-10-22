import type { Reroute } from "@sveltejs/kit/hooks";

// A Window opens index.html, as a file, below the path the host serves it from. SvelteKit routes paths, so the
// file's path is the root route.
export const reroute: Reroute = ({ url }) =>
  url.pathname.endsWith("/index.html") ? url.pathname.slice(0, -"index.html".length) : undefined;
