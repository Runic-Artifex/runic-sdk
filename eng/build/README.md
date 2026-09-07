# Managed build policies

The root `Directory.Build.props` supplies workspace identity and `RunicSdkRoot`.
Managed projects explicitly import their policy from this directory, so moving a
project does not silently change its language, analyzer or package settings.

- `application`: application runtime, bridge generators, hosting and CLI.
- `assets`: archive/runtime adapters and asset packer.
- `command-line`: command catalogs, generators and process APIs.
- `desktop`: native window/transport runtime and platform checks.
- `translations`: translation runtime, compiler, generators and tooling.

Props are imported before a project's property groups. Matching targets are imported
after its items. Root `Directory.Build.targets` applies Desktop's host profile
switch to source consumers. Desktop takes only WebView2's native loader assets;
its COM callbacks are generated at build time for NativeAOT. NuGet dependency
versions remain in the root
`Directory.Packages.props`; the translation compiler's existing Roslyn pin is scoped
there to translation projects, rather than becoming a workspace-wide downgrade.

These profiles preserve established product policies while the directory structure
is consolidated. They are not independent restore/build workspaces. Use root
commands and the artifact/component inventory in `eng/workspace.json`.
