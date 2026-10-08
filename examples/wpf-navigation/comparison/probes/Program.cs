using Comparison.Probes;

Console.WriteLine("ReactiveUI 26.0.1 RoutingState (real code; dispatcher emulated by a posting sequencer)");
foreach (var line in RoutingStateProbe.Run())
{
    Console.WriteLine("  " + line);
}

Console.WriteLine("Prism.Core 9.0.537 RegionNavigationJournal (real code) + port of Prism.Wpf RegionNavigationService ordering");
foreach (var line in PrismProbe.Run())
{
    Console.WriteLine("  " + line);
}
