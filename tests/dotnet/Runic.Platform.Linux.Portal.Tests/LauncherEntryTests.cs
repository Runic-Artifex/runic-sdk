using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Linux.Portal.Tests;

// Badges and progress as the Unity launcher entry: the properties a dock reads, and, on the private bus of
// dbus-run-session, the Update signals a listening dock receives and the Query a dock that starts later calls.
internal sealed class LauncherEntryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    public void Properties()
    {
        var count = PortalLauncherEntry.Properties(new DesktopBadge { Count = 120 }, new DesktopProgress(0.25));
        Check(count["count"].GetInt64() == 120 && count["count-visible"].GetBoolean() && !count["urgent"].GetBoolean()
            && count["progress"].GetDouble() == 0.25 && count["progress-visible"].GetBoolean(), "a count and progress");
        var dot = PortalLauncherEntry.Properties(new DesktopBadge(), new DesktopProgress(null));
        Check(!dot["count-visible"].GetBoolean() && dot["urgent"].GetBoolean() && !dot["progress-visible"].GetBoolean(),
            "a dot asks for attention; indeterminate progress shows no bar");
        var none = PortalLauncherEntry.Properties(null, null);
        Check(!none["count-visible"].GetBoolean() && !none["urgent"].GetBoolean() && !none["progress-visible"].GetBoolean(), "nothing shown");
        Check(PortalLauncherEntry.ObjectPath("org.example.Notes-app") == "/org/example/Notes_app/LauncherEntry", "the object path below the application's");
    }

    [Test]
    public async Task WithoutAnApplicationIdAsync()
    {
        var diagnostics = new List<PortalDiagnostic>();
        await using var taskbar = new PortalApplication(null, diagnostics.Add).CreateWindowTaskbar("unix:path=/nonexistent");
        Check(taskbar.Status is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the capability");
        Check(await taskbar.SetBadgeAsync(new() { Count = 1 }, CancellationToken.None) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "a badge without an ID");
        Check(await taskbar.SetProgressAsync(null, CancellationToken.None) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "progress without an ID");
        Check(diagnostics is [{ Code: "launcher-entry-application-id-required", Severity: PortalDiagnosticSeverity.Warning }], "diagnosed once");
    }

    [Test, Explicit, Category("DBus"), DisplayName("launcher entry: Update signals for badges and progress, Query, the most recent window wins")]
    public async Task UpdatesAndQueryAsync()
    {
        string id = "org.runic.tests.Launcher" + Guid.NewGuid().ToString("N")[..8];
        await using var dock = await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection) ? connection
            : throw new InvalidOperationException("Run the bus cases under dbus-run-session.");
        await using var updates = await dock.SubscribeAsync(new DBusSignalFilter { Interface = "com.canonical.Unity.LauncherEntry", Member = "Update" });
        await using var signals = updates.GetAsyncEnumerator();
        string? sender = null;
        async Task<ImmutableDictionary<string, DBusValue>> NextAsync()
        {
            Check(await signals.MoveNextAsync().AsTask().WaitAsync(Wait), "an Update signal");
            var signal = signals.Current;
            sender = signal.Sender;
            Check(signal.Arguments is [{ Signature.Value: "s" } uri, { Signature.Value: "a{sv}" }] && uri.GetString() == $"application://{id}.desktop",
                $"the signal names the desktop entry: {signal.Arguments.FirstOrDefault()}");
            return signal.Arguments[1].GetVariantDictionary();
        }

        var application = new PortalApplication(id);
        var first = application.CreateWindowTaskbar();
        var second = application.CreateWindowTaskbar();
        Check(await first.SetBadgeAsync(new() { Count = 3 }, CancellationToken.None) is PlatformResult<PlatformUnit>.Success, "the first window's badge");
        var shown = await NextAsync();
        Check(shown["count"].GetInt64() == 3 && shown["count-visible"].GetBoolean(), "the count");
        Check(await second.SetProgressAsync(new(0.5) { State = DesktopProgressState.Paused }, CancellationToken.None) is PlatformResult<PlatformUnit>.Success, "the second window's progress");
        shown = await NextAsync();
        Check(shown["count"].GetInt64() == 3 && shown["progress"].GetDouble() == 0.5 && shown["progress-visible"].GetBoolean(), "the count stays with the progress");

        // A dock that starts later asks the entry.
        var reply = await dock.CallMethodAsync(new DBusMethodCall(sender!, new DBusObjectPath(PortalLauncherEntry.ObjectPath(id)),
            "com.canonical.Unity.LauncherEntry", "Query")).AsTask().WaitAsync(Wait);
        Check(reply is [{ Signature.Value: "s" } queried, { Signature.Value: "a{sv}" } properties] && queried.GetString() == $"application://{id}.desktop"
            && properties.GetVariantDictionary()["count"].GetInt64() == 3, "Query answers the current state");

        // The second window's badge replaces the first's; closing the first leaves it.
        Check(await second.SetBadgeAsync(new(), CancellationToken.None) is PlatformResult<PlatformUnit>.Success, "the second window's dot");
        shown = await NextAsync();
        Check(!shown["count-visible"].GetBoolean() && shown["urgent"].GetBoolean(), "the dot");
        await first.DisposeAsync();
        await second.DisposeAsync();
        shown = await NextAsync();
        Check(!shown["urgent"].GetBoolean() && !shown["progress-visible"].GetBoolean(), "closing the window that shows them removes them");
        Console.WriteLine("PASS launcher entry Update signals, Query and the most recent window");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
