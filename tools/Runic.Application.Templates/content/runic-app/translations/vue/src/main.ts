import { createApp } from "vue";
import App from "./App.vue";
import { startTranslations } from "./translations";

// The page shows the application's language, from Frontend/translations; until the window's locale arrives, the
// browser's. <html lang dir> follow it.
startTranslations();

createApp(App).mount("#app");
