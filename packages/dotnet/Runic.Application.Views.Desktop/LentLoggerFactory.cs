using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views.Desktop;

// The application's ILoggerFactory, lent to a host that can outlive it. RunicDesktopHost.Run returns it when the
// application returns, before the caller disposes its services; a host message after that, such as an undeletable
// browser profile a minute later, goes to Trace instead of a disposed factory. A factory or logger that throws
// ObjectDisposedException while lent falls back to Trace too, so logging never breaks a release.
internal sealed class LentLoggerFactory(ILoggerFactory factory) : ILoggerFactory
{
    private volatile bool _returned;

    internal bool Returned => _returned;

    internal static LentLoggerFactory? From(ILoggerFactory? factory) => factory is null ? null : new(factory);

    // Resolves the factory registered in services; a disposed provider has none.
    internal static LentLoggerFactory? From(IServiceProvider? services)
    {
        try { return From(services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory); }
        catch (ObjectDisposedException) { return null; }
    }

    internal void Return() => _returned = true;

    public ILogger CreateLogger(string categoryName)
    {
        ILogger? logger = null;
        if (!_returned)
        {
            try { logger = factory.CreateLogger(categoryName); }
            catch (ObjectDisposedException) { }
        }
        return new LentLogger(this, logger);
    }

    // The application owns the factory and its providers.
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("The application owns this logger factory.");

    public void Dispose()
    {
    }

    private sealed class LentLogger(LentLoggerFactory owner, ILogger? logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (logger is null || owner.Returned) return null;
            try { return logger.BeginScope(state); }
            catch (ObjectDisposedException) { return null; }
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logger is not null && !owner.Returned)
            {
                try { return logger.IsEnabled(logLevel); }
                catch (ObjectDisposedException) { }
            }
            return DesktopTraceFallbackLogger.Instance.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logger is not null && !owner.Returned)
            {
                try
                {
                    logger.Log(logLevel, eventId, state, exception, formatter);
                    return;
                }
                catch (ObjectDisposedException) { }
            }
            DesktopTraceFallbackLogger.Instance.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

// Without a usable factory, Information and above go to System.Diagnostics.Trace, as Runic Desktop writes them
// without a logger: the shutdown notice as TraceInformation, warnings and errors as TraceWarning and TraceError.
internal sealed class DesktopTraceFallbackLogger : ILogger
{
    internal static readonly DesktopTraceFallbackLogger Instance = new();

    private DesktopTraceFallbackLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message = $"{message} {exception}";
        if (logLevel >= LogLevel.Error) Trace.TraceError(message);
        else if (logLevel == LogLevel.Warning) Trace.TraceWarning(message);
        else Trace.TraceInformation(message);
    }
}
