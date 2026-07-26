import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const root = import.meta.dirname;
const manifest = JSON.parse(await readFile(
  resolve(root, "dist/webuitoolkit.assets.json"),
  "utf8",
));

if (manifest.schema !== "webuitoolkit.frontend-assets/1" ||
    manifest.framework !== "cwhtml-htmx" ||
    manifest.mode !== "production") {
  throw new Error("The cwhtml asset manifest identity is invalid.");
}
if (!manifest.entrypoints.compiledApp.includes("-") ||
    !manifest.entrypoints.compiledStyles.includes("-")) {
  throw new Error("Production cwhtml assets must be content hashed.");
}
for (const required of ["cwhtml.js", "cwhtml.css"]) {
  if (manifest.files[required] === undefined) {
    throw new Error(`The cwhtml asset graph is missing ${required}.`);
  }
}

console.log("cwhtml + HTMX Vite production build verified.");
