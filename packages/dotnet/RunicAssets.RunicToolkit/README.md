# RunicAssets.RunicToolkit

This is the Runic Assets-owned Toolkit integration. It adapts the shared
`IAssetSource` to Runic Toolkit's `IFrontendAssetProvider`, translates immutable
manifest metadata, and derives the escaped application entry point without
making either core depend on the adapter.

`RunicAssets.RunicToolkit` depends on `RunicAssets` and the exact published
`RunicToolkit.Hosting.Abstractions` contract package. The dependency direction
stays `RunicAssets.RunicToolkit -> RunicAssets + RunicToolkit`, never the reverse.
