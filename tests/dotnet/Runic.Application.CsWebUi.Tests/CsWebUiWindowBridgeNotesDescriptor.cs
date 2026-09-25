using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Runic.Application.Bridge;

namespace Runic.Application.CsWebUi;

/// <summary>
/// Deliberately hand-written fixed Notes routes for the first Window Bridge host
/// fixture. It is test-only, not adapter or generated application surface.
/// </summary>
internal static class CsWebUiWindowBridgeNotesDescriptor
{
    internal const string GetRoute = "notes.title.get";
    internal const string SetRoute = "notes.title.set";
    internal const string WriteCheckedRoute = "notes.title.writeChecked";

    /// <summary>Attaches fixed routes only for a current document's mounted presentation.</summary>
    internal static WindowBridgeAttachment Attach(IWindowBridgeTransport transport, WindowBridgeCheckedTitleField title,
        Func<WindowBridgeConnection, WindowBridgeDocumentEpoch, string, bool> isPresentationMounted) =>
        WindowBridgeAttachment.Create(AttachEndpoints(transport, title, isPresentationMounted));

    internal static WindowBridgeEndpointLease[] AttachEndpoints(IWindowBridgeTransport transport, WindowBridgeCheckedTitleField title,
        Func<WindowBridgeConnection, WindowBridgeDocumentEpoch, string, bool> isPresentationMounted)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(isPresentationMounted);
        var leases = new List<WindowBridgeEndpointLease>(3);
        try
        {
            leases.Add(transport.Bind(GetRoute, arguments => Get(arguments, title, isPresentationMounted)));
            leases.Add(transport.Bind(SetRoute, arguments => Set(arguments, title, isPresentationMounted)));
            leases.Add(transport.Bind(WriteCheckedRoute, arguments => WriteChecked(arguments, title, isPresentationMounted)));
            return leases.ToArray();
        }
        catch
        {
            foreach (WindowBridgeEndpointLease lease in leases) lease.Dispose();
            throw;
        }
    }

    private static string Get(WindowBridgeArguments arguments, WindowBridgeCheckedTitleField title,
        Func<WindowBridgeConnection, WindowBridgeDocumentEpoch, string, bool> isPresentationMounted)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(arguments.GetString());
            if (!TryDocumentRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId))
                return RejectedWithoutCurrent("The title snapshot request is invalid.");
            if (!isPresentationMounted(arguments.Connection, epoch, presentationId)) return Unmounted();
            return JsonSerializer.Serialize(new { ok = true, snapshot = Snapshot(title.Snapshot()), error = (string?)null });
        }
        catch (JsonException) { return RejectedWithoutCurrent("The title snapshot request is invalid."); }
    }

    private static string Set(WindowBridgeArguments arguments, WindowBridgeCheckedTitleField title,
        Func<WindowBridgeConnection, WindowBridgeDocumentEpoch, string, bool> isPresentationMounted)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(arguments.GetString());
            if (!TrySetRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId, out string? requestId, out string? value))
                return RejectedWithoutCurrent("The title set request is invalid.");
            if (!isPresentationMounted(arguments.Connection, epoch, presentationId)) return Unmounted();
            return Receipt(title.Set(requestId, value));
        }
        catch (JsonException) { return RejectedWithoutCurrent("The title set request is invalid."); }
    }

    private static string WriteChecked(WindowBridgeArguments arguments, WindowBridgeCheckedTitleField title,
        Func<WindowBridgeConnection, WindowBridgeDocumentEpoch, string, bool> isPresentationMounted)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(arguments.GetString());
            if (!TryCheckedRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId, out string? requestId, out string? value, out WindowBridgeTitleBaseline? expected))
                return RejectedWithoutCurrent("The checked title request is invalid.");
            if (!isPresentationMounted(arguments.Connection, epoch, presentationId)) return Unmounted();
            return Receipt(title.WriteChecked(requestId, expected, value));
        }
        catch (JsonException) { return RejectedWithoutCurrent("The checked title request is invalid."); }
    }

    private static bool TryDocumentRequest(JsonElement request, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
        [NotNullWhen(true)] out string? presentationId)
    {
        epoch = null;
        presentationId = null;
        if (request.ValueKind != JsonValueKind.Object || !HasOnly(request, "documentEpoch", "presentationId")
            || !request.TryGetProperty("documentEpoch", out JsonElement element) || element.ValueKind != JsonValueKind.String
            || !TryPresentationId(request, out presentationId))
            return false;
        try { epoch = WindowBridgeDocumentEpoch.Create(element.GetString()!); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool TrySetRequest(JsonElement request, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
        [NotNullWhen(true)] out string? presentationId, out string? requestId, out string? value)
    {
        epoch = null;
        presentationId = null;
        requestId = value = null;
        if (request.ValueKind != JsonValueKind.Object || !HasOnly(request, "documentEpoch", "presentationId", "requestId", "value")
            || !request.TryGetProperty("documentEpoch", out JsonElement document) || document.ValueKind != JsonValueKind.String
            || !TryPresentationId(request, out presentationId)
            || !request.TryGetProperty("requestId", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("value", out JsonElement title) || title.ValueKind != JsonValueKind.String)
            return false;
        try { epoch = WindowBridgeDocumentEpoch.Create(document.GetString()!); }
        catch (ArgumentException) { return false; }
        requestId = id.GetString();
        value = title.GetString();
        return requestId is not null && value is not null;
    }

    private static bool TryCheckedRequest(JsonElement request, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
        [NotNullWhen(true)] out string? presentationId, out string? requestId, out string? value,
        out WindowBridgeTitleBaseline? expected)
    {
        epoch = null;
        presentationId = null;
        requestId = value = null;
        expected = null;
        if (request.ValueKind != JsonValueKind.Object || !HasOnly(request, "documentEpoch", "presentationId", "requestId", "value", "expected")
            || !request.TryGetProperty("documentEpoch", out JsonElement document) || document.ValueKind != JsonValueKind.String
            || !TryPresentationId(request, out presentationId)
            || !request.TryGetProperty("requestId", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("value", out JsonElement title) || title.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("expected", out JsonElement baseline) || baseline.ValueKind != JsonValueKind.Object
            || !HasOnly(baseline, "value", "version")
            || !baseline.TryGetProperty("value", out JsonElement baselineValue) || baselineValue.ValueKind != JsonValueKind.String
            || !baseline.TryGetProperty("version", out JsonElement baselineVersion) || !baselineVersion.TryGetInt64(out long version))
            return false;
        try { epoch = WindowBridgeDocumentEpoch.Create(document.GetString()!); }
        catch (ArgumentException) { return false; }
        requestId = id.GetString();
        value = title.GetString();
        string? expectedValue = baselineValue.GetString();
        if (requestId is null || value is null || expectedValue is null) return false;
        expected = new WindowBridgeTitleBaseline(version, expectedValue);
        return true;
    }

    private static bool TryPresentationId(JsonElement request, [NotNullWhen(true)] out string? presentationId)
    {
        presentationId = null;
        if (!request.TryGetProperty("presentationId", out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return false;
        string? value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        presentationId = value;
        return true;
    }

    private static bool HasOnly(JsonElement objectValue, params ReadOnlySpan<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in objectValue.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return true;
    }

    private static string RejectedWithoutCurrent(string error) =>
        JsonSerializer.Serialize(new { ok = false, kind = "rejected", current = (object?)null, error });

    private static string Unmounted() =>
        "{\"ok\":false,\"kind\":\"rejected\",\"current\":null,\"error\":\"The presentation is not mounted.\"}";

    private static object Snapshot(WindowBridgeTitleSnapshot snapshot) => new { value = snapshot.Value, version = snapshot.Version };

    private static string Receipt(WindowBridgeTitleReceipt receipt) =>
        JsonSerializer.Serialize(new
        {
            ok = receipt.Kind == WindowBridgeTitleWriteKind.Applied,
            kind = ReceiptKind(receipt.Kind),
            current = Snapshot(receipt.Current),
            error = receipt.Error,
        });

    private static string ReceiptKind(WindowBridgeTitleWriteKind kind) => kind switch
    {
        WindowBridgeTitleWriteKind.Applied => "applied",
        WindowBridgeTitleWriteKind.Conflict => "conflict",
        WindowBridgeTitleWriteKind.Rejected => "rejected",
        WindowBridgeTitleWriteKind.Expired => "expired",
        WindowBridgeTitleWriteKind.CommittedWithError => "committed-with-error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

}
