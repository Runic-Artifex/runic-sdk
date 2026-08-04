namespace RunicCommandLine.Tests;

internal static class DiagnosticBoundaryTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("diagnostics/kind-and-default-message-key-utf8-boundaries", KindAndMessageKeyBoundaries),
    ];

    private static ValueTask KindAndMessageKeyBoundaries()
    {
        CommandDiagnostic explicitKey = Create(new string('a', 128), "k");
        AssertEx.Equal(128, explicitKey.Kind.Length);
        AssertEx.Equal("k", explicitKey.MessageKey);

        AssertEx.Throws<ArgumentException>(() => Create(new string('a', 129), "k"));

        CommandDiagnostic defaultBoundary = Create(new string('a', 116));
        AssertEx.Equal(128, defaultBoundary.MessageKey.Length);
        AssertEx.Throws<ArgumentException>(() => Create(new string('a', 117)));
        return ValueTask.CompletedTask;
    }

    private static CommandDiagnostic Create(string kind, string? messageKey = null) => new(
        "RCLI2001",
        kind,
        "Safe message.",
        CommandDiagnosticPhase.Binding,
        CommandDiagnosticSeverity.Error,
        messageKey: messageKey);
}
