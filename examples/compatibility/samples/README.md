# Samples

These projects are an ordered learning path. They consume exact published
Runic Toolkit, Command Line, and Flow packages without referencing any
product source checkout.

| Level | Project | What it demonstrates |
| --- | --- | --- |
| 1 | `01-HelloLifecycle` | Application startup, mode selection, shutdown, and Generic Host composition |
| 2 | `02-GreetingCommandLine` | Typed command catalog, parsing, execution, and human/JSON output |
| 3 | `03-SetupApplication` | The official Effect Schema-first Application Bridge through React and a native CS-WebUI host |
| 4 | `04-SvelteKitSetupApplication` | Svelte 5 runes, SvelteKit SPA output, Vite 8 HMR/DevTools, and the same native bridge contract |

Start anywhere:

```bash
dotnet run --project samples/01-HelloLifecycle
dotnet run --project samples/02-GreetingCommandLine -- greet Ada
dotnet run --project samples/03-SetupApplication
dotnet run --project samples/03-SetupApplication -- --smoke-test
dotnet run --project samples/04-SvelteKitSetupApplication
dotnet run --project samples/04-SvelteKitSetupApplication -- --smoke-test
```

The React Setup application keeps state in React. The SvelteKit variant projects
the same authoritative state into Svelte 5 runes. In both cases transport,
validation, session, revisions, event sequence, operations, and reconnect
recovery belong to the framework-neutral Application Bridge runtime.

Bootstrap 5.3 and Font Awesome are the default visual baseline for desktop
samples. Assets must be pinned and served locally. Samples should prefer
Bootstrap's established layout, form, validation, navigation, modal, toast,
and accessibility patterns before adding custom CSS. Font Awesome icons must
retain accessible text or labels where the icon carries meaning.

This is a sample and migration convention, not a toolkit restriction. Core
packages remain styling-neutral, and their extension points must support
shadcn, Tailwind, raw CSS, or any consumer-owned design system.

On NixOS, run them inside the repository's direnv environment. The flake supplies
the pinned .NET SDK, native WebUI library, Chromium, and Linux WebView libraries.

Package-only canaries under [`../integrations`](../integrations) retain the
deterministic verification role.
