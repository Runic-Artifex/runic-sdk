using Runic.Application;

[assembly: RunicApplicationManifest("hostile", Version = "1.0.0", Provenance = "package")]
[assembly: RunicApplicationBridgeComposition(typeof(Handler), typeof(string))]

internal sealed class Handler;

internal static class Program
{
    private static void Main() { }
}
