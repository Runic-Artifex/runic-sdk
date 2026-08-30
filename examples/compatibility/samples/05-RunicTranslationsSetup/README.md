# 05 — Runic Translations Setup

Generate one catalog into typed C# and tree-shakable ESM, then use it safely
from a .NET backend and an SSR-capable SvelteKit frontend. The sample proves
English and German request-local formatting, typed text references, and a
version-skew fallback.

## Prerequisites

- .NET SDK 10.0.302 and Node.js 24.18 with npm 11.16.
- From the repository root, run `npm ci` to restore the locked frontend
  workspace packages.
- Restore the version-pinned Runic Translations local tool before building:
  `dotnet tool restore --tool-manifest samples/05-RunicTranslationsSetup/.config/dotnet-tools.json`.

## Run and verify

From the repository root:

```bash
npm ci
dotnet tool restore --tool-manifest samples/05-RunicTranslationsSetup/.config/dotnet-tools.json
dotnet build samples/05-RunicTranslationsSetup/Runic.TranslationsSetup.csproj
npm run typecheck --workspace @runic-artifex/translations-setup-application
npm run test --workspace @runic-artifex/translations-setup-application
npm run build --workspace @runic-artifex/translations-setup-application
npm run verify:production --workspace @runic-artifex/translations-setup-application
dotnet run --project samples/05-RunicTranslationsSetup --no-build -- --smoke-test
```

The smoke command exits with code 0 and prints:

```text
PASS: C# and ESM generation, typed transport, fallback, and concurrent locale isolation
```

For interactive development, use separate shells:

```bash
dotnet run --project samples/05-RunicTranslationsSetup --urls http://127.0.0.1:5080
npm run dev --workspace @runic-artifex/translations-setup-application -- --open /en
```

Visit `/en` and `/de`; the backend also exposes `GET /health` and returns a
typed `TranslationReference` from `POST /api/registration`.

## What this verifies

| Scenario | Expected behavior |
| --- | --- |
| Catalog compilation | The same catalog produces C# and ESM artifacts. |
| URL locale | `/en` and `/de` select the respective text; unsupported tags fail closed. |
| Typed transport | Backend validation text is a typed `TranslationReference`; browser decoding validates the catalog fingerprint, key, and arguments. |
| Version skew | English `fallbackText` is used only after a decode failure. |
| SSR isolation | Concurrent .NET and SvelteKit requests retain their explicit locale. |
| Production output | Verification rejects a sentinel and records tree-shaking byte totals in `Frontend/artifacts/bundle-metrics.json`. |

The MSBuild integration writes generated output beneath
`obj/net10.0/translations/`. For a deterministic command-line baseline, run
from this sample directory:

```bash
dotnet tool run runic-translations generate \
  --catalog Resources/setup.catalog.json \
  --documents Resources/setup.en.json Resources/setup.de.json \
  --output obj/cli-verification \
  --emit-esm
dotnet tool run runic-translations verify \
  --catalog Resources/setup.catalog.json \
  --documents Resources/setup.en.json Resources/setup.de.json \
  --output obj/cli-verification \
  --emit-esm
```

Generated ESM exposes an exact-key `m` namespace: single-segment keys use dot
access and dotted keys use bracket access. This sample re-exports that namespace
from `src/lib/messages.ts` and keeps its text-reference transport handlers at
the same boundary.

## Next

You have completed the learning path. Revisit the
[integration canaries](../../integrations/) for focused package-consumer and
NativeAOT checks.

[Runic Translations documentation](https://docs.runic-artifex.eu/products/runic-translations) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/05-RunicTranslationsSetup) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
