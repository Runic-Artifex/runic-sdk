using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// D-1: a failed route carries the exception type, message and stack only when its
// window allows it (development by default). Production replies keep the bounded message.
internal sealed class FailureDetailTests
{
    [Test]
    public void EnvironmentSelectsDevelopment()
    {
        static Func<string, string?> Environment(string? dotnet, string? aspnetcore) => name => name switch
        {
            "DOTNET_ENVIRONMENT" => dotnet,
            "ASPNETCORE_ENVIRONMENT" => aspnetcore,
            _ => null,
        };
        Require(BridgeFailureDetails.IsDevelopment(Environment("Development", null)), "DOTNET_ENVIRONMENT=Development was not development.");
        Require(BridgeFailureDetails.IsDevelopment(Environment("development", null)), "The environment name must compare case-insensitively.");
        Require(BridgeFailureDetails.IsDevelopment(Environment(null, "Development")), "ASPNETCORE_ENVIRONMENT=Development was not development.");
        Require(!BridgeFailureDetails.IsDevelopment(Environment("Production", "Development")), "DOTNET_ENVIRONMENT must take precedence.");
        Require(!BridgeFailureDetails.IsDevelopment(Environment(null, null)), "An unset environment must be production.");
    }

    // D38 Views-09: failure detail is a window option, read when the window opens, not a process-wide switch.
    [Test]
    public void TheWindowOptionDecidesAndDefaultsToTheEnvironment()
    {
        using var transport = new InMemoryViewTransport();
        using (var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = true }))
            Require(session.IncludesFailureDetail, "The explicit opt-in was ignored.");
        using (var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false }))
            Require(!session.IncludesFailureDetail, "The explicit opt-out was ignored.");
        using (var session = new WindowContentSession(transport))
            Require(session.IncludesFailureDetail == BridgeFailureDetails.DefaultIncluded, "The default did not defer to the environment.");
    }

    [Test]
    public void TwoWindowsInOneProcessKeepTheirOwnSetting()
    {
        using var productionTransport = new InMemoryViewTransport();
        using var developmentTransport = new InMemoryViewTransport();
        using var production = new WindowContentSession(productionTransport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        using var development = new WindowContentSession(developmentTransport, new WindowContentSessionOptions { IncludeFailureDetail = true });
        using var productionBridge = new FailingBridge(productionTransport, new Model(), production);
        using var developmentBridge = new FailingBridge(developmentTransport, new Model(), development);
        using var productionReply = JsonDocument.Parse(productionTransport.Call("failingThrow"));
        using var developmentReply = JsonDocument.Parse(developmentTransport.Call("failingThrow"));
        Require(!productionReply.RootElement.GetProperty("error").TryGetProperty("detail", out _),
            $"The production window leaked detail: {productionReply.RootElement}");
        Require(developmentReply.RootElement.GetProperty("error").TryGetProperty("detail", out _),
            $"The development window lost its detail: {developmentReply.RootElement}");
    }

    [Test]
    public void ProductionKeepsTheBoundedMessage()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        using var bridge = new FailingBridge(transport, new Model(), session);
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

    [Test]
    public void DevelopmentAddsTheExceptionDetail()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = true });
        using var bridge = new FailingBridge(transport, new Model(), session);
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

    [Test]
    public async Task ProductionOperationKeepsTheBoundedMessageAsync()
    {
        using var failed = await FailOperationAsync("production-failure", includeFailureDetail: false);
        var error = failed.RootElement.GetProperty("error");
        Require(error.GetProperty("message").GetString() == "The operation failed." && !error.TryGetProperty("detail", out _),
            $"A production operation failure leaked detail: {failed.RootElement}");
    }

    [Test]
    public async Task DevelopmentOperationAddsTheExceptionDetailAsync()
    {
        using var failed = await FailOperationAsync("development-failure", includeFailureDetail: true);
        var error = failed.RootElement.GetProperty("error");
        Require(error.GetProperty("message").GetString() == "The operation failed."
            && error.GetProperty("detail").GetProperty("message").GetString() == "Operation failed for customer-secret.",
            $"A development operation failure did not carry its detail: {failed.RootElement}");
    }

    private static async Task<JsonDocument> FailOperationAsync(string requestId, bool includeFailureDetail)
    {
        var model = new Model();
        using var host = new RunicWindowTestHost<Model>(model,
            (transport, content, vm) => new FailingBridge(transport, vm, content),
            new RunicWindowTestHostOptions { RootRoute = "failing", ViewLocator = new TestViewLocator(), IncludeFailureDetail = includeFailureDetail });
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
        : ViewModelBridge<Model>(transport, model, "failing", (writer, _, revision, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        },
        [new("Value", vm => vm.Value, (vm, e) => vm.Value = e.GetString())], [],
        [
            new("Throw", vm => vm.Throw),
            new("FailAsync", vm => vm.Throw) { ExecuteAsync = (_, _, _) => Task.FromException(new InvalidOperationException("Operation failed for customer-secret.")) },
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
