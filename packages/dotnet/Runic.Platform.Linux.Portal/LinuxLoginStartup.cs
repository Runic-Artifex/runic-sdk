using System.Text;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// Login startup on Linux. Outside a sandbox: an XDG autostart entry, which every desktop reads at login. Inside
// Flatpak or Snap, where the entry is out of reach: the Background portal, which asks the user.
internal sealed class LinuxLoginStartup(PortalApplication application, string? autostartDirectory = null, bool? sandboxed = null,
    string? address = null, string destination = "org.freedesktop.portal.Desktop") : IDesktopLoginStartup
{
    // Marks entries this provider wrote; it never changes or removes anyone else's.
    private const string Marker = "X-Runic-Application";
    private static readonly TimeSpan PortalTimeout = TimeSpan.FromMinutes(2);
    private bool Sandboxed => sandboxed ?? (File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null);

    public ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The portal can set autostart but not report it.
        if (Sandboxed) return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        if (EntryPath() is not { } path) return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        try
        {
            var entry = Read(path);
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Success(entry is not null && entry.Enabled));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Failed(PlatformFailureCode.IoError));
        }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.IsDefault || request.Arguments.Length > 64 || request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Supply at most 64 arguments without NUL characters.", nameof(request));
        if (request.Reason is { Length: > 256 }) throw new ArgumentException("The reason is at most 256 characters.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        List<string> command;
        try { command = Command(request.Arguments); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        if (Sandboxed) return await RequestAsync(true, request, command, cancellationToken).ConfigureAwait(false);
        if (EntryPath() is not { } path) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        try
        {
            var existing = Read(path);
            if (existing is { Ours: false })
            {
                application.Diagnose("login-startup-foreign-entry", "Another autostart entry uses this application's name.", $"Remove {path} or start the application from it.");
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy);
            }
            // The user turned it off in the desktop's startup settings; only the user turns it on again.
            if (existing is { Ours: true, Enabled: false })
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".runic-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, Entry(command, request.Reason), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError);
        }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed) return await RequestAsync(false, null, [], cancellationToken).ConfigureAwait(false);
        if (EntryPath() is not { } path) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        try
        {
            var existing = Read(path);
            if (existing is { Ours: false }) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
            File.Delete(path);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError);
        }
    }

    private string? EntryPath()
    {
        if (application.ApplicationId is not { } id)
        {
            application.Diagnose("login-startup-identity-unspecified", "No desktop application ID was supplied for login startup.",
                "Supply the installed reverse-DNS desktop ID; the autostart entry is named after it.");
            return null;
        }
        string? directory = autostartDirectory;
        if (directory is null)
        {
            string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            directory = Path.Combine(config, "autostart");
        }
        return Path.Combine(directory, id + ".desktop");
    }

    private sealed record Existing(bool Ours, bool Enabled);

    private static Existing? Read(string path)
    {
        if (!File.Exists(path)) return null;
        bool ours = false, hidden = false, disabled = false, entry = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('[')) { entry = line == "[Desktop Entry]"; continue; }
            if (!entry) continue;
            ours |= line.StartsWith(Marker + "=", StringComparison.Ordinal);
            hidden |= line.Equals("Hidden=true", StringComparison.Ordinal);
            disabled |= line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.Ordinal);
        }
        return new Existing(ours, !hidden && !disabled);
    }

    // This executable; a framework-dependent application runs as `dotnet App.dll`. A package's wrapper that names
    // itself (PackagedExecutable) starts instead of the program behind it.
    private static List<string> Command(IEnumerable<string> arguments)
    {
        string process = PackagedExecutable.Resolve(PackagedExecutable.Wrapper, Environment.ProcessPath)
            ?? throw new NativeBackendUnavailableException();
        var command = new List<string> { process };
        if (Path.GetFileNameWithoutExtension(process) == "dotnet" && Environment.GetCommandLineArgs() is [{ } assembly, ..]
            && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            command.Add(Path.GetFullPath(assembly));
        command.AddRange(arguments);
        return command;
    }

    private string Entry(IReadOnlyList<string> command, string? reason)
    {
        var text = new StringBuilder();
        text.Append("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Name=").Append(Value(application.ApplicationId!)).Append('\n');
        if (reason is not null) text.Append("Comment=").Append(Value(reason)).Append('\n');
        text.Append("Exec=").Append(string.Join(' ', command.Select(Quote))).Append('\n');
        text.Append("X-GNOME-Autostart-enabled=true\n");
        text.Append(Marker).Append('=').Append(Value(application.ApplicationId!)).Append('\n');
        return text.ToString();
    }

    // Desktop Entry values escape backslashes and line breaks.
    private static string Value(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    // An Exec argument: quoted when it needs to be, with `"`, `` ` ``, `$` and `\` escaped inside the quotes, and `%`
    // doubled so it is not a field code. Then escaped once more as a value.
    internal static string Quote(string argument)
    {
        string text = argument.Replace("%", "%%", StringComparison.Ordinal);
        if (text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+=:,@".Contains(c)))
            return Value(text);
        var quoted = new StringBuilder("\"");
        foreach (char c in text)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        return Value(quoted.Append('"').ToString());
    }

    private async ValueTask<PlatformResult<PlatformUnit>> RequestAsync(bool autostart, LoginStartupRequest? request, IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        PortalConnection? session = null;
        try
        {
            session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.OwnerChanged);
            deadline.CancelAfter(PortalTimeout);
            var connection = session.Connection;
            var prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName!.TrimStart(':').Replace('.', '_') + "/";
            var responses = Channel.CreateUnbounded<(string Path, uint Code, bool Autostart)>();
            // Subscribe before asking: the portal may answer before the call returns.
            using var subscription = await connection.AddMatchAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = session.Destination, PathNamespace = prefix.TrimEnd('/'),
                Interface = "org.freedesktop.portal.Request", Member = "Response",
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (message.PathAsString!, code, results.TryGetValue("autostart", out var value) && value.Type == VariantValueType.Bool && value.GetBool());
            }, value =>
            {
                if (value.HasValue) responses.Writer.TryWrite(value.Value);
                else if (value.IsCompletion) responses.Writer.TryComplete(value.Exception);
            }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String("runic_" + Guid.NewGuid().ToString("N")),
                ["autostart"] = VariantValue.Bool(autostart),
            };
            if (request?.Reason is { } reason) options["reason"] = VariantValue.String(reason);
            if (autostart) options["commandline"] = VariantValue.Array(command.ToArray());
            string handle = (await new Protocol.Background(connection, session.Destination, "/org/freedesktop/portal/desktop")
                .RequestBackgroundAsync("", options).WaitAsync(deadline.Token).ConfigureAwait(false)).ToString();
            if (!handle.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Invalid background request handle.");
            while (true)
            {
                var response = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (response.Path != handle) continue;
                if (response.Code != 0)
                    return new PlatformResult<PlatformUnit>.Failed(response.Code == 1 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError,
                        PortalErrors.Response(response.Code));
                // Granted background activity can still leave autostart off, for example by the user's choice.
                if (response.Autostart != autostart) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.portal.Error.NotAllowed" or "org.freedesktop.DBus.Error.AccessDenied")
        { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, PortalErrors.Reply(error)); }
        catch (Exception error) when (error is DBusExceptionBase or IOException or NativeBackendUnavailableException)
        {
            application.Diagnose("login-startup-portal-unavailable", error is DBusErrorReplyException reply ? reply.ErrorName : error.GetType().Name,
                "Check that the desktop's portal implements org.freedesktop.portal.Background.");
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { session?.Dispose(); }
    }
}
