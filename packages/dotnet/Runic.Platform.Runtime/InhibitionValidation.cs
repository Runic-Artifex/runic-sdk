namespace Runic.Platform.Runtime;

/// <summary>Shared validation for native inhibition requests.</summary>
public static class InhibitionValidation
{
    /// <summary>Validates flags and a bounded user-visible reason before touching native state.</summary>
    public static void Validate(DesktopInhibitionEffects effects, string reason)
    {
        if (effects == DesktopInhibitionEffects.None || (effects & ~(DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep)) != 0)
            throw new ArgumentOutOfRangeException(nameof(effects));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 128 || reason.Contains('\0')) throw new ArgumentException("Use a reason of at most 128 characters without NUL.", nameof(reason));
    }
}
