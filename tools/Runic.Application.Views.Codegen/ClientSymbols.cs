using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The assemblies without a PDB that each generated client depends on, for the emitter's RUNICBRIDGE076 (#1298):
/// <c>client-symbols.json</c> beside the contract IR. The generator reads member locations and the comments of partial
/// properties through the PDB, so a client generated without one loses its declaration map entries or comments, and a
/// committed client changes between configurations. The file holds <c>{ "owners": { id: [assembly…] }, "any": [assembly…] }</c>
/// with the ids of the IR's Views and shared clients; <c>any</c> applies to the other modules, such as the named types.
/// Written only when an assembly has no PDB.
/// </summary>
internal static class ClientSymbols
{
    internal const string FileName = "client-symbols.json";

    internal static void Write(string directory, IEnumerable<Type> models, IEnumerable<Type> sharedInterfaces)
    {
        var path = Path.Combine(directory, FileName);
        var any = SourceLocator.AssembliesWithoutPdb(null);
        if (any.Count == 0)
        {
            Remove(directory);
            return;
        }
        var owners = new JsonObject();
        foreach (var type in models.Concat(sharedInterfaces).OrderBy(type => type.FullName, StringComparer.Ordinal))
            if (SourceLocator.AssembliesWithoutPdb(type) is { Count: > 0 } assemblies)
                owners[type.FullName!] = new JsonArray([.. assemblies.Select(name => (JsonNode)name)]);
        var document = new JsonObject { ["owners"] = owners, ["any"] = new JsonArray([.. any.Select(name => (JsonNode)name)]) };
        GeneratedFile.WriteIfChanged(path, document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    internal static void Remove(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
