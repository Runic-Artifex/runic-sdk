import "@fortawesome/fontawesome-free/css/fontawesome.min.css";
import "@fortawesome/fontawesome-free/css/regular.min.css";
import "@fortawesome/fontawesome-free/css/solid.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "../../SimpleTodo/www/site.css";
import "../../AdvancedTodo/wwwroot/advanced-todo.css";

await import(
  "../../../src/WebUIToolkit.MVVM.Html.Htmx.CsWebUi/assets/webuitoolkit-htmx-cswebui-1.0.0.js"
);
const htmxModule = await import("htmx.org");
globalThis.htmx ??= htmxModule.default;
await import(
  "../../../src/WebUIToolkit.MVVM.Html.Htmx.Js/assets/htmx-csp-2.0.10.js"
);
await import(
  "../../../src/WebUIToolkit.MVVM.Html.Htmx.Js/assets/webuitoolkit-htmx-1.0.0.mjs"
);
await import("bootstrap/dist/js/bootstrap.bundle.min.js");
