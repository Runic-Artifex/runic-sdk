import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const index = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
assert.match(index, /<script src="\.\/runic-desktop\.js"><\/script>/);
assert.match(index, /(?:src|href)="\.\/assets\//);
assert.doesNotMatch(index, /(?:src|href)=["']\/(?:runic-desktop\.js|assets\/|_app\/)/);

console.log("React Desktop bootstrap and production assets are surface-relative.");
