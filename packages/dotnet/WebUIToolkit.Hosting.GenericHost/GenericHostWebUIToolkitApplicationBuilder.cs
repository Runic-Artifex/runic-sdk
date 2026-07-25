using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebUIToolkit.Hosting;

/// <summary>
/// Composes a Generic Host and the frozen WebUIToolkit lifecycle without exposing a
/// service provider from the built application.
/// </summary>
public sealed class GenericHostWebUIToolkitApplicationBuilder
{
    private readonly HostApplicationBuilder _hostBuilder;
    private readonly WebUIToolkitApplicationBuilder _applicationBuilder = new();
    private bool _useLoggerSink = true;
    private bool _built;

    /// <summary>Creates a builder using captured, caller-supplied arguments.</summary>
    public GenericHostWebUIToolkitApplicationBuilder(string[]? arguments = null)
    {
        _hostBuilder = Host.CreateApplicationBuilder(arguments ?? []);
    }

    /// <summary>Gets Generic Host configuration during construction.</summary>
    public ConfigurationManager Configuration => _hostBuilder.Configuration;

    /// <summary>Gets Generic Host service registrations during construction.</summary>
    public IServiceCollection Services => _hostBuilder.Services;

    /// <summary>Gets Generic Host logging configuration during construction.</summary>
    public ILoggingBuilder Logging => _hostBuilder.Logging;

    /// <summary>Gets the framework-neutral lifecycle builder during construction.</summary>
    public WebUIToolkitApplicationBuilder Application => _applicationBuilder;

    /// <summary>Disables automatic structured lifecycle logging.</summary>
    public GenericHostWebUIToolkitApplicationBuilder DisableLifecycleLogging()
    {
        EnsureMutable();
        _useLoggerSink = false;
        return this;
    }

    /// <summary>Builds a single-use application and binds Generic Host stopping.</summary>
    public WebUIToolkitApplication Build()
    {
        EnsureMutable();
        _built = true;

        IHost host = _hostBuilder.Build();
        try
        {
            IHostApplicationLifetime lifetime =
                host.Services.GetRequiredService<IHostApplicationLifetime>();
            var hostBridge = new GenericHostApplicationHost(host, lifetime);
            _applicationBuilder.UseHost(hostBridge);
            if (_useLoggerSink)
            {
                ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
                _applicationBuilder.TryUseLifecycleEventSink(
                    new LoggerApplicationLifecycleEventSink(
                        loggerFactory.CreateLogger("WebUIToolkit.Hosting.Lifecycle")));
            }

            WebUIToolkitApplication application = _applicationBuilder.Build();
            hostBridge.BindStopController(application.StopController);
            return application;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "A Generic Host application builder can build exactly one application.");
        }
    }
}
