using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal.Activation;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The application's bus name and its org.freedesktop.Application object on one connection, shared by the services that
// answer there: notifications take ActivateAction of their own action, D-Bus activation (ExportApplicationAsync) the
// rest. The first holder exports the object and owns the name; the last one to release removes both.
internal sealed class ApplicationBusObject : ApplicationService
{
    internal const string NotificationAction = "runic-notification";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static readonly Dictionary<(DBusConnection Connection, string Id), ApplicationBusObject> s_objects = [];

    private readonly DBusConnection _connection;
    private readonly string _id;
    private IAsyncDisposable? _export;
    private int _holders;

    private ApplicationBusObject(DBusConnection connection, string id) { _connection = connection; _id = id; }

    // ActivateAction of a notification: the action's parameters and the platform data. Throws ArgumentException for an
    // activation it does not accept.
    internal Action<ImmutableArray<DBusValue>, ImmutableDictionary<string, DBusValue>>? Notifications { get; set; }
    internal ApplicationActivation? Activation { get; set; }

    // The object path the Desktop Entry Specification derives from the application ID.
    internal static string ObjectPath(string id) => "/" + id.Replace('.', '/').Replace('-', '_');

    /// <exception cref="InvalidOperationException">Another object of this process holds the path on the connection.</exception>
    /// <exception cref="NameTakenException">Another connection owns the application's name.</exception>
    internal static async Task<ApplicationBusObject> AcquireAsync(DBusConnection connection, string id, CancellationToken cancellationToken)
    {
        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!s_objects.TryGetValue((connection, id), out var shared) || connection.Disconnected.IsCancellationRequested)
            {
                shared = new ApplicationBusObject(connection, id);
                var export = connection.Export(new DBusObjectPath(ObjectPath(id)), shared);
                try
                {
                    // The export above shows no other service of this process answers for the name, so a name this
                    // connection already owns, from a request whose caller stopped waiting, is ours.
                    var reply = await connection.RequestNameAsync(id, DBusRequestNameOptions.DoNotQueue, CancellationToken.None).AsTask()
                        .WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
                    if (reply is not (DBusRequestNameReply.PrimaryOwner or DBusRequestNameReply.AlreadyOwner)) throw new NameTakenException();
                }
                catch
                {
                    await export.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                shared._export = export;
                s_objects[(connection, id)] = shared;
            }
            shared._holders++;
            return shared;
        }
        finally { s_gate.Release(); }
    }

    internal async Task ReleaseAsync()
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--_holders > 0) return;
            if (s_objects.TryGetValue((_connection, _id), out var current) && ReferenceEquals(current, this)) s_objects.Remove((_connection, _id));
            if (_export is { } export) { _export = null; await export.DisposeAsync().ConfigureAwait(false); }
            if (_connection.Disconnected.IsCancellationRequested) return;
            try { await _connection.ReleaseNameAsync(_id).AsTask().WaitAsync(Timeout).ConfigureAwait(false); }
            catch (Exception error) when (error is DBusException or TimeoutException or ObjectDisposedException) { /* Closing the connection releases the name too. */ }
        }
        finally { s_gate.Release(); }
    }

    protected override ValueTask ActivateAsync(ImmutableDictionary<string, DBusValue> platformData, DBusMethodContext context) =>
        Activation is { } activation ? activation.LaunchAsync([], platformData, context.RequestAborted)
            : throw new DBusException(DBusException.UnknownMethod, "The application does not export D-Bus activation.");

    protected override ValueTask OpenAsync(ImmutableArray<string> uris, ImmutableDictionary<string, DBusValue> platformData, DBusMethodContext context) =>
        Activation is { } activation ? activation.LaunchAsync(uris, platformData, context.RequestAborted)
            : throw new DBusException(DBusException.UnknownMethod, "The application does not export D-Bus activation.");

    protected override ValueTask ActivateActionAsync(string actionName, ImmutableArray<DBusValue> parameter, ImmutableDictionary<string, DBusValue> platformData,
        DBusMethodContext context)
    {
        if (actionName == NotificationAction && Notifications is { } notifications)
        {
            try { notifications(parameter, platformData); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
            { throw new DBusException(DBusException.InvalidArgs, "Invalid notification activation."); }
            return default;
        }
        if (Activation is { } activation) return activation.ActionAsync(actionName, platformData, context.RequestAborted);
        throw new DBusException(DBusException.InvalidArgs, "Invalid notification activation.");
    }

    internal sealed class NameTakenException : Exception;
}

// D-Bus activation of the running instance: Activate is a launch without arguments, Open a launch with the URIs, and
// ActivateAction a launch with the Exec arguments of the installed entry's [Desktop Action] group. Each launch reaches
// the instance's Launched handlers, and through them the open requests, as a forwarded launch does.
internal sealed class ApplicationActivation(string applicationId, IApplicationInstance instance, DesktopApplicationsOptions options)
{
    private const int MaximumItems = 1024, MaximumLength = 32 * 1024;

    internal ValueTask LaunchAsync(ImmutableArray<string> arguments, ImmutableDictionary<string, DBusValue> platformData, CancellationToken cancellationToken)
    {
        if (arguments.Length > MaximumItems || arguments.Any(static argument => argument.Length > MaximumLength || argument.Contains('\0')))
            throw new DBusException(DBusException.InvalidArgs, "Too many or too long URIs.");
        return DeliverAsync(arguments, platformData, cancellationToken);
    }

    internal ValueTask ActionAsync(string actionName, ImmutableDictionary<string, DBusValue> platformData, CancellationToken cancellationToken) =>
        Arguments(actionName) is { } arguments ? DeliverAsync(arguments, platformData, cancellationToken)
            : throw new DBusException(DBusException.InvalidArgs, $"The desktop entry has no action \"{(actionName.Length > 64 ? actionName[..64] : actionName)}\".");

    // The Exec arguments of the action, without the program, from the installed entry that lists it in Actions.
    internal ImmutableArray<string>? Arguments(string actionName)
    {
        if (actionName.Length is 0 or > 255 || actionName.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return null;
        var entry = new DesktopEntryCatalog(options, DesktopLocale.None).Get(applicationId + ".desktop");
        if (entry is null || DesktopKeyFile.Load(entry.FilePath) is not { } file) return null;
        if (!file.List(DesktopEntry.Group, "Actions").Contains(actionName, StringComparer.Ordinal)) return null;
        if (file.String("Desktop Action " + actionName, "Exec") is not { Length: > 0 } exec || DesktopExec.Split(exec) is not { Length: > 0 } command) return null;
        return DesktopExec.Expand(command, entry, []) is [var expanded, ..] ? expanded[1..] : null;
    }

    private async ValueTask DeliverAsync(ImmutableArray<string> arguments, ImmutableDictionary<string, DBusValue> platformData, CancellationToken cancellationToken)
    {
        string? Text(string key) => platformData.TryGetValue(key, out var value) && value.Signature.Value == "s" && value.GetString() is { Length: > 0 } text ? text : null;
        string? token = Text("activation-token"), startup = Text("desktop-startup-id");
        DesktopActivationContext? context = null;
        if (token is not null || startup is not null)
        {
            try { context = new DesktopActivationContext(token, startup); }
            catch (ArgumentException) { /* Unusable activation data cannot raise a window; launch without it. */ }
        }
        var launch = new ApplicationLaunch(arguments, options.HomeDirectory) { PlatformContext = context };
        if (!await ApplicationInstance.DeliverAsync(instance, launch, cancellationToken).ConfigureAwait(false))
            throw new DBusException("org.freedesktop.DBus.Error.Failed", "The running instance did not take the activation.");
    }
}
