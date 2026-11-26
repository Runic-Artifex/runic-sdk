# Old-name guards

The old-name guards (D079.11) fail a build that still uses a name the rename release replaced. They ship in
`buildTransitive/Runic.Sdk.RenamedNames.targets` of `Runic.Application.Views` and `Runic.Desktop` (each package
imports it from its own targets, once even when both are referenced) through 1.0.x, and are removed in 2.0.

| Kind | Reported when | Code |
| --- | --- | --- |
| `packages` | an old package ID is in the package graph: a `PackageReference`, or a package NuGet resolved (`PackageDependencies` after `ResolvePackageAssets`) | `RUNICBRIDGE085` |
| `msbuild` | the project, its imports or the command line sets an old public property or item | `RUNICBRIDGE017` |
| `environment` | an old environment variable is set | `RUNICBRIDGE086` |

Each name is reported once, and the message names the new name; a package error also names `rax migrate`. The codes
are in [docs/diagnostics.md](../../../../docs/diagnostics.md#runicbridge085). 0.24 lists only the `RUNICBRIDGE017`
names of 0.7.0-preview.6 and 0.8; the rename release fills the package and environment lists.

## Changing the list

Edit [`old-names.json`](old-names.json), then regenerate both copies and run the tests:

```sh
node packages/dotnet/Runic.Application.Views/rename/old-names.mjs          # or bun
node packages/dotnet/Runic.Application.Views/rename/old-names.mjs --check  # what the test checks
bun test tests/engineering/old-name-guards.test.mjs
```

The input, version 1:

```jsonc
{
  "version": 1,
  "family": {
    "token": "RunicSdk",                 // prefix of the private items and the guard targets (RunicSdkReportRenamedNames)
    "buildName": "Runic",                // "The Runic build property X is renamed to Y."
    "helpLinkBase": "$(_RunicDiagnosticsHelpLinkBase)",
    "diagnostics": { "packages": "RUNICBRIDGE085", "msbuild": "RUNICBRIDGE017", "environment": "RUNICBRIDGE086" },
    "skipCondition": "…",                // optional: more conditions under which the guards stay quiet
    "root": "../../../..",               // the repository root, relative to this file; outputs are relative to it
    "outputs": [ "…/Runic.Sdk.RenamedNames.targets" ]
  },
  // The entries take the shape of the rename plan's rax-map.json names: old, new and (msbuild) kind.
  "packages":    [ { "old": "Runic.Desktop", "new": "RunicArtifex.Desktop" } ],
  "msbuild":     [ { "old": "RunicDesktopServer", "new": "RaxDesktopServer", "kind": "property" },
                   { "old": "RunicApplicationId", "new": "ApplicationId", "kind": "property",
                     "whenNewUnset": true,       // stays quiet when the project also sets the new name
                     "note": "…" } ],            // optional: a comment above the item
  "environment": [ { "old": "RUNIC_APPLICATION_URL", "new": "RAX_APPLICATION_URL" } ]
}
```

Package IDs compare without case. A family may list the old IDs of another family's
packages that its own packages cannot work beside.

The same `old-names.mjs` is in runic-translations-sdk and runic-cli-sdk (`eng/rename/`), with each repository's family
settings in its input; keep the copies identical.
