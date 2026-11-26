- **The renamed build names have their own list, the old-name guards (D079.11).** The `RunicBridge*`,
  `RunicViewsWindowProject`, `RunicApplicationId` and `RunicApplicationName` names that fail with `RUNICBRIDGE017` are
  now listed in `buildTransitive/Runic.Sdk.RenamedNames.targets`, which `Runic.Application.Views` and `Runic.Desktop`
  ship and import once. Each old name is still reported once, with the same message. The list gains two kinds that 0.24
  leaves empty: an old package ID in the package graph (`RUNICBRIDGE085`) and an old environment variable that is set
  (`RUNICBRIDGE086`). The rename release fills them, so a project that mixes packages of the old and the new names
  fails to build with an error that names the new ID and `rax migrate`
  ([RUNICBRIDGE085](../../../docs/diagnostics.md#runicbridge085)).
