# Runic Toolkit learning path

Learn the Runic Toolkit family by completing one runnable outcome at a time.
All samples target .NET 10 and consume released preview packages; none requires
a product-source checkout.

## Prerequisites

- .NET SDK 10.0.302.
- Node.js 24.18 and npm 11.16 for samples 3–5. From the repository root, run
  `npm ci` once before building their frontends.
- A supported desktop browser or WebView to open samples 3 and 4 interactively.
  Their smoke tests are headless.

| Step | Sample | Goal | Run | Smoke check |
| --- | --- | --- | --- | --- |
| 1 | [Hello lifecycle](01-HelloLifecycle/) | Compose startup, a UI mode, and shutdown. | `dotnet run --project samples/01-HelloLifecycle` | The normal run is its smoke check. |
| 2 | [Greeting command line](02-GreetingCommandLine/) | Parse and execute a typed command. | `dotnet run --project samples/02-GreetingCommandLine -- greet Ada` | `dotnet run --project samples/02-GreetingCommandLine -- greet Ada --output json` |
| 3 | [React Setup Application](03-SetupApplication/) | Run a React frontend in a native CS-WebUI host. | `dotnet run --project samples/03-SetupApplication` | `dotnet run --project samples/03-SetupApplication -- --smoke-test` |
| 4 | [SvelteKit Setup Application](04-SvelteKitSetupApplication/) | Run the same bridge contract with Svelte 5 and SvelteKit. | `dotnet run --project samples/04-SvelteKitSetupApplication` | `dotnet run --project samples/04-SvelteKitSetupApplication -- --smoke-test` |
| 5 | [Runic Translations Setup](05-RunicTranslationsSetup/) | Generate and use typed .NET and ESM translations. | Restore its local tool, then start the backend. | `dotnet run --project samples/05-RunicTranslationsSetup -- --smoke-test` |

The React sample keeps presentation state in React; the SvelteKit variant uses
Svelte 5 runes. In both, the Application Bridge owns transport validation,
session and revision handling, events, operations, and reconnect recovery.

Bootstrap 5.3 and Font Awesome are local sample assets, not Runic Toolkit
requirements. See [SharedAssets](SharedAssets/) for their pinned sources and
licenses.

Need a focused released-package check instead? See the
[integration canaries](../integrations/).

[Runic Toolkit documentation](https://docs.runic-artifex.eu/products/runic-toolkit) ·
[Package catalog and preview status](https://docs.runic-artifex.eu/packages) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
