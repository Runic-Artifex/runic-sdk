using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The wire codecs of an assembly's Bridges, one class per distinct node of their type graphs, in
/// <see cref="FileName"/> (runic-sdk#1109). Every member, command input and result and interaction value of the same
/// shape shares one codec, and a DTO's codec calls the codecs of its members, so a <c>Row</c> inside a list and a
/// <c>Row</c> command input use one <c>Row</c> class.
/// </summary>
/// <remarks>
/// A Bridge names its codecs with using aliases (<see cref="BridgeCodecAliases"/>), so the code that calls
/// <c>TitleValueCodec.Read</c> is unchanged. The classes are named once every Bridge is generated, because a name gets
/// a hash suffix only when two different codecs would share it. Until then generated text refers to a codec with a
/// token, which <see cref="WriteAll"/> replaces; Bridge files are therefore written by <see cref="WriteAll"/> too.
/// </remarks>
internal static class BridgeSharedCodecs
{
    /// <summary>The file, beside the Bridges; it matches their <c>*.Bridge.g.cs</c> Compile glob.</summary>
    internal const string FileName = "RunicShared.Codecs.Bridge.g.cs";
    internal const string ClassName = "RunicBridgeCodecs";
    internal const string Namespace = "Runic.Application.Views.Generated.Bridges";

    // A reference to a codec in generated text: Mark, the codec's index, Mark. U+0001 never occurs in generated C#
    // (string literals are JSON-escaped), so a token cannot be confused with code.
    private const char Mark = '\u0001';

    private static readonly List<Codec> Codecs = [];
    private static readonly Dictionary<string, Codec> ByBody = new(StringComparer.Ordinal);
    private static readonly List<(string Path, string Content)> Pending = [];

    /// <summary>Registers the codec of a node and its children, and returns a token that names its class.</summary>
    internal static string Reference(BridgeTypeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        // Children first: the body names them by their tokens, so equal nodes have equal bodies.
        var body = new StringBuilder();
        BridgeTypeGraph.AppendCSharpNodeCodec(body, node, Reference);
        var text = body.ToString();
        if (!ByBody.TryGetValue(text, out var codec))
        {
            codec = new Codec(Codecs.Count, text, BaseName(node), BridgeTypeGraph.CSharpNodeType(node),
                Hash(Resolve(text, index => Codecs[index].Hash)));
            Codecs.Add(codec);
            ByBody.Add(text, codec);
        }
        return Token(codec.Index);
    }

    /// <summary>Keeps a generated file that refers to shared codecs until <see cref="WriteAll"/> names them.</summary>
    internal static void WriteLater(string path, string content) => Pending.Add((path, content));

    /// <summary>Names the codecs, then writes the pending files and <see cref="FileName"/> into <paramref name="directory"/>.</summary>
    internal static void WriteAll(string directory)
    {
        AssignNames();
        // A Bridge is in the codecs' namespace, so RunicBridgeCodecs needs no qualification there.
        foreach (var (path, content) in Pending)
            GeneratedFile.WriteIfChanged(path, Resolve(content, index => $"{ClassName}.{Codecs[index].Name}"));
        Pending.Clear();
        GeneratedFile.WriteIfChanged(Path.Combine(directory, FileName), SharedFile());
    }

    private static string Token(int index) => Mark + index.ToString(CultureInfo.InvariantCulture) + Mark;

    private static string Resolve(string text, Func<int, string> name)
    {
        if (!text.Contains(Mark, StringComparison.Ordinal)) return text;
        var result = new StringBuilder(text.Length);
        var position = 0;
        while (text.IndexOf(Mark, position) is var start and >= 0)
        {
            var end = text.IndexOf(Mark, start + 1);
            result.Append(text, position, start - position);
            result.Append(name(int.Parse(text.AsSpan(start + 1, end - start - 1), CultureInfo.InvariantCulture)));
            position = end + 1;
        }
        return result.Append(text, position, text.Length - position).ToString();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // A codec is named after its C# type. Two different codecs with one name, such as two DTOs named Address in
    // different namespaces, both get the first eight hex digits of their content hash: the hash depends only on the
    // codec, so the names do not depend on the order in which the Bridges were generated.
    private static void AssignNames()
    {
        foreach (var group in Codecs.GroupBy(codec => codec.BaseName, StringComparer.Ordinal))
        {
            var codecs = group.ToArray();
            var length = 8;
            while (codecs.Length > 1 && codecs.Select(codec => codec.Hash[..length]).Distinct(StringComparer.Ordinal).Count() < codecs.Length)
                length += 4;
            foreach (var codec in codecs)
                codec.Name = Identifier(codecs.Length == 1 ? group.Key : group.Key + "_" + codec.Hash[..length]);
        }
    }

    // Write, Read and WriteItem are the members of every codec class, which C# does not allow a class to share with
    // its own name. A name that starts with a lower-case letter could be a keyword, so it is verbatim.
    private static string Identifier(string name) =>
        name is "Write" or "Read" or "WriteItem" or ClassName ? name + "_"
            : char.IsLower(name[0]) ? "@" + name
            : name;

    // The C# type, with Nullable for a nullable value and the collection's shape (ListOfRow, ArrayOfString,
    // IReadOnlyDictionaryOfInt32). ToPage marks an object only written to the page, whose Read throws (#312), so it
    // stays apart from the codec of the same type read from the page. A Custom codec names its codec class.
    private static string BaseName(BridgeTypeNode node)
    {
        var type = node.NonNullableType;
        var name = node.Kind switch
        {
            BridgeWireKind.Array => "ArrayOf" + BaseName(node.Element!),
            BridgeWireKind.List => TypeName(type.GetGenericTypeDefinition()) + "Of" + BaseName(node.Element!),
            BridgeWireKind.StringDictionary => (type.IsGenericType ? TypeName(type.GetGenericTypeDefinition()) : "Dictionary") + "Of" + BaseName(node.Value!),
            BridgeWireKind.Custom => TypeName(type) + "Via" + TypeName(node.CustomCodec!.CodecType),
            BridgeWireKind.Dto or BridgeWireKind.Union when !node.Direction.HasFlag(BridgeValueDirection.FromPage) => TypeName(type) + "ToPage",
            BridgeWireKind.Enum or BridgeWireKind.Dto or BridgeWireKind.Union or BridgeWireKind.TextReference
                or BridgeWireKind.Json or BridgeWireKind.Blob or BridgeWireKind.Upload => TypeName(type),
            // Scalars: String, Int32, Boolean, Guid, DateOnly, ...
            _ => node.Kind.ToString(),
        };
        return (node.IsNullable ? "Nullable" : "") + name;
    }

    // Box<Row> is BoxOfRow, Outer.Inner is Outer_Inner and int[] is ArrayOfInt32.
    private static string TypeName(Type type)
    {
        if (type.IsArray) return "ArrayOf" + TypeName(type.GetElementType()!);
        var name = type.Name;
        if (name.IndexOf('`') is var tick and >= 0) name = name[..tick];
        if (type.IsNested && !type.IsGenericParameter) name = TypeName(type.DeclaringType!) + "_" + name;
        if (type.IsConstructedGenericType)
            name += "Of" + string.Join("And", type.GetGenericArguments().Select(TypeName));
        return new string([.. name.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')]);
    }

    private static string SharedFile()
    {
        var cs = new StringBuilder();
        cs.AppendLine(GeneratedOutput.Header);
        cs.AppendLine("#nullable enable");
        cs.AppendLine($"namespace {Namespace};");
        cs.AppendLine();
        cs.AppendLine("// The wire codecs of this assembly's Bridges: one class per distinct value shape, shared by every member, command");
        cs.AppendLine("// and interaction that has it. A Bridge names its codecs with using aliases, such as");
        cs.AppendLine($"//     using TitleValueCodec = global::{Namespace}.{ClassName}.String;");
        cs.AppendLine("// A class is named after its C# type. Nullable marks a nullable value, ToPage an object only written to the page");
        cs.AppendLine("// (its Read throws), and a hash suffix tells apart two different codecs that would otherwise share a name.");
        cs.AppendLine($"internal static class {ClassName}");
        cs.AppendLine("{");
        var first = true;
        foreach (var codec in Codecs.OrderBy(codec => codec.Name, StringComparer.Ordinal))
        {
            if (!first) cs.AppendLine();
            first = false;
            cs.AppendLine($"    // {codec.CSharpType}");
            cs.AppendLine($"    internal static class {codec.Name}");
            cs.AppendLine("    {");
            var body = Resolve(codec.Body, index => $"{ClassName}.{Codecs[index].Name}");
            foreach (var line in body.TrimEnd('\n').Split('\n'))
                cs.AppendLine(line.Length == 0 ? "" : "    " + line);
            cs.AppendLine("    }");
        }
        cs.AppendLine("}");
        return cs.ToString();
    }

    private sealed class Codec(int index, string body, string baseName, string csharpType, string hash)
    {
        internal int Index { get; } = index;
        /// <summary>The class body, with tokens for the child codecs.</summary>
        internal string Body { get; } = body;
        internal string BaseName { get; } = baseName;
        internal string CSharpType { get; } = csharpType;
        /// <summary>The hash of the body with each child token replaced by the child's hash.</summary>
        internal string Hash { get; } = hash;
        internal string Name { get; set; } = "";
    }
}

/// <summary>
/// The codecs one Bridge file names. Each is a using alias of a shared codec class (<see cref="BridgeSharedCodecs"/>),
/// so the descriptors keep calling <c>{Member}ValueCodec.Read</c>, <c>{Command}InputCodec.Write</c> and so on. A
/// member with field paths gets a class of its own in the Bridge instead (<see cref="AppendFieldPathCodec"/>).
/// </summary>
internal sealed class BridgeCodecAliases
{
    private readonly List<(string Alias, string Codec)> _aliases = [];

    internal void Add(string alias, BridgeTypeGraph graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(graph);
        _aliases.Add((alias, BridgeSharedCodecs.Reference(graph.Root)));
    }

    /// <summary>Appends a member's codec class with its field-path setters (field-paths/1) to the Bridge class.</summary>
    internal static void AppendFieldPathCodec(StringBuilder source, string className, BridgeTypeGraph graph, BridgeTypeNode container,
        IReadOnlyList<BridgeFieldLeaf> leaves)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(graph);
        var body = new StringBuilder();
        graph.AppendCSharpFieldPathCodec(body, container, leaves, BridgeSharedCodecs.Reference);
        source.AppendLine($"    private static class {className}");
        source.AppendLine("    {");
        foreach (var line in body.ToString().TrimEnd('\n').Split('\n'))
            source.AppendLine(line.Length == 0 ? "" : "    " + line);
        source.AppendLine("    }");
    }

    /// <summary>The using directives, placed after the Bridge's namespace declaration.</summary>
    internal string Directives()
    {
        if (_aliases.Count == 0) return "";
        var directives = new StringBuilder();
        directives.AppendLine($"// This Bridge's codecs: each is the assembly's shared codec of its type, in {BridgeSharedCodecs.FileName}.");
        foreach (var (alias, codec) in _aliases) directives.AppendLine($"using {alias} = global::{BridgeSharedCodecs.Namespace}.{codec};");
        return directives.ToString();
    }
}
