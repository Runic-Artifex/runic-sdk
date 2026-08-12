# 03 — React Setup Application

Build a desktop setup wizard with a React frontend, a native CS-WebUI window,
and a validated Application Bridge contract. The host keeps navigation policy,
destination selection, installation work, progress, and cancellation; React
owns presentation state.

## Prerequisites

- .NET SDK 10.0.302.
- Node.js 24.18 and npm 11.16. Run `npm ci` from the repository root.
- A supported browser or WebView to open the native window. The smoke check is
  headless.

## Run and verify

From the repository root:

```bash
npm ci
npm run verify
dotnet run --project samples/03-SetupApplication
```

The final command opens a **Runic Toolkit · Setup** window. Complete the sample
flow to see the native host update the React UI.

For the deterministic host-only smoke check:

```bash
dotnet run --project samples/03-SetupApplication -- --smoke-test
```

It exits with code 0 and reports that completion, cancellation, failure, and
recovery passed.

To work on the frontend without a native host:

```bash
npm run dev:mock --workspace @runic-artifex/setup-application
```

## Contract boundary

Effect Schema defines the committed wire values. The C# generator consumes the
schemas and manifest under `Contract/generated/`; one managed Effect runtime
connects the frontend and an Effect Stream delivers validated host events. Keep
that generated contract stable when changing either side.

## Next

Continue to the [SvelteKit Setup Application](../04-SvelteKitSetupApplication/)
to use the same host contract with Svelte 5 runes.

[Application Bridge documentation](https://docs.runic-artifex.eu/application-bridge) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/03-SetupApplication) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
