# Runic.Application.Desktop

Application-owned composition for Runic Desktop. The package binds the existing
Application Bridge session to Desktop's presentation capability and maps the
application host lifetime onto a typed Desktop host, surface, and optional
window.

```csharp
var application = RunicApplication.CreateBuilder(args)
    .UseDesktop(new DesktopApplicationHostOptions
    {
        Title = "My application",
        Window = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
    })
    .Build();

await application.RunAsync();
```

Runic Desktop remains independently usable and has no dependency on this
package. Application Bridge retains command, event, revision, operation, and
schema ownership.

After start, `DesktopApplicationHost.Host` may create additional surfaces and
windows. They remain children of the same Desktop host and are therefore
closed by Application-owned stop and disposal.
