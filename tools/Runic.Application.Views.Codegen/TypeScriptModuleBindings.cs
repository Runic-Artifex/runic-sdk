using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// Keeps the generator's own top-level bindings in a generated TypeScript
/// module (runtime imports, the wire type, decoders, reference caches) apart
/// from the names the module derives from user types (#258).
/// </summary>
/// <remarks>
/// Emitters write each own binding through <see cref="Ref"/>, <see cref="Import"/>
/// or <see cref="Shorthand"/>. <see cref="Resolve"/> keeps the name unless a
/// derived binding of the same module (a ViewModel's <c>…State</c>,
/// <c>…Client</c> or <c>connect…</c>, a page function a module imports) uses it;
/// then the own binding becomes <c>_name</c>, and an import <c>name as _name</c>.
/// Derived names start with a ViewModel or View name, which must start with a
/// letter, or with <c>connect</c>, <c>page</c> or <c>mock</c>, so they never
/// start with an underscore. Output without a collision is unchanged.
/// </remarks>
internal static partial class TypeScriptModuleBindings
{
    private const char ReferenceMarker = '\u0003';
    private const char ImportMarker = '\u0004';
    private const char ShorthandMarker = '\u0005';

    /// <summary>A declaration of, or reference to, an own binding.</summary>
    internal static string Ref(string name) => $"{ReferenceMarker}{name}{ReferenceMarker}";

    /// <summary>An import specifier that binds <paramref name="name"/>.</summary>
    internal static string Import(string name) => $"{ImportMarker}{name}{ImportMarker}";

    /// <summary>A shorthand object property <c>{ name }</c> whose value is an own binding.</summary>
    internal static string Shorthand(string name) => $"{ShorthandMarker}{name}{ShorthandMarker}";

    /// <summary>
    /// Assigns the own bindings of <paramref name="module"/> and reports
    /// RUNICBRIDGE004 if a top-level name is still bound twice.
    /// </summary>
    internal static string Resolve(string module, string path, MemberInfo? owner)
    {
        var taken = TopLevelBindings(Marked().Replace(module, "")).ToHashSet(StringComparer.Ordinal);
        var resolved = Marked().Replace(module, match =>
        {
            var name = match.Groups["name"].Value;
            var local = name;
            while (taken.Contains(local)) local = "_" + local;
            return match.Groups["marker"].Value[0] switch
            {
                ImportMarker when local != name => $"{name} as {local}",
                ShorthandMarker when local != name => $"{name}: {local}",
                _ => local,
            };
        });
        if (TopLevelBindings(resolved).GroupBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"the generated TypeScript module {Path.GetFileName(path)} declares '{duplicate.Key}' more than once. Rename the ViewModel, View, command or type it is derived from.",
                owner);
        return resolved;
    }

    /// <summary>The names a module's top-level declarations and imports bind.</summary>
    internal static IEnumerable<string> TopLevelBindings(string module)
    {
        foreach (Match match in Declaration().Matches(module)) yield return match.Groups[1].Value;
        foreach (Match match in NamespaceImport().Matches(module)) yield return match.Groups[1].Value;
        foreach (Match match in NamedImport().Matches(module))
            foreach (var part in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.StartsWith("type ", StringComparison.Ordinal) ? part[5..].Trim() : part;
                var alias = name.IndexOf(" as ", StringComparison.Ordinal);
                yield return alias >= 0 ? name[(alias + 4)..].Trim() : name;
            }
    }

    [GeneratedRegex("(?<marker>[\u0003\u0004\u0005])(?<name>[A-Za-z_$][\\w$]*)\\k<marker>")]
    private static partial Regex Marked();

    // Generated modules start every top-level statement in column 0 and indent
    // everything nested.
    [GeneratedRegex(@"^(?:export\s+)?(?:async\s+)?(?:interface|type|function|const|let|var|class|enum)\s+([A-Za-z_$][\w$]*)", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^import\s+(?:type\s+)?\*\s+as\s+([A-Za-z_$][\w$]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceImport();

    [GeneratedRegex(@"^import\s+(?:type\s+)?\{([^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex NamedImport();
}
