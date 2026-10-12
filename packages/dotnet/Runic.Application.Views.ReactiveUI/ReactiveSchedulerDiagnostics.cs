// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Navigation;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

// Event 1043 for one model-context main-thread scheduler: logged once per ViewModel type that schedules work
// outside a model turn (W260-015), so a second offending ViewModel is not hidden behind the first. The scheduler
// does not know which ViewModel scheduled the work, so the first time it sees a work item outside a turn it looks
// for the nearest application INotifyPropertyChanged type on the call stack: the ViewModel whose method, lambda or
// async continuation pushed the value, or whose constructor subscribed to a source that replays. A timer or a
// service raising the value has no such frame and is reported once as an unidentified source.
internal sealed class OutsideModelTurnReporter
{
    internal const string UnidentifiedSource = "an unidentified source";
    // Bounds the stack inspections when a pipeline schedules a new work item for each value.
    private const int MaxInspections = 1024;
    private static readonly object Seen = new();
    private readonly ConditionalWeakTable<object, object> _inspected = new();
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private int _inspections;

    internal void Report(ILogger logger, object? work, string fallback)
    {
        if (work is not null && !_inspected.TryAdd(work, Seen)) return;
        if (Interlocked.Increment(ref _inspections) > MaxInspections) return;
        var source = FindViewModel() is { } viewModel ? viewModel.FullName ?? viewModel.Name : UnidentifiedSource;
        if (_reported.TryAdd(source, 0)) ReactiveLog.OutsideModelTurn(logger, source, fallback);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Only names the ViewModel in a diagnostic. Without method metadata a frame is skipped, and the warning names an unidentified source.")]
    private static Type? FindViewModel()
    {
        foreach (var frame in new StackTrace(2, false).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            // Lambdas, local functions and async state machines are nested compiler-generated types.
            while (type is { IsNested: true } && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                type = type.DeclaringType;
            if (type is not null && typeof(INotifyPropertyChanged).IsAssignableFrom(type) && !IsLibrary(type.Assembly))
                return type;
        }
        return null;
    }

    private static bool IsLibrary(Assembly assembly)
    {
        if (assembly == typeof(OutsideModelTurnReporter).Assembly || assembly == typeof(RunicModelContext).Assembly
            || assembly == typeof(WindowContentSession).Assembly)
            return true;
        var name = assembly.GetName().Name ?? string.Empty;
        return name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name.StartsWith("ReactiveUI", StringComparison.Ordinal) || name.StartsWith("DynamicData", StringComparison.Ordinal)
            || name.StartsWith("Splat", StringComparison.Ordinal);
    }
}

// Events 1090 and 1091: when a Runic Desktop, CS-WebUI or test-host window opens and ReactiveUI's main-thread
// scheduler is not the model-context scheduler that AddRunicReactiveModelContext() installs. Each is logged once
// per scheduler type. A UI dispatcher's scheduler, such as WPF's DispatcherSequencer, is not reported: native UI
// state belongs on it, and the WPF host's model context is that dispatcher.
internal static class ReactiveSchedulerReports
{
    private static readonly ConcurrentDictionary<(bool Replaced, string Scheduler), byte> Reported = new();

    internal static void NotInEffect(ILoggerFactory? loggerFactory, Type scheduler, bool replaced)
    {
        if (scheduler.Name is "DispatcherSequencer" or "DispatcherScheduler") return;
        var name = scheduler.FullName ?? scheduler.Name;
        if (!Reported.TryAdd((replaced, name), 0)) return;
        var logger = loggerFactory?.CreateLogger(RunicViewsTelemetry.LogCategory) ?? TraceLogger.Instance;
        if (replaced) ReactiveLog.SchedulerReplaced(logger, name);
        else ReactiveLog.SchedulerKept(logger, name);
    }
}

// Registered by AddRunicReactiveModelContext(). Runic Desktop, CS-WebUI and the test host call it with the
// application's logger factory before they resolve a window's ViewModel.
internal sealed class ReactiveSchedulerWindowObserver : IRunicWindowOpeningObserver
{
    public void OnWindowOpening(ILoggerFactory? loggerFactory) => RunicReactiveSchedulerProvider.OnWindowOpening(loggerFactory);
}
