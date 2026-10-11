using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SplitModel;

namespace SplitApp;

public partial class App : Application
{
    // The model project generated this composition.
    public static IServiceCollection AddServices(IServiceCollection services) => services.AddRunicViews();
}
