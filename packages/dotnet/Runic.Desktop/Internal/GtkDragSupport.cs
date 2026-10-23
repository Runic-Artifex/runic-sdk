using System.Text;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The toolkit-independent parts of drag and drop on GTK 3 and GTK 4 (B11): GdkDragAction values, the dragged data as
// GTK offers it, and which frames' drop reports count. Pure, so every OS tests it. Linked into Runic.Desktop.Gtk4.
internal static class GtkDragSupport
{
    private const DesktopDragEffects AllEffects = DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link;

    // GDK_BUTTON1_MASK, the same in GTK 3 and GTK 4.
    internal const uint Button1Mask = 1 << 8;

    // GTK 3's GdkDragAction numbers COPY, MOVE and LINK 2, 4 and 8, after DEFAULT; GTK 4's numbers them 1, 2 and 4, as
    // DesktopDragEffects does. Other actions (DEFAULT, PRIVATE, ASK) have no effect.
    internal static int ToGtk3Actions(DesktopDragEffects effects) => (int)(effects & AllEffects) << 1;

    internal static DesktopDragEffects FromGtk3Actions(int actions) => (DesktopDragEffects)((actions >> 1) & (int)AllEffects);

    internal static int ToGtk4Actions(DesktopDragEffects effects) => (int)(effects & AllEffects);

    internal static DesktopDragEffects FromGtk4Actions(int actions) => (DesktopDragEffects)(actions & (int)AllEffects);

    // The effect of a drop onto the window from the action GTK 3 reports, which the target (WebKit, from the page's
    // dropEffect) chose: Move before Copy before Link, Copy when none.
    internal static DesktopDragEffects DropEffect(DesktopDragEffects actions)
    {
        var effect = DesktopDragRules.Single(actions, AllEffects);
        return effect == DesktopDragEffects.None ? DesktopDragEffects.Copy : effect;
    }

    // GTK 4 reports the actions a drop offers, not the one WebKit chose: the only one, or Copy when the drop offers it
    // among others, as WebKit's default; the page's report then chooses among them (FileDropPairing).
    internal static DesktopDragEffects OfferedDropEffect(DesktopDragEffects offered)
    {
        offered &= AllEffects;
        if (offered is DesktopDragEffects.Copy or DesktopDragEffects.Move or DesktopDragEffects.Link) return offered;
        return offered.HasFlag(DesktopDragEffects.Copy) ? DesktopDragEffects.Copy : DropEffect(offered);
    }

    // The text/uri-list of a drag out: the paths as file URIs, then the URIs.
    internal static List<string> UriList(DesktopDragData data)
    {
        List<string> uris = [.. FileUris(data)];
        if (!data.Uris.IsDefault) uris.AddRange(data.Uris.Select(static uri => uri.AbsoluteUri));
        return uris;
    }

    // The paths of a drag out as file URIs, which the FileTransfer portal targets carry.
    internal static List<string> FileUris(DesktopDragData data) =>
        data.Paths.IsDefault ? [] : [.. data.Paths.Select(FileUri)];

    // The plain text of a drag out: the text, or the URIs one per line; null for a drag of paths alone.
    internal static string? PlainText(DesktopDragData data) =>
        data.Text ?? (data.Uris.IsDefaultOrEmpty ? null : string.Join('\n', data.Uris.Select(static uri => uri.AbsoluteUri)));

    // An absolute path as a file URI, escaped as g_filename_to_uri does: every byte of its UTF-8 except unreserved
    // characters and '/'.
    internal static string FileUri(string path)
    {
        var builder = new StringBuilder("file://", path.Length + 16);
        foreach (var value in Encoding.UTF8.GetBytes(path))
        {
            if (value is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/')
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    // Whether a drop report counts (D037.2): the top-level document's (no origin) always, a subframe's only when its
    // origin is the presented origin. The isolated-world script reports the origin, which the page cannot reach.
    internal static bool AcceptsReport(Uri? presented, string? frameOrigin) =>
        frameOrigin is null ||
        (presented is not null && Uri.TryCreate(frameOrigin, UriKind.Absolute, out var candidate) &&
         Uri.Compare(presented, candidate, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0);
}
