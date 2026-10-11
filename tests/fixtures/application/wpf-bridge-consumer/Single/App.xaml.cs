using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace WpfSingle;

public partial class App : Application
{
    // Resolves only when the bridge composition was generated in this project.
    public static IServiceCollection AddServices(IServiceCollection services) => services.AddRunicViews();
}
