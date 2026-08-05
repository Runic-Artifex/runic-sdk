# RunicToolkit.Hosting.Generators

The package ships both the registration attribute/diagnostic vocabulary and a Roslyn
incremental analyzer. Add assembly-level `RunicToolkitHostingRegistration` attributes
for closed service and implementation types. The generator emits ordinally sorted
closed factories plus serializer type metadata without reflection or runtime discovery.

Diagnostics `RTKHOST0001`–`RTKHOST0007` cover UI adapter/root/entry-point cardinality,
duplicate command or launch tokens, inaccessible factories, AOT reflection fallback,
and async lifecycle callbacks that cannot observe cancellation.

`HostingGeneratorContract.Version` identifies the descriptor semantics.
`HostingRegistrationDescriptor` represents validated registrations with canonical
metadata names and keys. `HostingRegistrationDescriptorComparer` supplies total,
ordinal ordering across kind, key, service type, implementation type, and factory
method. Descriptors deliberately exclude absolute paths and discovery order so the
same semantic input has the same order on every machine.

`HostingGeneratorDiagnostics` reserves `RTKHOST0001`–`RTKHOST0007` with stable
severity, message format, and remediation metadata. The package carries the generated
attribute vocabulary as a normal compile asset and the same assembly under
`analyzers/dotnet/cs`; no runtime scanning is used. It targets the repository's exact
.NET 10 SDK/compiler contract.
