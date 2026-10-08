import { bootstrapApplication } from "@angular/platform-browser";
import { App } from "./app/app";
import { keepFocusAcrossDisabling } from "../../Frontend/src/focus.js";

// Navigation and running commands disable their buttons; focus returns to them afterwards.
keepFocusAcrossDisabling();
bootstrapApplication(App).catch(error => console.error(error));
