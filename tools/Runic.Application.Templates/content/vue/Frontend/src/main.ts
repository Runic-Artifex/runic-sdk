import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { createApp } from "vue";
import App from "./App.vue";
import { counterBridge } from "./counter-bridge";

createApp(App).mount("#app");
globalThis.addEventListener("pagehide", () => void counterBridge.dispose(), { once: true });
