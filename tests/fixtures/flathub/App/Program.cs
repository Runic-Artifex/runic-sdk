using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

// Opened as runic-flathub-probe://receipt?path=<file>, writes what the installed Flatpak sees to the file.
using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
string url = args.Length > 0 ? args[0] : "";
int query = url.IndexOf("?path=", StringComparison.Ordinal);
if (query < 0)
{
    Console.Error.WriteLine("Usage: FlathubProbe runic-flathub-probe://receipt?path=<file>");
    return 2;
}
string contentRoot = Path.Combine(AppContext.BaseDirectory, "www");
using var stream = File.Create(Uri.UnescapeDataString(url[(query + "?path=".Length)..]));
using var json = new Utf8JsonWriter(stream);
json.WriteStartObject();
json.WriteString("applicationId", Environment.GetEnvironmentVariable("FLATPAK_ID"));
json.WriteString("url", url);
json.WriteString("contentRoot", contentRoot);
json.WriteBoolean("contentRootHasIndex", File.Exists(Path.Combine(contentRoot, "index.html")));
json.WriteBoolean("frontendDependency", File.Exists(Path.Combine(contentRoot, "is-number.json")));
json.WriteEndObject();
return 0;
