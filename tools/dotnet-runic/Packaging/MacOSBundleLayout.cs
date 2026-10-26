using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Runic.Application.Tool.Packaging;

/// <summary>What a file in the bundle is to <c>codesign</c>: not code, or a Mach-O executable, library or bundle.</summary>
internal enum MachOKind
{
    None,
    Executable,
    Library,
}

/// <summary>A Mach-O file inside the bundle other than its main executable, which is signed before the bundle.</summary>
internal sealed record NestedCode(string Path, MachOKind Kind);

/// <summary>
/// The bundle layout of the published files and the order they are signed in. Apple expects only code in
/// <c>Contents/MacOS</c>: Mach-O files go there, every other published file and directory to <c>Contents/Resources</c>
/// with a relative symbolic link in its place, so an application that reads a file next to its executable
/// (<see cref="AppContext.BaseDirectory"/>) still finds it. <c>codesign</c> seals a symbolic link as a resource.
/// The frontend (<c>www</c>) goes to <c>Contents/Resources</c> without a link, where Runic looks for it.
/// </summary>
internal static class MacOSBundleLayout
{
    /// <summary>
    /// Places the published directory in the bundle and returns the nested code, deepest first: the order
    /// <c>codesign</c> signs it in before the bundle, inside out.
    /// </summary>
    internal static IReadOnlyList<NestedCode> Place(string published, string app, string executable, string webRoot = "www")
    {
        string macOS = Path.Combine(app, "Contents", "MacOS");
        string resources = Path.Combine(app, "Contents", "Resources");
        Directory.CreateDirectory(macOS);
        Directory.CreateDirectory(resources);
        var nested = new List<NestedCode>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(published).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(entry);
            if (name == executable)
            {
                PackageTools.CopyFile(entry, Path.Combine(macOS, name));
                continue;
            }
            if (name == webRoot && Directory.Exists(entry))
            {
                // Runic finds the frontend in Contents/Resources/www (RunicWindowOptions.ResolveContentRoot).
                PackageTools.CopyDirectory(entry, Path.Combine(resources, name));
                continue;
            }
            PlaceEntry(entry, name, macOS, resources, nested);
        }
        return [.. nested.OrderByDescending(code => Depth(code.Path)).ThenBy(code => code.Path, StringComparer.Ordinal)];
    }

    private static void PlaceEntry(string source, string relative, string macOS, string resources, List<NestedCode> nested)
    {
        if (Directory.Exists(source))
        {
            if (!ContainsCode(source))
            {
                PackageTools.CopyDirectory(source, Path.Combine(resources, relative));
                Link(Path.Combine(macOS, relative), Path.Combine(resources, relative), directory: true);
                return;
            }
            // A directory with code keeps its code in Contents/MacOS; its other files go to Contents/Resources.
            Directory.CreateDirectory(Path.Combine(macOS, relative));
            foreach (string entry in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                PlaceEntry(entry, Path.Combine(relative, Path.GetFileName(entry)), macOS, resources, nested);
            return;
        }
        MachOKind kind = Classify(source);
        if (kind != MachOKind.None)
        {
            string destination = Path.Combine(macOS, relative);
            PackageTools.CopyFile(source, destination);
            nested.Add(new NestedCode(destination, kind));
            return;
        }
        string resource = Path.Combine(resources, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(resource)!);
        PackageTools.CopyFile(source, resource);
        Link(Path.Combine(macOS, relative), resource, directory: false);
    }

    private static void Link(string link, string target, bool directory)
    {
        string relative = Path.GetRelativePath(Path.GetDirectoryName(link)!, target);
        if (directory) Directory.CreateSymbolicLink(link, relative);
        else File.CreateSymbolicLink(link, relative);
    }

    private static bool ContainsCode(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(file => Classify(file) != MachOKind.None);

    private static int Depth(string path) => path.Count(character => character is '/' or '\\');

    /// <summary>Reads the Mach-O header: a thin file of either byte order, or the first architecture of a fat file.</summary>
    internal static MachOKind Classify(string path)
    {
        Span<byte> header = stackalloc byte[32];
        using FileStream stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 16) return MachOKind.None;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is 0xCAFEBABE or 0xCAFEBABF)
        {
            // A fat file: the big-endian architecture count, then each architecture's offset. Java class files share
            // the magic number; their version makes the count 45 or more.
            uint count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (count is 0 or >= 45) return MachOKind.None;
            uint offset = magic == 0xCAFEBABE
                ? BinaryPrimitives.ReadUInt32BigEndian(header[16..])
                : (uint)BinaryPrimitives.ReadUInt64BigEndian(header[16..]);
            if (offset == 0 || offset >= stream.Length) return MachOKind.None;
            stream.Position = offset;
            read = stream.ReadAtLeast(header, 16, throwOnEndOfStream: false);
            if (read < 16) return MachOKind.None;
            magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        }
        uint fileType;
        if (magic is 0xFEEDFACE or 0xFEEDFACF) fileType = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        else if (magic is 0xCEFAEDFE or 0xCFFAEDFE) fileType = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        else return MachOKind.None;
        // MH_EXECUTE is 2; MH_DYLIB 6, MH_BUNDLE 8 and the rest are loaded into a process.
        return fileType == 2 ? MachOKind.Executable : MachOKind.Library;
    }

    /// <summary>
    /// The codesign command lines, inside out: each nested library and executable with the hardened runtime, then the
    /// bundle with its entitlements. In the App Sandbox a nested executable inherits the application's sandbox
    /// (<paramref name="inheritEntitlements"/>); libraries take no entitlements.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> SigningCommands(
        string signingIdentity, string entitlements, string? inheritEntitlements, string app, IReadOnlyList<NestedCode> nested)
    {
        var commands = new List<IReadOnlyList<string>>();
        foreach (NestedCode code in nested)
        {
            string? codeEntitlements = code.Kind == MachOKind.Executable ? inheritEntitlements : null;
            commands.Add(Codesign(signingIdentity, codeEntitlements, code.Path));
        }
        commands.Add(Codesign(signingIdentity, entitlements, app));
        return commands;
    }

    /// <summary>
    /// One codesign command: the hardened runtime and the entitlements; a secure timestamp with a real identity, none ad
    /// hoc (<c>-</c>), which has no timestamp server.
    /// </summary>
    internal static IReadOnlyList<string> Codesign(string signingIdentity, string? entitlements, string path) =>
        ["--force", "--options", "runtime", .. signingIdentity == "-" ? Array.Empty<string>() : ["--timestamp"],
            .. entitlements is null ? Array.Empty<string>() : ["--entitlements", entitlements], "--sign", signingIdentity, path];

    /// <summary>The entitlements of a helper executable in a sandboxed bundle: it inherits the application's sandbox.</summary>
    internal const string InheritEntitlements = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.app-sandbox</key><true/>
  <key>com.apple.security.inherit</key><true/>
</dict>
</plist>

""";
}
