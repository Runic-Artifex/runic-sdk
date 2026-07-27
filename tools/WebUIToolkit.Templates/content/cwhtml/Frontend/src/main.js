import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import htmx from "htmx.org";

globalThis.htmx ??= htmx;
const runtimeRoot = "/_content/WebUIToolkit.MVVM.Html.Htmx.Js";
await import(/* @vite-ignore */ `${runtimeRoot}/htmx-csp-2.0.10.js`);
await import(/* @vite-ignore */ `${runtimeRoot}/webuitoolkit-htmx-1.0.0.mjs`);

if (import.meta.hot) {
  import.meta.hot.accept();
}
