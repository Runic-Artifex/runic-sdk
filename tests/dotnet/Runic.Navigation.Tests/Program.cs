using Runic.Navigation.Tests;

await ModelContextTests.RunAsync();
await NavigationTests.RunAsync();
await NavigationTests.RunThreadingAsync();
await NavigationTests.RunBackRacesAsync();
await NavigationTests.RunContextLifetimeAsync();
await NavigationTests.RunRetentionAsync();
NavigationSourceScanTests.Run();
Console.WriteLine("Runic.Navigation tests passed.");
