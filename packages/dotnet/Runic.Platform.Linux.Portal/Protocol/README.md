# Portal protocol generation

The Registry, Notification, Inhibit, Request, Session, Settings, OpenURI,
FileChooser, Background, Trash, NetworkMonitor and GlobalShortcuts XML files are
upstream inputs, pinned to the commit and SHA-256 hashes in `upstream.json`. Every file is unmodified except
`org.freedesktop.portal.Settings.xml`, whose deprecated `Read` method is removed
(the provider uses `ReadAll`, and the generator warns about the `Deprecated`
annotation as RUNICDBUS013); its manifest entry keeps upstream's hash as
`upstreamSha256` and says what changed. Their original license headers are
preserved; `COPYING` is upstream's license text. They are not relicensed under the
SDK's license.

Runic.Platform.Linux.DBus's source generator consumes these `AdditionalFiles` at
compile time and produces internal proxies on Runic.Platform.Linux.DBus. The
generator is a private build dependency, not a consumer/runtime dependency. Normal
builds do not fetch XML or introspect the user's session. Generated C# stays under
`obj`.

The provider uses the generated proxies' interface names, Registry.Register,
Notification.RemoveNotification, Trash.TrashFile, Request.Close, Session.Close and
version reads.
Calls that send an `a{sv}` of options (FileChooser, OpenURI, Inhibit, Background,
Notification.AddNotification, GlobalShortcuts) and replies with dictionaries a peer controls
(Settings.ReadAll, Request.Response results, the GlobalShortcuts signals) are written and read by hand, so the
options keep their order and a repeated key keeps its last value instead of failing
the read. Desktop identity, bus-name ownership, early-action tracking, the signal
subscriptions made before a request (Request.Response across the sender's
request-path namespace and the namespace-filtered Settings.SettingChanged),
returned-handle validation, file descriptors, portal version guards, deadlines,
cancellation, permission mapping and application callbacks remain handwritten. Generated members
for newer portal versions do not mean the public Runic API uses those features.

Run `direnv exec . node eng/portals/check-protocol.mjs` from the SDK root for an
offline integrity check. To update, select a reviewed upstream commit, download
its pinned `data/` files byte-for-byte, update the manifest's commit/tag/hashes and
review the XML diff and generated signatures. Preserve licensing. Do not replace
these files with runtime introspection output from one installed desktop.

Verification:

```sh
direnv exec . dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -- --treenode-filter "/*/*/*/*[Category=DBus]"
direnv exec . dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true -o artifacts/portal-generation-pilot
direnv exec . dbus-run-session -- artifacts/portal-generation-pilot/Runic.Platform.Linux.Portal.Tests
direnv exec . dbus-run-session -- artifacts/portal-generation-pilot/Runic.Platform.Linux.Portal.Tests --treenode-filter "/*/*/*/*[Category=DBus]"
```

For generated-source inspection, build the provider with
`-p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=obj/portal-generated`.

References: [Runic.Platform.Linux.DBus](../../Runic.Platform.Linux.DBus/README.md),
[upstream portal definitions](https://github.com/flatpak/xdg-desktop-portal/tree/1.22.1/data).
