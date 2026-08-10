import assert from "node:assert/strict";
import { Server } from "../.svelte-kit/output/server/index.js";
import { manifest } from "../.svelte-kit/output/server/manifest-full.js";

const server = new Server(manifest);
await server.init({ env: {}, read: undefined });

const responses = await Promise.all(
  Array.from({ length: 40 }, (_, index) => {
    const locale = index % 2 === 0 ? "en" : "de";
    return server.respond(new Request(`http://reference.invalid/${locale}`), {
      getClientAddress: () => "127.0.0.1",
    });
  }),
);

for (let index = 0; index < responses.length; index += 1) {
  const response = responses[index];
  const html = await response.text();
  const english = index % 2 === 0;
  assert.equal(response.status, 200);
  assert.match(html, new RegExp(`<html lang="${english ? "en" : "de"}">`));
  assert.match(html, english ? /Runic Translations setup/ : /Runic-Translations-Einrichtung/);
  assert.doesNotMatch(html, english ? /Runic-Translations-Einrichtung/ : /Runic Translations setup/);
}

console.log("PASS: 40 concurrent SvelteKit SSR requests retained their URL locale");
