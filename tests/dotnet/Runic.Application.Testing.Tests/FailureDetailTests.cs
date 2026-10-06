using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// D-1: a failed route carries the exception type, message and stack only in
// development. Production replies keep the bounded message.
internal static class FailureDetailTests
{
    public static async Task RunAsync()
    {
        EnvironmentSelectsDevelopment();
        var previous = BridgeDiagnostics.IncludeFailureDetail;
        try
        {
            BridgeDiagnostics.IncludeFailureDetail = false;
            ProductionKeepsTheBoundedMessage();
            await ProductionOperationKeepsTheBoundedMessageAsync();
            BridgeDiagnostics.IncludeFailureDetail = true;
            DevelopmentAddsTheExceptionDetail();
            await DevelopmentOperationAddsTheExceptionDetailAsync();
        }
        finally
        {
            BridgeDiagnostics.IncludeFailureDetail = previous;
        }
    }

    private static void EnvironmentSelectsDevelopment()
    {
        static Func<string, string?> Environment(string? dotnet, string? aspnetcore) => name => name switch
        {
            "DOTNET_ENVIRONMENT" => dotnet,
            "ASPNETCORE_ENVIRONMENT" => aspnetcore,
            _ => null,
        };
        Require(BridgeDiagnostics.IsDevelopment(Environment("Development", null)), "DOTNET_ENVIRONMENT=Development was not development.");
        Require(BridgeDiagnostics.IsDevelopment(Environment("development", null)), "The environment name must compare case-insensitively.");
        Require(BridgeDiagnostics.IsDevelopment(Environment(null, "Development")), "ASPNETCORE_ENVIRONMENT=Development was not development.");
        Require(!BridgeDiagnostics.IsDevelopment(Environment("Production", "Development")), "DOTNET_ENVIRONMENT must take precedence.");
        Require(!BridgeDiagnostics.IsDevelopment(Environment(null, null)), "An unset environment must be production.");

        var previous = BridgeDiagnostics.IncludeFailureDetail;
        try
        {
            BridgeDiagnostics.IncludeFailureDetail = null;
            Require(BridgeDiagnostics.IncludeFailureDetail is null, "The default must defer to the environment.");
            BridgeDiagnostics.IncludeFailureDetail = true;
            Require(BridgeDiagnostics.IncludesFailureDetail, "The explicit opt-in was ignored.");
            BridgeDiagnostics.IncludeFailureDetail = false;
            Require(!BridgeDiagnostics.IncludesFailureDetail, "The explicit opt-out was ignored.");
        }
        finally
        {
            BridgeDiagnostics.IncludeFailureDetail = previous;
        }
    }

    private static void ProductionKeepsTheBoundedMessage()
    {
        using var transport = new InMemoryViewTransport();
        using var bridge = new FailingBridge(transport, new Model());
        foreach (var route in new[] { "failingThrow", "failingSetValue" })
        {
            using var reply = JsonDocument.Parse(transport.Call(route, new(StringValue: "x")));
            var error = reply.RootElement.GetProperty("error");
            Require(!reply.RootElement.GetProperty("ok").GetBoolean() && error.GetProperty("kind").GetString() == "failed",
                $"{route} did not fail.");
            Require(!error.TryGetProperty("detail", out _), $"A production reply of {route} leaked exception detail: {reply.RootElement}");
            Require(!reply.RootElement.GetRawText().Contains("customer-secret", StringComparison.Ordinal),
                $"A production reply of {route} leaked the exception message.");
        }
        using var thrown = JsonDocument.Parse(transport.Call("failingThrow"));
        Require(thrown.RootElement.GetProperty("error").GetProperty("message").GetString() == "Throw failed.",
            "The production message changed.");
    }

    private static void DevelopmentAddsTheExceptionDetail()
    {
        using var transport = new InMemoryViewTransport();
        using var bridge = new FailingBridge(transport, new Model());
        using var reply = JsonDocument.Parse(transport.Call("failingThrow"));
        var error = reply.RootElement.GetProperty("error");
        Require(error.GetProperty("message").GetString() == "Throw failed.", "Development changed the bounded message.");
        var detail = error.GetProperty("detail");
        Require(detail.GetProperty("type").GetString() == typeof(InvalidOperationException).FullName,
            $"The detail did not name the exception type: {detail}");
        Require(detail.GetProperty("message").GetString() == "Disk full for customer-secret.",
            $"The detail did not carry the exception message: {detail}");
        var stack = detail.GetProperty("stack").GetString()!;
        Require(stack.Contains(nameof(Model.ThrowFromHandler), StringComparison.Ordinal),
            $"The detail stack did not name the throwing method: {stack}");

        using var setter = JsonDocument.Parse(transport.Call("failingSetValue", new(StringValue: "x")));
        Require(setter.RootElement.GetProperty("error").GetProperty("detail").GetProperty("type").GetString()
            == typeof(InvalidOperationException).FullName, "A failed setter did not carry its detail.");
    }

    private static async Task ProductionOperationKeepsTheBoundedMessageAsync()
    {
        using var failed = await FailOperationAsync("production-failure");
        var error = failed.RootElement.GetProperty("error");
        Require(error.GetProperty("message").GetString() == "The operation failed." && !error.TryGetProperty("detail", out _),
            $"A production operation failure leaked detail: {failed.RootElement}");
    }

    private static async Task DevelopmentOperationAddsTheExceptionDetailAsync()
    {
        using var failed = await FailOperationAsync("development-failure");
        var error = failed.RootElement.GetProperty("error");
        Require(error.GetProperty("message").GetString() == "The operation failed."
            && error.GetProperty("detail").GetProperty("message").GetString() == "Operation failed for customer-secret.",
            $"A development operation failure did not carry its detail: {failed.RootElement}");
    }

    private static async Task<JsonDocument> FailOperationAsync(string requestId)
    {
        var model = new Model();
        using var host = new RunicWindowTestHost<Model>(model, "failing",
            (transport, content, vm) => new FailingBridge(transport, vm, content), new TestViewLocator());
        using var admission = JsonDocument.Parse(host.Transport.Call("failingStartFailAsync", new(StringValue: requestId)));
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        return JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))));
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private readonly string _value = "";
        public Model() => Throw = new DelegateCommand(ThrowFromHandler);
        public ICommand Throw { get; }
        public string Value
        {
            get => _value;
            set => throw new InvalidOperationException($"Cannot store {value} for customer-secret.");
        }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public static void ThrowFromHandler() => throw new InvalidOperationException("Disk full for customer-secret.");
    }

    private sealed class FailingBridge(IBridgeTransport transport, Model model, WindowContentSession? content = null)
        : ViewModelBridge<Model>(transport, model, "failing", (writer, _, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        },
        [new("Value", vm => vm.Value, (vm, e) => vm.Value = e.GetString())],
        [
            new("Throw", vm => vm.Throw),
            new("FailAsync", vm => vm.Throw,
                ExecuteAsync: (_, _, _) => Task.FromException(new InvalidOperationException("Operation failed for customer-secret."))),
        ],
        contractFingerprint: "failure-detail", content: content);

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
