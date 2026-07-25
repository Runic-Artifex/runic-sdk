# WebUIToolkit.Hosting.GenericHost public API

All public types intentionally use the `WebUIToolkit.Hosting` namespace.

```csharp
public sealed class GenericHostApplicationHost : IApplicationHost
{
    public GenericHostApplicationHost(
        Microsoft.Extensions.Hosting.IHost host,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime);
    public void BindStopController(IApplicationStopController stopController);
    public ValueTask StartAsync(CancellationToken cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}

public sealed class GenericHostWebUIToolkitApplicationBuilder
{
    public GenericHostWebUIToolkitApplicationBuilder(string[]? arguments = null);
    public Microsoft.Extensions.Configuration.ConfigurationManager Configuration { get; }
    public Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; }
    public Microsoft.Extensions.Logging.ILoggingBuilder Logging { get; }
    public WebUIToolkitApplicationBuilder Application { get; }
    public GenericHostWebUIToolkitApplicationBuilder DisableLifecycleLogging();
    public WebUIToolkitApplication Build();
}

public sealed class LoggerApplicationLifecycleEventSink : IApplicationLifecycleEventSink
{
    public LoggerApplicationLifecycleEventSink(Microsoft.Extensions.Logging.ILogger logger);
    public void Publish(ApplicationLifecycleEvent lifecycleEvent);
}
```
