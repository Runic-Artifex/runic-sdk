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
        await transport.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (transport.Observed != 7)
            throw new InvalidOperationException("A native delivery callback could not read the model from outside its turn.");

        using var slow = new SlowTransport();
        using var delivery = Delivery(slow);
        slow.Hello();
        try
        {
            if (!delivery.EnqueueFrame(Bytes("first"))) throw new InvalidOperationException("The first frame was rejected.");
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < BridgeSnapshotDelivery.MaximumPendingEntries; index++)
                if (!delivery.EnqueueFrame(Bytes($"frame {index}"))) throw new InvalidOperationException("The bounded queue rejected a frame early.");
            if (delivery.EnqueueFrame(Bytes("overflow"))) throw new InvalidOperationException("A slow host retained an unbounded frame queue.");
            delivery.EnqueueState(Bytes("recovery state"));
            slow.Release.Set();
            await slow.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!slow.Frames.SequenceEqual(["first", "recovery state"]))
                throw new InvalidOperationException("A recovery state did not supersede pending frames.");
        }
        finally { slow.Release.Set(); }
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

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // A delivery queue of a route "rows" that captures no state and runs no unit of its own.
    private static BridgeSnapshotDelivery Delivery(IBridgeTransport transport) =>
        new(transport, "rows", BridgeModelTurn.For(new Model()), static () => null, static () => { }, nameof(Model));

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

    private sealed class ProbeBridge(IBridgeTransport transport, Model model) : ViewModelBridge<Model>(
        transport, model, "probe", static (writer, vm, _, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", vm.Value);
            writer.WriteEndObject();
        }, [new("Value", vm => vm.Value, (vm, args) => vm.Value = checked((int)args.GetInt64()))], [], []);

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

    private sealed class SlowTransport : PushTestTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public List<string> Frames { get; } = [];
        protected override void OnBroadcast(string json)
        {
            if (json == "first") { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(5)); }
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
