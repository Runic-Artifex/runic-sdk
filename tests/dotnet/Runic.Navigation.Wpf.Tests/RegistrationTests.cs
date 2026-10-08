using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// AddRunicWpfNavigation (W240-001 §8.2, §5.1).
internal static class RegistrationTests
{
    // Runs before the Application exists: resolution throws and never creates a dispatcher on the resolving thread.
    public static void WithoutApplication()
    {
        Require(Application.Current is null && Dispatcher.FromThread(Thread.CurrentThread) is null, "The test must run before any dispatcher exists.");
        using (var provider = new ServiceCollection().AddRunicWpfNavigation().BuildServiceProvider())
        {
            Require(Throws<InvalidOperationException>(() => provider.GetRequiredService<IRunicModelContext>()),
                "Resolving without an Application or a configured dispatcher did not throw.");
            Require(Throws<InvalidOperationException>(() => provider.GetRequiredService<RunicNavigator>()),
                "Resolving the navigator without an Application did not throw.");
        }
        Require(Dispatcher.FromThread(Thread.CurrentThread) is null, "Resolution created a dispatcher on the resolving thread.");

        Require(Throws<ArgumentException>(() => new ServiceCollection().AddRunicWpfNavigation(options =>
        {
            options.NavigatorLifetime = ServiceLifetime.Scoped;
            options.CreateEntryScopes = true;
        })), "Scoped with CreateEntryScopes did not throw.");
        Require(Throws<ArgumentException>(() => new ServiceCollection().AddRunicWpfNavigation(options =>
            options.NavigatorLifetime = ServiceLifetime.Transient)), "A transient navigator did not throw.");

        var scoped = new ServiceCollection().AddRunicWpfNavigation(options => options.NavigatorLifetime = ServiceLifetime.Scoped);
        Require(scoped.Single(descriptor => descriptor.ServiceType == typeof(RunicNavigator)).Lifetime == ServiceLifetime.Scoped
            && scoped.Single(descriptor => descriptor.ServiceType == typeof(IRunicModelContext)).Lifetime == ServiceLifetime.Singleton,
            "The scoped registration has the wrong lifetimes.");
        Require(!scoped.Any(descriptor => descriptor.ServiceType == typeof(INavigationViewLocator)),
            "A locator was registered without MapView or the convention.");
    }

    public static void WithApplication()
    {
        var provider = new ServiceCollection()
            .AddScoped<IRunicModelContext>(_ => throw new InvalidOperationException("Replaced."))
            .AddRunicWpfNavigation(options => options.MapView<DocumentViewModel, System.Windows.Controls.TextBox>())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        try
        {
            var context = provider.GetRequiredService<IRunicModelContext>();
            Require(context is DispatcherModelContext { Dispatcher: var dispatcher } && dispatcher == Application.Current.Dispatcher,
                "The model context isn't a DispatcherModelContext on the Application's dispatcher.");
            var navigator = provider.GetRequiredService<RunicNavigator>();
            using (var scope = provider.CreateScope())
                Require(ReferenceEquals(scope.ServiceProvider.GetRequiredService<RunicNavigator>(), navigator)
                    && ReferenceEquals(navigator.ModelContext, context), "The navigator isn't one singleton on the context.");
            Require(provider.GetService<INavigationViewLocator>() is not null, "MapView registered no locator.");

            // Entry scopes are on for the singleton navigator.
            var region = navigator.CreateRegion<object>(new object());
            Pump(region.PushAsync<CountingViewModel>());
            Require(region.CurrentEntry is { Services: var services } && !ReferenceEquals(services, provider),
                "A container-built entry of the singleton navigator has no entry scope.");
        }
        finally { Pump(provider.DisposeAsync().AsTask(), "provider disposal"); }
    }
}
