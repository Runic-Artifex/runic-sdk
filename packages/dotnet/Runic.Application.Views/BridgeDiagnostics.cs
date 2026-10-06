using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>Controls the local failure detail of Bridge error replies.</summary>
/// <remarks>
/// A failed route, operation admission or operation always replies with a bounded
/// message such as <c>"Save failed."</c>. With failure detail, the error also carries
/// <c>detail: {type, message, stack}</c> from the exception, which the TypeScript runtime
/// exposes as <c>BridgeError.detail</c> and the Vite plugin shows in DevTools. Detail can
/// contain file paths and application data, so production replies never include it unless
/// the application opts in.
/// </remarks>
public static class BridgeDiagnostics
{
    private const int MaximumTypeLength = 512;
    private const int MaximumMessageLength = 4 * 1024;
    private const int MaximumStackLength = 16 * 1024;
    private static volatile int _includeFailureDetail;

    /// <summary>
    /// Whether Bridge error replies carry the exception type, message and stack.
    /// <see langword="null"/>, the default, includes them only when the host environment is
    /// <c>Development</c>: <c>DOTNET_ENVIRONMENT</c>, or else <c>ASPNETCORE_ENVIRONMENT</c>,
    /// which <c>dotnet runic dev</c> sets. <see langword="true"/> opts in and
    /// <see langword="false"/> opts out regardless of the environment.
    /// </summary>
    public static bool? IncludeFailureDetail
    {
        get => _includeFailureDetail switch { 1 => true, 2 => false, _ => null };
        set => _includeFailureDetail = value switch { true => 1, false => 2, null => 0 };
    }

    internal static bool IncludesFailureDetail =>
        IncludeFailureDetail ?? IsDevelopment(Environment.GetEnvironmentVariable);

    internal static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the detail of <paramref name="error"/> when replies may carry it.</summary>
    internal static BridgeFailureDetail? Capture(Exception error) => IncludesFailureDetail
        ? new(Bound(error.GetType().FullName ?? error.GetType().Name, MaximumTypeLength),
            Bound(error.Message, MaximumMessageLength), Bound(error.ToString(), MaximumStackLength))
        : null;

    internal static void Write(Utf8JsonWriter writer, BridgeFailureDetail? detail)
    {
        if (detail is null) return;
        writer.WritePropertyName("detail");
        writer.WriteStartObject();
        writer.WriteString("type", detail.Type);
        writer.WriteString("message", detail.Message);
        writer.WriteString("stack", detail.Stack);
        writer.WriteEndObject();
    }

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : string.Concat(value.AsSpan(0, maximumLength - 1), "…");
}

/// <summary>Development-only exception detail of a failed Bridge reply.</summary>
internal sealed record BridgeFailureDetail(string Type, string Message, string Stack);
