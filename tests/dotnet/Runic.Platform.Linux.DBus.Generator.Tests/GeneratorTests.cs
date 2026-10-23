using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Runic.Platform.Linux.DBus.Generator;
using TUnit.Core;

// The generator in a compilation: what it generates, that the output compiles, and every diagnostic.
internal sealed class GeneratorTests
{
    private const string Interface = """
        <node>
          <interface name="org.example.Thing">
            <method name="Do"><arg name="in" type="s" direction="in"/><arg name="out" type="a(ia{sv})" direction="out"/></method>
            <method name="Nothing"/>
            <property name="State" type="u" access="readwrite"/>
            <property name="Data" type="a{oas}" access="read"/>
            <signal name="Changed"><arg name="class" type="(sv)"/><arg type="h"/></signal>
          </interface>
          <interface name="org.example.Bare">
            <method name="Ping"/>
          </interface>
        </node>
        """;

    [Test, DisplayName("proxies and a service compile, with keyword names, every container and public visibility")]
    public void Compiles()
    {
        var (output, diagnostics, sources) = Run([("Thing.xml", Interface, new() { ["RunicDBusMode"] = "Proxy,Service", ["Namespace"] = "Example.Bus", ["RunicDBusVisibility"] = "public" })]);
        Check(diagnostics.IsEmpty, $"generator diagnostics: {string.Join("; ", diagnostics)}");
        var errors = output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Check(errors.Count == 0, $"the generated code does not compile cleanly: {string.Join("; ", errors.Take(5))}");
        Check(sources.Length == 2, "one proxy file and one service file");
        Check(output.GetTypeByMetadataName("Example.Bus.Thing") is { DeclaredAccessibility: Accessibility.Public, IsSealed: true }, "a public proxy");
        Check(output.GetTypeByMetadataName("Example.Bus.ThingService") is { DeclaredAccessibility: Accessibility.Public, IsAbstract: true }, "a public service");
        Check(output.GetTypeByMetadataName("Example.Bus.ThingProperties") is not null, "a properties class");
        Check(output.GetTypeByMetadataName("Example.Bus.BareProperties") is not null, "an interface without properties still compiles cleanly");
        var proxy = sources.Single(static s => s.HintName.EndsWith(".Proxy.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Check(proxy.Contains("DoAsync(string @in,", StringComparison.Ordinal) && proxy.Contains("WatchChangedAsync", StringComparison.Ordinal), "keyword argument names are escaped");
        // A peer may repeat a dictionary key: readers of every dictionary type set entries in order, so the last one wins.
        Check(!proxy.Contains("ToImmutableDictionary", StringComparison.Ordinal)
            && proxy.Contains("ImmutableDictionary<global::Runic.Platform.Linux.DBus.DBusObjectPath, global::System.Collections.Immutable.ImmutableArray<string>>.Empty.SetItems(", StringComparison.Ordinal),
            "dictionary readers keep the last value of a repeated key");
    }

    [Test, DisplayName("internal is the default, RunicDBusClassName names the class, and other XML is ignored")]
    public void ClassNameVisibilityAndOtherFiles()
    {
        var (output, diagnostics, sources) = Run([
            ("Thing.xml", Interface, new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "Example.Bus", ["RunicDBusClassName"] = "Device" }),
            ("Other.xml", "<not-dbus/>", new() { ["DBusGeneratorMode"] = "Proxy", ["Namespace"] = "Elsewhere" }),
        ]);
        Check(diagnostics.IsEmpty && sources.Length == 1, "only the file with RunicDBusMode generates");
        Check(output.GetTypeByMetadataName("Example.Bus.Device") is { DeclaredAccessibility: Accessibility.Internal }, "the named, internal service");
    }

    // W319-003 D18 g: RunicDBusNamespace, with Namespace as its former name until 2.0; the new name wins.
    [Test, DisplayName("RunicDBusNamespace names the namespace, Namespace stays an alias, and the new name wins")]
    public void RunicDBusNamespaceWithAlias()
    {
        var (output, diagnostics, _) = Run([
            ("New.xml", Interface, new() { ["RunicDBusMode"] = "Service", ["RunicDBusNamespace"] = "Example.New", ["RunicDBusClassName"] = "NewDevice" }),
            ("Former.xml", Interface, new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "Example.Former", ["RunicDBusClassName"] = "FormerDevice" }),
            ("Both.xml", Interface, new() { ["RunicDBusMode"] = "Service", ["RunicDBusNamespace"] = "Example.Both", ["Namespace"] = "Example.Ignored", ["RunicDBusClassName"] = "BothDevice" }),
        ]);
        Check(diagnostics.IsEmpty, string.Join("; ", diagnostics));
        Check(output.GetTypeByMetadataName("Example.New.NewDevice") is not null, "RunicDBusNamespace names the namespace");
        Check(output.GetTypeByMetadataName("Example.Former.FormerDevice") is not null, "Namespace still names it");
        Check(output.GetTypeByMetadataName("Example.Both.BothDevice") is not null && output.GetTypeByMetadataName("Example.Ignored.BothDevice") is null,
            "RunicDBusNamespace wins over Namespace");
    }

    [Test, DisplayName("each invalid input reports its RUNICDBUS diagnostic")]
    public void Diagnostics()
    {
        Expect("RUNICDBUS001", ("A.xml", Interface, new() { ["RunicDBusMode"] = "Handler", ["Namespace"] = "N" }));
        Expect("RUNICDBUS002", ("A.xml", Interface, new() { ["RunicDBusMode"] = "Proxy" }));
        Expect("RUNICDBUS003", ("A.xml", Interface, new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N", ["RunicDBusVisibility"] = "protected" }));
        Expect("RUNICDBUS004", ("A.xml", null!, new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS005", ("A.xml", "<node><interface", new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS006", ("A.xml", "<interface name=\"a.b\"/>", new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS007", ("A.xml", "<node><interface name=\"nodots\"/></node>", new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS008", ("A.xml", "<node/>", new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS009", ("A.xml", Interface, new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "N", ["RunicDBusClassName"] = "Not Valid" }));
        Expect("RUNICDBUS009", ("A.xml", "<node><interface name=\"a.One\"/><interface name=\"a.Two\"/></node>",
            new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N", ["RunicDBusClassName"] = "Both" }));
        Expect("RUNICDBUS010", ("A.xml", Interface, new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }),
            ("B.xml", Interface, new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS011", ("A.xml", "<node><interface name=\"a.b\"><method name=\"Bad-Name\"/></interface></node>", new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS011", ("A.xml", "<node><interface name=\"a.b\"><property name=\"P\" type=\"a\" access=\"read\"/></interface></node>",
            new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        Expect("RUNICDBUS011", ("A.xml", "<node><interface name=\"a.b\"><property name=\"P\" type=\"s\" access=\"sometimes\"/></interface></node>",
            new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        var descriptor = Run([("A.xml", Interface, new() { ["RunicDBusMode"] = "Handler", ["Namespace"] = "N" })]).Diagnostics[0].Descriptor;
        Check(descriptor.HelpLinkUri.EndsWith("docs/diagnostics.md#runicdbus001", StringComparison.Ordinal), $"help link: {descriptor.HelpLinkUri}");
    }

    [Test, DisplayName("a generated member that would hide, overload or duplicate another reports RUNICDBUS012")]
    public void NameCollisions()
    {
        static string Node(string members, string name = "a.Thing") => $"<node><interface name=\"{name}\">{members}</interface></node>";
        // Proxy members that take a DBusProxy member's name, or another generated member's.
        foreach (var members in new[]
        {
            "<method name=\"Call\"/>",                                                  // CallAsync
            "<method name=\"GetProperty\"/>",                                           // GetPropertyAsync
            "<signal name=\"Signal\"/>",                                                // WatchSignalAsync
            "<signal name=\"PropertyChanges\"/>",                                       // ReadPropertyChanges
            "<signal name=\"Properties\"/>",                                            // WatchPropertiesAsync
            "<property name=\"Properties\" type=\"s\" access=\"read\"/>",               // GetPropertiesAsync
            "<property name=\"Invalidated\" type=\"s\" access=\"read\"/>",              // ThingProperties.Invalidated
            "<method name=\"GetName\"/><property name=\"Name\" type=\"s\" access=\"read\"/>", // GetNameAsync twice
            "<method name=\"ToString\"/><method name=\"to_string\"/>",                    // ToStringAsync twice
        })
            Expect("RUNICDBUS012", ("A.xml", Node(members), new() { ["RunicDBusMode"] = "Proxy", ["Namespace"] = "N" }));
        // Service members that take a DBusObject member's name, or another generated member's.
        foreach (var members in new[]
        {
            "<property name=\"Path\" type=\"o\" access=\"read\"/>",                     // DBusObject.Path
            "<signal name=\"Signal\"/>",                                                // EmitSignal
            "<method name=\"HandleMethod\"/>",                                          // HandleMethodAsync
            "<property name=\"ThingInterface\" type=\"s\" access=\"read\"/>",           // the interface constant
            "<property name=\"GetType\" type=\"s\" access=\"read\"/>",                  // object.GetType
            "<property name=\"MaxConcurrentCalls\" type=\"i\" access=\"read\"/>",       // DBusObject.MaxConcurrentCalls
        })
            Expect("RUNICDBUS012", ("A.xml", Node(members), new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "N" }));
        Expect("RUNICDBUS012", ("A.xml", "<node><interface name=\"a.b\"><method name=\"Do\"/></interface><interface name=\"c.d\"><method name=\"Do\"/></interface></node>",
            new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "N" }));
        Expect("RUNICDBUS012", ("A.xml", "<node><interface name=\"a.Same\"/><interface name=\"b.Same\"/></node>",
            new() { ["RunicDBusMode"] = "Service", ["Namespace"] = "N" }));

        // Names that only resemble inherited members still generate, and the output no longer hides warnings.
        var (output, diagnostics, sources) = Run([("A.xml", Node("<method name=\"Calls\"/><signal name=\"Signals\"/><property name=\"Paths\" type=\"ao\" access=\"read\"/>"),
            new() { ["RunicDBusMode"] = "Proxy,Service", ["Namespace"] = "N" })]);
        Check(diagnostics.IsEmpty, $"generator diagnostics: {string.Join("; ", diagnostics)}");
        Check(output.GetDiagnostics().All(static d => d.Severity < DiagnosticSeverity.Warning), "the near-miss names compile cleanly");
        Check(sources.All(static s => !s.SourceText.ToString().Contains("CS0108", StringComparison.Ordinal) && !s.SourceText.ToString().Contains("CS0114", StringComparison.Ordinal)),
            "generated code does not suppress member hiding warnings");
    }

    // The generated code is API in the consumer's assembly: pin its shape for the test interface, which uses every type.
    // Run with RUNIC_UPDATE_GOLDEN=1 to rewrite the files after an intended change.
    [Test, DisplayName("the generated proxy and service match the golden files")]
    public void GoldenOutput()
    {
        string root = WorkspaceRoot();
        string xml = File.ReadAllText(Path.Combine(root, "tests", "dotnet", "Runic.Platform.Linux.DBus.Tests", "Interfaces", "org.runic.Test.Media.xml"));
        var (output, diagnostics, sources) = Run([("org.runic.Test.Media.xml", xml,
            new() { ["RunicDBusMode"] = "Proxy,Service", ["RunicDBusNamespace"] = "Runic.Platform.Linux.DBus.Tests.Generated" })]);
        Check(diagnostics.IsEmpty && output.GetDiagnostics().All(static d => d.Severity < DiagnosticSeverity.Warning), "the test interface generates cleanly");
        bool update = Environment.GetEnvironmentVariable("RUNIC_UPDATE_GOLDEN") == "1";
        foreach (var source in sources)
        {
            // The header names the generator's version, which changes with every build of a release.
            string actual = System.Text.RegularExpressions.Regex.Replace(source.SourceText.ToString().ReplaceLineEndings("\n"),
                "(Runic\\.Platform\\.Linux\\.DBus\\.Generator\"?,? \"?)[0-9][^ \")]*", "$1<version>");
            // A .golden suffix keeps the files out of the build.
            string golden = Path.Combine(root, "tests", "dotnet", "Runic.Platform.Linux.DBus.Generator.Tests", "golden", source.HintName + ".golden");
            if (update) { File.WriteAllText(golden, actual); continue; }
            Check(File.Exists(golden) && File.ReadAllText(golden).ReplaceLineEndings("\n") == actual,
                $"{source.HintName} differs from {golden}. Review the change and run with RUNIC_UPDATE_GOLDEN=1.");
        }
    }

    [Test, DisplayName("diagnostics point into the XML file: at the element, at the parser's position, or at the start")]
    public void Locations()
    {
        static Location Locate(string id, string text, Dictionary<string, string> metadata)
        {
            var diagnostic = Run([("A.xml", text, metadata)]).Diagnostics.Single(d => d.Id == id);
            Check(diagnostic.Location.Kind == LocationKind.ExternalFile && diagnostic.Location.GetLineSpan().Path == "/project/A.xml",
                $"{id} is located in the file: {diagnostic.Location}");
            return diagnostic.Location;
        }
        static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
        var proxy = new Dictionary<string, string> { ["RunicDBusMode"] = "Proxy", ["RunicDBusNamespace"] = "N" };
        Check(Line(Locate("RUNICDBUS001", Interface, new() { ["RunicDBusMode"] = "Handler", ["RunicDBusNamespace"] = "N" })) == 1, "a metadata error at the start");
        var xml = Locate("RUNICDBUS005", "<node>\n  <interface name=\"a.b\">\n  </node>", proxy);
        Check(Line(xml) == 3, $"an XML error at the parser's line: {xml.GetLineSpan()}");
        var member = Locate("RUNICDBUS011", "<node>\n  <interface name=\"a.b\">\n    <method name=\"Bad-Name\"/>\n  </interface>\n</node>", proxy);
        Check(Line(member) == 3 && member.GetLineSpan().StartLinePosition.Character == 5 && member.SourceSpan.Start == "<node>\n  <interface name=\"a.b\">\n    <".Length,
            $"a member error at its element: {member.GetLineSpan()} {member.SourceSpan}");
        var collision = Locate("RUNICDBUS012", "<node>\n  <interface name=\"a.b\">\n    <method name=\"Ok\"/>\n    <method name=\"Call\"/>\n  </interface>\n</node>", proxy);
        Check(Line(collision) == 4, $"a collision at the member that collides: {collision.GetLineSpan()}");
        Check(Line(Locate("RUNICDBUS007", "<node>\n\n  <interface name=\"nodots\"/>\n</node>", proxy)) == 3, "an interface error at the interface");
        var duplicate = Run([("A.xml", Interface, proxy), ("B.xml", Interface, proxy)]).Diagnostics.First(static d => d.Id == "RUNICDBUS010");
        Check(duplicate.Location.GetLineSpan().Path == "/project/A.xml" && duplicate.AdditionalLocations.Single().GetLineSpan().Path == "/project/B.xml",
            "a duplicate type points at both files");
    }

    [Test, DisplayName("RUNICDBUS013 warns about Deprecated and, for proxies, NoReply annotations, and still generates")]
    public void IgnoredAnnotations()
    {
        const string Annotated = """
            <node>
              <interface name="org.example.Old">
                <annotation name="org.freedesktop.DBus.Deprecated" value="true"/>
                <method name="Fire"><annotation name="org.freedesktop.DBus.Method.NoReply" value="true"/></method>
                <method name="Read"><annotation name="org.freedesktop.DBus.Deprecated" value="false"/></method>
                <property name="Level" type="u" access="read"><annotation name="org.freedesktop.DBus.Deprecated" value="true"/></property>
                <signal name="Gone"><annotation name="org.gtk.GDBus.C.Name" value="true"/></signal>
              </interface>
            </node>
            """;
        static List<Diagnostic> Warnings(string mode) => [.. Run([("Old.xml", Annotated, new() { ["RunicDBusMode"] = mode, ["RunicDBusNamespace"] = "N" })])
            .Diagnostics.Where(static d => d.Id == "RUNICDBUS013")];
        var both = Warnings("Proxy,Service");
        Check(both.Count == 3 && both.All(static d => d.Severity == DiagnosticSeverity.Warning), $"three warnings, once per file: {string.Join("; ", both)}");
        string text = string.Join("\n", both.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
        Check(text.Contains("interface org.example.Old", StringComparison.Ordinal) && text.Contains("property org.example.Old.Level", StringComparison.Ordinal)
            && text.Contains("org.freedesktop.DBus.Method.NoReply annotation of the method org.example.Old.Fire", StringComparison.Ordinal), text);
        Check(Warnings("Service").Count == 2, "a service has no reply to skip, so NoReply is not reported");
        Check(Line(both.Single(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("NoReply", StringComparison.Ordinal)).Location) == 4,
            "the warning points at the annotation");
        var (output, diagnostics, sources) = Run([("Old.xml", Annotated, new() { ["RunicDBusMode"] = "Proxy,Service", ["RunicDBusNamespace"] = "N" })]);
        Check(sources.Length == 2 && diagnostics.All(static d => d.Severity == DiagnosticSeverity.Warning)
            && output.GetDiagnostics().All(static d => d.Severity < DiagnosticSeverity.Warning), "the code is still generated and compiles");
        Check(both[0].Descriptor.HelpLinkUri.EndsWith("docs/diagnostics.md#runicdbus013", StringComparison.Ordinal), both[0].Descriptor.HelpLinkUri);

        static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
    }

    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Expect(string id, params (string Name, string Text, Dictionary<string, string> Metadata)[] files)
    {
        var diagnostics = Run(files).Diagnostics;
        Check(diagnostics.Any(d => d.Id == id && d.Severity == DiagnosticSeverity.Error), $"{id} expected, got: {string.Join("; ", diagnostics.Select(static d => d.Id + " " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))}");
    }

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<GeneratedSourceResult> Sources) Run(
        (string Name, string Text, Dictionary<string, string> Metadata)[] files)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies().Where(static a => !a.IsDynamic && a.Location.Length > 0)
            .Select(static a => MetadataReference.CreateFromFile(a.Location)).Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(Runic.Platform.Linux.DBus.DBusValue).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Runic.Platform.PlatformUnit).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(System.Collections.Immutable.ImmutableArray).Assembly.Location))
            .DistinctBy(static r => r.Display).ToList();
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create("Generated", [CSharpSyntaxTree.ParseText("", parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var texts = files.Select(static f => (AdditionalText)new Text("/project/" + f.Name, f.Text)).ToImmutableArray();
        var options = new Options(files.ToDictionary(static f => "/project/" + f.Name, static f => f.Metadata));
        var driver = CSharpGeneratorDriver.Create([new DBusSourceGenerator().AsSourceGenerator()], texts, parse, options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult();
        return (output, result.Diagnostics, result.Results.SelectMany(static r => r.GeneratedSources).ToImmutableArray());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => text is null ? null : SourceText.From(text);
    }

    private sealed class Options(Dictionary<string, Dictionary<string, string>> metadata) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values([]);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Values([]);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            new Values(metadata.TryGetValue(textFile.Path, out var values)
                ? values.ToDictionary(static entry => "build_metadata.AdditionalFiles." + entry.Key, static entry => entry.Value) : []);
    }

    private sealed class Values(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }
}
