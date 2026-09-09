using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Application;

/// <summary>Creates a minimal application host from the generated composition manifest.</summary>
public static class RunicApplication
{
    /// <summary>Creates a builder using the generated manifest in the calling application.</summary>
    public static RunicApplicationBuilder CreateBuilder(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new RunicApplicationBuilder(RunicApplicationManifestRegistry.GetRequired(), arguments);
    }
}

/// <summary>Receives the compile-time generated manifest before the application entry point runs.</summary>
public static class RunicApplicationManifestRegistry
{
    private static ApplicationCompositionManifest? _manifest;

    /// <summary>Registers the one generated manifest in the consuming application.</summary>
    public static void Register(ApplicationCompositionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Interlocked.CompareExchange(ref _manifest, manifest, null) is not null)
        {
            throw new InvalidOperationException("An application process can register exactly one generated Runic composition manifest.");
        }
    }

    internal static ApplicationCompositionManifest GetRequired() => Volatile.Read(ref _manifest) ?? throw new InvalidOperationException(
        "No generated Runic application manifest is available. Declare [assembly: RunicApplicationManifest(\"entry-point\")].");
}

/// <summary>Configures the small runtime shell around an immutable generated manifest.</summary>
public sealed class RunicApplicationBuilder
{
    private readonly string[] _arguments;
    private readonly ApplicationCompositionManifest _manifest;
    private IApplicationHost? _host;
    private readonly ServiceCollection _services = new();
    private bool _built;

    /// <summary>Initializes a builder from one immutable manifest.</summary>
    public RunicApplicationBuilder(ApplicationCompositionManifest manifest, string[] arguments)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = (string[])arguments.Clone();
        RunicApplicationBridgeCompositionRegistry.ConfigureServices(_services);
    }

    /// <summary>Gets the authoritative manifest consumed by this builder.</summary>
    public ApplicationCompositionManifest Manifest => _manifest;

    /// <summary>Gets application services, including generated bridge parts.</summary>
    public IServiceCollection Services => _services;

    /// <summary>Uses the one concrete host selected by platform integration.</summary>
    public RunicApplicationBuilder UseHost(IApplicationHost host)
    {
        ObjectDisposedException.ThrowIf(_built, this);
        _host = host ?? throw new ArgumentNullException(nameof(host));
        return this;
    }

    /// <summary>Freezes the manifest and selected host.</summary>
    public ApplicationHost Build()
    {
        ObjectDisposedException.ThrowIf(_built, this);
        IApplicationHost host = _host ?? throw new InvalidOperationException("Select a platform host such as UseDesktop before building the application.");
        _built = true;
        ServiceProvider services = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return new ApplicationHost(_manifest, _arguments, host, services);
    }
}

/// <summary>Runs one selected host against one immutable composition manifest.</summary>
public sealed class ApplicationHost : IAsyncDisposable
{
    private readonly string[] _arguments;
    private readonly IApplicationHost _host;
    private readonly ServiceProvider _services;
    private int _run;
    private Task? _stopping;
    private readonly object _stopGate = new();

    /// <summary>Initializes a host from the generated manifest and selected integration.</summary>
    public ApplicationHost(
        ApplicationCompositionManifest manifest,
        string[] arguments,
        IApplicationHost host,
        ServiceProvider services)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _arguments = arguments is null ? throw new ArgumentNullException(nameof(arguments)) : (string[])arguments.Clone();
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Capabilities = new ApplicationCapabilityProjection(Manifest, _host);
    }

    /// <summary>Gets the only composition authority for this application run.</summary>
    public ApplicationCompositionManifest Manifest { get; }

    /// <summary>Gets the selected host's explicit projection of manifest-declared capabilities.</summary>
    public ApplicationCapabilityProjection Capabilities { get; }

    /// <summary>Gets an immutable copy of launch arguments.</summary>
    public ReadOnlyMemory<string> Arguments => _arguments;

    /// <summary>Runs from a synchronous process entry point, servicing the selected host's main-thread event loop.</summary>
    public void Run(CancellationToken cancellationToken = default)
    {
        if (_host is IApplicationMainThreadHost mainThread) mainThread.Run(() => RunAsync(cancellationToken));
        else RunAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>Starts, waits for owned shutdown, then stops the selected host exactly once.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _run, 1) != 0)
        {
            throw new InvalidOperationException("An application host can run exactly once.");
        }

        try
        {
            await _host.StartAsync(Manifest, _arguments, _services, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // The start failure remains the observable fault; cleanup is best effort.
            }
            throw;
        }
        Exception? waitFailure = null;
        try
        {
            await _host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            waitFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch when (waitFailure is not null)
            {
                // The wait fault is the primary observable application failure.
            }
        }
    }

    private Task StopAsync()
    {
        lock (_stopGate) return _stopping ??= StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        List<Exception> errors = [];
        try
        {
            foreach (var participant in _services.GetServices<IApplicationStoppingParticipant>())
                try { await participant.StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        catch (Exception error) { errors.Add(error); }
        try { await _host.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Application shutdown failed.", errors);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _host.DisposeAsync().ConfigureAwait(false); }
        finally { await _services.DisposeAsync().ConfigureAwait(false); }
    }
}

/// <summary>Defines the minimal platform host boundary.</summary>
public interface IApplicationHost : IAsyncDisposable
{
    /// <summary>Starts from the generated manifest and snapshotted arguments.</summary>
    ValueTask StartAsync(
        ApplicationCompositionManifest manifest,
        ReadOnlyMemory<string> arguments,
        IServiceProvider services,
        CancellationToken cancellationToken);

    /// <summary>Completes only after the host's owned lifetime has ended.</summary>
    ValueTask WaitForShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Stops the host after a completed run.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Services native main-thread events for the entire application run, including shutdown.</summary>
public interface IApplicationMainThreadHost
{
    /// <summary>Runs from the process main thread until application work and cleanup complete.</summary>
    void Run(Func<Task> application);
}

/// <summary>Drains application-owned native resources before the host stops its event loop.</summary>
public interface IApplicationStoppingParticipant
{
    /// <summary>Stops accepting work and asynchronously drains submitted work. Implementations must be idempotent.</summary>
    ValueTask StopAsync();
}
