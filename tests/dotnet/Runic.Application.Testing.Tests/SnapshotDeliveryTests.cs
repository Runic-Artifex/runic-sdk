using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class SnapshotDeliveryTests
{
    public static async Task RunAsync()
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
        using var delivery = new BridgeSnapshotDelivery(slow, "rows", BridgeModelTurn.For(new Model()));
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
        transport, model, "probe", static (writer, vm, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", vm.Value);
            writer.WriteEndObject();
        }, [new("Value", vm => vm.Value, (vm, args) => vm.Value = checked((int)args.GetInt64()))], []);

    private sealed class ReentrantTransport : IBridgeTransport, IDisposable
    {
        public InMemoryViewTransport Inner { get; } = new();
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
