import { bootstrapApplication } from "@angular/platform-browser";
import { provideZonelessChangeDetection } from "@angular/core";
import { Widget, WIDGET_PAGE } from "./app/widget";

// The widget's own page, in its own window: the page's Runic instance.
bootstrapApplication(Widget, { providers: [provideZonelessChangeDetection(), { provide: WIDGET_PAGE, useValue: true }] }).catch(error => console.error(error));
