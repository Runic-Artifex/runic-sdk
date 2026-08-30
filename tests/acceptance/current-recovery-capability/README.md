# Recovery and capability consumer

This exact-local profile combines the generated `Runic.Application` manifest and deterministic headless host with the published Editor recovery smoke. It consumes only the explicit local package feed and Editor publish directory; it creates isolated .NET/NuGet caches and has no product-source project reference.

```sh
RUNIC_W50_RECOVERY_NUGET_FEED=/absolute/path/feed \
RUNIC_W50_RECOVERY_APPLICATION_VERSION=0.2.0-preview.w50002 \
RUNIC_W50_RECOVERY_EDITOR_DIRECTORY=/absolute/path/editor-publish \
nix develop ../runic-toolkit -c ./eng/current-recovery-capability/verify.mjs run-twice > receipt.json

RUNIC_W50_RECOVERY_NUGET_FEED=/absolute/path/feed \
RUNIC_W50_RECOVERY_APPLICATION_VERSION=0.2.0-preview.w50002 \
RUNIC_W50_RECOVERY_EDITOR_DIRECTORY=/absolute/path/editor-publish \
nix develop ../runic-toolkit -c ./eng/current-recovery-capability/verify.mjs verify-twice receipt.json
```

The receipt binds the three package archives and Editor binary hashes. It proves undeclared/unconfigured/mismatched capability failures, normal/fault/cancellation lifecycle precedence, complete/rollback interrupted-journal recovery, blocked mutation, stale-session invalidation, and the existing Editor diagnostic schema without storing workspace, source, translation, review, session, or bridge content.
