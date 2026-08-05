# Runic Assets

Runic Assets is a framework-neutral static-asset model for .NET applications.
It provides safe paths, immutable manifests, embedded and development sources,
and a portable ZIP archive. Its core is trimming- and NativeAOT-compatible and
does not depend on a UI or web framework.

This repository was extracted from Runic Toolkit with its product history
intact. It uses independent `RunicAssets*` package, assembly, namespace, and
archive identities without compatibility aliases for the retired Toolkit-owned
identity.

## Projects

| Project | Purpose |
| --- | --- |
| `RunicAssets` | Transport-neutral contracts, sources, validation, media types, and portable archives |
| `RunicAssets.CsWebUi` | Assets-owned adapter to CsWebUi's in-memory VFS |
| `RunicAssets.AspNetCore` | Exact ASP.NET Core endpoints with cache and entity-tag metadata |
| `integrations/RunicAssets.RunicToolkit` | Published Toolkit frontend-asset integration owned by Runic Assets |

The Toolkit adapter is part of the standalone solution and consumes Toolkit
contracts as exact packages. This preserves the dependency direction: adapters
depend on both products; neither core depends on an integration.

## Archives

`AssetArchive` writes a standard ZIP containing a canonical
`runic-assets.json` manifest and declared files below `assets/`. Paths and
metadata are validated on read, undeclared content is rejected, and no private
host-specific archive format is required.

## Development

```bash
nix develop
./eng/verify.sh
```

Verification performs a warning-free Release build, contract and adapter tests,
an isolated package-consumer test, and NativeAOT publication and execution.

## Prerelease packages

Pull requests produce validated, non-published artifacts for `RunicAssets`,
`RunicAssets.CsWebUi`, and `RunicAssets.AspNetCore`. Publishing to GitHub
Packages is a separate manually guarded workflow action.

```bash
./eng/pack.sh 0.1.0-preview.local.1 /tmp/runic-assets-packages
```

## License

Runic Assets is licensed under the [MIT License](LICENSE).
