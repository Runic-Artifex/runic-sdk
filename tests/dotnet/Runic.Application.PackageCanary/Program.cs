using System;
using System.Collections.Generic;
using Runic.Application;
using Runic.Application.Hosting;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("package.canary", Version = "1.0.0", Provenance = "package")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1", "feed")]

var manifest = new ApplicationCompositionManifest(
    "package.canary",
    "1.0.0",
    "package",
    artifacts: [new ApplicationManifestArtifact("assets", "runic.assets/1", "feed")]);
var host = new DeterministicApplicationTestHost();
var endpoint = new ApplicationBridgeWebSocketOptions
{
    AllowedOrigins = new HashSet<string>(StringComparer.Ordinal) { "https://canary.runic.example" },
};
Console.WriteLine($"{manifest.Schema}|{host.Ids.Next("canary")}|{endpoint.IsOriginAllowed("https://canary.runic.example")}");
