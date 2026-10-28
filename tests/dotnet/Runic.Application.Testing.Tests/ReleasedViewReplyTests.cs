using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A page's own command can replace the page in its region (issue #246). The command runs, and
// .NET releases the page's bridge before the reply could name a revision of the page's route. The reply says so: it is
// `ok` with `released: true` and no revision, so a client resolves the call instead of waiting for a revision the
// route never reaches. A call that failed keeps its error and is also `released` (specs/application/README.md,
// "Released by the call").
public sealed class ReleasedViewReplyTests
{
    [Test]
    public void ACommandThatReplacesItsPageRepliesReleased()
    {
        using var fixture = new Fixture();
        var reply = Read(fixture.Transport.Command(fixture.Route, "leave", connectionKey: Fixture.Connection, clientKey: Fixture.Client));
        RequireReleased(reply, fixture);
    }

    [Test]
    public async Task AnAwaitedCommandThatReplacesItsPageRepliesReleased()
    {
        using var fixture = new Fixture();
        var reply = Read(await fixture.Transport.CommandAsync(fixture.Route, "leaveAsync", connectionKey: Fixture.Connection,
            clientKey: Fixture.Client));
        RequireReleased(reply, fixture);
    }

    [Test]
    public void ASetterThatReplacesItsPageRepliesReleased()
    {
        using var fixture = new Fixture();
        var reply = Read(fixture.Transport.Set(fixture.Route, "value", "7", connectionKey: Fixture.Connection, clientKey: Fixture.Client));
        RequireReleased(reply, fixture);
        Require(fixture.Page.Value == 7, "The setter did not run.");
    }

    [Test]
    public void ARejectedCallOnAReleasedPageKeepsItsKind()
    {
        using (var fixture = new Fixture())
        {
            var reply = Read(fixture.Transport.Set(fixture.Route, "value", "-1", connectionKey: Fixture.Connection, clientKey: Fixture.Client));
            // Protocol 4: a rejected reply carries no revision, so it has no released flag in its place either.
            Require(!reply.Ok && reply.ErrorKind == "rejected" && reply.Reason == "invalid-input" && !reply.HasRevision,
                $"A rejected call on a released page replied {reply.Json}.");
        }

        // A call that ran and failed keeps its error and replies released instead of its revision.
        using (var fixture = new Fixture())
        {
            var reply = Read(fixture.Transport.Set(fixture.Route, "value", "-2", connectionKey: Fixture.Connection, clientKey: Fixture.Client));
            Require(fixture.Page.Ran, "The setter did not run.");
            Require(!reply.Ok && reply.ErrorKind == "failed" && !reply.HasRevision && reply.Released,
                $"A failed call on a released page replied {reply.Json}.");
        }
    }

    // A checked write that released its page was processed, but has neither revision nor receipt.
    [Test]
    public void ACheckedWriteThatReplacesItsPageRepliesReleased()
    {
        using var fixture = new Fixture();
        var reply = Read(fixture.Transport.Write(fixture.Route, "count", "leave", expectedVersion: 0, expectedValueJson: "0", valueJson: "3",
            connectionKey: Fixture.Connection));
        Require(fixture.Page.Ran && fixture.Page.Count == 3, "The checked write did not run.");
        // Protocol 4: a write that ran and succeeded stays ok, as every call released by itself does.
        Require(reply.Ok && reply.ErrorKind is null && !reply.HasRevision && reply.Released
            && !reply.Json.Contains("\"receipt\"", StringComparison.Ordinal),
            $"A checked write that released its own page replied {reply.Json}.");
    }

    // A snapshot read has nothing to report once its bridge is released while it is captured.
    [Test]
    public void ASnapshotReadOfAReleasedPageIsReleased()
    {
        using var fixture = new Fixture();
        fixture.Page.LeaveOnSnapshot = true;
        var reply = Read(fixture.Transport.Snapshot(fixture.Route, Fixture.Connection));
        Require(fixture.Page.Ran, "The snapshot writer did not release the page.");
        // Protocol 4: replies name no protocol; the hello does.
        Require(!reply.Ok && reply.ErrorKind == "released" && !reply.HasState && !reply.HasRevision && !reply.Released,
            $"A snapshot read of a released page replied {reply.Json}.");
    }

    private static void RequireReleased(Reply reply, Fixture fixture)
    {
        Require(fixture.Page.Ran, "The command did not run.");
        var snapshot = Read(fixture.Transport.Snapshot(fixture.Route, Fixture.Connection));
        Require(!snapshot.Ok && snapshot.ErrorKind == "released", $"The page's bridge was not released: {snapshot.Json}");
        Require(reply.Ok && reply.ErrorKind is null && !reply.HasRevision && !reply.HasState && reply.Released,
            $"A call that released its own page replied {reply.Json}.");
    }

    private static Reply Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var hasError = root.TryGetProperty("error", out var error);
        return new(json,
            root.GetProperty("ok").GetBoolean(),
            root.TryGetProperty("state", out _),
            root.TryGetProperty("revision", out _),
            root.TryGetProperty("released", out var released) && released.ValueKind == JsonValueKind.True,
            hasError ? error.GetProperty("kind").GetString() : null,
            hasError ? error.GetProperty("message").GetString() : null,
            hasError && error.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
    }

    private sealed record Reply(string Json, bool Ok, bool HasState, bool HasRevision, bool Released, string? ErrorKind, string? Message,
        string? Reason);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { RootModel = Root });
            Page = new Page(() => Present(new Page(() => { })));
            Route = $"content{Present(Page).Id}";
            var open = Transport.Open(Route, "browser:page", Connection, Client);
            Require(open.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal), $"The page did not mount: {open}");
        }

        internal const string Connection = "connection";
        internal const string Client = "client";

        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public Shell Root { get; } = new();
        public Page Page { get; }
        public string Route { get; }

        private PageReference Present(Page page) =>
            Session.Present(Root, "Main", "page", page, (_, current, route) =>
                Session.AttachPresentation<PageView, Page>(current, route,
                    (transport, presented, presentedRoute) => new PageBridge(transport, presented, presentedRoute, Session),
                    () => new PageView()));

        public void Dispose()
        {
            Session.Dispose();
            Transport.Dispose();
        }
    }

    internal sealed class Shell : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    internal sealed class Page(Action leave) : INotifyPropertyChanged
    {
        private int _value;
        private int _count;
        public bool Ran { get; private set; }
        public bool LeaveOnSnapshot { get; set; }
        public int Value
        {
            get => _value;
            set
            {
                Leave();
                // -2 fails the setter after it ran; another negative value is invalid input.
                if (value == -2) throw new InvalidOperationException("The setter failed.");
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _value = value;
            }
        }
        public int Count
        {
            get => _count;
            set
            {
                _count = value;
                Leave();
            }
        }
        // Read by the snapshot writer.
        public int Snapshot
        {
            get
            {
                if (LeaveOnSnapshot)
                {
                    LeaveOnSnapshot = false;
                    Leave();
                }
                return _value;
            }
        }
        public ICommand LeaveCommand => new Command(Leave);
        public void Leave()
        {
            Ran = true;
            leave();
        }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class Command(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    private sealed class PageBridge(IBridgeTransport transport, Page model, string route, WindowContentSession content)
        : ViewModelBridge<Page>(transport, model, route, static (writer, vm, revision, writeFields) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("value", vm.Snapshot);
            writer.WriteNumber("count", vm.Count);
            writeFields(writer);
            writer.WriteEndObject();
        },
        [new("Value", vm => vm.Value, (vm, arguments) => vm.Value = (int)arguments.GetInt64())],
        [new("Count", "count", CheckedFieldValueKind.Int32, vm => vm.Count, (vm, value) => vm.Count = (int)value!)],
        [
            new("Leave", vm => vm.LeaveCommand),
            new("LeaveAsync", vm => vm.LeaveCommand) { ExecuteAsync = async (vm, _, _) =>
            {
                await Task.Yield();
                vm.Leave();
            } },
        ],
        contractFingerprint: "released-view-reply", content: content);

    // Implements IRunicView directly so the bridge generator does not treat
    // this private probe as an application View.
    private sealed class PageView : IRunicView
    {
        public object? DataContext { get; set; }
    }
}
