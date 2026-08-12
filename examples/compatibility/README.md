![Runic Toolkit banner](.github/assets/brand/banner.png)

# Runic Toolkit Examples

Build a small .NET application, then follow it through a typed command line,
a native React setup experience, a SvelteKit variant, and localized text. Each
sample is runnable on its own and uses released Runic Artifex packages rather
than a product-source checkout.

## Start here

| Step | Sample | Outcome |
| --- | --- | --- |
| 1 | [Hello lifecycle](samples/01-HelloLifecycle/) | Compose startup work, a UI launch mode, and orderly shutdown. |
| 2 | [Greeting command line](samples/02-GreetingCommandLine/) | Parse a typed command and present human or JSON output. |
| 3 | [React Setup Application](samples/03-SetupApplication/) | Drive a native CS-WebUI setup wizard through the Application Bridge. |
| 4 | [SvelteKit Setup Application](samples/04-SvelteKitSetupApplication/) | Use the same native bridge contract with Svelte 5 runes and SvelteKit. |
| 5 | [Runic Translations Setup](samples/05-RunicTranslationsSetup/) | Generate typed .NET and ESM translations with isolated request locales. |

Start with the first sample:

```bash
dotnet run --project samples/01-HelloLifecycle
```

It prints workspace startup, `Hello from Runic Toolkit!`, and an orderly close.
Each sample README includes its prerequisites, smoke command, expected result,
and next step.

## Prerequisites

- .NET SDK 10.0.302 (the repository pins it in [`global.json`](global.json)).
- Node.js 24.18 and npm 11.16 for samples 3–5; install their locked workspace
  dependencies once with `npm ci` from the repository root.
- A supported desktop browser or WebView for the interactive CS-WebUI samples.
  The headless smoke checks do not open a window.

The committed [`.npmrc`](.npmrc) supplies the scoped npm registry
configuration. Check the public package catalog for the current availability of
each preview artifact.

## Choose a package

These examples are a learning repository, not a package. Use the product that
matches the problem in your own application; preview packages should be tested
against your supported runtime before production use.

| Need | Install | Learn more |
| --- | --- | --- |
| Application lifecycle and desktop/browser hosting | `dotnet add package RunicToolkit.Hosting --prerelease` | [Runic Toolkit](https://docs.runic-artifex.eu/products/runic-toolkit) |
| Typed command-line applications | `dotnet add package RunicCommandLine --prerelease` | [Runic Command Line](https://docs.runic-artifex.eu/products/runic-command-line) |
| A validated native-to-web application contract | `dotnet add package RunicToolkit.ApplicationBridge --prerelease` | [Application Bridge](https://docs.runic-artifex.eu/application-bridge) |
| Flow orchestration without a UI | `dotnet add package RunicFlow --prerelease` | [Runic Flow](https://docs.runic-artifex.eu/products/runic-flow) |
| Typed localization for .NET and ESM | `dotnet add package RunicTranslations --prerelease` | [Runic Translations](https://docs.runic-artifex.eu/products/runic-translations) |
| Application Bridge in a browser frontend | `npm install @runic-artifex/application-bridge` | [JavaScript packages](https://docs.runic-artifex.eu/packages) |

For the full package catalog, compatibility notes, and release status, see
[Runic Artifex packages](https://docs.runic-artifex.eu/packages) and
[releases](https://docs.runic-artifex.eu/releases). The examples pin known-good
preview versions in `Directory.Packages.props` and `package-lock.json`; use
those pins when reproducing a sample, not as a general version-selection policy.

## Verification

Run the locked frontend checks and the headless samples after `npm ci`:

```bash
dotnet tool restore --tool-manifest samples/05-RunicTranslationsSetup/.config/dotnet-tools.json
dotnet build samples/05-RunicTranslationsSetup/RunicTranslationsSetup.csproj
npm run verify
dotnet run --project samples/01-HelloLifecycle --configuration Release
dotnet run --project samples/02-GreetingCommandLine --configuration Release -- greet Runic
dotnet run --project samples/03-SetupApplication --configuration Release -- --smoke-test
dotnet run --project samples/04-SvelteKitSetupApplication --configuration Release -- --smoke-test
dotnet run --project samples/05-RunicTranslationsSetup --configuration Release -- --smoke-test
```

The package-only [integration canaries](integrations/) provide focused checks
for the broader Runic Toolkit family, including NativeAOT coverage where it is
supported.

## Documentation and support

[Documentation](https://docs.runic-artifex.eu/) ·
[Examples on GitHub](https://github.com/Runic-Artifex/runic-toolkit-examples) ·
[Report an issue](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)

Runic Toolkit Examples is licensed under the [MIT License](LICENSE).
