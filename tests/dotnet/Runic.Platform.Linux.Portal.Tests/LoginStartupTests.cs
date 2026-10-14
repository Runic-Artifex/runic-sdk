using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;
using Tmds.DBus.Protocol;

internal sealed class LoginStartupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Test, DisplayName("login startup: an autostart entry that respects the user's choice and others' entries")]
    public async Task AutostartEntryAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-autostart-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var startup = new LinuxLoginStartup(new PortalApplication("org.example.App"), directory, sandboxed: false);
            string path = Path.Combine(directory, "org.example.App.desktop");
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: false }, "off until enabled");
            Check(!Directory.Exists(directory), "nothing is written before EnableAsync");

            Check(await startup.EnableAsync(new LoginStartupRequest(["--background", "two words", "50%", "$HOME"]) { Reason = "Check for updates" })
                is PlatformResult<PlatformUnit>.Success, "enable");
            string entry = File.ReadAllText(path);
            Check(entry.StartsWith("[Desktop Entry]\nType=Application\n", StringComparison.Ordinal) && entry.Contains("Comment=Check for updates\n", StringComparison.Ordinal)
                && entry.Contains("X-Runic-Application=org.example.App\n", StringComparison.Ordinal), $"entry content: {entry}");
            Check(entry.Contains(" --background \"two words\" \"50%%\" \"\\\\$HOME\"\n", StringComparison.Ordinal), $"Exec quoting: {entry}");
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: true }, "on after enabling");
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Success, "enabling again replaces the entry");
            Check(Directory.GetFiles(directory).Length == 1, "no temporary file is left");

            // The user turned it off in the desktop's startup settings.
            File.WriteAllText(path, File.ReadAllText(path) + "Hidden=true\n");
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: false }, "a hidden entry is off");
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
                "the user's choice is not overridden");
            Check(await startup.DisableAsync() is PlatformResult<PlatformUnit>.Success && !File.Exists(path), "disable removes the entry");

            File.WriteAllText(path, "[Desktop Entry]\nType=Application\nExec=other\n");
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }
                && File.ReadAllText(path).Contains("Exec=other", StringComparison.Ordinal), "another entry is left alone");
            Check(await startup.DisableAsync() is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied } && File.Exists(path),
                "another entry is not removed");

            var unnamed = new LinuxLoginStartup(new PortalApplication(), directory, sandboxed: false);
            Check(await unnamed.IsEnabledAsync() is PlatformResult<bool>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }, "no ID, no entry");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Test, NotInParallel("RUNIC_APPLICATION_EXECUTABLE"), DisplayName("login startup: the entry starts the wrapper that RUNIC_APPLICATION_EXECUTABLE names")]
    public async Task WrapperEntryAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-autostart-" + Guid.NewGuid().ToString("N")[..8]);
        string wrapper = Path.Combine(directory, "bin", "my app");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
            File.WriteAllText(wrapper, "");
            var startup = new LinuxLoginStartup(new PortalApplication("org.example.Wrapped"), directory, sandboxed: false);
            string path = Path.Combine(directory, "org.example.Wrapped.desktop");

            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_EXECUTABLE", wrapper);
            Check(await startup.EnableAsync(new LoginStartupRequest(["--tray"])) is PlatformResult<PlatformUnit>.Success, "enable");
            string entry = File.ReadAllText(path);
            Check(entry.Contains($"Exec=\"{wrapper}\" --tray\n", StringComparison.Ordinal), $"the wrapper starts: {entry}");

            // A relative or missing path is not a wrapper of this process.
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_EXECUTABLE", Path.Combine(directory, "missing"));
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Success, "enable again");
            entry = File.ReadAllText(path);
            Check(!entry.Contains(directory + "/missing", StringComparison.Ordinal) && entry.Contains(Environment.ProcessPath!, StringComparison.Ordinal),
                $"the process starts instead: {entry}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_EXECUTABLE", null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test, Explicit, Category("DBus"), DisplayName("login startup: the Background portal in a sandbox, granted and refused")]
    public async Task BackgroundPortalAsync()
    {
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        var portal = new BackgroundPortal(service);
        service.AddMethodHandler(portal);
        var startup = new LinuxLoginStartup(new PortalApplication(), sandboxed: true, destination: service.UniqueName!);

        Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Unavailable, "the portal cannot report autostart");
        Check(await startup.EnableAsync(new LoginStartupRequest(["--background"]) { Reason = "Updates" }).AsTask().WaitAsync(Wait)
            is PlatformResult<PlatformUnit>.Success, "granted");
        Check(portal.Autostart == true && portal.Reason == "Updates" && portal.Commandline?[^1] == "--background", "autostart, reason and command line sent");
        Check(await startup.DisableAsync().AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success && portal.Autostart == false, "disable asks without autostart");

        portal.Code = 1;
        Check(await startup.EnableAsync(new LoginStartupRequest([])).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "a refused request is denied");
        portal.Code = 0;
        portal.GrantAutostart = false;
        Check(await startup.EnableAsync(new LoginStartupRequest([])).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "background without autostart is denied");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    // org.freedesktop.portal.Background.RequestBackground, answering through Request.Response.
    private sealed class BackgroundPortal(DBusConnection connection) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public uint Code;
        public bool GrantAutostart = true;
        public bool? Autostart;
        public string? Reason;
        public string[]? Commandline;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString != "RequestBackground") { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            var reader = context.Request.GetBodyReader();
            _ = reader.ReadString();
            var options = reader.ReadDictionaryOfStringToVariantValue();
            Autostart = options["autostart"].GetBool();
            Reason = options.TryGetValue("reason", out var reason) ? reason.GetString() : null;
            Commandline = options.TryGetValue("commandline", out var commandline) ? commandline.GetArray<string>() : null;
            string handle = Path + "/request/" + context.Request.SenderAsString![1..].Replace('.', '_') + "/" + options["handle_token"].GetString();
            var reply = context.CreateReplyWriter("o");
            try { reply.WriteObjectPath(new ObjectPath(handle)); context.Reply(reply.CreateMessage()); }
            finally { reply.Dispose(); }
            var signal = connection.GetMessageWriter();
            try
            {
                signal.WriteSignalHeader(path: handle, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                signal.WriteUInt32(Code);
                var results = signal.WriteDictionaryStart();
                signal.WriteDictionaryEntryStart(); signal.WriteString("background"); signal.WriteVariantBool(true);
                signal.WriteDictionaryEntryStart(); signal.WriteString("autostart"); signal.WriteVariantBool(Autostart == true && GrantAutostart);
                signal.WriteDictionaryEnd(results);
                connection.TrySendMessage(signal.CreateMessage());
            }
            finally { signal.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}
