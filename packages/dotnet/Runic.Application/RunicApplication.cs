using System;
using System.Threading;
using System.Threading.Tasks;

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
    private bool _built;

    /// <summary>Initializes a builder from one immutable manifest.</summary>
    public RunicApplicationBuilder(ApplicationCompositionManifest manifest, string[] arguments)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = (string[])arguments.Clone();
    }

    /// <summary>Gets the authoritative manifest consumed by this builder.</summary>
    public ApplicationCompositionManifest Manifest => _manifest;

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
        _built = true;
        return new ApplicationHost(_manifest, _arguments, _host ?? throw new InvalidOperationException(
            "Select a platform host such as UseDesktop before building the application."));
    }
}

/// <summary>Runs one selected host against one immutable composition manifest.</summary>
public sealed class ApplicationHost : IAsyncDisposable
{
    private readonly string[] _arguments;
    private readonly IApplicationHost _host;
    private int _run;

    /// <summary>Initializes a host from the generated manifest and selected integration.</summary>
    public ApplicationHost(ApplicationCompositionManifest manifest, string[] arguments, IApplicationHost host)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _arguments = arguments is null ? throw new ArgumentNullException(nameof(arguments)) : (string[])arguments.Clone();
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Capabilities = new ApplicationCapabilityProjection(Manifest, _host);
    }

    /// <summary>Gets the only composition authority for this application run.</summary>
    public ApplicationCompositionManifest Manifest { get; }

    /// <summary>Gets the selected host's explicit projection of manifest-declared capabilities.</summary>
    public ApplicationCapabilityProjection Capabilities { get; }

    /// <summary>Gets an immutable copy of launch arguments.</summary>
    public ReadOnlyMemory<string> Arguments => _arguments;

    /// <summary>Starts, waits for owned shutdown, then stops the selected host exactly once.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _run, 1) != 0)
        {
            throw new InvalidOperationException("An application host can run exactly once.");
        }

        try
        {
            await _host.StartAsync(Manifest, _arguments, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
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
                await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch when (waitFailure is not null)
            {
                // The wait fault is the primary observable application failure.
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

/// <summary>Defines the minimal platform host boundary.</summary>
public interface IApplicationHost : IAsyncDisposable
{
    /// <summary>Starts from the generated manifest and snapshotted arguments.</summary>
    ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, CancellationToken cancellationToken);

    /// <summary>Completes only after the host's owned lifetime has ended.</summary>
    ValueTask WaitForShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Stops the host after a completed run.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}
