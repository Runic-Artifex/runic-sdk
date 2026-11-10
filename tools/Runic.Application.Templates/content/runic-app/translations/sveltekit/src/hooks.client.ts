// The Runic development client: it reports Views failures to the development server's timeline and warns about a
// text field that loses typed characters. runic({ html: false }) in vite.config.ts serves it under `vite dev` and
// replaces it with an empty module in the build.
import "@runic-artifex/vite-plugin-runic/client";
import { startTranslations } from "./lib/translations";

// The page shows the application's language, from Frontend/translations; until the window's locale arrives, the
// browser's. <html lang dir> follow it.
startTranslations();
