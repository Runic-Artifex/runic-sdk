using System.Text;
using System.Text.Json;

namespace Runic.CommandLine.Tests;

internal static class ProtocolCorpusTests
{
    private static readonly string ProtocolRoot = Path.Combine(AppContext.BaseDirectory, "Corpus", "protocol");
    private static readonly WireCorpus Wire = Read("wire-inputs.json", CorpusJsonContext.Default.WireCorpus);
    private static readonly SemanticProtocolCorpus Semantic = Read(
        "invalid-structures.json",
        CorpusJsonContext.Default.SemanticProtocolCorpus);
    private static readonly string[] ExampleNames =
        ["success", "command-fault", "validation", "host-fault", "cancelled"];

    public static IReadOnlyList<TestCase> All { get; } = CreateTests();

    private static TestCase[] CreateTests()
    {
        AssertEx.Equal(15, Wire.Cases.Length, "The frozen wire corpus row count changed.");
        AssertEx.Equal(23, Semantic.Cases.Length, "The frozen semantic protocol corpus row count changed.");

        return
        [
            new TestCase("protocol/manifest-identities-and-limits", Manifest),
            .. Wire.Cases.Select(test => new TestCase($"protocol/{test.Id}", () => WireCase(test))),
            .. Semantic.Cases.Select(test => new TestCase($"protocol/{test.Id}", () => SemanticCase(test))),
            .. ExampleNames.Select(name => new TestCase($"protocol/example.{name}", () => Example(name))),
        ];
    }

    private static ValueTask Manifest()
    {
        ProtocolManifest manifest = Read("manifest.json", CorpusJsonContext.Default.ProtocolManifest);
        AssertEx.Equal(1, manifest.FormatVersion);
        AssertEx.Equal(CliProtocol.Identity, manifest.Protocol);
        AssertEx.Equal(CommandOutputClassifier.EnvironmentVariableName, manifest.OutputEnvironmentVariable);
        AssertEx.Equal("Runic.CommandLine", manifest.ImplementationNamespace);
        AssertEx.Equal("utf-8", manifest.Encoding);
        AssertEx.Equal("one-json-object-one-lf", manifest.Framing);
        AssertEx.Equal(CommandJsonEnvelopeReader.DefaultMaximumFrameBytes, manifest.Limits.FramedBytes);
        AssertEx.Equal(32, manifest.Limits.JsonDepth);
        AssertEx.Equal(128, manifest.Limits.RequestIdBytes);
        AssertEx.Equal(512, manifest.Limits.CommandBytes);
        AssertEx.Equal(128, manifest.Limits.PayloadTypeBytes);
        AssertEx.Equal(64, manifest.Limits.DiagnosticCount);
        AssertEx.Equal(15, manifest.Fixtures.Single(static fixture => fixture.Id == "protocol.wire-inputs").CaseCount);
        AssertEx.Equal(23, manifest.Fixtures.Single(static fixture => fixture.Id == "protocol.invalid-structures").CaseCount);
        return ValueTask.CompletedTask;
    }

    private static ValueTask WireCase(WireCase test)
    {
        byte[] frame = test.WireRecipe is null
            ? Convert.FromBase64String(test.WireUtf8Base64!)
            : CreateWire(test.WireRecipe);
        if (test.ExpectedWireBytes is int expectedBytes)
        {
            AssertEx.Equal(expectedBytes, frame.Length, test.Id);
        }

        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(
                frame,
                "sample.status/1",
                CorpusJsonContext.Default.ProtocolPayload));
        AssertEx.Equal(
            test.Expected.Reason,
            exception.Kind,
            $"{test.Id}: expected {test.Expected.Reason}, found {exception.Kind}.");
        return ValueTask.CompletedTask;
    }

    private static ValueTask SemanticCase(SemanticProtocolCase test)
    {
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(test.Document);
        byte[] frame = new byte[document.Length + 1];
        document.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        string expectedPayloadType = test.Expected.Reason == "invalid-payload-type"
            ? "sample.status/1"
            : test.ClientContext?.SupportedPayloadTypes?.FirstOrDefault()
            ?? (test.Document.TryGetProperty("payloadType", out JsonElement payloadType) &&
                payloadType.ValueKind == JsonValueKind.String
                    ? payloadType.GetString()!
                    : "sample.status/1");

        if (test.Expected.Kind == "accepted")
        {
            CommandResponse<ProtocolPayload> response = CommandJsonEnvelopeReader.Read(
                frame,
                expectedPayloadType,
                CorpusJsonContext.Default.ProtocolPayload);
            AssertEx.Equal(test.Document.GetProperty("success").GetBoolean(), response.Success, test.Id);
            AssertEx.Equal(test.Document.GetProperty("exitCode").GetInt32(), response.ExitCode, test.Id);
            AssertEx.Equal(test.Document.GetProperty("requestId").GetString(), response.RequestId, test.Id);
            AssertEx.Equal(test.Document.GetProperty("command").GetString(), response.Command, test.Id);
            AssertEx.Equal(test.Document.GetProperty("diagnostics").GetArrayLength(), response.Diagnostics.Count, test.Id);
        }
        else
        {
            CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
                CommandJsonEnvelopeReader.Read(
                    frame,
                    expectedPayloadType,
                    CorpusJsonContext.Default.ProtocolPayload));
            AssertEx.Equal(test.Expected.Reason, exception.Kind, test.Id);
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask Example(string name)
    {
        byte[] expected = File.ReadAllBytes(Path.Combine(ProtocolRoot, "examples", $"{name}.json"));
        AssertEx.True(expected.Length > 2 && expected[^1] == (byte)'\n', $"Example {name} is not LF framed.");

        byte[] actual = name switch
        {
            "success" => CommandJsonEnvelopeWriter.Serialize(
                CommandResponse.Succeeded(
                    "req-export-0001",
                    "export",
                    "sample.export-result/1",
                    new ExportPayload(42, "result.json")),
                CorpusJsonContext.Default.ExportPayload),
            "command-fault" => Failure(
                "req-export-0002", "export", 10, "RCLI3001",
                "The export could not be completed.",
                new Dictionary<string, string> { ["reason"] = "destination-unavailable" }, true),
            "validation" => ValidationExample(),
            "host-fault" => Failure(
                "req-generated-0004", "status", 70, "RCLI5000",
                "The command host could not complete the request."),
            "cancelled" => Failure(
                "req-copy-0005", "copy", 4, "RCLI4000",
                "The command was cancelled.", retryable: true),
            _ => throw new InvalidOperationException($"Unknown protocol example '{name}'."),
        };

        AssertEx.SequenceEqual(expected, actual, $"Canonical example '{name}' differs from stable writer output.");
        CommandResponse<ExportPayload> roundTrip = CommandJsonEnvelopeReader.Read(
            expected,
            name == "success" ? "sample.export-result/1" : "sample.unused/1",
            CorpusJsonContext.Default.ExportPayload);
        AssertEx.Equal(name == "success", roundTrip.Success);
        return ValueTask.CompletedTask;
    }

    private static byte[] ValidationExample()
    {
        var diagnostic = new CommandDiagnostic(
            "RCLI2001",
            "invalid-format",
            "The format is not supported.",
            CommandDiagnosticPhase.Binding,
            CommandDiagnosticSeverity.Error,
            2,
            ["format"],
            new CommandPath(["export"]),
            "diagnostics.invalid-format");
        return Failure(
            "req-export-0003",
            "export",
            3,
            "RCLI2001",
            "One or more values are invalid.",
            diagnostics: [diagnostic]);
    }

    private static byte[] Failure(
        string requestId,
        string command,
        int exitCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool retryable = false,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failed<ExportPayload>(
                requestId,
                command,
                exitCode,
                new CommandFault(code, message, details, retryable),
                diagnostics),
            CorpusJsonContext.Default.ExportPayload);

    private static byte[] CreateWire(WireRecipe recipe)
    {
        AssertEx.Equal("repeat-utf8", recipe.Kind);
        string wire = string.Concat(recipe.Prefix, new string(recipe.Value.Single(), recipe.Count), recipe.Suffix);
        return Encoding.UTF8.GetBytes(wire);
    }

    private static T Read<T>(string relativePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(ProtocolRoot, relativePath)), typeInfo)
        ?? throw new InvalidOperationException($"Protocol corpus '{relativePath}' could not be read.");
}
