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

public static class WebUiApp
{
    public static WebUiAppBuilder CreateBuilder(string[]? arguments = null);
}

public sealed class WebUiAppBuilder
{
    public WebUiAppBuilder(string[]? arguments = null);
    public IReadOnlyList<string> Arguments { get; }
    public Microsoft.Extensions.Configuration.ConfigurationManager Configuration { get; }
    public Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; }
    public Microsoft.Extensions.Logging.ILoggingBuilder Logging { get; }
    public WebUIToolkitApplicationBuilder Application { get; }
    public TFeature GetOrAddFeature<TFeature>(Func<TFeature> factory) where TFeature : class;
    public bool TryGetFeature<TFeature>(out TFeature? feature) where TFeature : class;
    public WebUiAppBuilder OnBuilt(Action<WebUIToolkitApplication> action);
    public WebUiAppBuilder DisableLifecycleLogging();
    public WebUIToolkitApplication Build();
    public Task<int> RunAsync(CancellationToken cancellationToken = default);
}

public sealed class LoggerApplicationLifecycleEventSink : IApplicationLifecycleEventSink
{
    public LoggerApplicationLifecycleEventSink(Microsoft.Extensions.Logging.ILogger logger);
    public void Publish(ApplicationLifecycleEvent lifecycleEvent);
}
```
