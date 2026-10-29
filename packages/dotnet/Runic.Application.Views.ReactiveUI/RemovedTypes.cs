// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Runic.Navigation;

#pragma warning disable CA1040 // Tombstones have no members: using one is the error.

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

// Tombstones (0.14): removed types that applications upgrading from older releases still name. Each is an error that
// names the replacement, instead of CS0246. They are removed in the release after 0.14.

/// <summary>Removed in 0.9: use <see cref="ReactiveSchedulers"/>.</summary>
[Obsolete("RunicReactiveSchedulerProvider was removed in 0.9: use ReactiveSchedulers.For(context) and " +
    "ReactiveSchedulers.InstallMainThreadScheduler(loggerFactory). See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RunicReactiveSchedulerProvider
{
    private RunicReactiveSchedulerProvider() { }

    /// <summary>Removed in 0.9: use <see cref="ReactiveSchedulers.InstallMainThreadScheduler"/>.</summary>
    [Obsolete("Use ReactiveSchedulers.InstallMainThreadScheduler(loggerFactory).", error: true)]
    public static bool InstallMainThreadScheduler(ILoggerFactory? loggerFactory = null) =>
        throw new NotSupportedException("Use ReactiveSchedulers.InstallMainThreadScheduler(loggerFactory).");
}

/// <summary>Removed in 0.9: use <c>IReactiveSchedulerProvider</c>.</summary>
[Obsolete("IRunicReactiveSchedulerProvider was removed in 0.9: use IReactiveSchedulerProvider, or ReactiveSchedulers.For(context). " +
    "See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IRunicReactiveSchedulerProvider;
