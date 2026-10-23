using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// <c>package appimage</c>: an AppDir (the publish folder in <c>usr/lib/&lt;id&gt;</c>, <c>AppRun</c>, the desktop entry
/// and icon at its root, the metainfo) made into an AppImage with <c>appimagetool</c> from PATH and a local AppImage
/// runtime, so nothing is downloaded while packaging. GTK and WebKitGTK come from the host (D039.15). Unsigned.
/// </summary>
internal static partial class AppImagePackager
{
    /// <summary>The environment variable that names the AppImage runtime file (type2-runtime's <c>runtime-&lt;arch&gt;</c>).</summary>
    internal const string RuntimeVariable = "RUNIC_APPIMAGE_RUNTIME";

    /// <summary>The <c>AppRun</c> script, which starts the executable with the AppImage's arguments.</summary>
    internal static string AppRun(PackageIdentity identity) => $"""
#!/bin/sh
# Starts {identity.Executable} from the AppImage; GTK and WebKitGTK come from the host system.
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/lib/{identity.Id}/{identity.Executable}" "$@"

""";

    /// <summary>The AppImage's file name: <c>&lt;Name&gt;-&lt;version&gt;-&lt;arch&gt;.AppImage</c>, with no spaces.</summary>
    internal static string FileName(PackageIdentity identity, string architecture) =>
        $"{NonFileNameCharacters().Replace(identity.Name, "_")}-{identity.Version}-{architecture}.AppImage";

    /// <summary>
    /// The runtime file: <c>RUNIC_APPIMAGE_RUNTIME</c>, otherwise <c>runtime-&lt;arch&gt;</c> beside appimagetool.
    /// </summary>
    internal static string? FindRuntime(string appimagetool, string architecture)
    {
        if (Environment.GetEnvironmentVariable(RuntimeVariable) is { Length: > 0 } configured)
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appimagetool))!, $"runtime-{architecture}");
        return File.Exists(beside) ? beside : null;
    }

    internal static async Task RunAsync(PackageContext context, CancellationToken cancellationToken)
    {
        PackageIdentity identity = context.Identity;
        (_, string architecture) = LinuxPackaging.Architecture(context.Runtime);
        string appimagetool = PackageTools.Require("appimagetool", "appimagetool from https://github.com/AppImage/appimagetool/releases");
        string runtime = FindRuntime(appimagetool, architecture) ?? throw PackageTools.Missing($"runtime-{architecture}",
            $"the AppImage runtime from https://github.com/AppImage/type2-runtime/releases beside appimagetool, or name it with {RuntimeVariable}");
        string appDir = Path.Combine(context.Output, identity.Id + ".AppDir");
        string entry = await LinuxPackaging.StageAsync(context, PackageTarget.AppImage, appDir, cancellationToken).ConfigureAwait(false);
        await LinuxPackaging.ValidateDesktopEntryAsync(context, entry, required: false, cancellationToken).ConfigureAwait(false);
        // appimagetool validates the metainfo under the name AppImage tools look for, <id>.appdata.xml.
        string metainfo = Path.Combine(appDir, "usr", "share", "metainfo", identity.Id);
        File.Move(metainfo + ".metainfo.xml", metainfo + ".appdata.xml", overwrite: true);
        // appimagetool reads the desktop entry and its icon at the AppDir's root; .DirIcon is the AppImage's icon.
        File.Copy(entry, Path.Combine(appDir, identity.Id + ".desktop"), overwrite: true);
        string icon = Path.Combine(appDir, "usr", "share", "icons", "hicolor", "512x512", "apps", identity.Id + ".png");
        File.Copy(icon, Path.Combine(appDir, identity.Id + ".png"), overwrite: true);
        File.Copy(icon, Path.Combine(appDir, ".DirIcon"), overwrite: true);
        string appRun = LinuxPackaging.Write(Path.Combine(appDir, "AppRun"), AppRun(identity));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(appRun, File.GetUnixFileMode(appRun) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        LinuxPackaging.NormalizeModes(appDir);
        string output = Path.Combine(context.Output, FileName(identity, architecture));
        PackageTools.Delete(output);
        await PackageTools.RunAsync(appimagetool, ["--runtime-file", runtime, appDir, output], context.Output, cancellationToken,
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["ARCH"] = architecture }).ConfigureAwait(false);
        PackageTools.Delete(appDir);
        context.Warn("The AppImage is unsigned (appimagetool --sign signs it) and uses the host's GTK and WebKitGTK, which the target system must provide.");
        context.Add(output, "appimage", "none");
    }

    [GeneratedRegex("[^A-Za-z0-9._+-]")]
    private static partial Regex NonFileNameCharacters();
}
