// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive.Builder;

namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
using ReactiveUI.Builder;

namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>Configures ReactiveUI's builder for Runic windows.</summary>
public static class RunicReactiveUIBuilderExtensions
{
    /// <summary>
    /// Makes ReactiveUI's main-thread scheduler the model-context scheduler that
    /// <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/> installs, as <c>WithAvalonia()</c>,
    /// <c>WithMaui()</c> and <c>WithWpf()</c> set their platform's scheduler. Use it when the application already
    /// builds ReactiveUI with <c>RxAppBuilder.CreateReactiveUIBuilder()</c>: <c>BuildApp()</c> then keeps Runic's
    /// scheduler instead of replacing it with ReactiveUI's default.
    /// </summary>
    /// <remarks>
    /// The builder applies the scheduler in <c>BuildApp()</c>, through <c>WithMainThreadScheduler</c>, so a later
    /// <c>WithMainThreadScheduler</c> call on the same builder replaces it. Still call
    /// <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/>, which registers the model context
    /// and its scheduler; both calls use the same scheduler.
    /// </remarks>
    /// <param name="builder">The ReactiveUI builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IReactiveUIBuilder WithRunicModelContext(this IReactiveUIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMainThreadScheduler(RunicReactiveSchedulerProvider.BuilderScheduler());
    }
}
