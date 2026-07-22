namespace WebUIToolkit.MVVM.Build.Tests;

internal static class Program
{
    public static int Main()
    {
        TestRunner runner = new();
        ParserContractTests.Register(runner);
        ParserCorpusTests.Register(runner);
        SemanticContractTests.Register(runner);
        GeneratorContractTests.Register(runner);
        IncrementalContractTests.Register(runner);
        BuildIntegrationTests.Register(runner);
        HostileInputTests.Register(runner);
        return runner.Run();
    }
}
