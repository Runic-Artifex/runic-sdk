using System.Text.Json;

namespace Runic.Application.Views;

// A failed route, operation admission or operation always replies with a bounded
// message such as "Save failed.". With failure detail, the error also carries
// detail: {type, message, stack} from the exception, which the TypeScript runtime
// exposes as BridgeError.detail and the Vite plugin shows in DevTools. Detail can
// contain file paths and application data, so a window includes it only when its
// options (WindowContentSessionOptions.IncludeFailureDetail, which hosts fill from
// RunicWindowOptions.IncludeFailureDetail) or a Development environment allow it.
internal static class BridgeFailureDetails
{
    private const int MaximumTypeLength = 512;
    private const int MaximumMessageLength = 4 * 1024;
    private const int MaximumStackLength = 16 * 1024;

    /// <summary>Whether a Bridge without a window session includes failure detail.</summary>
    internal static bool DefaultIncluded => IsDevelopment(Environment.GetEnvironmentVariable);

    internal static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the detail of <paramref name="error"/> when replies may carry it.</summary>
    internal static BridgeFailureDetail? Capture(Exception error, bool include) => include
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
