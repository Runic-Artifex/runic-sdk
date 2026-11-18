- **A RID-specific publish of a Runic Desktop application ships only its own
  operating system's platform code.** `dotnet publish -r <rid>` for Windows, macOS or
  Linux, with or without ReadyToRun, leaves out the other operating systems'
  `Runic.Platform` providers:
  `Runic.Platform.Windows` outside Windows, `Runic.Platform.MacOS` outside macOS, and
  `Runic.Platform.Linux`, `.Linux.Portal`, `.Linux.DBus`, `.Linux.Gtk4`, `.Linux.Polkit`
  with the Linux-only `Tmds.DBus.Protocol` and GirCore GTK 4 bindings outside Linux.
  The publish's `deps.json` leaves them out too. The Desktop template's publish loses
  2.4 MB on linux-x64 (13,089,260 to 10,674,346 bytes), 1.9 MB on win-x64 and 3.4 MB
  on osx-arm64, and 4.1 MB with ReadyToRun on linux-x64 (26,229,892 to 22,107,458
  bytes); a GTK 4 application also leaves its 6.8 MB of GirCore bindings out on
  Windows and macOS. `dotnet build`, `dotnet run`, a publish without `-r` and trimmed or
  NativeAOT publishes keep every assembly as before. CS-WebUI applications carry
  no platform providers and are unchanged.

  Runic calls each provider only on its own operating system. Code of your own that
  calls another operating system's `Runic.Platform` API from a method that also runs
  on this one, even behind `OperatingSystem.IsWindows()` or a similar check, now
  throws `FileNotFoundException` in a RID-specific publish instead of reaching the
  check: .NET loads every assembly a method uses when it compiles the method. Move
  such a call into a method of its own that only that operating system runs (mark it
  `[MethodImpl(MethodImplOptions.NoInlining)]`), or set
  `<RunicPublishAllPlatformAssemblies>true</RunicPublishAllPlatformAssemblies>` to
  publish every operating system's assemblies as before.
