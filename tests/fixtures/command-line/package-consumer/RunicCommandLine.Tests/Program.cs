using RunicCommandLine.Tests;

return await TestRunner.RunAsync(
    GrammarCorpusTests.All,
    ParserAdversarialTests.All,
    OutputClassificationCorpusTests.All,
    CatalogTests.All,
    DiagnosticBoundaryTests.All,
    DispatcherTests.All,
    OutputTests.All,
    ProtocolCorpusTests.All);
