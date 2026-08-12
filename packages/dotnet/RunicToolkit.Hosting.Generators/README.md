# RunicToolkit.Hosting.Generators

Generate closed hosting registrations and serializer metadata without runtime discovery or reflection.

```bash
dotnet add package RunicToolkit.Hosting.Generators --prerelease
```

Requires the .NET 10 SDK/compiler. Use it in NativeAOT-sensitive applications that need compile-time registrations; most template-based applications do not need to add it directly.

```csharp
using RunicToolkit.Hosting.Generators;

[assembly: RunicToolkitHostingRegistration(
    HostingRegistrationKind.Session,
    typeof(IMyService),
    typeof(MyService))]
```

All registration types must be closed and accessible to generated code. `RTKHOST0001`–`RTKHOST0007` explain unsupported registration shapes and reflection fallbacks. See the [generator source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Hosting.Generators), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
