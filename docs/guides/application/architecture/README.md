# Architecture

The dependency direction is deliberately inward:

1. `RunicToolkit.Collections`, `RunicToolkit.Desktop`, and the Application
   Bridge contract kernel define framework-neutral contracts.
2. Effect Schema and the canonical bridge manifest define encoded wire data;
   generators consume committed artifacts and never start Node during C# builds.
3. Presentation-framework adapters project validated application state without
   owning transport lifecycle or protocol parsing.
4. Hosting core depends on abstractions; Generic Host, WebUi, and CsWebUi are
   explicit adapters layered above it.
5. `RunicToolkit.Frontend.Sdk` coordinates Node workspaces without assuming an
   authoring language.
6. TypeScript React, Vue, Svelte, and Angular adapters depend on the neutral
   Application Bridge service and treat their UI frameworks as peers.

Independent products own outward integration packages. Flow, Assets, Command
Line, Text Resources, and future integrations may depend on Toolkit packages,
while Toolkit core does not depend on those products.

Cross-domain source references inside this repository are declared in
`eng/ownership.json`. Cross-repository composition is verified through packed
NuGet/npm consumers rather than source references.
