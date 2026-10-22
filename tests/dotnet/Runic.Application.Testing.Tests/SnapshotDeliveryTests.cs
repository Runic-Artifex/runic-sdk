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
        _ = transport.Inner.Call("probeSetValue", new(Int64Value: 7));
        await transport.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (transport.Observed != 7)
            throw new InvalidOperationException("A native delivery callback could not read the model from outside its turn.");

        using var slow = new SlowTransport();
        using var delivery = new BridgeSnapshotDelivery(slow, "rows", BridgeModelTurn.For(new Model()), nameof(Model));
        try
        {
            if (!delivery.EnqueueDelta("first")) throw new InvalidOperationException("The first delta was rejected.");
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 64; index++)
                if (!delivery.EnqueueDelta($"delta {index}")) throw new InvalidOperationException("The bounded queue rejected a frame early.");
            if (delivery.EnqueueDelta("overflow")) throw new InvalidOperationException("A slow host retained an unbounded delta queue.");
            delivery.Enqueue("recovery snapshot");
            slow.Release.Set();
            await slow.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!slow.Frames.SequenceEqual(["first", "recovery snapshot"]))
                throw new InvalidOperationException("A recovery snapshot did not supersede pending deltas.");
        }
        finally { slow.Release.Set(); }
    }

    // I-018: a UTF-8 transport receives exactly the bytes a string transport's frames encode to, in the same order.
    [Test]
    public async Task Utf8TransportsReceiveTheBytesOfTheStringFramesAsync()
    {
        string[] frames = ["""{"title":"Grüße 🎉","n":1}""", """{"delta":["ä","\u2028"]}""", """{"error":"ok"}"""];
        using var strings = new RecordingTransport();
        using var bytes = new RecordingUtf8Transport();
        using var memory = new InMemoryBridgeTransport();
        var turn = BridgeModelTurn.For(new Model());
        using var toStrings = new BridgeSnapshotDelivery(strings, "rows", turn, nameof(Model));
        using var toBytes = new BridgeSnapshotDelivery(bytes, "rows", turn, nameof(Model));
        using var toMemory = new BridgeSnapshotDelivery(memory, "rows", turn, nameof(Model));
        var published = new List<string>();
        for (var index = 0; index < frames.Length; index++)
        {
            // One frame at a time: a state supersedes frames still queued before it.
            Action<BridgeSnapshotDelivery> enqueue = index switch
            {
                0 => delivery => delivery.Enqueue(frames[0]),
                1 => delivery => { if (!delivery.EnqueueDelta(frames[1])) throw new InvalidOperationException("A delta was rejected."); },
                _ => delivery => delivery.EnqueueFailure(frames[2]),
            };
            enqueue(toStrings);
            enqueue(toBytes);
            enqueue(toMemory);
            var count = index + 1;
            await WaitUntilAsync(() =>
            {
                published.AddRange(memory.TakePublications().Select(publication => publication.Json));
                return strings.Count == count && bytes.Count == count && published.Count == count;
            });
        }
        if (!strings.Frames.SequenceEqual(frames) || !published.SequenceEqual(frames))
            throw new InvalidOperationException($"The string frames differ: [{string.Join(", ", strings.Frames)}], [{string.Join(", ", published)}].");
        if (!bytes.Frames.Zip(frames).All(pair => pair.First.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(pair.Second))))
            throw new InvalidOperationException(
                $"The UTF-8 frames differ from the string frames: [{string.Join(", ", bytes.Frames.Select(Encoding.UTF8.GetString))}].");
        if (bytes.StringCalls != 0) throw new InvalidOperationException("The Bridge called the string path of a UTF-8 transport.");
    }

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

    private sealed class RecordingTransport : IBridgeTransport, IDisposable
    {
        private readonly List<string> _frames = [];
        public string[] Frames { get { lock (_frames) return [.. _frames]; } }
        public int Count { get { lock (_frames) return _frames.Count; } }
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => throw new NotSupportedException();
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            throw new NotSupportedException();
        public void Publish(string name, string stateJson)
        {
            lock (_frames) _frames.Add(stateJson);
        }
        public void Dispose() { }
    }

    private sealed class RecordingUtf8Transport : IBridgeUtf8Transport, IDisposable
    {
        private readonly List<byte[]> _frames = [];
        public byte[][] Frames { get { lock (_frames) return [.. _frames]; } }
        public int Count { get { lock (_frames) return _frames.Count; } }
        public int StringCalls { get; private set; }
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => throw new NotSupportedException();
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            throw new NotSupportedException();
        public void Publish(string name, string stateJson) => StringCalls++;
        public ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default)
        {
            // The memory is valid only until the returned task completes.
            lock (_frames) _frames.Add(stateUtf8.ToArray());
            return ValueTask.CompletedTask;
        }
        public void Dispose() { }
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

    private sealed class ReentrantTransport : IBridgeTransport, IDisposable
    {
        public InMemoryBridgeTransport Inner { get; } = new();
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Observed { get; private set; }
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => Inner.Bind(name, handler);
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            Inner.BindAsync(name, handler);
        public void Publish(string name, string stateJson)
        {
            try
            {
                using var reply = JsonDocument.Parse(Inner.Call("probeSnapshot"));
                Observed = reply.RootElement.GetProperty("state").GetProperty("value").GetInt32();
                Delivered.TrySetResult();
            }
            catch (Exception error) { Delivered.TrySetException(error); }
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class SlowTransport : IBridgeTransport, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public List<string> Frames { get; } = [];
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => throw new NotSupportedException();
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) => throw new NotSupportedException();
        public void Publish(string name, string stateJson)
        {
            if (stateJson == "first") { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(5)); }
            Frames.Add(stateJson);
            if (stateJson == "recovery snapshot") Completed.TrySetResult();
        }
        public void Dispose() => Release.Dispose();
    }
}
