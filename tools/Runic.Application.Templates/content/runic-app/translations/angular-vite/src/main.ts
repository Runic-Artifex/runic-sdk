import { bootstrapApplication } from "@angular/platform-browser";
import { AppComponent } from "./app/app";
import { startTranslations } from "./translations";

// runic() in vite.config.ts adds the Runic development client to the page while Vite serves it: it reports Views
// failures to the development server's terminal and warns about a text field that loses typed characters.

// The page shows the application's language, from Frontend/translations; until the window's locale arrives, the
// browser's. <html lang dir> follow it.
startTranslations();

bootstrapApplication(AppComponent).catch(error => console.error(error));
