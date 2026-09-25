import { copyFile, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const source = fileURLToPath(new URL("../../../packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js", import.meta.url));
const target = fileURLToPath(new URL("./public/runic-cswebui.js", import.meta.url));
await mkdir(fileURLToPath(new URL("./public/", import.meta.url)), { recursive: true });
await copyFile(source, target);
