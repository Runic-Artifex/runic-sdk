namespace Runic.Platform;

// One source: Runic.Platform checks with this copy, and Runic.Platform.Runtime compiles the file too for the providers.
// The argument checks of IWindowPlatformServices.SetBadgeAsync and SetProgressAsync, shared by their implementations.
internal static class DesktopTaskbarRules
{
    internal const int MaximumShownCount = 99;

    internal static void Check(DesktopBadge? badge, CancellationToken cancellationToken)
    {
        if (badge is not null)
        {
            if (badge.Count is < 1) throw new ArgumentException("A badge count is at least 1; use null for a dot.", nameof(badge));
            if (badge.Description?.Contains('\0') == true) throw new ArgumentException("A badge description must not contain NUL characters.", nameof(badge));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void Check(DesktopProgress? progress, CancellationToken cancellationToken)
    {
        if (progress is not null)
        {
            if (progress.Value is { } value && (!double.IsFinite(value) || value < 0 || value > 1))
                throw new ArgumentException("A progress value is from 0 to 1, or null for unknown progress.", nameof(progress));
            if (!Enum.IsDefined(progress.State)) throw new ArgumentException($"The progress state {(int)progress.State} is not defined.", nameof(progress));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    // The text of a badge: the count up to 99, then 99+; empty for a dot.
    internal static string Label(DesktopBadge badge) => badge.Count switch
    {
        null => "",
        > MaximumShownCount => "99+",
        { } count => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
