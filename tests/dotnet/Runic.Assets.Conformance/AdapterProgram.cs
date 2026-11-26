using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Assets.Conformance;

// The conformance adapter of the .NET Assets reader (eng/conformance/PROTOCOL.md, "Assets"): one JSON request per line
// on stdin, one response per line on stdout; the rest of the process's output goes to stderr. The driver owns the
// vectors and the comparison. This class only calls the reader in the requested mode, opens every asset it reports and
// says what it served.
internal static class AdapterProgram
{
    private const string Protocol = "rax.conformance-adapter/1";
    private const string ResourceName = "Runic.Assets.Conformance.Archive";

    public static async Task<int> Main()
    {
        var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        // Nothing but responses may reach stdout.
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject request;
            try { request = JsonNode.Parse(line)!.AsObject(); }
            catch (JsonException error) { await Console.Error.WriteLineAsync($"unreadable request ({error.Message}): {line}").ConfigureAwait(false); continue; }
            var seq = request["seq"]?.DeepClone();
            var op = request["op"]?.GetValue<string>() ?? "";
            var args = request["args"] as JsonObject ?? [];
            JsonObject response;
            try { response = new JsonObject { ["seq"] = seq, ["ok"] = await HandleAsync(op, args).ConfigureAwait(false) }; }
            catch (AdapterFailure failure) { response = Error(seq, failure.Code, failure.Message); }
            catch (Exception error) { response = Error(seq, "failed", $"{error.GetType().Name}: {error.Message}"); }
            await protocolOut.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            if (op == "shutdown") break;
        }

        return 0;
    }

    private static JsonObject Error(JsonNode? seq, string code, string message) =>
        new() { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<JsonNode> HandleAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "describe":
                return new JsonObject
                {
                    ["protocol"] = Protocol,
                    ["implementation"] = new JsonObject
                    {
                        ["id"] = "dotnet-assets", ["name"] = "Runic.Assets",
                        ["version"] = typeof(AssetArchive).Assembly.GetName().Version?.ToString(),
                    },
                    ["corpora"] = new JsonArray("assets/2"),
                    ["roles"] = new JsonArray("reader"),
                    ["features"] = new JsonArray(),
                    // Both reading modes; no error codes, since the reader reports every broken rule as
                    // InvalidDataException (specs/assets/archive-v1.md, "Notes for .NET").
                    ["capabilities"] = new JsonArray("eager-reading", "lazy-reading"),
                };
            case "shutdown":
                return new JsonObject();
            case "assets.read":
                byte[] archive = Convert.FromBase64String(args["archive"]?.GetValue<string>() ?? throw new AdapterFailure("failed", "Missing argument archive."));
                return args["mode"]?.GetValue<string>() switch
                {
                    "eager" => await ReadAsync(archive, lazy: false).ConfigureAwait(false),
                    "lazy" => await ReadAsync(archive, lazy: true).ConfigureAwait(false),
                    var mode => throw new AdapterFailure("failed", $"Unknown mode '{mode}'."),
                };
            default:
                throw new AdapterFailure("unsupported", $"The .NET Assets adapter has no operation {op}.");
        }
    }

    // Eager: AssetArchive.Read. Lazy: AssetArchive.ReadEmbedded, from an assembly whose only manifest resource is the
    // archive. Then every reported asset is opened, in order, and read to its end.
    private static async Task<JsonObject> ReadAsync(byte[] archive, bool lazy)
    {
        AssetArchiveSource source;
        try
        {
            source = lazy
                ? AssetArchive.ReadEmbedded(new ResourceAssembly(archive), ResourceName)
                : AssetArchive.Read(new MemoryStream(archive, writable: false));
        }
        catch (Exception error)
        {
            return Invalid("read", error);
        }

        var assets = new JsonArray();
        foreach (AssetDescriptor asset in source.Manifest.Assets)
        {
            byte[] served;
            try
            {
                await using AssetReadSnapshot snapshot = await source.OpenAsync(asset.RelativePath).ConfigureAwait(false);
                using var copy = new MemoryStream();
                await snapshot.Content.CopyToAsync(copy).ConfigureAwait(false);
                served = copy.ToArray();
            }
            catch (Exception error)
            {
                return Invalid("open", error);
            }

            assets.Add(new JsonObject
            {
                ["path"] = asset.RelativePath,
                ["mediaType"] = asset.MediaType,
                ["length"] = asset.Length,
                ["sha256"] = asset.Sha256,
                ["entryPoint"] = asset.IsEntryPoint,
                ["cacheMode"] = asset.CacheMode.ToString(),
                ["served"] = new JsonObject { ["length"] = served.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(served)) },
            });
        }

        return new JsonObject { ["result"] = "valid", ["assets"] = assets };
    }

    // The reader's documented failure is InvalidDataException, without a code. Anything else is reported as it is, so
    // the case fails (PROTOCOL.md).
    private static JsonObject Invalid(string stage, Exception error)
    {
        var result = new JsonObject { ["result"] = "invalid", ["stage"] = stage, ["message"] = $"{error.GetType().Name}: {error.Message}" };
        if (error is not InvalidDataException) result["error"] = $"unmapped:{error.GetType().Name}";
        return result;
    }

    // An assembly whose one manifest resource is an archive, as AssetArchive.ReadEmbedded reads it.
    private sealed class ResourceAssembly(byte[] archive) : Assembly
    {
        public override Stream? GetManifestResourceStream(string name) =>
            StringComparer.Ordinal.Equals(name, ResourceName) ? new MemoryStream(archive, writable: false) : null;

        public override string[] GetManifestResourceNames() => [ResourceName];

        public override AssemblyName GetName() => new("Runic.Assets.Conformance.Vector");
    }

    private sealed class AdapterFailure(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
