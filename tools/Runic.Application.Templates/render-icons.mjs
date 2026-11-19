// Renders the runic-app template's placeholder icon, content/runic-app/icons/icon.svg, into the files the project
// and its packages use: icon.png (512 px, the application's icon, from which the build derives the Windows .ico and the
// macOS icons and dotnet runic package the packages' icons, sdk#1207), and the templates' .template.config/icon.png
// (64 px, Visual Studio's New Project and Add New Item dialogs). Needs ImageMagick 7 (`magick`) with SVG support. Run
// it after changing icon.svg:
//
//   bun tools/Runic.Application.Templates/render-icons.mjs
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const content = join(dirname(fileURLToPath(import.meta.url)), "content");
const icons = join(content, "runic-app/icons");
// The icon that ide.host.json names, for each template.
const templateIcons = ["runic-app", "runic-view"].map(template => join(content, template, ".template.config/icon.png"));

function render(size) {
  const result = spawnSync("magick", ["-background", "none", "-density", "384", join(icons, "icon.svg"),
    "-resize", `${size}x${size}`, "-depth", "8", "-strip", "-define", "png:exclude-chunks=date,time", "png:-"],
    { maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`magick failed: ${result.stderr}`);
  return result.stdout;
}

if (import.meta.main) {
  writeFileSync(join(icons, "icon.png"), render(512));
  for (const icon of templateIcons) writeFileSync(icon, render(64));
  console.log(`Rendered ${join(icons, "icon.png")} and the templates' icon.png from icon.svg (${readFileSync(join(icons, "icon.svg")).length} bytes).`);
}
