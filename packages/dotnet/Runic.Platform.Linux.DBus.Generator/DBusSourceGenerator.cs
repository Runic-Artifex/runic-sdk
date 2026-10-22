// The input handling, grouping, diagnostics and naming are adapted from Tmds.DBus.Generator
// (https://github.com/tmds/Tmds.DBus 0.95.1, MIT License, Copyright 2006 Alp Toker, 2010 Other Contributors, 2016 Tom Deseyn);
// see NOTICE and licenses/Tmds.DBus-LICENSE.txt in the Runic.Platform.Linux.DBus package.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Runic.Platform.Linux.DBus.Generator;

/// <summary>Generates typed proxies and services from D-Bus introspection XML in AdditionalFiles.</summary>
/// <remarks>
/// A file takes part when it has <c>RunicDBusMode</c> metadata (<c>Proxy</c> or <c>Service</c>) and a
/// <c>RunicDBusNamespace</c> (or its former name <c>Namespace</c>);
/// other XML files, such as those of Tmds.DBus.Generator, are left alone.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class DBusSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) =>
            {
                var options = pair.Right.GetOptions(pair.Left);
                options.TryGetValue("build_metadata.AdditionalFiles.RunicDBusMode", out string? mode);
                if (string.IsNullOrEmpty(mode)) return null;
                // RunicDBusNamespace wins; Namespace, its former name, stays an alias until 2.0.
                if (!options.TryGetValue("build_metadata.AdditionalFiles.RunicDBusNamespace", out string? ns) || string.IsNullOrWhiteSpace(ns))
                    options.TryGetValue("build_metadata.AdditionalFiles.Namespace", out ns);
                options.TryGetValue("build_metadata.AdditionalFiles.RunicDBusVisibility", out string? visibility);
                options.TryGetValue("build_metadata.AdditionalFiles.RunicDBusClassName", out string? className);
                var text = pair.Left.GetText(cancellationToken)?.ToString();
                return new InputFile(pair.Left.Path, text, mode!, ns, visibility, className);
            })
            .Where(static file => file is not null)
            .Select(static (file, _) => file!)
            .Collect();
        context.RegisterSourceOutput(files, static (output, inputs) => Generate(output, inputs));
    }

    private static void Generate(SourceProductionContext output, ImmutableArray<InputFile> inputs)
    {
        var units = new List<GenerationUnit>();
        foreach (var input in inputs)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            units.AddRange(Parse(output, input));
        }
        string version = GeneratorVersion();
        foreach (var group in units.GroupBy(static unit => unit.Namespace, StringComparer.Ordinal))
        {
            // Two files must not generate the same type in one namespace.
            var names = group.SelectMany(static unit => unit.TypeNames.Select(name => (name, unit))).GroupBy(static entry => entry.name).Where(static entry => entry.Count() > 1).ToList();
            foreach (var duplicate in names)
            {
                var files = duplicate.Select(static entry => entry.unit.Input).GroupBy(static input => input.Path).Select(static inputs => inputs.First()).ToList();
                output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.DuplicateTypeName, files[0].Locate(), files.Skip(1).Select(static input => input.Locate()),
                    duplicate.Key, group.Key, string.Join(", ", files.Select(static input => input.FileName))));
            }
            if (names.Count > 0) continue;
            foreach (var unit in group)
            {
                try
                {
                    string source = DBusEmitter.Emit(unit, version);
                    output.AddSource($"{unit.Namespace}.{System.IO.Path.GetFileNameWithoutExtension(unit.FileName)}.{unit.Mode}.g.cs", SourceText.From(source, Encoding.UTF8));
                }
                catch (DBusNameCollisionException error)
                {
                    output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.NameCollision, unit.Input.Locate(error.Node), unit.FileName, error.Message));
                }
                catch (DBusGenerationException error)
                {
                    output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidMember, unit.Input.Locate(error.Node), unit.FileName, error.Message));
                }
            }
        }
    }

    // A file can generate proxies, a service, or both: RunicDBusMode="Proxy,Service". (A semicolon would start a comment
    // in the analyzer configuration that carries the metadata.)
    private static List<GenerationUnit> Parse(SourceProductionContext output, InputFile input)
    {
        var modes = input.Mode.Split([','], StringSplitOptions.RemoveEmptyEntries).Select(static mode => mode.Trim()).ToList();
        if (modes.Count == 0 || modes.Any(static mode => !string.Equals(mode, "Proxy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "Service", StringComparison.OrdinalIgnoreCase)))
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidMode, input.Locate(), input.FileName, input.Mode));
            return [];
        }
        if (ParseNode(output, input) is not { } node) return [];
        var services = modes.Select(static mode => string.Equals(mode, "Service", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        ReportIgnoredAnnotations(output, input, node.Interfaces, proxy: services.Contains(false));
        return services
            .Where(service => !(node.ClassName is not null && !service && node.Interfaces.Count > 1 && Report(output, input, node.ClassName)))
            .Select(service => new GenerationUnit(input, node.Namespace, service, node.IsPublic, node.ClassName, node.Interfaces))
            .ToList();
    }

    private static bool Report(SourceProductionContext output, InputFile input, string className)
    {
        output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidClassName, input.Locate(), input.FileName, className));
        return true;
    }

    // Standard annotations whose meaning the generated code does not carry out (RUNICDBUS013), once per file. The
    // EmitsChangedSignal annotation is applied; annotations of other tools, such as org.gtk.GDBus.*, are not D-Bus's own.
    private static void ReportIgnoredAnnotations(SourceProductionContext output, InputFile input, List<XElement> interfaces, bool proxy)
    {
        foreach (var element in interfaces)
        {
            string iface = (string)element.Attribute("name")!;
            foreach (var target in new[] { element }.Concat(element.Elements().Where(static e => e.Name.LocalName is "method" or "signal" or "property")))
            {
                string what = target == element ? $"interface {iface}" : $"{target.Name.LocalName} {iface}.{(string?)target.Attribute("name")}";
                foreach (var annotation in target.Elements().Where(static e => e.Name.LocalName == "annotation"))
                {
                    string name = (string?)annotation.Attribute("name") ?? "";
                    if (!string.Equals(((string?)annotation.Attribute("value"))?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) continue;
                    string? effect = name switch
                    {
                        "org.freedesktop.DBus.Deprecated" => "the generated members are not marked obsolete",
                        // A service replies anyway, and a caller that asked for no reply ignores it; a proxy waits for one.
                        "org.freedesktop.DBus.Method.NoReply" when proxy && target.Name.LocalName == "method" =>
                            "the generated proxy method waits for a reply that the service may never send; pass a cancellation token or set the proxy's Timeout",
                        _ => null,
                    };
                    if (effect is not null)
                        output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.IgnoredAnnotation, input.Locate(annotation), input.FileName, name, what, effect));
                }
            }
        }
    }

    private sealed class Node(string ns, bool isPublic, string? className, List<XElement> interfaces)
    {
        public string Namespace { get; } = ns;
        public bool IsPublic { get; } = isPublic;
        public string? ClassName { get; } = className;
        public List<XElement> Interfaces { get; } = interfaces;
    }

    private static Node? ParseNode(SourceProductionContext output, InputFile input)
    {
        if (string.IsNullOrWhiteSpace(input.Namespace))
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.MissingNamespace, input.Locate(), input.FileName));
            return null;
        }
        bool isPublic;
        if (string.IsNullOrEmpty(input.Visibility) || string.Equals(input.Visibility, "internal", StringComparison.OrdinalIgnoreCase)) isPublic = false;
        else if (string.Equals(input.Visibility, "public", StringComparison.OrdinalIgnoreCase)) isPublic = true;
        else
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidVisibility, input.Locate(), input.FileName, input.Visibility));
            return null;
        }
        if (input.Text is null)
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.UnreadableFile, input.Locate(), input.FileName));
            return null;
        }
        XDocument document;
        try { document = XDocument.Parse(input.Text, LoadOptions.SetLineInfo); }
        catch (System.Xml.XmlException error)
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidXml, input.Locate(error.LineNumber, error.LinePosition), input.FileName, error.Message));
            return null;
        }
        if (document.Root?.Name.LocalName != "node")
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidRoot, input.Locate(document.Root), input.FileName, document.Root?.Name.LocalName ?? ""));
            return null;
        }
        var interfaces = new List<XElement>();
        foreach (var element in document.Root.Elements().Where(static element => element.Name.LocalName == "interface"))
        {
            string name = (string?)element.Attribute("name") ?? "";
            if (!DBusEmitter.IsInterfaceName(name))
            {
                output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidInterfaceName, input.Locate(element), input.FileName, name));
                continue;
            }
            interfaces.Add(element);
        }
        if (interfaces.Count == 0)
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.NoInterfaces, input.Locate(document.Root), input.FileName));
            return null;
        }
        string? className = string.IsNullOrWhiteSpace(input.ClassName) ? null : input.ClassName!.Trim();
        if (className is not null && !SyntaxFacts(className))
        {
            output.ReportDiagnostic(Diagnostic.Create(DBusDiagnostics.InvalidClassName, input.Locate(), input.FileName, className));
            return null;
        }
        return new Node(input.Namespace!.Trim(), isPublic, className, interfaces);
    }

    private static bool SyntaxFacts(string identifier) =>
        identifier.Length > 0 && (char.IsLetter(identifier[0]) || identifier[0] == '_') && identifier.All(static c => char.IsLetterOrDigit(c) || c == '_');

    private static string GeneratorVersion()
    {
        string version = typeof(DBusSourceGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        int metadata = version.IndexOf('+');
        return metadata < 0 ? version : version.Substring(0, metadata);
    }

}

// One AdditionalFiles item with its metadata. Diagnostics point into it: at an XML element, or at its start.
internal sealed class InputFile(string path, string? text, string mode, string? ns, string? visibility, string? className) : IEquatable<InputFile>
{
    private SourceText? _source;

    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string? Text { get; } = text;
    public string Mode { get; } = mode;
    public string? Namespace { get; } = ns;
    public string? Visibility { get; } = visibility;
    public string? ClassName { get; } = className;

    public Location Locate(XObject? node = null) =>
        node is System.Xml.IXmlLineInfo info && info.HasLineInfo() ? Locate(info.LineNumber, info.LinePosition) : Locate(0, 0);

    // Lines and positions count from 1, as XML line information does; 0 is the start of the file.
    public Location Locate(int line, int position)
    {
        var start = new LinePosition(Math.Max(line - 1, 0), Math.Max(position - 1, 0));
        int offset = 0;
        if (Text is not null)
        {
            _source ??= SourceText.From(Text);
            if (start.Line < _source.Lines.Count)
            {
                var row = _source.Lines[start.Line];
                offset = Math.Min(row.Start + start.Character, row.End);
                start = new LinePosition(start.Line, offset - row.Start);
            }
            else start = default;
        }
        else start = default;
        return Location.Create(Path, new TextSpan(offset, 0), new LinePositionSpan(start, start));
    }

    public bool Equals(InputFile? other) => other is not null && Path == other.Path && Text == other.Text && Mode == other.Mode
        && Namespace == other.Namespace && Visibility == other.Visibility && ClassName == other.ClassName;
    public override bool Equals(object? obj) => Equals(obj as InputFile);
    public override int GetHashCode() => (Path, Text, Mode, Namespace, Visibility, ClassName).GetHashCode();
}

internal sealed class GenerationUnit(InputFile input, string ns, bool service, bool isPublic, string? className, List<XElement> interfaces)
{
    public InputFile Input { get; } = input;
    public string FileName => Input.FileName;
    public string Namespace { get; } = ns;
    public bool Service { get; } = service;
    public bool IsPublic { get; } = isPublic;
    public List<XElement> Interfaces { get; } = interfaces;
    public string Mode => Service ? "Service" : "Proxy";

    // A service is one class for the whole node; a proxy is one class per interface.
    public string ServiceClassName => className ?? DBusEmitter.Prettify(DBusEmitter.LastSegment((string)Interfaces[0].Attribute("name")!)) + "Service";
    public string ProxyClassName(XElement element) => className ?? DBusEmitter.Prettify(DBusEmitter.LastSegment((string)element.Attribute("name")!));

    public IEnumerable<string> TypeNames => Service ? [ServiceClassName]
        : Interfaces.SelectMany(element => new[] { ProxyClassName(element), ProxyClassName(element) + "Properties" });
}

// The node, when known, is the XML element the diagnostic points at.
internal sealed class DBusGenerationException(string message, XObject? node = null) : Exception(message)
{
    public XObject? Node { get; } = node;
}

// A generated member would take the name of an inherited or another generated member (RUNICDBUS012).
internal sealed class DBusNameCollisionException(string message, XObject? node = null) : Exception(message)
{
    public XObject? Node { get; } = node;
}

internal static class DBusDiagnostics
{
    private const string Category = "Runic.Platform.Linux.DBus";

    public static readonly DiagnosticDescriptor InvalidMode = Error("RUNICDBUS001", "Invalid D-Bus generation mode",
        "The RunicDBusMode of '{0}' is '{1}'; use Proxy or Service");
    public static readonly DiagnosticDescriptor MissingNamespace = Error("RUNICDBUS002", "Missing namespace",
        "'{0}' has no RunicDBusNamespace metadata; set RunicDBusNamespace on its AdditionalFiles item");
    public static readonly DiagnosticDescriptor InvalidVisibility = Error("RUNICDBUS003", "Invalid visibility",
        "The RunicDBusVisibility of '{0}' is '{1}'; use internal or public");
    public static readonly DiagnosticDescriptor UnreadableFile = Error("RUNICDBUS004", "Unreadable file", "'{0}' could not be read");
    public static readonly DiagnosticDescriptor InvalidXml = Error("RUNICDBUS005", "Invalid XML", "'{0}' is not valid XML: {1}");
    public static readonly DiagnosticDescriptor InvalidRoot = Error("RUNICDBUS006", "Invalid root element",
        "'{0}' must have a <node> root element, not <{1}>");
    public static readonly DiagnosticDescriptor InvalidInterfaceName = Error("RUNICDBUS007", "Invalid interface name",
        "An <interface> in '{0}' has the name '{1}', which is not a D-Bus interface name");
    public static readonly DiagnosticDescriptor NoInterfaces = Error("RUNICDBUS008", "No interfaces", "'{0}' declares no <interface>");
    public static readonly DiagnosticDescriptor InvalidClassName = Error("RUNICDBUS009", "Invalid class name",
        "The RunicDBusClassName of '{0}' is '{1}', which is not an identifier or names a proxy for more than one interface");
    public static readonly DiagnosticDescriptor DuplicateTypeName = Error("RUNICDBUS010", "Duplicate generated type",
        "The type '{0}' would be generated more than once in namespace '{1}', from {2}; set RunicDBusClassName or another RunicDBusNamespace");
    public static readonly DiagnosticDescriptor InvalidMember = Error("RUNICDBUS011", "Invalid interface member", "'{0}': {1}");
    public static readonly DiagnosticDescriptor NameCollision = Error("RUNICDBUS012", "Generated member name collision", "'{0}': {1}");
    public static readonly DiagnosticDescriptor IgnoredAnnotation = new("RUNICDBUS013", "Ignored D-Bus annotation",
        "'{0}': the {1} annotation of the {2} is ignored; {3}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: RunicReleaseLinks.Diagnostic("RUNICDBUS013"));

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: RunicReleaseLinks.Diagnostic(id));
}
