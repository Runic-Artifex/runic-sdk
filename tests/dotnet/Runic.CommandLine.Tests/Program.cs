using Runic.CommandLine.Tests;

return await TestRunner.RunAsync(
    ApplicationTests.All,
    GrammarCorpusTests.All,
    ParserAdversarialTests.All,
    OutputClassificationCorpusTests.All,
    CatalogTests.All,
    DiagnosticBoundaryTests.All,
    DispatcherTests.All,
    OutputTests.All,
    ProtocolCorpusTests.All);
