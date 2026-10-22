using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Runic.Application.Tool;

/// <summary>
/// The <c>runic.application.tool.dev/1</c> payload: the configuration <c>dotnet runic dev</c> evaluated. README.md
/// documents its JSON schema; keep both in sync. Property names are spelled out so that the JSON context's options
/// cannot rename them.
/// </summary>
internal sealed record DevCommandResult(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("frontendDirectory")] string FrontendDirectory,
    [property: JsonPropertyName("frontendOutputDirectory")] string FrontendOutputDirectory,
    [property: JsonPropertyName("webRoot")] string WebRoot,
    [property: JsonPropertyName("developmentServer")] string? DevelopmentServer,
    [property: JsonPropertyName("dryRun")] bool DryRun) : IToolCommandResult<DevCommandResult>
{
    internal const string PayloadType = "runic.application.tool.dev/1";

    static string IToolCommandResult<DevCommandResult>.PayloadType => PayloadType;

    static JsonTypeInfo<DevCommandResult> IToolCommandResult<DevCommandResult>.JsonTypeInfo => DevCommandJsonContext.Default.DevCommandResult;

    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public DevCommandResult WithHumanOutput(string humanOutput) => this with { HumanOutput = humanOutput };

    public override string ToString() => HumanOutput;

    internal static DevCommandResult Create(DevProjectConfiguration configuration, bool dryRun) => new(
        configuration.ProjectPath,
        configuration.WorkspaceRoot,
        configuration.FrontendOutputDirectory,
        configuration.RuntimeWebRoot,
        configuration.HasDevelopmentServer ? configuration.DevelopmentServerKind : null,
        dryRun);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DevCommandResult))]
internal sealed partial class DevCommandJsonContext : JsonSerializerContext;

/// <summary>
/// The <c>runic.application.tool.size/1</c> payload: where the <c>runic.size/2</c> report is and its totals.
/// A failed publish or verification carries it in <c>fault.data</c>. README.md documents its JSON schema.
/// </summary>
internal sealed record SizeCommandResult(
    [property: JsonPropertyName("report")] string Report,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("publishExitCode")] int PublishExitCode,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("compressedBytes")] long CompressedBytes,
    [property: JsonPropertyName("verification")] SizeCommandVerification Verification) : IToolCommandResult<SizeCommandResult>
{
    internal const string PayloadType = "runic.application.tool.size/1";

    static string IToolCommandResult<SizeCommandResult>.PayloadType => PayloadType;

    static JsonTypeInfo<SizeCommandResult> IToolCommandResult<SizeCommandResult>.JsonTypeInfo => SizeCommandJsonContext.Default.SizeCommandResult;

    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public SizeCommandResult WithHumanOutput(string humanOutput) => this with { HumanOutput = humanOutput };

    public override string ToString() => HumanOutput;
}

internal sealed record SizeCommandVerification(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exitCode")] int? ExitCode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SizeCommandResult))]
internal sealed partial class SizeCommandJsonContext : JsonSerializerContext;
