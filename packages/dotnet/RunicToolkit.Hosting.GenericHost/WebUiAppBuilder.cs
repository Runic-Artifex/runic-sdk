using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RunicToolkit.Hosting;

/// <summary>Creates the shared high-level application builder used by every frontend.</summary>
public static class WebUiApp
{
    /// <summary>Creates a Generic Host-backed application builder.</summary>
    public static WebUiAppBuilder CreateBuilder(string[]? arguments = null) => new(arguments);
}

/// <summary>
/// Shared high-level application builder. Frontend packages add their own extension
/// members while the common builder owns hosting, lifecycle, and application execution.
/// </summary>
public sealed class WebUiAppBuilder
{
    private readonly GenericHostRunicToolkitApplicationBuilder _host;
    private readonly string[] _arguments;
    private readonly Dictionary<Type, object> _features = [];
    private readonly List<Action<RunicToolkitApplication>> _builtActions = [];
    private bool _built;

    /// <summary>Creates a builder using captured, caller-supplied arguments.</summary>
    public WebUiAppBuilder(string[]? arguments = null)
    {
        _arguments = arguments is null ? [] : (string[])arguments.Clone();
        Arguments = Array.AsReadOnly(_arguments);
        _host = new GenericHostRunicToolkitApplicationBuilder(_arguments);
    }

    /// <summary>Gets the immutable launch argument snapshot.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets Generic Host configuration during construction.</summary>
    public ConfigurationManager Configuration => _host.Configuration;

    /// <summary>Gets Generic Host service registrations during construction.</summary>
    public IServiceCollection Services => _host.Services;

    /// <summary>Gets Generic Host logging configuration during construction.</summary>
    public ILoggingBuilder Logging => _host.Logging;

    /// <summary>Gets the framework-neutral lifecycle builder during construction.</summary>
    public RunicToolkitApplicationBuilder Application => _host.Application;

    /// <summary>
    /// Gets or creates extension-owned state without requiring the common builder to
    /// reference the extending frontend package.
    /// </summary>
    public TFeature GetOrAddFeature<TFeature>(Func<TFeature> factory)
        where TFeature : class
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        if (_features.TryGetValue(typeof(TFeature), out object? existing))
        {
            return (TFeature)existing;
        }

        TFeature feature = factory() ??
            throw new InvalidOperationException("A WebUiApp feature factory returned null.");
        _features.Add(typeof(TFeature), feature);
        return feature;
    }

    /// <summary>Looks up state contributed by a frontend extension package.</summary>
    public bool TryGetFeature<TFeature>(out TFeature? feature)
        where TFeature : class
    {
        if (_features.TryGetValue(typeof(TFeature), out object? value))
        {
            feature = (TFeature)value;
            return true;
        }

        feature = null;
        return false;
    }

    /// <summary>
    /// Registers an extension hook that receives the frozen application. This is
    /// primarily used to bind adapter state composed before the lifecycle is built.
    /// </summary>
    public WebUiAppBuilder OnBuilt(Action<RunicToolkitApplication> action)
    {
        EnsureMutable();
        _builtActions.Add(action ?? throw new ArgumentNullException(nameof(action)));
        return this;
    }

    /// <summary>Disables automatic structured lifecycle logging.</summary>
    public WebUiAppBuilder DisableLifecycleLogging()
    {
        EnsureMutable();
        _host.DisableLifecycleLogging();
        return this;
    }

    /// <summary>Freezes the shared host and all frontend registrations.</summary>
    public RunicToolkitApplication Build()
    {
        EnsureMutable();
        _built = true;
        RunicToolkitApplication application = _host.Build();
        try
        {
            foreach (Action<RunicToolkitApplication> action in _builtActions)
            {
                action(application);
            }

            return application;
        }
        catch
        {
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Builds, runs, and disposes the configured application.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using RunicToolkitApplication application = Build();
        ApplicationRunResult result = await application
            .RunAsync(_arguments, cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode ?? 1;
    }

    private void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "A WebUiApp builder cannot be changed or built again after Build().");
        }
    }
}
