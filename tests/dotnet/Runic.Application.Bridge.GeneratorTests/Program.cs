using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Bridge;
using Runic.Application.Bridge.Generators;

const string valid = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;
[assembly: ApplicationBridgeContract("test.contract", 1, ContractName = "Test")]
namespace Test;
public sealed partial class State {
    [BridgeSnapshot] private Snapshot Snapshot => new(0);
}
public sealed partial class Commands(State state) {
    [BridgeCommand(AdvancesRevision = true)] private Receipt Handle(Request command, BridgeCommandContext context, CancellationToken cancellationToken) => new(command.Value);
}
public sealed record Snapshot(int Count);
public sealed record Request([property: BridgeMinimum(1), BridgeMaximum(10)] int Value);
public sealed record Receipt(int Value);
""";
var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Append(typeof(BridgeCommandAttribute).Assembly.Location).Distinct(StringComparer.Ordinal);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
(int Count, string? Fingerprint, string? Ir) Inspect(string source)
{
    var compilation = CSharpCompilation.Create("Subject", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), "Subject.cs")], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    var lowerer = new MemberBridgeLowerer(compilation);
    string? ir = lowerer.Inspect([]);
    string? fingerprint = ir is null ? null : JsonDocument.Parse(ir).RootElement.GetProperty("fingerprint").GetProperty("value").GetString();
    return (lowerer.Errors.Count, fingerprint, ir);
}
void Accept(string name, string source)
{
    var result = Inspect(source);
    if (result.Count != 0 || result.Ir is null) throw new InvalidOperationException($"Expected valid {name}: {result.Count} diagnostics.");
    Console.WriteLine("PASS " + name);
}
void Reject(string name, string source)
{
    var result = Inspect(source);
    if (result.Count == 0 || result.Ir is not null) throw new InvalidOperationException("Expected diagnostic for " + name);
    Console.WriteLine("PASS rejects " + name);
}
Accept("private sync commands and property snapshots", valid);
Accept("Task command", valid.Replace("private Receipt Handle", "private Task<Receipt> Handle").Replace("=> new(command.Value);", "=> Task.FromResult(new Receipt(command.Value));"));
Accept("ValueTask command", valid.Replace("private Receipt Handle", "private ValueTask<Receipt> Handle").Replace("=> new(command.Value);", "=> ValueTask.FromResult(new Receipt(command.Value));"));
Accept("async context snapshot", valid.Replace("private Snapshot Snapshot => new(0);", "private ValueTask<Snapshot> Snapshot(BridgeSnapshotContext context, CancellationToken cancellationToken) => ValueTask.FromResult(new Snapshot(0));"));
Accept("optional nullable field", valid.Replace("Receipt(int Value)", "Receipt(BridgeOptional<string?> Value)"));
Accept("UUID and enum fields", valid.Replace("Receipt(int Value)", "Receipt(Guid Id, Mode Mode)\n; public enum Mode { First, [System.Text.Json.Serialization.JsonStringEnumMemberName(\"stable\")] Second }\npublic sealed record Unused(int Value)"));
Reject("duplicate snapshots", valid.Replace("[BridgeSnapshot] private Snapshot Snapshot", "[BridgeSnapshot] private Snapshot Another => new(1);\n    [BridgeSnapshot] private Snapshot Snapshot"));
Reject("multiple contract roots", valid.Replace("namespace Test;", "[assembly: ApplicationBridgeContract(\"second.contract\", 1)]\nnamespace Test;"));
Reject("no snapshots", valid.Replace("[BridgeSnapshot]", ""));
Reject("nonpartial part", valid.Replace("partial class Commands", "class Commands"));
Reject("multiple DTO parameters", valid.Replace("Request command,", "Request command, Request other,"));
Reject("duplicate infrastructure parameters", valid.Replace("CancellationToken cancellationToken)", "CancellationToken cancellationToken, CancellationToken again)"));
Reject("void command", valid.Replace("Receipt Handle", "void Handle"));
Reject("nongeneric Task", valid.Replace("Receipt Handle", "Task Handle"));
Reject("generic method", valid.Replace("Handle(Request", "Handle<T>(Request"));
Reject("ref request", valid.Replace("Handle(Request", "Handle(ref Request"));
Reject("mutable DTO", valid.Replace("public sealed record Request([property: BridgeMinimum(1), BridgeMaximum(10)] int Value);", "public sealed class Request { public int Value { get; set; } }"));
Reject("decimal", valid.Replace("record Receipt(int Value)", "record Receipt(decimal Value)"));
Reject("date-time", valid.Replace("record Receipt(int Value)", "record Receipt(DateTime Value)"));
Reject("unsafe integer", valid.Replace("record Receipt(int Value)", "record Receipt(long Value)"));
Reject("custom converter", valid.Replace("public sealed record Receipt", "[System.Text.Json.Serialization.JsonConverter(typeof(object))] public sealed record Receipt"));
Reject("enum converter", valid.Replace("record Receipt(int Value)", "record Receipt(Mode Value)") + "\n[System.Text.Json.Serialization.JsonConverter(typeof(object))] public enum Mode { First }");
Reject("cancellable without operation", valid.Replace("AdvancesRevision = true", "Cancellable = true"));
Reject("operation without identifier", valid.Replace("AdvancesRevision = true", "StartsOperation = true"));
Reject("duplicate tags", valid.Replace("public sealed record Receipt", "[BridgeTag(\"Request\")] public sealed record Receipt"));
Reject("invalid bounds", valid.Replace("BridgeMinimum(1), BridgeMaximum(10)", "BridgeMinimum(20), BridgeMaximum(10)"));
var baseline = Inspect(valid);
var renamed = Inspect(valid.Replace("namespace Test;", "namespace Moved;").Replace("class Commands", "class OtherCommands").Replace(" Handle(", " ArbitraryMethodName("));
if (baseline.Fingerprint != renamed.Fingerprint) throw new InvalidOperationException("Language bindings changed the wire fingerprint.");
var reordered = Inspect(valid.Replace("BridgeMinimum(1), BridgeMaximum(10)", "BridgeMaximum(10), BridgeMinimum(1)"));
if (baseline.Fingerprint != reordered.Fingerprint) throw new InvalidOperationException("Attribute order changed the fingerprint.");
if (baseline.Fingerprint == Inspect(valid.Replace("BridgeMaximum(10)", "BridgeMaximum(11)")).Fingerprint) throw new InvalidOperationException("Wire constraints must affect the fingerprint.");
Console.WriteLine("PASS deterministic lowering excludes language bindings and includes wire constraints.");

// A referenced project must not inject incompatible module metadata.
var malformed = CSharpCompilation.Create("MalformedModule", [CSharpSyntaxTree.ParseText("""
using Runic.Application.Bridge;
[assembly: BridgeModule("{\"version\":0,\"assembly\":\"MalformedModule\"}")]
""")], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var moduleImage = new MemoryStream();
var emitted = malformed.Emit(moduleImage);
if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
var subject = CSharpCompilation.Create("Subject", [CSharpSyntaxTree.ParseText(valid)],
    references.Append(MetadataReference.CreateFromImage(moduleImage.ToArray())),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
var moduleLowerer = new MemberBridgeLowerer(subject);
if (moduleLowerer.Inspect(["MalformedModule"]) is not null || !moduleLowerer.Errors.Any(error => error.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Conflicting bridge module metadata", StringComparison.Ordinal)))
    throw new InvalidOperationException("Conflicting referenced module metadata must produce a bridge diagnostic.");
Console.WriteLine("PASS rejects conflicting referenced module metadata.");
