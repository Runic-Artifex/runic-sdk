import { bootstrapApplication } from "@angular/platform-browser";
import { syncDocumentLocale } from "@runic-artifex/views";
import { AppComponent } from "./app/app";

// The Runic development client, under `ng serve` only: it reports Views failures to the development server's
// terminal and warns about a text field that loses typed characters. A production build defines ngDevMode as false
// and leaves the import out.
if (typeof ngDevMode === "undefined" || ngDevMode) await import("@runic-artifex/vite-plugin-runic/client");

// <html lang="en"> is the starter's language. Once the application sets the window's locale (IRunicWindowLocale),
// lang and dir follow it, and the Translations runtime that configureTranslatedText connects.
syncDocumentLocale();

bootstrapApplication(AppComponent).catch(error => console.error(error));
