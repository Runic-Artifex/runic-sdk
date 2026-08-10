# Runic Translations Setup reference

This package-only compatibility fixture shows the same schema-v2 catalog compiled
into generated C# and tree-shakable ESM for a .NET backend and an SSR-capable
SvelteKit/Vite frontend. It has no source-project reference to a Runic product.

## Clean-checkout verification

Run these commands from the repository root after configuring package
authentication as described in the root README:

```bash
dotnet tool restore --tool-manifest samples/05-RunicTranslationsSetup/.config/dotnet-tools.json
dotnet restore samples/05-RunicTranslationsSetup/RunicTranslationsSetup.csproj
dotnet build samples/05-RunicTranslationsSetup/RunicTranslationsSetup.csproj
npm ci
npm run typecheck --workspace @runic-artifex/translations-setup-application
npm run test --workspace @runic-artifex/translations-setup-application
npm run build --workspace @runic-artifex/translations-setup-application
npm run verify:production --workspace @runic-artifex/translations-setup-application
dotnet run --project samples/05-RunicTranslationsSetup/RunicTranslationsSetup.csproj --no-build -- --smoke-test
```

The MSBuild integration calls the version-pinned `runic-translations` local tool
and writes generated artifacts below `obj/net10.0/translations/`. To create a
separate deterministic CLI baseline for editor changes, run:

```bash
cd samples/05-RunicTranslationsSetup
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

For interactive use, start the backend and frontend in separate shells:

```bash
dotnet run --project samples/05-RunicTranslationsSetup/RunicTranslationsSetup.csproj --urls http://127.0.0.1:5080
npm run dev --workspace @runic-artifex/translations-setup-application -- --open /en
```

## Scenario coverage

| # | Scenario | Fixture |
|---|---|---|
| 1 | Compile one catalog into C# and ESM | MSBuild, generator, and pinned CLI tool |
| 2 | English/German URL locale | `/en` and `/de`; unsupported tags fail closed |
| 3 | Switch locale through supported integration | Ordinary SvelteKit links plus explicit generated message locale |
| 4 | Typed backend validation reference | `POST /api/registration` returns `TranslationReference` JSON |
| 5 | Browser contract validation | Generated `decodeTextReference` tests fingerprint, key, and arguments |
| 6 | Version-skew fallback | The sender's English `fallbackText` is used only after decode failure |
| 7 | Concurrent SSR isolation | 100 interleaved calls pass locale explicitly; .NET uses one manager per request |
| 8 | Tree-shaking measurement | Production verification rejects the sentinel and records byte totals |
| 9 | Validate a dynamic locale pack | Blocked: preview 4.3 rejects the canonical v2 locale output path when JSON is selected |
| 10 | Editor and CLI workflow | CLI verification is automated; editor steps below use its supported open/save workflow |

Bundle metrics are emitted to
`Frontend/artifacts/bundle-metrics.json` and uploaded by CI. They are deliberately
generated rather than committed, so measurements always describe the tested
production build.

## Editor workflow

Open this directory in Runic Translations Editor, select `Resources/setup.catalog.json`,
edit either locale, save, and run the CLI `verify` command above. The editor does
not currently expose a stable headless automation API, so CI validates the exact
files produced by its supported save workflow through the public CLI rather than
driving editor internals.

The generated API in the currently published preview uses identifiers such as
`m$Application$Title`. The sample keeps those names behind `src/lib/messages.ts`.
When the separately tracked ergonomic `m` namespace ships publicly, this one
wrapper is the intended migration point; the fixture does not depend on an
unpublished compiler shape.
