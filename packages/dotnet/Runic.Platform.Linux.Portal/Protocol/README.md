# Portal protocol generation pilot

Registry and Notification XML are unmodified upstream inputs, pinned to the
commit and SHA-256 hashes in `upstream.json`. Their original license headers are
preserved; `COPYING` is upstream's license text. They are not relicensed under the
SDK's license.

`Tmds.DBus.Generator` 0.95.1 consumes these `AdditionalFiles` at compile time and
produces internal proxies using `Tmds.DBus.Protocol` 0.95.1. The generator is a
private build dependency, not a consumer/runtime dependency. Normal builds do not
fetch XML or introspect the user's session. Generated C# stays under `obj`.

The provider uses generated Registry.Register, Notification.Add/Remove, version
reads and ActionInvoked decoding. Extensible notification dictionaries, desktop
identity, bus-name ownership, early-action tracking, deadlines, cancellation,
permission mapping and application callbacks remain handwritten. Generated v2
members do not mean the public Runic API uses v2 features.

Run `direnv exec . node eng/portals/check-protocol.mjs` from the SDK root for an
offline integrity check. To update, select a reviewed upstream commit, download
its two `data/` files byte-for-byte, update the manifest's commit/tag/hashes and
review the XML diff and generated signatures. Preserve licensing. Do not replace
these files with runtime introspection output from one installed desktop.

Verification:

```sh
direnv exec . dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -- --dbus
direnv exec . dotnet publish tests/dotnet/Runic.Platform.Linux.Portal.Tests -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true -o artifacts/portal-generation-pilot
direnv exec . dbus-run-session -- artifacts/portal-generation-pilot/Runic.Platform.Linux.Portal.Tests --dbus
```

For generated-source inspection, build the provider with
`-p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=obj/portal-generated`.

References: [Tmds documentation](https://tmds.github.io/Tmds.DBus/),
[upstream portal definitions](https://github.com/flatpak/xdg-desktop-portal/tree/1.22.1/data).
