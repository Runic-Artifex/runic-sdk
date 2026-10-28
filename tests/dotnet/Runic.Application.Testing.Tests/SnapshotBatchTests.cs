using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Count writer invocations rather than delivery timing. A batch is one publication unit (specs/application/
// state-changes.md, "Publication units"), and the delivery runs a requested unit when the host can take its frame;
// this suite proves that batching avoids discarded captures and never shows a half-applied batch. A route publishes
// once a client read its state, so each test reads it first and counts the captures after that read.
internal sealed class SnapshotBatchTests
{
    [Test]
    public async Task CoalescesBulkNotifications()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "bulk", counter);
        var revision = Read(transport, "bulk", counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            for (var value = 1; value <= 100; value++) model.Value = value;
            Require(counter.Count == 0, "A batch captured state before its outer scope ended.");
        }

        await counter.SettleAsync(1);
        Require(counter.Count == 1, "One hundred changes should capture exactly one snapshot.");
        // Protocol 4 counts revisions per publication unit, not per notification: the batch is one frame.
        var frames = Frames(transport, "bulk");
        Require(frames.Count == 1 && frames[0].BaseRevision == revision && frames[0].Revision > revision && frames[0].Value == 100,
            $"The batch was not one frame with its final value: {string.Join(", ", frames)}.");
        Require(model.Value == 100, "The batch did not preserve the final model state.");
    }

    // vrx-06: BatchBridgeSnapshots is core API for any IObservable<T>, not a ReactiveUI flavor's.
    [Test]
    public async Task ObservableChangesetsCaptureOnceEach()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "observable", counter);
        Read(transport, "observable", counter);
        var source = new Subject<int[]>();
        var completed = false;
        using var subscription = source.BatchBridgeSnapshots(model).Subscribe(new Observer<int[]>(changes =>
        {
            foreach (var value in changes) model.Value = value;
            Require(counter.Count == 0, "A changeset captured state before its delivery ended.");
        }, () => completed = true));

        source.OnNext([1, 2, 3]);
        await counter.SettleAsync(1);
        Require(counter.Count == 1 && model.Value == 3, "One changeset should capture exactly one snapshot.");
        Require(Frames(transport, "observable").Select(frame => frame.Value).SequenceEqual([3]), "One changeset should publish one frame.");
        source.OnCompleted();
        Require(completed, "Completion was not forwarded.");
    }

    private sealed class Subject<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer);
            return new Unsubscriber(() => _observers.Remove(observer));
        }
        public void OnNext(T value) { foreach (var observer in _observers.ToArray()) observer.OnNext(value); }
        public void OnCompleted() { foreach (var observer in _observers.ToArray()) observer.OnCompleted(); }
        private sealed class Unsubscriber(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }

    private sealed class Observer<T>(Action<T> next, Action completed) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() => completed();
    }

    [Test]
    public async Task NestedScopesFlushOnce()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "nested", counter);
        Read(transport, "nested", counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Value = 1;
            using (BridgeSnapshotBatch.Begin(model)) model.Value = 2;
            Require(counter.Count == 0, "An inner scope flushed a model batch early.");
            model.Value = 3;
        }

        await counter.SettleAsync(1);
        Require(counter.Count == 1, "Nested scopes should have one outer-scope capture.");
        Require(Frames(transport, "nested").Select(frame => frame.Value).SequenceEqual([3]), "Nested scopes did not publish one frame.");
    }

    [Test]
    public async Task ExceptionStillFlushes()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "exception", counter);
        Read(transport, "exception", counter);

        try
        {
            using var batch = BridgeSnapshotBatch.Begin(model);
            model.Value = 42;
            throw new InvalidOperationException("expected");
        }
        catch (InvalidOperationException) { }

        await counter.SettleAsync(1);
        Require(counter.Count == 1 && model.Value == 42,
            "Disposing a batch during exception unwinding did not publish its final state.");
        Require(Frames(transport, "exception").Select(frame => frame.Value).SequenceEqual([42]), "Exception unwinding lost the pending notification.");
    }

    [Test]
    public async Task AttachesEveryBridgeAndKeepsModelsSeparate()
    {
        var first = new Model();
        var second = new Model();
        using var firstTransport = new InMemoryBridgeTransport();
        using var secondTransport = new InMemoryBridgeTransport();
        using var otherTransport = new InMemoryBridgeTransport();
        var firstCounter = new CaptureCounter();
        var secondCounter = new CaptureCounter();
        var otherCounter = new CaptureCounter();
        using var firstBridge = new ProbeBridge(firstTransport, first, "first", firstCounter);
        using var secondBridge = new ProbeBridge(secondTransport, first, "second", secondCounter);
        using var otherBridge = new ProbeBridge(otherTransport, second, "other", otherCounter);
        Read(firstTransport, "first", firstCounter);
        Read(secondTransport, "second", secondCounter);
        Read(otherTransport, "other", otherCounter);

        using (BridgeSnapshotBatch.Begin(first))
        {
            first.Value = 7;
            second.Value = 8;
            await otherCounter.SettleAsync(1);
            Require(firstCounter.Count == 0 && secondCounter.Count == 0,
                "A batch did not defer every bridge attached to its model.");
            Require(otherCounter.Count == 1,
                "A batch for one model deferred an unrelated model.");
        }

        await firstCounter.SettleAsync(1);
        await secondCounter.SettleAsync(1);
        Require(firstCounter.Count == 1 && secondCounter.Count == 1,
            "The outer scope did not flush each attached bridge once.");
        Require(otherCounter.Count == 1,
            "Flushing one model changed another model's capture count.");
    }

    // A request inside an open batch publishes its own unit and replies at once with its revision (the inclusion rule:
    // the state at that revision includes the request's effects); the batch's end publishes only what is left.
    [Test]
    public async Task RouteRepliesRemainImmediate()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        using var content = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model });
        var counter = new CaptureCounter();
        using var bridge = new CheckedProbeBridge(transport, model, content, counter);
        // Content-session revisions are window-monotonic rather than starting
        // at zero for each bridge.
        var baseline = Read(transport, "checked", counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            using var writeReply = JsonDocument.Parse(transport.Write("checked", "value", "batched-write", 0, "0", "9"));
            Require(writeReply.RootElement.GetProperty("ok").GetBoolean()
                && writeReply.RootElement.GetProperty("receipt").GetProperty("kind").GetString() == "applied",
                $"A checked write did not reply with its receipt while the batch was open: {writeReply.RootElement}");
            var written = writeReply.RootElement.GetProperty("revision").GetInt64();
            Require(written > baseline, "The checked-write reply did not expose its revision during the batch.");
            Require(counter.Count == 1, "A checked-write reply did not capture immediately.");

            using var commandReply = JsonDocument.Parse(transport.Command("checked", "increment"));
            Require(commandReply.RootElement.GetProperty("ok").GetBoolean(), $"The command failed: {commandReply.RootElement}");
            var commanded = commandReply.RootElement.GetProperty("revision").GetInt64();
            Require(commanded > written, "The command reply did not advance beyond the checked-write revision during the batch.");
            Require(counter.Count == 2, "A command reply did not capture immediately.");
            var frames = Frames(transport, "checked");
            Require(frames.Select(frame => (frame.Revision, frame.Value)).SequenceEqual([(written, (int?)9), (commanded, 10)]),
                $"The replies' revisions were not the frames with their effects: {string.Join(", ", frames)}.");
        }

        await counter.SettleAsync(3);
        Require(counter.Count == 3,
            "Deferred notifications did not publish once after immediate route replies completed.");
        Require(Frames(transport, "checked").Count == 0,
            "The batch's end published again what the replies' units had published.");
    }

    // A writer failure, including ObjectDisposedException from a getter, fails
    // that capture only. The next change requests another one.
    [Test]
    public async Task FailedCaptureKeepsDelivering()
    {
        var model = new Model();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new ThrowingProbeBridge(transport, model);
        Read(transport, "throwing", new CaptureCounter());
        model.Value = 13;
        await Task.Delay(100);
        Require(transport.TakePushes("throwing").Count == 0, "A failed capture published a state.");
        model.Value = 14;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<Push> published = [];
        while (published.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
            published.AddRange(Frames(transport, "throwing"));
        }
        Require(published.Count == 1 && published[0].Value == 14,
            "Delivery stopped after a snapshot writer threw ObjectDisposedException.");
    }

    // Without a batch, a busy host still costs one capture per delivered state.
    [Test]
    public async Task BurstBehindBusyHostCapturesOnce()
    {
        var model = new Model();
        using var transport = new HeldTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "burst", counter);
        Read(transport, "burst", counter);

        model.Value = 1;
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var value = 2; value <= 100; value++) model.Value = value;
        Require(counter.Count == 1, "Changes behind a busy host captured states the host could not take.");
        transport.Release.Set();
        await counter.SettleAsync(2);
        Require(counter.Count == 2 && transport.Values.SequenceEqual([1, 100]),
            $"The host did not receive the latest state once: {string.Join(", ", transport.Values)}.");
    }

    // A state requested before a batch began is captured when the host can
    // take it. If the batch is still open then, the capture waits for its end.
    [Test]
    public async Task DoesNotCaptureAnOpenBatch()
    {
        var model = new Model();
        using var transport = new HeldTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "held", counter);
        Read(transport, "held", counter);

        model.Value = 1;
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Value = 2;
        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Value = 3;
            transport.Release.Set();
            await Task.Delay(100);
            Require(counter.Count == 1 && transport.Values.SequenceEqual([1]), "The delivery captured a half-applied batch.");
            model.Value = 4;
        }

        await counter.SettleAsync(2);
        Require(counter.Count == 2 && transport.Values.SequenceEqual([1, 4]),
            $"The batch did not publish its final state once: {string.Join(", ", transport.Values)}.");
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private int _value;
        public Model() => Increment = new DelegateCommand(() => Value++);
        public ICommand Increment { get; }
        public int Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // Reads a route's state, as a client does before it applies frames, and returns its revision. The counter
    // starts after the read.
    private static long Read(IBridgeTransport transport, string route, CaptureCounter counter)
    {
        var snapshot = BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "snapshot");
            writer.WriteString("route", route);
        });
        var reply = transport switch
        {
            InMemoryBridgeTransport memory => memory.Send(snapshot),
            PushTestTransport push => push.Request(snapshot),
            _ => throw new ArgumentException("An unknown transport.", nameof(transport)),
        };
        counter.Count = 0;
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.GetProperty("revision").GetInt64();
    }

    // The frames pushed on a route since the last call, with the value each sets.
    private static List<Push> Frames(InMemoryBridgeTransport transport, string route) =>
        [.. transport.TakePushes(route).Select(push =>
        {
            using (push) return Push.Parse(push.RootElement);
        })];

    private sealed record Push(long? BaseRevision, long Revision, int? Value)
    {
        internal static Push Parse(JsonElement root) => new(
            root.TryGetProperty("baseRevision", out var baseRevision) ? baseRevision.GetInt64() : null,
            root.GetProperty("revision").GetInt64(),
            root.GetProperty("kind").GetString() switch
            {
                "state" => root.GetProperty("state").GetProperty("value").GetInt32(),
                "frame" => root.GetProperty("changes").EnumerateArray()
                    .Where(change => change.GetProperty("field").GetString() == "value")
                    .Select(change => (int?)change.GetProperty("value").GetInt32()).FirstOrDefault(),
                _ => null,
            });
    }

    // Delivery captures on its own thread.
    private sealed class CaptureCounter
    {
        private readonly object _gate = new();
        private int _count;

        public int Count
        {
            get { lock (_gate) return _count; }
            set { lock (_gate) _count = value; }
        }

        public void Capture()
        {
            lock (_gate) _count++;
        }

        // Waits for the expected captures, then a little longer for any extra.
        public async Task SettleAsync(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Count < expected && DateTime.UtcNow < deadline) await Task.Delay(5);
            await Task.Delay(50);
        }
    }

    // Holds the first push until released, as a busy host does, and records the value of each push.
    private sealed class HeldTransport : PushTestTransport
    {
        private readonly List<int> _values = [];
        public ManualResetEventSlim Release { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int[] Values { get { lock (_values) return [.. _values]; } }
        protected override void OnBroadcast(string json)
        {
            using (var push = JsonDocument.Parse(json))
                if (Push.Parse(push.RootElement).Value is { } value) lock (_values) _values.Add(value);
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        public override void Dispose()
        {
            Release.Set();
            base.Dispose();
        }
    }

    private sealed class ProbeBridge(IBridgeTransport transport, Model model, string route, CaptureCounter counter)
        : ViewModelBridge<Model>(transport, model, route, [new BridgeStateMember<Model>("value", (writer, vm) =>
        {
            counter.Capture();
            writer.WriteNumberValue(vm.Value);
        })], [], [], []);

    private sealed class ThrowingProbeBridge(IBridgeTransport transport, Model model)
        : ViewModelBridge<Model>(transport, model, "throwing", [new BridgeStateMember<Model>("value", static (writer, vm) =>
        {
            ObjectDisposedException.ThrowIf(vm.Value == 13, vm);
            writer.WriteNumberValue(vm.Value);
        })], [], [], []);

    private sealed class CheckedProbeBridge(IBridgeTransport transport, Model model,
        WindowContentSession content, CaptureCounter counter)
        : ViewModelBridge<Model>(transport, model, "checked", [new BridgeStateMember<Model>("value", (writer, vm) =>
        {
            counter.Capture();
            writer.WriteNumberValue(vm.Value);
        })], [],
        [new("Value", "value", CheckedFieldValueKind.Int32, vm => vm.Value,
            (vm, value) => vm.Value = (int)value!)],
        [new("Increment", vm => vm.Increment)], contractFingerprint: "snapshot-batch", content: content);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
