# Local support-envelope consumer

This exact-local fixture consumes only a directly packed `dotnet-runic` Tool archive and a directly published Editor directory supplied as explicit paths. It copies those artifacts into fresh temporary directories, uses isolated .NET and NuGet caches, and never adds a product project reference.

Run it from a Nix .NET SDK environment:

```sh
RUNIC_W50_TOOL_PACKAGE=/absolute/path/dotnet-runic.0.2.0-preview.w50001.nupkg \
RUNIC_W50_EDITOR_DIRECTORY=/absolute/path/editor-publish \
nix develop ../runic-toolkit -c npm run verify:current-support-envelope > receipt.json

RUNIC_W50_TOOL_PACKAGE=/absolute/path/dotnet-runic.0.2.0-preview.w50001.nupkg \
RUNIC_W50_EDITOR_DIRECTORY=/absolute/path/editor-publish \
nix develop ../runic-toolkit -c node eng/current-support-envelope/verify.mjs verify-twice receipt.json
```

The receipt binds the Tool archive and Editor binary hashes, and `verify-twice` recomputes those hashes from the explicit supplied paths. Each journey previews the explicit Editor collector and omissions, collects byte-identical canonical envelopes twice, verifies removal of both envelopes, rejects workspace-root/relative-path/token/source/translation/review hostile inputs, and records zero outbound transport attempts. The verifier fails closed if provenance or any of that evidence is forged or softened.
