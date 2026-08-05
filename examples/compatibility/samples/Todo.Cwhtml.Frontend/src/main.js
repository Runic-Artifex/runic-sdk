import "@fortawesome/fontawesome-free/css/fontawesome.min.css";
import "@fortawesome/fontawesome-free/css/regular.min.css";
import "@fortawesome/fontawesome-free/css/solid.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "../../SimpleTodo/www/site.css";
import "../../AdvancedTodo/wwwroot/advanced-todo.css";

await import("runic-markup-htmx-cswebui");
const htmxModule = await import("htmx.org");
globalThis.htmx ??= htmxModule.default;
await import("runic-markup-htmx-csp");
await import("runic-markup-htmx");
await import("bootstrap/dist/js/bootstrap.bundle.min.js");

globalThis.__runicToolkitViteModuleRevision =
  (globalThis.__runicToolkitViteModuleRevision ?? 0) + 1;
if (import.meta.hot) {
  await import("./hmr-probe.js");
  import.meta.hot.accept();
}
