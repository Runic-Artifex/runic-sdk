using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace Runic.Platform.Linux.Portal;

// Registration outside a sandbox: the desktop entry $XDG_DATA_HOME/applications/<id>.desktop with Exec="<exe>" %U, the
// MIME types of its schemes (x-scheme-handler/<scheme>) and file types, and X-Runic-Registered=true; for a file type
// the shared MIME-info database does not know with its extension, a definition in $XDG_DATA_HOME/mime/packages/<id>.xml.
// update-mime-database and update-desktop-database refresh the caches when they are installed; GIO also watches the
// directories. An entry of the same ID anywhere in XDG_DATA_DIRS, or one without the marker, belongs to the package:
// a conflict, and nothing is written. Inside Flatpak or Snap the manifest exports the entry instead.
internal sealed class LinuxOpenRegistration(PortalApplication application, string applicationId, DesktopApplicationsOptions? options = null,
    Func<string, IReadOnlyList<string>, bool>? runTool = null) : IDesktopOpenRegistration
{
    internal const string Marker = "X-Runic-Registered";
    private const string MimeNamespace = "http://www.freedesktop.org/standards/shared-mime-info";
    private readonly DesktopApplicationsOptions _options = options ?? DesktopApplicationsOptions.FromProcess();

    private string EntryPath => Path.Combine(_options.DataHome, "applications", applicationId + ".desktop");
    private string MimePath => Path.Combine(_options.DataHome, "mime", "packages", applicationId + ".xml");

    public ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Sandboxed) return Result(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        return Run(() =>
        {
            if (DesktopKeyFile.Load(EntryPath) is not { } file || file.String(DesktopEntry.Group, Marker) != "true"
                || file.String(DesktopEntry.Group, "Exec") != DesktopKeyFile.Unescape(Exec(registration)) || Shadowed())
                return new PlatformResult<bool>.Success(false);
            var listed = file.List(DesktopEntry.Group, "MimeType").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mime = SharedMimeDatabase.Load(_options.DataSearch);
            foreach (var type in registration.UrlSchemes.Select(static scheme => "x-scheme-handler/" + scheme.ToLowerInvariant())
                .Concat(registration.FileTypes.Select(type => Resolve(type, mime).Type ?? "")))
                if (!listed.Contains(type)) return new PlatformResult<bool>.Success(false);
            return new PlatformResult<bool>.Success(true);
        });
    }

    public ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.UrlSchemes.IsEmpty && registration.FileTypes.IsEmpty)
            throw new ArgumentException("Supply at least one URL scheme or file type.", nameof(registration));
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Sandboxed) return Result(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        return Run<PlatformUnit>(() =>
        {
            if (Conflict() is { } owner)
            {
                application.Diagnose("open-registration-foreign-entry", $"{owner} already registers this application ID; nothing was registered.",
                    "The application's package registers it: open URLs and files through that entry, or remove it to register at run time.");
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict,
                    new PlatformDiagnostic("Runic.OpenRegistration", 0, $"{owner} belongs to the application's package or another program."));
            }
            var mime = SharedMimeDatabase.Load(_options.DataSearch);
            var types = new List<string>();
            var definitions = new List<(DesktopFileType Type, string MimeType)>();
            foreach (var type in registration.FileTypes)
            {
                var (resolved, define) = Resolve(type, mime);
                if (resolved is null)
                    return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData, new PlatformDiagnostic("Runic.OpenRegistration", 0,
                        $"The shared MIME-info database has no type for {type.Extension}; set the file type's MimeType."));
                if (!types.Contains(resolved, StringComparer.OrdinalIgnoreCase)) types.Add(resolved);
                if (define) definitions.Add((type, resolved));
            }
            var mimeTypes = registration.UrlSchemes.Select(static scheme => "x-scheme-handler/" + scheme.ToLowerInvariant()).Concat(types).ToList();
            if (definitions.Count > 0) Write(MimePath, MimeXml(definitions));
            else if (File.Exists(MimePath)) File.Delete(MimePath);
            Write(EntryPath, Entry(registration, mimeTypes));
            Refresh(definitions.Count > 0 || File.Exists(MimePath));
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        });
    }

    public ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Sandboxed) return Result(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        return Run(() =>
        {
            bool removed = false, mimeRemoved = false;
            if (DesktopKeyFile.Load(EntryPath) is { } file && file.String(DesktopEntry.Group, Marker) == "true") { File.Delete(EntryPath); removed = true; }
            if (File.Exists(MimePath) && File.ReadAllText(MimePath).Contains(Marker, StringComparison.Ordinal)) { File.Delete(MimePath); removed = mimeRemoved = true; }
            if (removed) Refresh(mimeRemoved);
            return new PlatformResult<bool>.Success(removed);
        });
    }

    // The path of an entry of this ID that is not this provider's: in XDG_DATA_DIRS, or in XDG_DATA_HOME without the
    // marker. A MIME definition file of the same name without the marker counts too.
    private string? Conflict()
    {
        if (File.Exists(EntryPath) && DesktopKeyFile.Load(EntryPath)?.String(DesktopEntry.Group, Marker) != "true") return EntryPath;
        foreach (string directory in _options.DataDirectories)
        {
            string path = Path.Combine(directory, "applications", applicationId + ".desktop");
            if (File.Exists(path)) return path;
        }
        if (File.Exists(MimePath) && !File.ReadAllText(MimePath).Contains(Marker, StringComparison.Ordinal)) return MimePath;
        return null;
    }

    private bool Shadowed() => _options.DataDirectories.Any(directory => File.Exists(Path.Combine(directory, "applications", applicationId + ".desktop")));

    // The file type's MIME type, and whether it needs a definition: a type the database does not give its extension.
    private static (string? Type, bool Define) Resolve(DesktopFileType type, SharedMimeDatabase mime)
    {
        var known = mime.MatchName("file" + type.Extension);
        if (type.MimeType is { } declared)
            return (declared, !known.Contains(declared, StringComparer.OrdinalIgnoreCase) && !known.Select(mime.Unalias).Contains(mime.Unalias(declared), StringComparer.OrdinalIgnoreCase));
        return (known.IsEmpty ? null : known[0], false);
    }

    private static string Entry(DesktopOpenRegistration registration, List<string> mimeTypes)
    {
        var text = new StringBuilder("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Name=").Append(Value(registration.DisplayName)).Append('\n');
        text.Append("Exec=").Append(Exec(registration)).Append('\n');
        text.Append("Terminal=false\n");
        text.Append("MimeType=").Append(string.Concat(mimeTypes.Select(static type => type + ";"))).Append('\n');
        text.Append(Marker).Append("=true\n");
        return text.ToString();
    }

    // "<exe>" %U, or "dotnet" "<app.dll>" %U for a framework-dependent application, quoted by the Exec rules.
    internal static string Exec(DesktopOpenRegistration registration)
    {
        var command = new List<string>();
        if (registration.Executable is { } executable) command.Add(executable);
        else
        {
            string process = PackagedExecutable.Resolve(PackagedExecutable.Wrapper, Environment.ProcessPath)
                ?? throw new InvalidOperationException("The process path is unknown; set Executable.");
            command.Add(process);
            if (Path.GetFileNameWithoutExtension(process) == "dotnet" && Environment.GetCommandLineArgs() is [{ } assembly, ..]
                && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                command.Add(Path.GetFullPath(assembly));
        }
        return string.Join(' ', command.Select(LinuxLoginStartup.Quote)) + " %U";
    }

    private string MimeXml(List<(DesktopFileType Type, string MimeType)> definitions)
    {
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        text.Append("<mime-info xmlns=\"").Append(MimeNamespace).Append("\">\n");
        text.Append("  <!-- ").Append(Marker).Append(": ").Append(Xml(applicationId)).Append(" -->\n");
        foreach (var group in definitions.GroupBy(static definition => definition.MimeType, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("  <mime-type type=\"").Append(Xml(group.Key)).Append("\">\n");
            if (group.Select(static definition => definition.Type.Description).FirstOrDefault(static description => description is not null) is { } comment)
                text.Append("    <comment>").Append(Xml(comment)).Append("</comment>\n");
            if (group.Select(static definition => definition.Type.IconPath).FirstOrDefault(static icon => icon is not null && !icon.Contains('/')) is { } icon)
                text.Append("    <icon name=\"").Append(Xml(icon)).Append("\"/>\n");
            foreach (var definition in group)
                text.Append("    <glob pattern=\"*").Append(Xml(definition.Type.Extension.ToLowerInvariant())).Append("\"/>\n");
            text.Append("  </mime-type>\n");
        }
        return text.Append("</mime-info>\n").ToString();
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".runic-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    // The caches, where their tools are installed; a missing tool or a failure leaves GIO's own directory watch.
    private void Refresh(bool mime)
    {
        if (mime) RunTool("update-mime-database", [Path.Combine(_options.DataHome, "mime")]);
        RunTool("update-desktop-database", [Path.Combine(_options.DataHome, "applications")]);
    }

    private void RunTool(string name, IReadOnlyList<string> arguments)
    {
        if (runTool is not null) { runTool(name, arguments); return; }
        if (DesktopEntry.FindProgram(name, _options.SearchPath) is not { } program) return;
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30))) process.Kill();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { /* The cache stays as it was. */ }
    }

    private static string Value(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);

    private static ValueTask<PlatformResult<T>> Run<T>(Func<PlatformResult<T>> action)
    {
        try { return ValueTask.FromResult(action()); }
        catch (UnauthorizedAccessException) { return Result<T>(new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied)); }
        catch (IOException) { return Result<T>(new PlatformResult<T>.Failed(PlatformFailureCode.IOError)); }
    }
}
