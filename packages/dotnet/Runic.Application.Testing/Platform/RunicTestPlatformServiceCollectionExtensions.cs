using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        return services.AddPlatformInterfaces();
    }

    /// <summary>
    /// In an application started by a serve-only browser test with a <see cref="RunicServeOnlyTestPlatform"/>, registers
    /// test platform fakes that the test scripts across the process boundary; otherwise does nothing.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <remarks>
    /// <para>
    /// The fakes are registered only when both <c>RUNIC_APPLICATION_SERVE_ONLY=1</c> and
    /// <see cref="DirectoryEnvironmentVariable"/> are set, which
    /// <see cref="RunicServeOnlyTestPlatform.Configure(System.Diagnostics.ProcessStartInfo)"/> does. Every window then
    /// shares one <see cref="RunicTestPlatform"/>, since the script is one queue. Call it after the application's own
    /// <c>AddRunicPlatformServices()</c>.
    /// </para>
    /// <para>
    /// Production builds must not reference this package. Call this method only from a test build of the application,
    /// for example under a build constant that a test-only configuration defines; see the package README.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRunicServeOnlyTestPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY") != "1" ||
            Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is not { Length: > 0 } directory)
        {
            return services;
        }
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"The test platform directory '{directory}' of {DirectoryEnvironmentVariable} does not exist.");
        var channel = new ServeOnlyPlatformChannel(directory);
        services.RemoveAll<RunicTestNotifications>();
        services.RemoveAll<RunicTestPlatform>();
        services.AddSingleton<RunicTestNotifications>();
        services.AddSingleton(provider => new RunicTestPlatform(provider.GetRequiredService<RunicTestNotifications>(), channel));
        return services.AddPlatformInterfaces();
    }

    private static IServiceCollection AddPlatformInterfaces(this IServiceCollection services)
    {
        services.RemoveAll<IDesktopNotifications>();
        services.AddSingleton<IDesktopNotifications>(static provider => provider.GetRequiredService<RunicTestNotifications>());
        Replace<IWindowPlatformServices>(services, static provider => provider.GetRequiredService<RunicTestPlatform>());
        Replace<IFileDialogs>(services, static provider => provider.GetRequiredService<RunicTestPlatform>().FileDialogs);
        Replace<ITextClipboard>(services, static provider => provider.GetRequiredService<RunicTestPlatform>().Clipboard);
        Replace<IDesktopClipboard>(services, static provider => provider.GetRequiredService<RunicTestPlatform>().Clipboard);
        Replace<IDesktopFileLauncher>(services, static provider => provider.GetRequiredService<RunicTestPlatform>());
        Replace<IDesktopMessageBoxes>(services, static provider => provider.GetRequiredService<RunicTestPlatform>().MessageBoxes);
        Replace<IDesktopMonitors>(services, static provider => ((IWindowPlatformServices)provider.GetRequiredService<RunicTestPlatform>()).Monitors);
        return services;
    }

    private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
    {
        services.RemoveAll<T>();
        services.AddScoped(factory);
    }
}
