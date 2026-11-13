import type { RunicComponentMount } from "@runic-artifex/views/component";
import { renderWidget } from "./widget";
// The component's CSS becomes constructable stylesheets that the element adopts in its shadow root.
import "./widget.css";

// The component build's entry (vite.config.ts): the mount function of the widget's custom element. It runs inside the
// element's own Runic instance, connected to the guest's Window, so the widget connects there and never to the host
// page's instance. It renders into the shadow root's container, touches nothing of the host's document, and returns the
// cleanup that runs when the host page removes the element.
const mount: RunicComponentMount = ({ container }) => {
  const stop = new AbortController();
  renderWidget(container, stop.signal).catch(error => {
    if (!stop.signal.aborted) console.error("The widget could not connect.", error);
  });
  return () => stop.abort();
};

export default mount;
