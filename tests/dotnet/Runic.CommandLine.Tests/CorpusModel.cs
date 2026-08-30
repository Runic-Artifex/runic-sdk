using System.Text.Json;
using System.Text.Json.Serialization;

namespace Runic.CommandLine.Tests;

internal sealed record GrammarCorpus(
    int FormatVersion,
    string Protocol,
    CorpusCatalog Catalog,
    CorpusCase[] Cases);

internal sealed record CorpusCatalog(CorpusCommand[] Commands);

internal sealed record CorpusCommand(
    string[] Path,
    string[] Aliases,
    CorpusOption[] Options,
    CorpusArgument[] Arguments);

internal sealed record CorpusOption(
    string Id,
    [property: JsonPropertyName("long")] string LongName,
    string[] Aliases,
    CorpusArity Arity,
    string Repeat);

internal sealed record CorpusArgument(string Id, CorpusArity Arity);

internal sealed record CorpusArity(JsonElement Minimum, JsonElement Maximum);

internal sealed record CorpusCase(string Id, string[] Args, CorpusExpected Expected);

internal sealed record CorpusExpected(
    string Kind,
    string[]? CommandPath,
    CorpusValue[]? Options,
    CorpusValue[]? Arguments,
    CorpusDiagnostic[]? Diagnostics);

internal sealed record CorpusValue(string Id, string[] Values);

internal sealed record CorpusDiagnostic(string Code, string Kind, int TokenIndex);

internal sealed record OutputClassificationCorpus(
    int FormatVersion,
    string Protocol,
    string EnvironmentVariable,
    OutputClassificationCase[] Cases);

internal sealed record OutputClassificationCase(
    string Id,
    string[] Args,
    Dictionary<string, string?> Environment,
    OutputClassificationExpected Expected);

internal sealed record OutputClassificationExpected(
    string Kind,
    string? Mode,
    string? Source,
    CorpusDiagnostic[]? Diagnostics);

internal sealed record ProtocolManifest(
    int FormatVersion,
    string Protocol,
    string OutputEnvironmentVariable,
    string ImplementationNamespace,
    string Encoding,
    string Framing,
    ProtocolLimits Limits,
    ProtocolFixture[] Fixtures);

internal sealed record ProtocolLimits(
    int FramedBytes,
    int JsonDepth,
    int RequestIdBytes,
    int CommandBytes,
    int PayloadTypeBytes,
    int CodeBytes,
    int MessageBytes,
    int DiagnosticCount,
    int FaultDetailCount,
    int DiagnosticArgumentCount,
    int DetailKeyBytes,
    int DetailValueBytes,
    int DiagnosticKindBytes,
    int DiagnosticCommandPathBytes,
    int DiagnosticMessageKeyBytes,
    int DiagnosticArgumentBytes);

internal sealed record ProtocolFixture(string Id, string Class, string Path, string? Outcome, int? CaseCount);

internal sealed record WireCorpus(int FormatVersion, string Protocol, string Representation, WireCase[] Cases);

internal sealed record WireCase(
    string Id,
    string? WireUtf8Base64,
    WireRecipe? WireRecipe,
    int? ExpectedWireBytes,
    ProtocolExpected Expected);

internal sealed record WireRecipe(string Kind, string Prefix, string Value, int Count, string Suffix);

internal sealed record SemanticProtocolCorpus(int FormatVersion, string Protocol, SemanticProtocolCase[] Cases);

internal sealed record SemanticProtocolCase(
    string Id,
    JsonElement Document,
    ProtocolClientContext? ClientContext,
    ProtocolExpected Expected);

internal sealed record ProtocolClientContext(string[]? SupportedPayloadTypes, string? ExpectedPayloadJsonKind);

internal sealed record ProtocolExpected(
    string Kind,
    string Reason,
    bool? PayloadDeserializationAttempted);

internal sealed record ProtocolPayload(
    string? State,
    int? FuturePayloadField,
    int? Written,
    string? OutputPath);

internal sealed record ExportPayload(int Written, string OutputPath);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GrammarCorpus))]
[JsonSerializable(typeof(OutputClassificationCorpus))]
[JsonSerializable(typeof(ProtocolManifest))]
[JsonSerializable(typeof(WireCorpus))]
[JsonSerializable(typeof(SemanticProtocolCorpus))]
[JsonSerializable(typeof(ProtocolPayload))]
[JsonSerializable(typeof(ExportPayload))]
internal sealed partial class CorpusJsonContext : JsonSerializerContext;
