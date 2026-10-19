using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Runic.Platform.Windows;

// The registry keys the shell's associations live under: HKEY_CLASSES_ROOT, the user's classes over the machine's.
internal interface IWindowsClasses
{
    bool Exists(string key);
    string? Value(string key, string? name);
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryClasses : IWindowsClasses
{
    public bool Exists(string key)
    {
        try { using var opened = Registry.ClassesRoot.OpenSubKey(key); return opened is not null; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    public string? Value(string key, string? name)
    {
        try
        {
            using var opened = Registry.ClassesRoot.OpenSubKey(key);
            return opened?.GetValue(name) is string value && value.Length != 0 ? value : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

internal enum AssociationKind { Extension, Protocol, Class }

// What the shell associates applications with: a file name extension (".txt"), a URL protocol ("https") or a class
// (Directory).
internal readonly record struct Association(AssociationKind Kind, string Name);

// The content types of the Windows provider. A type's ID is its MIME type when the MIME database maps that type back
// to the same extension, so the ID stands for exactly one extension's associations; otherwise it is the extension.
// x-scheme-handler/<scheme> names a URL protocol and inode/directory file system folders, as on Linux.
internal static class WindowsContentTypes
{
    internal const string Directory = "inode/directory", Generic = "application/octet-stream", SchemePrefix = "x-scheme-handler/";
    private const string MimeDatabase = @"MIME\Database\Content Type\";
    private static readonly System.Buffers.SearchValues<char> NotInExtension = System.Buffers.SearchValues.Create(".\\/:*?\"<>| \t\r\n");

    // false: the ID is malformed. true with null: the ID is well formed but nothing on this system maps it to an
    // association, such as a MIME type without a registered extension.
    internal static bool TryResolve(string id, IWindowsClasses classes, out Association? association)
    {
        association = null;
        if (id.StartsWith('.'))
        {
            if (!IsExtension(id)) return false;
            association = new(AssociationKind.Extension, id.ToLowerInvariant());
            return true;
        }
        if (id.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string scheme = id[SchemePrefix.Length..];
            if (!IsScheme(scheme)) return false;
            association = new(AssociationKind.Protocol, scheme.ToLowerInvariant());
            return true;
        }
        if (string.Equals(id, Directory, StringComparison.OrdinalIgnoreCase))
        {
            association = new(AssociationKind.Class, "Directory");
            return true;
        }
        if (!IsMime(id)) return false;
        string? extension = MimeExtension(id, classes);
        if (extension is not null) association = new(AssociationKind.Extension, extension);
        return true;
    }

    // The type a name's extension identifies, or null when the registry does not know the extension.
    internal static DesktopContentType? FromExtension(string extension, IWindowsClasses classes)
    {
        if (!IsExtension(extension) || !classes.Exists(extension)) return null;
        extension = extension.ToLowerInvariant();
        string? mime = classes.Value(extension, "Content Type")?.Trim().ToLowerInvariant();
        if (mime is not null && !IsMime(mime)) mime = null;
        bool identifies = mime is not null && string.Equals(MimeExtension(mime, classes), extension, StringComparison.Ordinal);
        string id = identifies ? mime! : extension;
        bool text = string.Equals(classes.Value(extension, "PerceivedType"), "text", StringComparison.OrdinalIgnoreCase)
            || mime?.StartsWith("text/", StringComparison.Ordinal) == true;
        return new(id)
        {
            MimeType = mime,
            Parents = text && id != "text/plain" ? ["text/plain"] : [],
            Extensions = [extension[1..]],
        };
    }

    // A type known only by its MIME type, such as one detected from content.
    internal static DesktopContentType FromMime(string mime, IWindowsClasses classes)
    {
        mime = mime.ToLowerInvariant();
        string? extension = MimeExtension(mime, classes);
        return new(mime)
        {
            MimeType = mime,
            Parents = mime.StartsWith("text/", StringComparison.Ordinal) && mime != "text/plain" ? ["text/plain"] : [],
            Extensions = extension is null ? [] : [extension[1..]],
        };
    }

    internal static DesktopContentType Folder() => new(Directory);

    private static string? MimeExtension(string mime, IWindowsClasses classes)
    {
        string? extension = classes.Value(MimeDatabase + mime, "Extension")?.Trim();
        return extension is not null && IsExtension(extension) ? extension.ToLowerInvariant() : null;
    }

    internal static bool IsExtension(string value) =>
        value.Length is > 1 and <= 255 && value[0] == '.' && value.AsSpan(1).IndexOfAny(NotInExtension) < 0
        && !value.AsSpan(1).ContainsAnyInRange('\0', '\u001f');

    // RFC 3986: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    internal static bool IsScheme(string value)
    {
        if (value.Length is 0 or > 255 || !char.IsAsciiLetter(value[0])) return false;
        foreach (char c in value) if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        return true;
    }

    // RFC 6838 type "/" subtype, restricted-name characters.
    internal static bool IsMime(string value)
    {
        int slash = value.IndexOf('/');
        return slash > 0 && slash < value.Length - 1 && value.IndexOf('/', slash + 1) < 0
            && Restricted(value.AsSpan(0, slash)) && Restricted(value.AsSpan(slash + 1));
        static bool Restricted(ReadOnlySpan<char> name)
        {
            if (name.Length > 127 || !char.IsAsciiLetterOrDigit(name[0])) return false;
            foreach (char c in name) if (!char.IsAsciiLetterOrDigit(c) && c is not ('!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+')) return false;
            return true;
        }
    }

    // A DefaultIcon value: "path,index", with environment variables and optional quotes. "%1" means each file has its
    // own icon, which a type does not.
    internal static (string File, int Index)? ParseIconLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Contains("%1", StringComparison.Ordinal)) return null;
        string file = Environment.ExpandEnvironmentVariables(location.Trim());
        int index = 0;
        int comma = file.LastIndexOf(',');
        if (comma > 0 && int.TryParse(file.AsSpan(comma + 1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            index = parsed;
            file = file[..comma];
        }
        file = file.Trim().Trim('"').Trim();
        return file.Length == 0 || file.Contains('\0') ? null : (file, index);
    }
}
