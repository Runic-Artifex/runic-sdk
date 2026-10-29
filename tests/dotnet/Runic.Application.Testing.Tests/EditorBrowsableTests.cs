using System.ComponentModel;
using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Types that only generated bridges call stay public, because generated code
// in application assemblies calls them, but stay out of completion lists and out
// of the application namespaces (D02: Runic.Application.Views.Generated).
internal sealed class EditorBrowsableTests
{
    private static readonly Type[] GeneratedCodeOnly =
    [
        typeof(PropertyDescriptor<>),
        typeof(CheckedPropertyDescriptor<>),
        typeof(CheckedFieldValueKind),
        typeof(CommandDescriptor<>),
        typeof(BridgeProtocol),
        typeof(ViewModelBridge<>),
        typeof(BridgeJson),
        typeof(BridgeWire),
        typeof(BridgeValueCodec<>),
        typeof(BridgeDataSubscriptionMember),
        typeof(BridgeCollectionDescriptor<>),
        typeof(BridgeInteractionDescriptor<>),
        typeof(BridgeValidation),
        typeof(BridgeValidationChild),
        typeof(BridgeContractShape),
        typeof(BridgeOperationResult),
        typeof(BridgeOperationResultKind),
        typeof(BridgeOperationDeliveryFailure),
        typeof(BridgeOperationDeliveryFailureKind),
        typeof(BridgeOperationStream),
        typeof(BridgeOperationStreamItem),
        typeof(BridgeOperationStreamRead),
        typeof(BridgeOperationExecution),
        typeof(ReactiveCommandExecution),
        typeof(ReactiveInteractionDescriptor),
    ];

    [Test]
    public void GeneratedCodeOnlyTypesAreHiddenFromEditors()
    {
        foreach (var type in GeneratedCodeOnly)
            Require(type.IsPublic && type.GetCustomAttribute<EditorBrowsableAttribute>(false)?.State == EditorBrowsableState.Never,
                $"{type.FullName} must stay public and carry [EditorBrowsable(EditorBrowsableState.Never)].");
    }

    [Test]
    public void GeneratedCodeOnlyTypesLiveInTheGeneratedNamespaces()
    {
        foreach (var type in GeneratedCodeOnly)
            Require(type.Namespace is { } ns && ns.EndsWith(".Generated", StringComparison.Ordinal),
                $"{type.FullName} must be in a .Generated namespace.");
        // Every other [EditorBrowsable(Never)] type of the core package is host-adapter API, not generated-code support.
        var hidden = typeof(WindowContentSession).Assembly.GetExportedTypes()
            .Where(type => type.GetCustomAttribute<EditorBrowsableAttribute>(false)?.State == EditorBrowsableState.Never
                && type.Namespace == "Runic.Application.Views"
                // Error tombstones of removed types (0.14) are hidden too; they are no API.
                && type.GetCustomAttribute<ObsoleteAttribute>(false) is not { IsError: true })
            .Select(type => type.Name).Order(StringComparer.Ordinal);
        Require(hidden.SequenceEqual(["IRunicWindowOpeningObserver", "RunicWindowOpening`1"]),
            $"Unexpected hidden types in Runic.Application.Views: {string.Join(", ", hidden)}");
    }

    // D46 c: the descriptor grows through init properties, not a positional constructor.
    [Test]
    public void CommandDescriptorTakesOnlyTheNameAndGetter()
    {
        var constructor = typeof(CommandDescriptor<object>).GetConstructors().Single();
        Require(constructor.GetParameters().Select(parameter => parameter.Name).SequenceEqual(["name", "get"]),
            "CommandDescriptor<T> must take only the command name and getter.");
        Require(typeof(CommandDescriptor<object>).GetMethod("<Clone>$") is null, "CommandDescriptor<T> must not be a record.");
        foreach (var property in typeof(CommandDescriptor<object>).GetProperties().Where(property => property.Name is not ("Name" or "Get")))
            Require(property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true,
                $"CommandDescriptor<T>.{property.Name} must be an init property.");
        Require(typeof(CheckedPropertyDescriptor<object>).GetConstructors().Single().GetParameters()
                .All(parameter => char.IsLower(parameter.Name![0])), "CheckedPropertyDescriptor<T> parameters must be camelCase.");
        Require(typeof(ViewModelBridge<>).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Count(constructor => constructor.IsFamily) == 1, "ViewModelBridge<T> must have one protected constructor.");
        Require(typeof(BridgeOperationResult).GetProperty("Empty") is null, "BridgeOperationResult.Empty must be gone.");
    }

    // D46 d: one options constructor (and the transport-only shorthand), no CancellationToken in the middle.
    [Test]
    public void WindowContentSessionHasTheOptionsConstructorsOnly()
    {
        var constructors = typeof(WindowContentSession).GetConstructors()
            .Select(constructor => string.Join(",", constructor.GetParameters().Select(parameter => parameter.ParameterType.Name)))
            .Order(StringComparer.Ordinal);
        Require(constructors.SequenceEqual(["IBridgeTransport", "IBridgeTransport,WindowContentSessionOptions"]),
            $"Unexpected WindowContentSession constructors: {string.Join("; ", constructors)}");
    }

    // D35 e: presentation bookkeeping and test-only session members are internal.
    [Test]
    public void SessionBookkeepingIsInternal()
    {
        Require(typeof(WindowContentSession).Assembly.GetType("Runic.Application.Views.IRunicWebPresentationLifetime") is { IsPublic: false },
            "IRunicWebPresentationLifetime must be internal.");
        foreach (var name in new[] { "Expose", "Suspend", "Forget", "BeginCloseAsync" })
            Require(typeof(WindowContentSession).GetMethod(name, BindingFlags.Instance | BindingFlags.Public) is null,
                $"WindowContentSession.{name} must not be public.");
        var drain = typeof(WindowContentSession).GetMethod(nameof(WindowContentSession.DrainAsync))!;
        Require(drain.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(TimeSpan), typeof(CancellationToken)]),
            "DrainAsync must take a timeout and a cancellation token.");
    }

    // D30 Views-11: severity grows with its value, as LogLevel does.
    [Test]
    public void WindowDiagnosticSeverityGrowsWithItsValue()
    {
        Require((int)RunicWindowDiagnosticSeverity.Information == 0 && (int)RunicWindowDiagnosticSeverity.Warning == 1
            && (int)RunicWindowDiagnosticSeverity.Error == 2, "RunicWindowDiagnosticSeverity must be Information, Warning, Error.");
        Require(new RunicWindowDiagnostic("code", "message").Severity == RunicWindowDiagnosticSeverity.Error,
            "A Window diagnostic must still default to Error.");
    }

    // D38 Views-10 and D21: the hot-reload notification is a standard event on a Runic* type.
    [Test]
    public void HotReloadRestartIsAnEventHandler()
    {
        var restart = typeof(RunicViewsHotReload).GetEvent(nameof(RunicViewsHotReload.RestartRequired))!;
        Require(restart.EventHandlerType == typeof(EventHandler<RunicHotReloadRestartEventArgs>),
            $"RestartRequired is {restart.EventHandlerType}.");
        Require(new RunicHotReloadRestartEventArgs("Counter").Mismatch == "Counter", "The event data lost its mismatch.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
