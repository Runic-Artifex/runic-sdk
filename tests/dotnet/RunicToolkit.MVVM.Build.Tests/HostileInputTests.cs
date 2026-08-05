using System;
using System.Linq;
using System.Threading;
using RunicToolkit.MVVM.Build.Compiler;
using RunicToolkit.MVVM.Build.Generation;

namespace RunicToolkit.MVVM.Build.Tests;

internal static class HostileInputTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("parser rejects oversized source before dispatch", RejectsOversizedSource);
        runner.Add("parser caps adversarial diagnostic floods", CapsDiagnosticFloods);
        runner.Add("generator rejects code and path injection", RejectsInjection);
        runner.Add("generator observes pre-canceled work", ObservesCancellation);
    }

    private static void RejectsOversizedSource()
    {
        const string source = "protocol runic.toolkit.mvvm/1;";
        BindingCompilerLimits limits = new()
        {
            MaxSourceCharacters = 8,
            MaxSourceUtf8Bytes = 64,
        };
        BindingParseResult result = BindingParser.Parse(source, "limits/oversized.rtkmvvm", limits);
        BindingDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.True(result.HasErrors, "Oversized input must fail compilation.");
        Assert.Equal(BindingDiagnosticIds.SourceLimitExceeded, diagnostic.Id);
        Assert.Equal(0, diagnostic.Span.Start.Offset);
        Assert.Equal(source.Length, diagnostic.Span.End.Offset);
        Assert.Equal("limits/oversized.rtkmvvm", diagnostic.Span.LogicalPath);
    }

    private static void CapsDiagnosticFloods()
    {
        BindingCompilerLimits limits = new()
        {
            MaxDiagnostics = 3,
        };
        BindingParseResult result = BindingParser.Parse("@@@@@@@@@@@@@@@@", "limits/flood.rtkmvvm", limits);

        Assert.True(result.Diagnostics.Count <= limits.MaxDiagnostics,
            "Hostile input must never exceed the configured diagnostic ceiling.");
        Assert.True(result.Diagnostics.Any(static diagnostic =>
                diagnostic.Id == BindingDiagnosticIds.DiagnosticLimitExceeded),
            "The final retained diagnostic must explain suppression.");
    }

    private static void RejectsInjection()
    {
        bool namespaceRejected = false;
        try
        {
            _ = DeterministicBindingGenerator.Generate(new BindingGenerationInput(
                "settings",
                "Example; System.Console.WriteLine(1)",
                "SettingsBindings",
                Array.Empty<BindingGenerationMember>()));
        }
        catch (ArgumentException)
        {
            namespaceRejected = true;
        }

        Assert.True(namespaceRejected, "Generated namespaces must reject C# injection tokens.");

        bool typeRejected = false;
        try
        {
            _ = DeterministicBindingGenerator.Generate(new BindingGenerationInput(
                "settings",
                "Example",
                "../SettingsBindings",
                Array.Empty<BindingGenerationMember>()));
        }
        catch (ArgumentException)
        {
            typeRejected = true;
        }

        Assert.True(typeRejected, "Generated type and hint names must reject path injection tokens.");
    }

    private static void ObservesCancellation()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        bool canceled = false;
        try
        {
            _ = DeterministicBindingGenerator.Generate(
                new BindingGenerationInput(
                    "settings",
                    "Example",
                    "SettingsBindings",
                    [new BindingGenerationMember(1, "name", BindingGenerationMemberKind.Property)]),
                cancellationToken: source.Token);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == source.Token)
        {
            canceled = true;
        }

        Assert.True(canceled, "Cancellation must preserve the caller's token.");
    }
}
