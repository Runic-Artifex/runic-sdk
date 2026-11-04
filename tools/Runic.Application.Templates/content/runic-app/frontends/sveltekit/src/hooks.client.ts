// The Runic development client: it reports Views failures to the development server's timeline and warns about a
// text field that loses typed characters. runic({ html: false }) in vite.config.ts serves it under `vite dev` and
// replaces it with an empty module in the build.
import "@runic-artifex/vite-plugin-runic/client";
import { syncDocumentLocale } from "@runic-artifex/views";

// <html lang="en"> is the starter's language. Once the application sets the window's locale (IRunicWindowLocale),
// lang and dir follow it, and the Translations runtime that configureTranslatedText connects.
syncDocumentLocale();
