# WebUIToolkit.Hosting.Generators

Wave B freezes the dependency-neutral contract for later hosting registration
generation. The project targets `$(WebUIToolkitGeneratorTargetFramework)`
(`netstandard2.0`), uses only the BCL, and is intentionally not packable until a real
incremental generator and its consumer tests exist.

`HostingGeneratorContract.Version` identifies the descriptor semantics.
`HostingRegistrationDescriptor` represents validated registrations with canonical
metadata names and keys. `HostingRegistrationDescriptorComparer` supplies total,
ordinal ordering across kind, key, service type, implementation type, and factory
method. Descriptors deliberately exclude absolute paths and discovery order so the
same semantic input has the same order on every machine.

`HostingGeneratorDiagnostics` reserves `WUTHOST0001`–`WUTHOST0007` with stable
severity, message format, and remediation metadata. These descriptors do not expose
Roslyn types; a later analyzer package can translate them to its native diagnostics.

## Deferred implementation

- No Roslyn package, incremental generator, analyzer, or generated source is present.
- No reflection or assembly scanning discovers registrations.
- No MSBuild integration or file-writing task is present. Frontend manifest creation
  remains owned by `WebUIToolkit.Hosting.Build`.
- Generator packaging under `analyzers/dotnet/cs`, snapshot tests, incremental-update
  tests, consumer generation tests, and diagnostic locations remain a later tranche.
- Concrete MVVM, WebUi, command-line, and external `cs-webui` adapter registrations
  remain Wave C concerns and are represented only by dependency-neutral metadata.
