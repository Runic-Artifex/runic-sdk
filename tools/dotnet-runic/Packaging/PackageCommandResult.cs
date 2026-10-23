using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// The <c>runic.application.tool.package/1</c> payload: the target, the identity it packaged and the files it wrote.
/// README.md documents its JSON schema; keep both in sync.
/// </summary>
internal sealed record PackageCommandResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("identity")] PackageCommandIdentity Identity,
    [property: JsonPropertyName("packages")] IReadOnlyList<PackageCommandPackage> Packages,
    [property: JsonPropertyName("warnings")] IReadOnlyList<PackageCommandWarning> Warnings) : IToolCommandResult<PackageCommandResult>
{
    internal const string PayloadType = "runic.application.tool.package/1";

    static string IToolCommandResult<PackageCommandResult>.PayloadType => PayloadType;

    static JsonTypeInfo<PackageCommandResult> IToolCommandResult<PackageCommandResult>.JsonTypeInfo =>
        PackageCommandJsonContext.Default.PackageCommandResult;

    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public PackageCommandResult WithHumanOutput(string humanOutput) => this with { HumanOutput = humanOutput };

    public override string ToString() => HumanOutput;
}

internal sealed record PackageCommandIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// One file or directory the target wrote. <c>signing</c> is <c>none</c>, <c>adhoc</c> (macOS without an identity),
/// <c>test</c> (a self-signed certificate) or <c>identity</c> (the consumer's certificate, Developer ID or GPG key).
/// </summary>
internal sealed record PackageCommandPackage(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("signing")] string Signing);

/// <summary>
/// A warning of a successful run: a <c>RAPPPKG2xxx</c> code from docs/diagnostics.md, which CI can match, and the
/// human message, which may change.
/// </summary>
internal sealed record PackageCommandWarning(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageCommandResult))]
internal sealed partial class PackageCommandJsonContext : JsonSerializerContext;
