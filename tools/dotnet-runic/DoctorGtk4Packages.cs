using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Runic.Desktop.Internal;

namespace Runic.Application.Tool;

/// <summary>The distribution packages that provide the GTK 4 profile's native libraries.</summary>
/// <param name="Distribution">The distribution family named in messages, such as Fedora.</param>
/// <param name="Gtk4">The package that provides libgtk-4.so.1.</param>
/// <param name="WebKit6">The package that provides libwebkitgtk-6.0.</param>
internal sealed record DoctorGtk4Packages(string Distribution, string Gtk4, string WebKit6)
{
    private static readonly (string Id, DoctorGtk4Packages Packages)[] Known =
    [
        ("debian", new("Debian/Ubuntu", "libgtk-4-1", "libwebkitgtk-6.0-4")),
        ("ubuntu", new("Debian/Ubuntu", "libgtk-4-1", "libwebkitgtk-6.0-4")),
        ("fedora", new("Fedora", "gtk4", "webkitgtk6.0")),
        ("arch", new("Arch Linux", "gtk4", "webkitgtk-6.0")),
        ("opensuse", new("openSUSE", "libgtk-4-1", "libwebkitgtk-6_0-4")),
        ("suse", new("openSUSE", "libgtk-4-1", "libwebkitgtk-6_0-4")),
        ("nixos", new("NixOS", "gtk4", "webkitgtk_6_0")),
    ];

    /// <summary>
    /// Reads ID, then ID_LIKE, from an os-release file and returns the matching packages, or null for an unknown or
    /// unreadable distribution. An ID such as opensuse-tumbleweed matches its family prefix.
    /// </summary>
    internal static DoctorGtk4Packages? FromOsRelease(string path)
    {
        Dictionary<string, string> values;
        try
        {
            if (!File.Exists(path)) return null;
            values = File.ReadLines(path)
                .Select(static line => line.Trim())
                .Where(static line => line.Length != 0 && !line.StartsWith('#') && line.Contains('='))
                .Select(static line => (Key: line[..line.IndexOf('=')], Value: line[(line.IndexOf('=') + 1)..].Trim('"', '\'')))
                .GroupBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        IEnumerable<string> ids = new[] { values.GetValueOrDefault("ID") ?? string.Empty }
            .Concat((values.GetValueOrDefault("ID_LIKE") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(static id => id.ToLowerInvariant());
        foreach (string id in ids)
        {
            foreach ((string known, DoctorGtk4Packages packages) in Known)
            {
                if (id == known || id.StartsWith(known + "-", StringComparison.Ordinal)) return packages;
            }
        }
        return null;
    }

    /// <summary>
    /// Names what to install for the missing libraries: the distribution's packages when known, otherwise the sonames.
    /// </summary>
    internal static string Remediation(DoctorGtk4Packages? packages, Version minimumGtk4, bool gtk4, bool webKit6)
    {
        var wanted = new List<string>();
        if (gtk4) wanted.Add($"GTK {minimumGtk4} or newer");
        if (webKit6) wanted.Add("WebKitGTK 6.0");
        string what = string.Join(" and ", wanted);
        if (packages is not null)
        {
            var names = new List<string>();
            if (gtk4) names.Add(packages.Gtk4);
            if (webKit6) names.Add(packages.WebKit6);
            return $"install {what} (on {packages.Distribution}: {string.Join(" and ", names)})";
        }
        var sonames = new List<string>();
        if (gtk4) sonames.Add(Gtk4NativeLibraries.Gtk);
        if (webKit6) sonames.Add(string.Join(" or ", Gtk4NativeLibraries.WebKit));
        return $"install {what} so the native loader finds {string.Join(" and ", sonames)}";
    }
}
