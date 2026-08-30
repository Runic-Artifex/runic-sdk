using System;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("aot.application", Version = "1.0.0", Provenance = "aot")]

DeterministicApplicationTestHost host = new();
await using ApplicationHost application = RunicApplication.CreateBuilder([]).UseHost(host).Build();
await application.RunAsync();
Console.WriteLine(application.Manifest.ToJson());
return host.Lifecycle.Length == 3 ? 0 : 1;
