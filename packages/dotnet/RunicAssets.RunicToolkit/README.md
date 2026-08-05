# RunicAssets.RunicToolkit

This is the Runic Assets-owned Toolkit integration seam. It validates the
shared `IAssetSource` and derives the escaped application entry point without
making the core package depend on Runic Toolkit.

The source remains outside the standalone solution until Runic Toolkit hosting
contracts are published as packages. At that point this directory becomes the
`RunicAssets.RunicToolkit` adapter package; the dependency direction stays
`RunicAssets.RunicToolkit -> RunicAssets + RunicToolkit`, never the reverse.
