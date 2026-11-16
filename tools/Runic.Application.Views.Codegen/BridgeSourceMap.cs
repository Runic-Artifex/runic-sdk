using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The development source map (#951): every Bridge member of every generated ViewModel with its C# symbol and source
/// location. The generator writes it as <c>sources.json</c> beside its generated C# in <c>obj</c>, where
/// <c>@runic-artifex/vite-plugin-runic</c> serves it during <c>vite dev</c>. It never enters the generated TypeScript,
/// the build output or a package, so production output does not depend on it.
/// </summary>
/// <remarks>
/// <para>
/// A location is <c>{ "file", "line", "column" }</c>: the absolute local path of an existing source file and a 1-based
/// line and column, as in a compiler diagnostic. The same table is the source of the generated clients' declaration
/// maps (#952), which convert it to their relative, 0-based form.
/// </para>
/// <para>
/// Locations come from the model assembly's portable PDB (<see cref="SourceLocator"/>), then from a light scan of the
/// type's source files for declarations without sequence points (an auto-property, a CommunityToolkit
/// <c>[ObservableProperty]</c> field). A method or accessor is located at its declaration above its first sequence point
/// (<see cref="Declared"/>), so every build configuration writes the same map. Without a PDB or source files an entry
/// has no location; the map is still written.
/// </para>
/// </remarks>
internal static class BridgeSourceMap
{
    internal const string FileName = "sources.json";
    internal const string Format = "runic.views.bridge-sources/1";

    internal sealed record Location(string File, int Line, int Column);

    internal sealed record ViewModelEntry(string Id, string Name, string Route, string Contract,
        IReadOnlyList<string> PageKinds, Location? Location);

    /// <summary>One Bridge member. <paramref name="Id"/> is <c>{viewModel}/{kind}/{member}</c>.</summary>
    internal sealed record MemberEntry(string Id, string ViewModel, string Member, string Kind, string Symbol,
        string? Property, Location? Location);

    private static readonly List<ViewModelEntry> ViewModels = [];
    private static readonly Dictionary<string, MemberEntry> Members = new(StringComparer.Ordinal);

    // The member kinds, in the order the map lists them.
    internal const string State = "state";
    internal const string Setter = "setter";
    internal const string Command = "command";
    internal const string CommandState = "commandState";
    internal const string Interaction = "interaction";
    internal const string Request = "request";
    private static readonly string[] KindOrder = [State, Setter, Command, CommandState, Interaction, Request];

    /// <summary>The location of a member id (<c>{viewModel}/{kind}/{member}</c>) or ViewModel id, for a client's declaration map (#952).</summary>
    internal static Location? LocationOf(string id) => Members.TryGetValue(id, out var member) ? member.Location
        : ViewModels.FirstOrDefault(entry => entry.Id == id)?.Location;

    internal static void RecordViewModel(Type model, string name, string route, string contract, IEnumerable<string> pageKinds) =>
        ViewModels.Add(new(model.FullName!, name, route, contract,
            pageKinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), At(SourceLocator.Own(model))));

    /// <summary>A state member, read through the property's getter.</summary>
    internal static void RecordState(Type model, string member, PropertyInfo property) =>
        Add(model, State, member, Symbol(property), null,
            Declared(property.GetMethod) ?? PropertyDeclaration(property) ?? At(SourceLocator.Own(property)));

    /// <summary>A state member the page sets (<c>set…</c>/<c>write…</c>), through the property's setter.</summary>
    internal static void RecordSetter(Type model, string member, PropertyInfo property) =>
        Add(model, Setter, member, Symbol(property) + ".set", null,
            Declared(property.SetMethod) ?? PropertyDeclaration(property) ?? At(SourceLocator.Own(property)));

    /// <summary>
    /// A command. Its location is the handler: the method its MVVM library generated the command from (a
    /// CommunityToolkit <c>[RelayCommand]</c> method), else a method group the property's initializer or assignment
    /// passes (<c>ReactiveCommand.CreateFromTask(SaveAsync)</c>, <c>new RelayCommand(Save)</c>), else that assignment,
    /// else the property.
    /// </summary>
    internal static void RecordCommand(Type model, string member, PropertyInfo command, MethodInfo? source)
    {
        var (handler, location) = CommandHandler(command, source);
        Add(model, Command, member, handler is null ? Symbol(command) : Symbol(handler), handler is null ? null : Symbol(command), location);
    }

    /// <summary>A state member a command adds (its availability or execution state), located at the command.</summary>
    internal static void RecordCommandState(Type model, string member, PropertyInfo command)
    {
        var commandId = Id(model, Command, LowerFirst(command.Name[..^"Command".Length]));
        if (Members.TryGetValue(commandId, out var entry))
            Add(model, CommandState, member, entry.Symbol, entry.Property ?? Symbol(command), entry.Location);
    }

    internal static void RecordInteraction(Type model, string member, PropertyInfo property, bool request) =>
        Add(model, request ? Request : Interaction, member, Symbol(property), null,
            Declared(property.GetMethod) ?? PropertyDeclaration(property) ?? At(SourceLocator.Own(property)));

    /// <summary>
    /// Writes the map to <paramref name="directory"/>, only when its content changed, so a dev server that watches
    /// it sees a change only when there is one.
    /// </summary>
    internal static void Write(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var content = Serialize();
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        // The map is a development convenience: an unwritable map must not fail the build.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Runic Bridge: the development source map {path} could not be written: {error.Message}");
        }
    }

    /// <summary>Removes the map of an earlier generation, for a project that no longer generates Bridges.</summary>
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

    internal static string Serialize()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = true,
                   // Paths and symbols stay readable: the map is never embedded in HTML.
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteStartArray("viewModels");
            foreach (var entry in ViewModels.OrderBy(entry => entry.Id, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("id", entry.Id);
                json.WriteString("name", entry.Name);
                json.WriteString("route", entry.Route);
                json.WriteString("contract", entry.Contract);
                json.WriteStartArray("pageKinds");
                foreach (var kind in entry.PageKinds) json.WriteStringValue(kind);
                json.WriteEndArray();
                json.WriteString("symbol", entry.Id);
                WriteLocation(json, entry.Location);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("members");
            foreach (var entry in Members.Values.OrderBy(entry => entry.ViewModel, StringComparer.Ordinal)
                         .ThenBy(entry => Array.IndexOf(KindOrder, entry.Kind)).ThenBy(entry => entry.Member, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("id", entry.Id);
                json.WriteString("viewModel", entry.ViewModel);
                json.WriteString("member", entry.Member);
                json.WriteString("kind", entry.Kind);
                json.WriteString("symbol", entry.Symbol);
                if (entry.Property is not null) json.WriteString("property", entry.Property);
                WriteLocation(json, entry.Location);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    private static void WriteLocation(Utf8JsonWriter json, Location? location)
    {
        if (location is null) return;
        json.WriteStartObject("location");
        json.WriteString("file", location.File);
        json.WriteNumber("line", location.Line);
        json.WriteNumber("column", location.Column);
        json.WriteEndObject();
    }

    private static string Id(Type model, string kind, string member) => $"{model.FullName}/{kind}/{member}";

    private static void Add(Type model, string kind, string member, string symbol, string? property, Location? location) =>
        Members[Id(model, kind, member)] = new(Id(model, kind, member), model.FullName!, member, kind, symbol, property, location);

    private static Location? At(SourceLocator.Location? location) =>
        location is { } found && found.Line > 0 && found.Column > 0 && File.Exists(found.Path)
            ? new(Path.GetFullPath(found.Path), found.Line, found.Column) : null;

    /// <summary>
    /// The declaration of <paramref name="method"/>: its name, or an accessor's <c>get</c>/<c>set</c>/<c>init</c>
    /// keyword (the property's name for an expression-bodied property). The PDB locates a method at its first
    /// sequence point, which is the body's <c>{</c> in a Debug build but its first statement in an optimized one, so
    /// the map would depend on the configuration that built it; the declaration above that point does not.
    /// </summary>
    private static Location? Declared(MethodInfo? method)
    {
        if (method is null || At(SourceLocator.Own(method)) is not { } point) return null;
        if (SourceDocumentation.Lines(point.File) is not { } lines || point.Line > lines.Length) return point;
        var accessor = method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal)
            || method.Name.StartsWith("set_", StringComparison.Ordinal));
        var name = new Regex(@"(?<![\w@.])@?(?<name>" + Regex.Escape(accessor ? method.Name[4..] : method.Name)
            + (accessor ? @")\b" : @")\s*(?:<[^<>()]*>\s*)?\("), RegexOptions.CultureInvariant);
        // A declaration spans a few lines at most: attributes, parameters, the body's "{".
        for (var line = point.Line - 1; line >= 0 && line >= point.Line - 1 - DeclarationLines; line--)
        {
            // On the point's own line, only text before it: "void Save() => Run();" or "get => _count;".
            var limit = line == point.Line - 1 ? point.Column - 1 : lines[line].Length;
            if (accessor && Last(AccessorKeyword.Matches(lines[line]), limit) is { } keyword)
                return new(point.File, line + 1, keyword.Index + 1);
            if (Last(name.Matches(lines[line]), limit) is { } declaration)
                return new(point.File, line + 1, declaration.Index + 1);
        }
        return point;

        static Group? Last(MatchCollection matches, int limit) =>
            matches.Select(match => match.Groups["name"]).LastOrDefault(group => group.Index < limit);
    }

    private const int DeclarationLines = 30;

    // An accessor: "get;", "get =>", "get {", "private set" or "init" at the end of a line.
    private static readonly Regex AccessorKeyword = new(@"(?<![\w@.])(?<name>get|set|init)\b(?=\s*(?:=>|\{|;|$))",
        RegexOptions.CultureInvariant);

    private static string LowerFirst(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    // Ns.Type.Member for a property, Ns.Type.Method(Type, Type) for a method.
    private static string Symbol(MemberInfo member) => member switch
    {
        MethodInfo method => $"{TypeName(method.DeclaringType!)}.{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => ShortTypeName(parameter.ParameterType)))})",
        _ => $"{TypeName(member.DeclaringType!)}.{member.Name}",
    };

    private static string TypeName(Type type) => (type.FullName ?? type.Name).Split('`')[0].Replace('+', '.');

    private static string ShortTypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return ShortTypeName(underlying) + "?";
        if (type.IsArray) return ShortTypeName(type.GetElementType()!) + "[]";
        var name = type.Name.Split('`')[0];
        return type.IsGenericType ? $"{name}<{string.Join(", ", type.GenericTypeArguments.Select(ShortTypeName))}>" : name;
    }

    private const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;

    private static (MethodInfo? Handler, Location? Location) CommandHandler(PropertyInfo command, MethodInfo? source)
    {
        if (source is not null && Declared(source) is { } sourceLocation) return (source, sourceLocation);
        if (Assignment(command) is { } assignment)
        {
            // A method group of the declaring type.
            if (assignment.MethodGroup is { } name && command.DeclaringType is { } owner)
                foreach (var method in owner.GetMethods(AllDeclared).Where(method => method.Name == name).OrderBy(method => method.MetadataToken))
                    if (Declared(method) is { } handlerLocation) return (method, handlerLocation);
            return (source, assignment.Location);
        }
        return (source, Declared(command.GetMethod) ?? PropertyDeclaration(command) ?? At(SourceLocator.Own(command)));
    }

    // Where the command property gets its value on one line: "SaveCommand { get; } = new RelayCommand(Save);" or
    // "SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave);". The method group is the first argument
    // when it is a plain identifier.
    private static (Location Location, string? MethodGroup)? Assignment(PropertyInfo property)
    {
        if (property.DeclaringType is not { } type) return null;
        var name = new Regex(@"(?<![\w@.])@?" + Regex.Escape(property.Name) + @"\b", RegexOptions.CultureInvariant);
        var matches = new List<(string Path, string[] Lines, int Line, int Column, string? MethodGroup)>();
        foreach (var path in SourceLocator.Documents(type))
        {
            if (SourceDocumentation.Lines(path) is not { } lines) continue;
            for (var line = 0; line < lines.Length; line++)
            {
                foreach (Match match in name.Matches(lines[line]))
                {
                    var rest = lines[line][(match.Index + match.Length)..];
                    // Past an accessor list, "{ get; }", to the initializer.
                    if (rest.TrimStart().StartsWith('{')) rest = rest[(rest.IndexOf('}') is var close and >= 0 ? close + 1 : rest.Length)..];
                    var assign = rest.TrimStart();
                    if (!assign.StartsWith('=') || assign.StartsWith("==", StringComparison.Ordinal) || assign.StartsWith("=>", StringComparison.Ordinal)) continue;
                    var argument = MethodGroupPattern.Match(assign);
                    matches.Add((path, lines, line, match.Index + 1, argument.Success ? argument.Groups["method"].Value : null));
                }
            }
        }
        if (matches.Count > 1)
            matches = matches.Where(match => SourceDocumentation.EnclosingTypeName(match.Lines, match.Line) == SourceDocumentation.SimpleName(type)).ToList();
        return matches.Count == 1 ? (new Location(Path.GetFullPath(matches[0].Path), matches[0].Line + 1, matches[0].Column), matches[0].MethodGroup) : null;
    }

    /// <summary>
    /// The declaration of a property without sequence points of its own on disk: a partial property whose
    /// implementation a source generator writes, an auto-property, or the CommunityToolkit <c>[ObservableProperty]</c>
    /// field it is generated from. Null unless exactly one declaration is found.
    /// </summary>
    internal static Location? PropertyDeclaration(PropertyInfo property)
    {
        if (property.DeclaringType is not { } type) return null;
        var lower = LowerFirst(property.Name);
        var declaration = new Regex(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|protected|private|static|virtual|override|sealed|abstract|new|required|partial|readonly)\s+)+[^;={}()]*?(?<![\w@.])@?(?<name>"
            + Regex.Escape(property.Name) + @")\s*(?:\{|=>)", RegexOptions.CultureInvariant);
        // [ObservableProperty] private int _count; (or count, m_count) generates Count.
        var field = new Regex(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:private|protected|internal|public|readonly)\s+)*[\w.<>?,\[\]\s]+?\s(?<name>(?:_|m_)?"
            + Regex.Escape(lower) + @")\s*[;=]", RegexOptions.CultureInvariant);
        var matches = new List<(string Path, string[] Lines, int Line, int Column)>();
        foreach (var path in SourceLocator.Documents(type))
        {
            if (SourceDocumentation.Lines(path) is not { } lines) continue;
            for (var line = 0; line < lines.Length; line++)
            {
                if (declaration.Match(lines[line]) is { Success: true } own)
                    matches.Add((path, lines, line, own.Groups["name"].Index + 1));
                else if (field.Match(lines[line]) is { Success: true } backing && ObservablePropertyAbove(lines, line))
                    matches.Add((path, lines, line, backing.Groups["name"].Index + 1));
            }
        }
        if (matches.Count > 1)
            matches = matches.Where(match => SourceDocumentation.EnclosingTypeName(match.Lines, match.Line) == SourceDocumentation.SimpleName(type)).ToList();
        return matches.Count == 1 ? new Location(Path.GetFullPath(matches[0].Path), matches[0].Line + 1, matches[0].Column) : null;
    }

    // [ObservableProperty] on the field's line or on the attribute lines above it.
    private static bool ObservablePropertyAbove(string[] lines, int line)
    {
        for (var index = line; index >= 0 && index >= line - 4; index--)
        {
            var text = lines[index].Trim();
            if (text.Contains("ObservableProperty", StringComparison.Ordinal)) return true;
            if (index < line && !text.StartsWith('[') && !text.StartsWith("//", StringComparison.Ordinal) && text.Length > 0) return false;
        }
        return false;
    }

    // "= new RelayCommand(Save)" or "= ReactiveCommand.CreateFromTask<int>(this.LoadAsync, canLoad)".
    private static readonly Regex MethodGroupPattern = new(
        @"^=\s*(?:new\b[^(;]*|[\w.]+(?:<[^>;]*>)?)\s*\(\s*(?:this\s*\.\s*)?(?<method>[A-Za-z_]\w*)\s*[,)]",
        RegexOptions.CultureInvariant);
}
