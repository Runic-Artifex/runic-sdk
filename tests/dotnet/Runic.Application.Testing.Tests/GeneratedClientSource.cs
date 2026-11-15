namespace Runic.Application.Testing.Tests;

/// <summary>
/// A generated client's source: its types in <c>&lt;view&gt;.contract.d.ts</c> (#952) followed by its code in
/// <c>&lt;view&gt;.ts</c>, for checks of the client's surface that do not depend on which of the two files declares it.
/// </summary>
internal static class GeneratedClientSource
{
    internal static string Read(string directory, string module)
    {
        var contract = Path.Combine(directory, module + ".contract.d.ts");
        return (File.Exists(contract) ? File.ReadAllText(contract) + "\n" : "") + File.ReadAllText(Path.Combine(directory, module + ".ts"));
    }
}
