// Renders the runic-app template's placeholder icon, content/runic-app/icons/icon.svg, into the files the project
// and its packages use: icon.png (512 px, the desktop entry, Flatpak and window icons) and icon.ico (16 to 256 px,
// the Windows executable's icon). As Windows' own icons do, the ICO holds 32-bit bitmaps up to 48 px and a PNG at
// 64 px and above, which keeps it small. Needs ImageMagick 7 (`magick`) with SVG support. Run it after changing icon.svg:
//
//   bun tools/Runic.Application.Templates/render-icons.mjs
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const icons = join(dirname(fileURLToPath(import.meta.url)), "content/runic-app/icons");

function render(size, format = "png") {
  const result = spawnSync("magick", ["-background", "none", "-density", "384", join(icons, "icon.svg"),
    "-resize", `${size}x${size}`, "-depth", "8", "-strip", "-define", "png:exclude-chunks=date,time", `${format}:-`],
    { maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`magick failed: ${result.stderr}`);
  return result.stdout;
}

// An ICO bitmap: BITMAPINFOHEADER with twice the height, bottom-up BGRA rows, then an all-zero AND mask.
export function dib(size, rgba) {
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);
  header.writeInt32LE(size, 4);
  header.writeInt32LE(size * 2, 8);
  header.writeUInt16LE(1, 12);
  header.writeUInt16LE(32, 14);
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++)
    for (let x = 0; x < size; x++) {
      const from = (y * size + x) * 4, to = ((size - 1 - y) * size + x) * 4;
      pixels[to] = rgba[from + 2];
      pixels[to + 1] = rgba[from + 1];
      pixels[to + 2] = rgba[from];
      pixels[to + 3] = rgba[from + 3];
    }
  header.writeUInt32LE(pixels.length, 20);
  const mask = Buffer.alloc(Math.ceil(size / 32) * 4 * size);
  return Buffer.concat([header, pixels, mask]);
}

// ICONDIR, one ICONDIRENTRY per image (a width or height of 256 is written as 0), then the PNG images.
// `png` is the image's data: PNG, or a bitmap from dib().
export function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, png }, index) => {
    const entry = 6 + 16 * index;
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...images.map(image => image.png)]);
}

if (import.meta.main) {
  writeFileSync(join(icons, "icon.png"), render(512));
  writeFileSync(join(icons, "icon.ico"), ico([256, 128, 64, 48, 32, 24, 16].map(size =>
    ({ size, png: size >= 64 ? render(size) : dib(size, render(size, "rgba")) }))));
  console.log(`Rendered ${join(icons, "icon.png")} and icon.ico from icon.svg (${readFileSync(join(icons, "icon.svg")).length} bytes).`);
}
