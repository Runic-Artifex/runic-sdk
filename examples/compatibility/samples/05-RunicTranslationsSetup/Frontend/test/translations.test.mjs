import assert from "node:assert/strict";
import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";
import { after, before, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

let server;
let messages;
let runtime;
let transport;

before(async () => {
  server = await createServer({
    root: fileURLToPath(new URL("../", import.meta.url)),
    configFile: false,
    logLevel: "silent",
    plugins: [
      runicTranslations({
        manifest: fileURLToPath(
          new URL(
            "../../obj/net10.0/translations/setup.esm/web-module-manifest-v1.json",
            import.meta.url,
          ),
        ),
      }),
    ],
    server: { middlewareMode: true },
  });
  messages = await server.ssrLoadModule("virtual:runic-translations/setup");
  runtime = await server.ssrLoadModule("virtual:runic-translations/setup/runtime");
  transport = await server.ssrLoadModule("virtual:runic-translations/setup/transport");
});

after(async () => {
  await server?.close();
});

test("one generated catalog formats English and German with explicit request locales", () => {
  assert.equal(messages.m$Application$Title({ locale: "en" }), "Runic Translations setup");
  assert.equal(messages.m$Application$Title({ locale: "de" }), "Runic-Translations-Einrichtung");
  assert.equal(runtime.resolveLocale("de-DE"), "de");
});

test("concurrent SSR work stays isolated because locale is request-scoped input", async () => {
  const calls = Array.from({ length: 100 }, (_, index) => {
    const locale = index % 2 === 0 ? "en" : "de";
    const expected = locale === "en" ? "The field email is required." : "Das Feld email ist erforderlich.";
    return Promise.resolve().then(() => {
      const actual = messages.m$Validation$Required({ field: "email" }, { locale });
      assert.equal(actual, expected);
      return actual;
    });
  });

  const results = await Promise.all(calls);
  assert.equal(results.filter((value) => value.startsWith("The field")).length, 50);
  assert.equal(results.filter((value) => value.startsWith("Das Feld")).length, 50);
});

test("browser transport validates fingerprint, key, and typed arguments", () => {
  const wire = referenceWire();
  const decoded = transport.decodeTextReference(wire);
  assert.equal(decoded.ok, true);
  assert.equal(
    transport.formatTextReference(
      decoded.value,
      {
        "Validation.Required": (inputs, options) =>
          messages.m$Validation$Required({ field: inputs.field }, options),
      },
      { locale: "de" },
    ),
    "Das Feld email ist erforderlich.",
  );

  assert.deepEqual(
    transport.decodeTextReference({ ...wire, contractFingerprint: `sha256:${"0".repeat(64)}` }),
    { ok: false, reason: "fingerprint-mismatch" },
  );
  assert.deepEqual(
    transport.decodeTextReference({ ...wire, key: "Validation.Unknown" }),
    { ok: false, reason: "unknown-key" },
  );
  assert.deepEqual(
    transport.decodeTextReference({ ...wire, arguments: { field: false } }),
    { ok: false, reason: "invalid-argument:field" },
  );
});

test("version skew falls back only to sender-provided plain text", async () => {
  const module = await server.ssrLoadModule("/src/lib/messages.ts");
  const skewed = { ...referenceWire(), contractFingerprint: `sha256:${"0".repeat(64)}` };
  assert.equal(module.localizeReference(skewed, "de"), "The field email is required.");

  const withoutFallback = { ...skewed };
  delete withoutFallback.fallbackText;
  assert.equal(module.localizeReference(withoutFallback, "de"), "Unlocalizable server message (fingerprint-mismatch)");
});

function referenceWire() {
  return {
    version: 1,
    catalog: runtime.catalog,
    contractFingerprint: runtime.contractFingerprint,
    key: "Validation.Required",
    arguments: { field: "email" },
    fallbackText: "The field email is required.",
  };
}
