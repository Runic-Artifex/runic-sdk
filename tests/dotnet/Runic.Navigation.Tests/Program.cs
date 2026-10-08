using Runic.Navigation.Tests;

await ModelContextTests.RunAsync();
await NavigationTests.RunAsync();
await NavigationTests.RunThreadingAsync();
NavigationSourceScanTests.Run();
Console.WriteLine("Runic.Navigation tests passed.");
