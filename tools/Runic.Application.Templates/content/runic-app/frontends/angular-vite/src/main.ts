import { bootstrapApplication } from "@angular/platform-browser";
import { syncDocumentLocale } from "@runic-artifex/views";
import { AppComponent } from "./app/app";

// runic() in vite.config.ts adds the Runic development client to the page while Vite serves it: it reports Views
// failures to the development server's terminal and warns about a text field that loses typed characters.

// <html lang="en"> is the starter's language. Once the application sets the window's locale (IRunicWindowLocale),
// lang and dir follow it, and the Translations runtime that configureTranslatedText connects.
syncDocumentLocale();

bootstrapApplication(AppComponent).catch(error => console.error(error));
