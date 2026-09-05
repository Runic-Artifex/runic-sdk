using System.Windows;
using CustomerMigration.Before;
using CustomerMigration.Domain;
namespace CustomerMigration.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Separate default data file allows both applications to be evaluated safely.
        string path = Environment.GetEnvironmentVariable("RUNIC_CUSTOMERS_FILE") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runic", "CustomerMigration", "before-customers.json");
        using var directory = new CustomerDirectory(path);
        new Application().Run(new CustomerWindow(new(new(directory, TimeSpan.FromMilliseconds(300)))));
    }
}
