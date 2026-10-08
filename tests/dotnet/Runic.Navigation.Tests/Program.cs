using Runic.Navigation.Tests;

await ModelContextTests.RunAsync();
await NavigationTests.RunAsync();
await NavigationTests.RunThreadingAsync();
await NavigationTests.RunBackRacesAsync();
NavigationSourceScanTests.Run();
Console.WriteLine("Runic.Navigation tests passed.");
