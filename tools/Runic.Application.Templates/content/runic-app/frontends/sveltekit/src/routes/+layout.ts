import { runicPrerenderedPageOptions } from "@runic-artifex/sveltekit/page-options";

// The page is prerendered to dist/index.html. It renders in the window only, where the Views client connects to
// the .NET Window, so the server renders an empty shell.
export const { prerender } = runicPrerenderedPageOptions;
export const ssr = false;
