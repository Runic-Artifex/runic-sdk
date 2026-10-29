import { bootstrapApplication } from "@angular/platform-browser";
import { AppComponent } from "./app/app";

// The Runic development client, under `ng serve` only: it reports Views failures to the development server's
// terminal and warns about a text field that loses typed characters. A production build defines ngDevMode as false
// and leaves the import out.
if (typeof ngDevMode === "undefined" || ngDevMode) await import("@runic-artifex/vite-plugin-runic/client");

bootstrapApplication(AppComponent).catch(error => console.error(error));
