using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

internal sealed class PortalDesktopSettings(string? address = null, string destination = "org.freedesktop.portal.Desktop") : DesktopSettingsSource
{
    private static readonly string[] Namespaces = ["org.freedesktop.appearance"];
    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new DBusConnection(address ?? DBusAddress.Session ?? throw new NativeBackendUnavailableException());
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new PlatformResult<DesktopAppearance>.Success(await connection.CallMethodAsync(Request(connection),
                static (message, _) => Read(message)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException)
        { return new PlatformResult<DesktopAppearance>.Unavailable(UnavailableReason.BackendUnavailable); }
    }
    private MessageBuffer Request(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Settings", member: "ReadAll", signature: "as");
        writer.WriteArray(Namespaces);
        return writer.CreateMessage();
    }
    internal static DesktopAppearance Read(Message message)
    {
        var result = new DesktopAppearance();
        var reader = message.GetBodyReader();
        var namespaces = reader.ReadDictionaryStart();
        while (reader.HasNext(namespaces))
        {
            var ns = reader.ReadString();
            var values = reader.ReadDictionaryStart();
            while (reader.HasNext(values))
            {
                var key = reader.ReadString();
                var value = reader.ReadVariantValue();
                if (ns != "org.freedesktop.appearance") continue;
                if (key == "color-scheme" && value.Type == VariantValueType.UInt32)
                    result = result with { ColorScheme = value.GetUInt32() switch { 1 => DesktopColorScheme.Dark, 2 => DesktopColorScheme.Light, _ => DesktopColorScheme.NoPreference } };
                if (key == "contrast" && value.Type == VariantValueType.UInt32)
                    result = result with { HighContrast = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
                if (key == "reduced-motion" && value.Type == VariantValueType.UInt32)
                    result = result with { ReducedMotion = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
                if (key == "accent-color" && value.Type == VariantValueType.Struct)
                {
                    try
                    {
                        if (value.Count != 3) continue;
                        var color = (value.GetItem(0).GetDouble(), value.GetItem(1).GetDouble(), value.GetItem(2).GetDouble());
                        if (color.Item1 is >= 0 and <= 1 && color.Item2 is >= 0 and <= 1 && color.Item3 is >= 0 and <= 1)
                            result = result with { AccentColor = new(color.Item1, color.Item2, color.Item3) };
                    }
                    catch (InvalidOperationException) { /* Unknown/malformed optional preference. */ }
                }
            }
        }
        return result;
    }
}
