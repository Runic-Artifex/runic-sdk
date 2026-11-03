using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>Registers the test platform fakes in place of the application's platform services.</summary>
public static class RunicTestPlatformServiceCollectionExtensions
{
    /// <summary>The environment variable that names a <see cref="RunicServeOnlyTestPlatform"/>'s directory.</summary>
    public const string DirectoryEnvironmentVariable = "RUNIC_TEST_PLATFORM_DIRECTORY";

    /// <summary>
    /// Registers a <see cref="RunicTestPlatform"/> for each window scope, as <see cref="IWindowPlatformServices"/>,
    /// <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/>, <see cref="IDesktopClipboard"/>,
    /// <see cref="IDesktopFileLauncher"/> and <see cref="IDesktopMessageBoxes"/>, and one
    /// <see cref="RunicTestNotifications"/> as <see cref="IDesktopNotifications"/> for the application.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// The registrations replace those of <c>AddRunicPlatformServices()</c>, whether it ran before or after this call.
    /// <see cref="RunicWindowTestHost.Create{TViewModel}(IServiceProvider, RunicWindowTestHostOptions?)"/> resolves the
    /// scope's platform as <see cref="RunicWindowTestHost{TViewModel}.Platform"/>; the scope closes it when it is
    /// disposed, and so does disposing the host.
    /// </remarks>
    public static IServiceCollection AddRunicTestPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<RunicTestNotifications>();
        services.RemoveAll<RunicTestPlatform>();
        services.AddSingleton<RunicTestNotifications>();
        services.AddScoped(static provider => new RunicTestPlatform(provider.GetRequiredService<RunicTestNotifications>()));
        return services.AddPlatformInterfaces(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// In a test build of an application started by a serve-only browser test with a
    /// <see cref="RunicServeOnlyTestPlatform"/>, registers test platform fakes that the test scripts across the process
    /// boundary; otherwise does nothing.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Serve-only mode and <see cref="DirectoryEnvironmentVariable"/> are set, but the application's entry assembly does
    /// not carry <see cref="RunicServeOnlyTestPlatformAttribute"/>, or the directory does not exist or other users can
    /// write to it.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The fakes are registered only when the build opted in and both <c>RUNIC_APPLICATION_SERVE_ONLY=1</c> and
    /// <see cref="DirectoryEnvironmentVariable"/> are set, which
    /// <see cref="RunicServeOnlyTestPlatform.Configure(System.Diagnostics.ProcessStartInfo)"/> does. The build opts in
    /// with <c>[assembly: RunicServeOnlyTestPlatform]</c> on the entry assembly, which a test-only configuration adds,
    /// for example with an <c>AssemblyAttribute</c> item; see the package README. When the fakes are registered, the
    /// first window logs warning 1158 <c>ServeOnlyTestPlatformActive</c>.
    /// </para>
    /// <para>
    /// Every window shares one <see cref="RunicTestPlatform"/>, since the script is one queue; closing a window does
    /// not close it. Call this method after the application's own <c>AddRunicPlatformServices()</c>.
    /// </para>
    /// <para>
    /// Production builds must not reference this package. Call this method only from a test build of the application.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRunicServeOnlyTestPlatform(this IServiceCollection services) =>
        AddServeOnlyTestPlatform(services, Assembly.GetEntryAssembly());

    internal static IServiceCollection AddServeOnlyTestPlatform(IServiceCollection services, Assembly? entryAssembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY") != "1" ||
            Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is not { Length: > 0 } directory)
        {
            return services;
        }
        if (entryAssembly?.IsDefined(typeof(RunicServeOnlyTestPlatformAttribute), inherit: false) != true)
        {
            throw new InvalidOperationException($"{DirectoryEnvironmentVariable} is set, but the application's entry assembly " +
                $"'{entryAssembly?.GetName().Name}' does not opt in with [assembly: RunicServeOnlyTestPlatform]; only a test build of " +
                "the application may use the serve-only test platform.");
        }
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"The test platform directory '{directory}' of {DirectoryEnvironmentVariable} does not exist.");
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException($"Other users can write to the test platform directory '{directory}' of " +
                $"{DirectoryEnvironmentVariable}; it must be writable only by its owner.");
        }
        var channel = new ServeOnlyPlatformChannel(directory);
        services.RemoveAll<RunicTestNotifications>();
        services.RemoveAll<RunicTestPlatform>();
        services.AddSingleton<RunicTestNotifications>();
        services.AddSingleton(provider =>
        {
            var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("Runic.Application.Testing") ?? NullLogger.Instance;
            TestPlatformLog.ServeOnlyTestPlatformActive(logger, directory);
            return new RunicTestPlatform(provider.GetRequiredService<RunicTestNotifications>(), channel);
        });
        // Singletons: a window scope must not dispose, and so close, the platform the other windows share.
        return services.AddPlatformInterfaces(ServiceLifetime.Singleton);
    }

    private static IServiceCollection AddPlatformInterfaces(this IServiceCollection services, ServiceLifetime lifetime)
    {
        services.RemoveAll<IDesktopNotifications>();
        services.AddSingleton<IDesktopNotifications>(static provider => provider.GetRequiredService<RunicTestNotifications>());
        Replace<IWindowPlatformServices>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>());
        Replace<IFileDialogs>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>().FileDialogs);
        Replace<ITextClipboard>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>().Clipboard);
        Replace<IDesktopClipboard>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>().Clipboard);
        Replace<IDesktopFileLauncher>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>());
        Replace<IDesktopMessageBoxes>(services, lifetime, static provider => provider.GetRequiredService<RunicTestPlatform>().MessageBoxes);
        Replace<IDesktopMonitors>(services, lifetime, static provider => ((IWindowPlatformServices)provider.GetRequiredService<RunicTestPlatform>()).Monitors);
        return services;
    }

    private static void Replace<T>(IServiceCollection services, ServiceLifetime lifetime, Func<IServiceProvider, T> factory) where T : class
    {
        services.RemoveAll<T>();
        services.Add(new ServiceDescriptor(typeof(T), factory, lifetime));
    }
}

internal static partial class TestPlatformLog
{
    [LoggerMessage(EventId = 1158, EventName = "ServeOnlyTestPlatformActive", Level = LogLevel.Warning,
        Message = "The serve-only test platform is active: scripted fakes replace the platform services and record their calls in {Directory}. Only a test build may run this way.")]
    internal static partial void ServeOnlyTestPlatformActive(ILogger logger, string directory);
}
