import { mount } from "svelte";
import App from "./App.svelte";
import { keepFocusAcrossDisabling } from "../../Frontend/src/focus.js";

// Navigation and running commands disable their buttons; focus returns to them afterwards.
keepFocusAcrossDisabling();
mount(App, { target: document.getElementById("app")! });
