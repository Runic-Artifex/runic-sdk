import { mount } from "svelte";
import App from "./App.svelte";
import { startTranslations } from "./translations";

// The page shows the application's language, from Frontend/translations; until the window's locale arrives, the
// browser's. <html lang dir> follow it.
startTranslations();

mount(App, { target: document.getElementById("app")! });
