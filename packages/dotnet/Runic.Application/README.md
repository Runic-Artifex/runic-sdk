# Runic.Application

`Runic.Application` owns the generated `runic.application/1` composition
manifest and the minimal host that consumes it. Declare application facts at the
assembly boundary, then keep the entry point small:

```csharp
await RunicApplication.CreateBuilder(args).Build().RunAsync();
```

The manifest is the application composition authority. Hosts, testing, tooling,
and publishers consume it; they do not rebuild it from parallel configuration.
`ApplicationHost.Capabilities` projects only the manifest-declared capability
names. A host must report each status explicitly; hosts without a capability
projection, and unconfigured headless capabilities, are unavailable with a
stable reason.

The former `RunicToolkit.Hosting`, `RunicToolkit.Desktop`,
`RunicToolkit.Hosting.Abstractions`, and `RunicToolkit.Hosting.Generators`
packages are preview identities. Move to `Runic.Application`; builds that still
reference a preview identity receive `RAPP0001` with this migration destination.
