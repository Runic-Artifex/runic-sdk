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
| 3 | [React Setup Application](samples/03-SetupApplication/) | Drive a native Runic Desktop setup wizard through the Application Bridge. |
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
- Node.js 24.18 and Bun 1.4.0 for samples 3–5. The current checkout retains
  historical samples, so do not run its root `bun install` as a package-acquisition
  step.
- A supported desktop browser or WebView for the interactive Desktop samples.
  The headless smoke checks do not open a window.

The historical package pins in this repository are retained for receipt
verification, not for local package acquisition. The current package-only
[Desktop clean-room journey](eng/current-clean-install/) takes its exact NuGet
and npm inputs from the compatibility set and requires all feeds explicitly.

The older W10–W70 receipt artifacts remain available as historical engineering
evidence. Their retired package identities and feeds are constrained by
[`eng/current-evidence-allowlist.json`](eng/current-evidence-allowlist.json);
they are not a competing current-release definition.

## Choose a package

These examples are a learning repository, not a package. Use the product that
matches the problem in your own application; preview packages should be tested
against your supported runtime before production use.

| Need | Install | Learn more |
| --- | --- | --- |
| Application lifecycle and generated manifests | `dotnet add package Runic.Application --prerelease` | [Runic Toolkit](https://docs.runic-artifex.eu/products/runic-toolkit) |
| Typed command-line applications | `dotnet add package Runic.CommandLine --prerelease` | [Runic Command Line](https://docs.runic-artifex.eu/products/runic-command-line) |
| A validated application bridge contract | `dotnet add package Runic.Application.Bridge --prerelease` | [Application Bridge](https://docs.runic-artifex.eu/application-bridge) |
| A current Svelte/Vite application template | `dotnet new install Runic.Application.Templates::<candidate>` | [Current Svelte template consumer](eng/current-svelte-template/) |
| Typed localization for .NET and ESM | `dotnet add package Runic.Translations --prerelease` | [Runic Translations](https://docs.runic-artifex.eu/products/runic-translations) |
| Application Bridge in a browser frontend | `npm install @runic-artifex/application-bridge` | [JavaScript packages](https://docs.runic-artifex.eu/packages) |

For the full package catalog, compatibility notes, and release status, see
[Runic Artifex packages](https://docs.runic-artifex.eu/packages) and
[releases](https://docs.runic-artifex.eu/releases). The examples pin known-good
preview versions in `Directory.Packages.props` and `package-lock.json`; use
those pins when reproducing a sample, not as a general version-selection policy.

## Verification

The commands below document the frozen examples receipt and must not be used
for local package acquisition. Its pinned package graph is historical. For the
current package-only path, use the explicit-feed
[Desktop clean-room journey](eng/current-clean-install/).

```bash
dotnet tool restore --tool-manifest samples/05-RunicTranslationsSetup/.config/dotnet-tools.json
dotnet build samples/05-RunicTranslationsSetup/Runic.TranslationsSetup.csproj
bun run verify
dotnet run --project samples/01-HelloLifecycle --configuration Release
dotnet run --project samples/02-GreetingCommandLine --configuration Release -- greet Runic
dotnet run --project samples/03-SetupApplication --configuration Release -- --smoke-test
dotnet run --project samples/04-SvelteKitSetupApplication --configuration Release -- --smoke-test
dotnet run --project samples/05-RunicTranslationsSetup --configuration Release -- --smoke-test
```

The package-only [integration canaries](integrations/) provide focused checks
for the broader Runic Toolkit family, including NativeAOT coverage where it is
supported.

Maintainers advance the immutable GitHub Packages integration set using the
manual [CI candidate procedure](eng/CI.md). The examples CI downloads those
exact candidates and never rebuilds producer repositories.

## Documentation and support

[Documentation](https://docs.runic-artifex.eu/) ·
[Examples on GitHub](https://github.com/Runic-Artifex/runic-toolkit-examples) ·
[Report an issue](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)

Runic Toolkit Examples is licensed under the [MIT License](LICENSE).
