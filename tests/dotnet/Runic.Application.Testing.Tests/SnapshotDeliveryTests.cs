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
}
