using System.Collections.Immutable;
using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;

// Run-time registration in a temporary XDG tree (DesktopApplicationsFixture), never the user's directories, and D-Bus
// activation of a running instance on the test session bus (dbus-run-session in CI).
internal sealed class ActivationTests
{
    private const string Id = "org.example.Notes";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test, DisplayName("open registration: a marked desktop entry and MIME definitions in XDG_DATA_HOME, refreshed, replaced and removed")]
    public async Task RegistersADesktopEntryAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        DesktopApplicationsFixture.Write(Path.Combine(fixture.Share, "mime", "globs2"), "50:text/plain:*.txt\n50:text/markdown:*.md\n");
        var tools = new List<string>();
        var diagnostics = new List<PortalDiagnostic>();
        var registration = new LinuxOpenRegistration(new PortalApplication(Id, diagnostics.Add), Id, fixture.Options(),
            (tool, arguments) => { tools.Add(tool + " " + string.Join(' ', arguments)); return true; });
        var notes = new DesktopOpenRegistration("Notes & Co")
        {
            UrlSchemes = ["org.example.notes"],
            FileTypes = [new(".notes") { MimeType = "application/x-example-notes", Description = "Notes document", IconPath = "org.example.Notes" },
                new(".TXT"), new(".md") { MimeType = "text/markdown" }],
            Executable = "/opt/Notes App/notes",
        };
        Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Success { Value: false }, "nothing before RegisterAsync");
        Check(!Directory.Exists(Path.Combine(fixture.DataHome, "applications")), "nothing is written before RegisterAsync");
        Check(await registration.RegisterAsync(notes) is PlatformResult<PlatformUnit>.Success, "register");
        string entry = File.ReadAllText(Path.Combine(fixture.DataHome, "applications", Id + ".desktop"));
        Check(entry == "[Desktop Entry]\nType=Application\nName=Notes & Co\nExec=\"/opt/Notes App/notes\" %U\nTerminal=false\n"
            + "MimeType=x-scheme-handler/org.example.notes;application/x-example-notes;text/plain;text/markdown;\nX-Runic-Registered=true\n", entry);
        string xml = File.ReadAllText(Path.Combine(fixture.DataHome, "mime", "packages", Id + ".xml"));
        Check(xml.Contains("<mime-type type=\"application/x-example-notes\">", StringComparison.Ordinal) && xml.Contains("<glob pattern=\"*.notes\"/>", StringComparison.Ordinal)
            && xml.Contains("<comment>Notes document</comment>", StringComparison.Ordinal) && xml.Contains("<icon name=\"org.example.Notes\"/>", StringComparison.Ordinal)
            && !xml.Contains("text/markdown", StringComparison.Ordinal) && !xml.Contains("text/plain", StringComparison.Ordinal), "only the unknown type is defined: " + xml);
        Check(tools.SequenceEqual([$"update-mime-database {Path.Combine(fixture.DataHome, "mime")}", $"update-desktop-database {Path.Combine(fixture.DataHome, "applications")}"]),
            "the caches are refreshed: " + string.Join(" | ", tools));
        Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Success { Value: true }, "registered");
        Check(await registration.IsRegisteredAsync(notes with { Executable = "/opt/other" }) is PlatformResult<bool>.Success { Value: false }, "another executable");
        Check(await registration.IsRegisteredAsync(notes with { UrlSchemes = ["other"] }) is PlatformResult<bool>.Success { Value: false }, "another scheme");

        // A registration replaces the earlier one; known types need no definition file.
        var known = new DesktopOpenRegistration("Notes") { FileTypes = [new(".txt")], Executable = "/opt/notes" };
        Check(await registration.RegisterAsync(known) is PlatformResult<PlatformUnit>.Success, "register again");
        Check(!File.Exists(Path.Combine(fixture.DataHome, "mime", "packages", Id + ".xml")), "the definition file goes with the type");
        Check(await registration.RegisterAsync(new DesktopOpenRegistration("Notes") { FileTypes = [new(".unknown")], Executable = "/opt/notes" })
            is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }, "an extension the database does not know needs a MIME type");

        Check(await registration.UnregisterAsync(known) is PlatformResult<bool>.Success { Value: true }, "unregister");
        Check(!File.Exists(Path.Combine(fixture.DataHome, "applications", Id + ".desktop")), "the entry is removed");
        Check(await registration.UnregisterAsync(known) is PlatformResult<bool>.Success { Value: false }, "nothing left");
        Check(diagnostics.Count == 0, "no diagnostics");
    }

    [Test, DisplayName("open registration: an entry the package installed, or one without the marker, is never overwritten")]
    public async Task RefusesForeignEntriesAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        var diagnostics = new List<PortalDiagnostic>();
        var registration = new LinuxOpenRegistration(new PortalApplication(Id, diagnostics.Add), Id, fixture.Options(), static (_, _) => true);
        var notes = new DesktopOpenRegistration("Notes") { UrlSchemes = ["org.example.notes"], Executable = "/opt/notes" };
        string installed = DesktopApplicationsFixture.Write(Path.Combine(fixture.Share, "applications", Id + ".desktop"),
            "[Desktop Entry]\nType=Application\nName=Notes\nExec=/usr/bin/notes %U\n");
        var refused = await registration.RegisterAsync(notes);
        Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict, Diagnostic.Message: { } message } && message.Contains(installed, StringComparison.Ordinal), $"{refused}");
        Check(!Directory.Exists(Path.Combine(fixture.DataHome, "applications")), "nothing is written");
        Check(diagnostics is [{ Code: "open-registration-foreign-entry", Severity: PortalDiagnosticSeverity.Warning }], "the conflict is diagnosed");
        File.Delete(installed);

        string own = DesktopApplicationsFixture.Write(Path.Combine(fixture.DataHome, "applications", Id + ".desktop"),
            "[Desktop Entry]\nType=Application\nName=Notes\nExec=/home/user/notes %U\n");
        Check(await registration.RegisterAsync(notes) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict }, "an unmarked user entry");
        Check(await registration.UnregisterAsync(notes) is PlatformResult<bool>.Success { Value: false } && File.Exists(own), "an unmarked entry is not removed");

        var sandboxed = new LinuxOpenRegistration(new PortalApplication(Id), Id, fixture.Options(sandboxed: true), static (_, _) => true);
        Check(await sandboxed.RegisterAsync(notes) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "the sandbox's manifest registers");
        Check(await sandboxed.IsRegisteredAsync(notes) is PlatformResult<bool>.Unavailable, "sandboxed is registered");
        try { _ = new PortalApplication().CreateOpenRegistration(); throw new InvalidOperationException("Registration without an ID was created."); }
        catch (InvalidOperationException error) when (error.Message.Contains("application's ID", StringComparison.Ordinal)) { }
    }

    [Test, DisplayName("D-Bus activation: ActivateAction maps to the Exec arguments of the entry's action")]
    public void MapsDesktopActions()
    {
        using var fixture = new DesktopApplicationsFixture();
        DesktopApplicationsFixture.Write(Path.Combine(fixture.Share, "applications", Id + ".desktop"),
            "[Desktop Entry]\nType=Application\nName=Notes\nExec=notes %U\nDBusActivatable=true\nActions=new-window;quiet;\n\n"
            + "[Desktop Action new-window]\nName=New window\nExec=notes --new-window \"two words\" %U\n\n[Desktop Action unlisted]\nName=x\nExec=notes --x\n");
        var activation = new ApplicationActivation(Id, new NoInstance(), fixture.Options());
        Check(activation.Arguments("new-window") is { } arguments && arguments.SequenceEqual(["--new-window", "two words"]), "the action's arguments without the program and field codes");
        Check(activation.Arguments("unlisted") is null, "an action not in Actions");
        Check(activation.Arguments("quiet") is null, "an action without a group");
        Check(activation.Arguments("../x") is null && activation.Arguments("") is null, "an invalid name");
    }

    [Test, Explicit, Category("DBus"), DisplayName("D-Bus activation: Activate, Open and ActivateAction reach the running instance; a taken name is refused")]
    public async Task ExportsTheApplicationAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        string id = "org.runic.tests.Activation" + Guid.NewGuid().ToString("N")[..8];
        DesktopApplicationsFixture.Write(Path.Combine(fixture.DataHome, "applications", id + ".desktop"),
            $"[Desktop Entry]\nType=Application\nName=Activation\nExec=notes %U\nDBusActivatable=true\nActions=compose;\n\n[Desktop Action compose]\nName=Compose\nExec=notes --compose\n");
        string directory = Directory.CreateTempSubdirectory("runic-activation-").FullName;
        try
        {
            var claim = await ApplicationInstance.ClaimAsync(id, new ApplicationLaunch([], "/"), directory, CancellationToken.None);
            if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } })
                throw new InvalidOperationException($"The instance was not claimed: {claim}");
            await using (instance)
            {
                var launches = Channel.CreateUnbounded<ApplicationLaunch>();
                instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);
                await using var opens = ApplicationInstance.CreateOpenRequests(new DesktopOpenOptions(["org.example.notes"]) { IncludeFiles = true }, instance);
                var opened = Channel.CreateUnbounded<DesktopOpenRequest>();
                opens.Opened += (_, request) => opened.Writer.TryWrite(request);

                var diagnostics = new List<PortalDiagnostic>();
                var application = new PortalApplication(id, diagnostics.Add);
                var exported = await application.ExportApplicationAsync(instance, null, fixture.Options(), CancellationToken.None);
                Check(exported.TryGetValue(out var export), $"exported: {exported}");
                await using (export!)
                {
                    await using var client = await ConnectAsync();
                    var path = new DBusObjectPath("/" + id.Replace('.', '/'));
                    var token = DBusValue.VariantDictionary([KeyValuePair.Create("activation-token", DBusValue.From("token-1"))]);
                    await CallAsync(client, id, path, "Activate", [token]);
                    var activated = await NextAsync(launches);
                    Check(activated.Arguments.IsEmpty && activated.PlatformContext?.ActivationToken == "token-1", "Activate is a launch without arguments, with the token");

                    string file = Path.Combine(directory, "note.txt");
                    await File.WriteAllTextAsync(file, "note");
                    await CallAsync(client, id, path, "Open", [DBusValue.StringArray(["org.example.notes://item?id=1", new Uri(file).AbsoluteUri]), EmptyData()]);
                    var openLaunch = await NextAsync(launches);
                    Check(openLaunch.Arguments.Length == 2, "Open is a launch with the URIs");
                    var request = await NextAsync(opened);
                    Check(request.Items is [{ Scheme: "org.example.notes" }, { IsFile: true } item] && item.LocalPath == file, "the open requests take the URIs");

                    await CallAsync(client, id, path, "ActivateAction", [DBusValue.From("compose"), DBusValue.Array("v", []), EmptyData()]);
                    Check((await NextAsync(launches)).Arguments.SequenceEqual(["--compose"]), "ActivateAction is a launch with the action's Exec arguments");
                    try { await CallAsync(client, id, path, "ActivateAction", [DBusValue.From("missing"), DBusValue.Array("v", []), EmptyData()]); throw new InvalidOperationException("An unknown action was accepted."); }
                    catch (DBusException error) when (error.ErrorName == DBusException.InvalidArgs) { }

                    // Another process owns the name: the export of a second application object is refused.
                    await using var other = await ConnectAsync();
                    string taken = id + ".Taken";
                    Check(await other.RequestNameAsync(taken, DBusRequestNameOptions.DoNotQueue) == DBusRequestNameReply.PrimaryOwner, "the other connection owns the name");
                    var takenApplication = new PortalApplication(taken, diagnostics.Add);
                    var refused = await takenApplication.ExportApplicationAsync(instance, null, fixture.Options(), CancellationToken.None);
                    Check(refused is PlatformResult<IAsyncDisposable>.Failed { Code: PlatformFailureCode.ResourceBusy }, $"a taken name: {refused}");
                    Check(diagnostics.Any(static diagnostic => diagnostic.Code == "dbus-activation-name-taken"), "the taken name is diagnosed");
                }
                // Disposal releases the name.
                await using var after = await ConnectAsync();
                Check(await after.RequestNameAsync(id, DBusRequestNameOptions.DoNotQueue) == DBusRequestNameReply.PrimaryOwner, "the name is released");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static DBusValue EmptyData() => DBusValue.VariantDictionary([]);

    private static async Task CallAsync(DBusConnection connection, string destination, DBusObjectPath path, string member, ImmutableArray<DBusValue> arguments) =>
        await connection.CallMethodAsync(new DBusMethodCall(destination, path, "org.freedesktop.Application", member) { Arguments = arguments })
            .AsTask().WaitAsync(Wait);

    private static async Task<DBusConnection> ConnectAsync() =>
        await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection) ? connection
            : throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static async Task<T> NextAsync<T>(Channel<T> channel) => await channel.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    private sealed class NoInstance : IApplicationInstance
    {
        public event EventHandler<ApplicationLaunch>? Launched { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
