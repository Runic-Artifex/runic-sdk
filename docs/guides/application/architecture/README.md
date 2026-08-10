# Architecture

The dependency direction is deliberately inward:

1. `RunicToolkit.Collections`, `RunicToolkit.Desktop`, and the Application
   Bridge contract kernel define framework-neutral contracts.
2. Effect Schema and the canonical bridge manifest define encoded wire data;
   generators consume committed artifacts and never start Node during C# builds.
3. Presentation frameworks consume one framework-neutral controller.
   Framework-owned integration repositories may publish idiomatic lifecycle
   projections without owning protocol state.
4. Hosting core depends on abstractions; Generic Host, WebUi, and CsWebUi are
   explicit adapters layered above it.
5. `RunicToolkit.Hosting.Build` turns an explicitly built frontend directory
   into a verified application asset manifest.
6. React, Vue, Svelte, Angular, Avalonia, or future renderers own only their
   presentation state and framework lifecycle; the bridge owns validation,
   transport, revisions, reconnects, cancellation, and command semantics.

Independent products own outward integration packages. Flow, Assets, Command
Line, Runic Translations, and future integrations may depend on Toolkit packages,
while Toolkit core does not depend on those products.

Cross-domain source references inside this repository are declared in
`eng/ownership.json`. Cross-repository composition is verified through packed
NuGet/npm consumers rather than source references.
