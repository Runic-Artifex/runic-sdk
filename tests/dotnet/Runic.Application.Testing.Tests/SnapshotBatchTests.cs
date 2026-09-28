using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Count writer invocations rather than delivery timing. BridgeSnapshotDelivery
// already coalesces delivery; this suite proves that batching avoids the
// discarded snapshot captures in the first place.
internal static class SnapshotBatchTests
{
    public static Task RunAsync()
    {
        CoalescesBulkNotifications();
        NestedScopesFlushOnce();
        ExceptionStillFlushes();
        AttachesEveryBridgeAndKeepsModelsSeparate();
        RouteRepliesRemainImmediate();
        return Task.CompletedTask;
    }

    private static void CoalescesBulkNotifications()
    {
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "bulk", counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            for (var value = 1; value <= 100; value++) model.Value = value;
            Require(counter.Count == 0, "A batch captured state before its outer scope ended.");
        }

        Require(counter.Count == 1, "One hundred changes should capture exactly one snapshot.");
        Require(counter.Revisions.Single() == 100,
            "The coalesced snapshot did not preserve every deferred notification revision.");
        Require(model.Value == 100, "The batch did not preserve the final model state.");
    }

    private static void NestedScopesFlushOnce()
    {
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "nested", counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Value = 1;
            using (BridgeSnapshotBatch.Begin(model)) model.Value = 2;
            Require(counter.Count == 0, "An inner scope flushed a model batch early.");
            model.Value = 3;
        }

        Require(counter.Count == 1, "Nested scopes should have one outer-scope capture.");
        Require(counter.Revisions.Single() == 3, "Nested notifications did not advance their revisions.");
    }

    private static void ExceptionStillFlushes()
    {
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        var counter = new CaptureCounter();
        using var bridge = new ProbeBridge(transport, model, "exception", counter);

        try
        {
            using var batch = BridgeSnapshotBatch.Begin(model);
            model.Value = 42;
            throw new InvalidOperationException("expected");
        }
        catch (InvalidOperationException) { }

        Require(counter.Count == 1 && model.Value == 42,
            "Disposing a batch during exception unwinding did not publish its final state.");
        Require(counter.Revisions.Single() == 1, "Exception unwinding lost the pending notification revision.");
    }

    private static void AttachesEveryBridgeAndKeepsModelsSeparate()
    {
        var first = new Model();
        var second = new Model();
        using var firstTransport = new InMemoryViewTransport();
        using var secondTransport = new InMemoryViewTransport();
        using var otherTransport = new InMemoryViewTransport();
        var firstCounter = new CaptureCounter();
        var secondCounter = new CaptureCounter();
        var otherCounter = new CaptureCounter();
        using var firstBridge = new ProbeBridge(firstTransport, first, "first", firstCounter);
        using var secondBridge = new ProbeBridge(secondTransport, first, "second", secondCounter);
        using var otherBridge = new ProbeBridge(otherTransport, second, "other", otherCounter);

        using (BridgeSnapshotBatch.Begin(first))
        {
            first.Value = 7;
            second.Value = 8;
            Require(firstCounter.Count == 0 && secondCounter.Count == 0,
                "A batch did not defer every bridge attached to its model.");
            Require(otherCounter.Count == 1,
                "A batch for one model deferred an unrelated model.");
        }

        Require(firstCounter.Count == 1 && secondCounter.Count == 1,
            "The outer scope did not flush each attached bridge once.");
        Require(otherCounter.Count == 1,
            "Flushing one model changed another model's capture count.");
    }

    private static void RouteRepliesRemainImmediate()
    {
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        using var content = new WindowContentSession(transport, rootModel: model);
        var counter = new CaptureCounter();
        using var bridge = new CheckedProbeBridge(transport, model, content, counter);

        using (BridgeSnapshotBatch.Begin(model))
        {
            const string write = """
                {"requestId":"batched-write","expectedVersion":0,"expectedValue":0,"value":9}
                """;
            using var writeReply = JsonDocument.Parse(transport.Call("checkedWriteValue", new(StringValue: write)));
            Require(writeReply.RootElement.GetProperty("receipt").GetProperty("kind").GetString() == "applied"
                && writeReply.RootElement.GetProperty("state").GetProperty("value").GetInt32() == 9,
                "A checked write did not return its state and receipt while the batch was open.");
            Require(writeReply.RootElement.GetProperty("state").GetProperty("revision").GetInt64() == 1,
                "The checked-write reply did not expose its notification revision during the batch.");
            Require(counter.Count == 1, "A checked-write reply did not capture immediately.");

            using var commandReply = JsonDocument.Parse(transport.Call("checkedIncrement"));
            Require(commandReply.RootElement.GetProperty("ok").GetBoolean()
                && commandReply.RootElement.GetProperty("state").GetProperty("value").GetInt32() == 10,
                "A command reply did not return its state while the batch was open.");
            Require(commandReply.RootElement.GetProperty("state").GetProperty("revision").GetInt64() == 2,
                "The command reply did not advance beyond the checked-write revision during the batch.");
            Require(counter.Count == 2, "A command reply did not capture immediately.");
        }

        Require(counter.Count == 3,
            "Deferred notifications did not publish once after immediate route replies completed.");
        Require(counter.Revisions.SequenceEqual([1, 2, 2]),
            "The final deferred capture did not retain the latest immediate-reply revision.");
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

    private sealed class CaptureCounter
    {
        public int Count { get; set; }
        public List<long> Revisions { get; } = [];

        public void Capture(long revision)
        {
            Count++;
            Revisions.Add(revision);
        }
    }

    private sealed class ProbeBridge(IBridgeTransport transport, Model model, string route, CaptureCounter counter)
        : ViewModelBridge<Model>(transport, model, route, (writer, vm, revision) =>
        {
            counter.Capture(revision);
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("value", vm.Value);
            writer.WriteEndObject();
        }, [], []);

    private sealed class CheckedProbeBridge(IBridgeTransport transport, Model model,
        WindowContentSession content, CaptureCounter counter)
        : ViewModelBridge<Model>(transport, model, "checked", (writer, vm, revision, writeFields) =>
        {
            counter.Capture(revision);
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("value", vm.Value);
            writeFields(writer);
            writer.WriteEndObject();
        }, [],
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
