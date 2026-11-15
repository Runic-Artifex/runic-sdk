import { runicComponentMount } from "@runic-artifex/angular/experimental";
import { Widget } from "./app/widget";

// The component build's entry (vite.config.ts): the mount function of the widget's custom element. Each element gets
// an Angular application of its own, connected to its own Runic instance, so the widget connects to the guest's Window
// and never to the host page's instance. Its styles become stylesheets of the element's shadow root, and removing the
// element destroys the application: nothing of the host's document changes.
export default runicComponentMount(Widget);
