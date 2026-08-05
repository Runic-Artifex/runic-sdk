# Architecture

The dependency direction is deliberately inward:

1. `RunicToolkit.Collections`, `RunicToolkit.Desktop`, and `RunicToolkit.MVVM`
   define framework-neutral contracts.
2. `RunicToolkit.MVVM.Build` and the binding compiler generate code against the
   MVVM contract.
3. CommunityToolkit.Mvvm and ReactiveUI adapters depend on MVVM core.
4. Hosting core depends on abstractions; Generic Host, WebUi, and CsWebUi are
   explicit adapters layered above it.
5. `RunicToolkit.Frontend.Sdk` coordinates Node workspaces and exposes a generic
   external-compiler seam without depending on a markup language.
6. TypeScript React, Vue, Svelte, and Angular adapters depend on
   `@runic-artifex/mvvm` and treat their UI frameworks as peers.

Independent products own outward integration packages. For example,
`RunicMarkup.RunicToolkit.*` may depend on Toolkit packages, while Toolkit core
does not depend on Runic Markup. The same rule applies to Flow, Assets, Command
Line, Text Resources, and future integrations.

Cross-domain source references inside this repository are declared in
`eng/ownership.json`. Cross-repository composition is verified through packed
NuGet/npm consumers rather than source references.
