using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class SnapshotDeliveryTests
{
    [Test]
    public async Task DeliveryReadsOutsideTurnsAndBoundsSlowHostsAsync()
    {
        var model = new Model();
        await using var context = new RunicModelContext();
        using var ownership = RunicModelContextRegistry.Shared.Bind(context, model);
        using var transport = new ReentrantTransport();
        using var bridge = new ProbeBridge(transport, model);
        // The route publishes frames once a client read its state.
        _ = transport.Request("""{"kind":"snapshot","route":"probe"}""");
        _ = transport.Request("""{"kind":"set","route":"probe","member":"value","value":7,"edit":1}""");
        await transport.Delivered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        if (transport.Observed != 7)
            throw new InvalidOperationException("A native delivery callback could not read the model from outside its turn.");

        using var slow = new SlowTransport();
        using var delivery = Delivery(slow);
        slow.Hello();
        try
        {
            if (!delivery.EnqueueFrame(Bytes("first"))) throw new InvalidOperationException("The first frame was rejected.");
            await slow.Entered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < BridgeSnapshotDelivery.MaximumPendingEntries; index++)
                if (!delivery.EnqueueFrame(Bytes($"frame {index}"))) throw new InvalidOperationException("The bounded queue rejected a frame early.");
            if (delivery.EnqueueFrame(Bytes("overflow"))) throw new InvalidOperationException("A slow host retained an unbounded frame queue.");
            delivery.EnqueueState(Bytes("recovery state"));
            slow.Release.Set();
            await slow.Completed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
            if (!slow.Frames.SequenceEqual(["first", "recovery state"]))
                throw new InvalidOperationException("A recovery state did not supersede pending frames.");
        }
        finally { slow.Release.Set(); }
    }

    // #1387: the drain waited for a model turn to run a requested unit, as the route's first unit does after attach,
    // while that turn queued a burst of frames. It took the first frame only after the turn, so that frame counted
    // toward the bounds, and the 65th frame of the burst captured a recovery state although nothing was being
    // delivered. The oldest frame is the one being delivered as soon as it is queued.
    //
    // #1439: the burst starts only once the drain waits for the turn. A drain that started after the first frame was
    // queued took that frame before the unit and delivered it while the turn still ran, which needs no turn; the next
    // frame was then the one being delivered, so the queue rightly had room for the overflow frame.
    [Test]
    public async Task TheFirstFrameIsBeingDeliveredWhileTheDrainWaitsForATurnAsync()
    {
        var model = new Model();
        await using var context = new HeldTurnContext();
        using var ownership = RunicModelContextRegistry.Shared.Bind(context, model);
        var turn = BridgeModelTurn.For(model);
        var unitRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new RecordingTransport();
        using var delivery = new BridgeSnapshotDelivery(transport, "rows", turn, static () => (byte[]?)null,
            () => unitRan.TrySetResult(), nameof(Model));
        transport.Hello();
        turn.Run(() =>
        {
            delivery.RequestUnit();
            // The drain took the unit and waits for this turn: it can neither run the unit nor deliver anything
            // before the turn ends.
            if (!context.TurnRequested.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))))
                throw new TimeoutException("The drain did not wait for a turn to run the requested unit.");
            if (!delivery.EnqueueFrame(Bytes("first"))) throw new InvalidOperationException("The first frame was rejected.");
            for (var index = 0; index < BridgeSnapshotDelivery.MaximumPendingEntries; index++)
                if (!delivery.EnqueueFrame(Bytes($"frame {index}")))
                    throw new InvalidOperationException($"Frame {index} behind the first was rejected: the first frame counted toward the bounds.");
            if (delivery.EnqueueFrame(Bytes("overflow"))) throw new InvalidOperationException("The queue retained more than 64 frames behind the first.");
            delivery.EnqueueState(Bytes("recovery state"));
        });
        await unitRan.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => transport.Count == 2);
        var delivered = transport.Frames.Select(Encoding.UTF8.GetString).ToArray();
        if (!delivered.SequenceEqual(["first", "recovery state"]))
            throw new InvalidOperationException($"The recovery state did not follow the first frame: [{string.Join(", ", delivered)}].");
    }

    // I-018: a transport receives exactly the bytes of the pushes that were queued, in order. (Protocol 3 also had
    // string transports, whose frames had to match; protocol 4 has only UTF-8 broadcasts.)
    [Test]
    public async Task TransportsReceiveTheBytesOfThePushesAsync()
    {
        string[] pushes = ["""{"kind":"state","title":"Grüße 🎉","n":1}""", """{"kind":"frame","delta":["ä","\u2028"]}""", """{"kind":"failure","error":"ok"}"""];
        using var bytes = new RecordingTransport();
        using var memory = new InMemoryBridgeTransport();
        using var toBytes = Delivery(bytes);
        using var toMemory = Delivery(memory);
        bytes.Hello();
        memory.Hello();
        var published = new List<string>();
        for (var index = 0; index < pushes.Length; index++)
        {
            // One push at a time: a state or failure supersedes what is still queued before it.
            Action<BridgeSnapshotDelivery> enqueue = index switch
            {
                0 => delivery => delivery.EnqueueState(Bytes(pushes[0])),
                1 => delivery => { if (!delivery.EnqueueFrame(Bytes(pushes[1]))) throw new InvalidOperationException("A frame was rejected."); },
                _ => delivery => delivery.EnqueueFailure(Bytes(pushes[2])),
            };
            enqueue(toBytes);
            enqueue(toMemory);
            var count = index + 1;
            await WaitUntilAsync(() =>
            {
                published.AddRange(memory.TakeMessages().Select(message => message.Json));
                return bytes.Count == count && published.Count == count;
            });
        }
        if (!published.SequenceEqual(pushes))
            throw new InvalidOperationException($"The in-memory pushes differ: [{string.Join(", ", published)}].");
        if (!bytes.Frames.Zip(pushes).All(pair => pair.First.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(pair.Second))))
            throw new InvalidOperationException(
                $"The UTF-8 pushes differ from the queued ones: [{string.Join(", ", bytes.Frames.Select(Encoding.UTF8.GetString))}].");
    }

    // 0.13.1: a reply's wait for the pushes before it completed inside the drain, so the caller resumed there. A caller
    // that then waited synchronously for the next push of the same route waited for the drain it held: the route kept a
    // unit requested but never run, and every later reply waited for ReplyWaitLimit.
    [Test]
    public async Task AWaitForThePushesDoesNotResumeOnTheDrainAsync()
    {
        using var transport = new GatedTransport();
        using var delivery = Delivery(transport);
        transport.Hello();
        if (!delivery.EnqueueFrame(Bytes("first"))) throw new InvalidOperationException("The first frame was rejected.");
        await transport.Entered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        var settled = delivery.WhenSettled() ?? throw new InvalidOperationException("A frame on its way did not keep the delivery unsettled.");
        var resumed = settled.ContinueWith(_ =>
        {
            // What a test or a custom transport does after an awaited reply: send the next request, which queues a
            // frame, and wait for it synchronously.
            if (!delivery.EnqueueFrame(Bytes("second"))) throw new InvalidOperationException("The second frame was rejected.");
            return transport.Second.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(2)));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        transport.Release.Set();
        if (!await resumed.WaitScaledAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("The caller resumed on the drain, which could not deliver the frame it waited for.");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // A delivery queue of a route "rows" that captures no state and runs no unit of its own.
    private static BridgeSnapshotDelivery Delivery(IBridgeTransport transport) =>
        new(transport, "rows", BridgeModelTurn.For(new Model()), static () => (byte[]?)null, static () => { }, nameof(Model));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                throw new TimeoutException("The frames were not delivered.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingTransport : PushTestTransport
    {
        private readonly List<byte[]> _frames = [];
        public byte[][] Frames { get { lock (_frames) return [.. _frames]; } }
        public int Count { get { lock (_frames) return _frames.Count; } }
        protected override ValueTask OnBroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            // The memory is valid only until the returned task completes.
            lock (_frames) _frames.Add(message.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    // A model context whose turns run on the thread that starts them. A turn requested from another thread while one
    // runs is queued, sets TurnRequested and runs when the running turn ends, so a test knows when the drain waits.
    private sealed class HeldTurnContext : IRunicModelContext
    {
        private readonly object _gate = new();
        private readonly Queue<(Action Turn, TaskCompletionSource Done)> _queued = new();
        private int _executing;

        public ManualResetEventSlim TurnRequested { get; } = new();

        public bool IsExecuting => Volatile.Read(ref _executing) == Environment.CurrentManagedThreadId;

        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException { add { } remove { } }

        // The delivery neither posts turns nor asks for a result.
        public bool TryPost(Action turn) => throw new NotSupportedException();

        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
        {
            if (IsExecuting)
            {
                turn();
                return ValueTask.CompletedTask;
            }
            lock (_gate)
            {
                if (_executing != 0)
                {
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _queued.Enqueue((turn, done));
                    TurnRequested.Set();
                    return new(done.Task);
                }
                _executing = Environment.CurrentManagedThreadId;
            }
            try { turn(); }
            finally { RunQueuedTurns(); }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TurnRequested.Dispose();
            return ValueTask.CompletedTask;
        }

        // The thread that ran a turn runs the turns queued meanwhile before another thread can start one.
        private void RunQueuedTurns()
        {
            while (true)
            {
                (Action Turn, TaskCompletionSource Done) next;
                lock (_gate)
                {
                    if (!_queued.TryDequeue(out next))
                    {
                        _executing = 0;
                        return;
                    }
                }
                try
                {
                    next.Turn();
                    next.Done.TrySetResult();
                }
                catch (Exception error) { next.Done.TrySetException(error); }
            }
        }
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private int _value;
        public int Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ProbeBridge(IBridgeTransport transport, Model model) : ViewModelBridge(
        transport, model, typeof(Model), "probe", [new BridgeStateMember("value", static (writer, vm) => writer.WriteNumberValue(((Model)vm).Value))],
        [new("Value", "value", vm => ((Model)vm).Value, (vm, value) => ((Model)vm).Value = BridgeWire.ReadInt32(value))], [], []);

    private sealed class ReentrantTransport : PushTestTransport
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Observed { get; private set; }
        protected override void OnBroadcast(string json)
        {
            try
            {
                using var reply = JsonDocument.Parse(Request("""{"kind":"snapshot","route":"probe"}"""));
                Observed = reply.RootElement.GetProperty("state").GetProperty("value").GetInt32();
                Delivered.TrySetResult();
            }
            catch (Exception error) { Delivered.TrySetException(error); }
        }
    }

    // Holds the first push until Release is set, and reports the second one.
    private sealed class GatedTransport : PushTestTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Second { get; } = new();
        protected override void OnBroadcast(string json)
        {
            if (json == "first") { Entered.TrySetResult(); Release.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))); }
            else if (json == "second") Second.Set();
        }
        public override void Dispose()
        {
            Release.Set();
            base.Dispose();
        }
    }

    private sealed class SlowTransport : PushTestTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public List<string> Frames { get; } = [];
        protected override void OnBroadcast(string json)
        {
            if (json == "first") { Entered.TrySetResult(); Release.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))); }
            Frames.Add(json);
            if (json == "recovery state") Completed.TrySetResult();
        }
        public override void Dispose()
        {
            Release.Dispose();
            base.Dispose();
        }
    }
}
