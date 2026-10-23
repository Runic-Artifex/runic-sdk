namespace Runic.Platform;

// The rules of DesktopDragData, shared by every drag source. One source: Runic.Desktop uses this copy through
// InternalsVisibleTo, and Runic.Application.Views.Desktop compiles the file too for the WPF windows' services.
internal static class DesktopDragRules
{
    internal const int MaximumItems = 1024;
    internal const int MaximumTextLength = 1024 * 1024;
    private const DesktopDragEffects AllEffects = DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link;

    // Throws ArgumentException (or ArgumentNullException) for data no drag source accepts.
    internal static void Check(DesktopDragData data, string parameterName = "data")
    {
        ArgumentNullException.ThrowIfNull(data, parameterName);
        var paths = data.Paths.IsDefault ? [] : data.Paths;
        var uris = data.Uris.IsDefault ? [] : data.Uris;
        if (paths.IsEmpty && data.Text is null && uris.IsEmpty)
            throw new ArgumentException("A drag needs at least one path, text or URI.", parameterName);
        if (paths.Length > MaximumItems || uris.Length > MaximumItems)
            throw new ArgumentException($"A drag has at most {MaximumItems} paths and {MaximumItems} URIs.", parameterName);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Every dragged path must be absolute.", parameterName);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new ArgumentException("Every dragged path must name an existing file or directory.", parameterName);
        }
        if (data.Text is { } text && (text.Length > MaximumTextLength || text.Contains('\0')))
            throw new ArgumentException($"The dragged text has more than {MaximumTextLength} characters or a NUL character.", parameterName);
        foreach (var uri in uris)
        {
            if (uri is null || !uri.IsAbsoluteUri)
                throw new ArgumentException("Every dragged URI must be absolute.", parameterName);
            if (uri.IsFile || string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Drag local files through Paths, not as file: URIs.", parameterName);
        }
        var effects = data.AllowedEffects;
        if (effects == DesktopDragEffects.None || (effects & ~AllEffects) != 0)
            throw new ArgumentException("AllowedEffects must combine Copy, Move and Link.", parameterName);
        if ((effects & (DesktopDragEffects.Move | DesktopDragEffects.Link)) != 0 && paths.IsEmpty)
            throw new ArgumentException("Move and Link need dragged paths.", parameterName);
    }

    // The effect a drop performed, reduced to the one the source allowed: Move before Copy before Link, as the
    // desktops report a single effect. None when the target chose nothing the source allowed.
    internal static DesktopDragEffects Single(DesktopDragEffects performed, DesktopDragEffects allowed)
    {
        var effect = performed & allowed;
        if (effect.HasFlag(DesktopDragEffects.Move)) return DesktopDragEffects.Move;
        if (effect.HasFlag(DesktopDragEffects.Copy)) return DesktopDragEffects.Copy;
        if (effect.HasFlag(DesktopDragEffects.Link)) return DesktopDragEffects.Link;
        return DesktopDragEffects.None;
    }
}
