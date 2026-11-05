import { createApp } from "vue";
import { syncDocumentLocale } from "@runic-artifex/views";
import App from "./App.vue";

// <html lang="en"> is the starter's language. Once the application sets the window's locale (IRunicWindowLocale),
// lang and dir follow it, and the Translations runtime that configureTranslatedText connects.
syncDocumentLocale();

createApp(App).mount("#app");
