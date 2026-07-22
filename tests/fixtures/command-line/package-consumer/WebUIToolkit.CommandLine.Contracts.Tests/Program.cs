using System;
using System.Collections.Generic;
using WebUIToolkit.CommandLine;

namespace WebUIToolkit.CommandLine.Contracts.Tests;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("protocol identity is stable", ProtocolIdentityIsStable),
        ("exit category names are stable", ExitCategoryNamesAreStable),
        ("default exit codes are stable", DefaultExitCodesAreStable),
        ("unknown exit category has no default", UnknownExitCategoryHasNoDefault),
        ("success outcome has no fault", SuccessOutcomeHasNoFault),
        ("nullable success payload is supported", NullableSuccessPayloadIsSupported),
        ("outcomes cannot bypass invariant factories", OutcomesCannotBypassInvariantFactories),
        ("failure outcomes have no value", FailureOutcomesHaveNoValue),
        ("success cannot be constructed as failure", SuccessCannotBeConstructedAsFailure),
        ("unknown category cannot be constructed as failure", UnknownCategoryCannotBeConstructedAsFailure),
        ("failure requires a fault", FailureRequiresFault),
        ("fault requires a stable code", FaultRequiresStableCode),
        ("fault requires a safe message", FaultRequiresSafeMessage),
        ("fault details are copied and observable", FaultDetailsAreCopiedAndObservable),
        ("fault details reject blank keys", FaultDetailsRejectBlankKeys),
        ("fault details reject null values", FaultDetailsRejectNullValues),
        ("output environment name is stable", OutputEnvironmentNameIsStable),
        ("output mode names are stable", OutputModeNamesAreStable),
        ("explicit human output wins over malformed environment", ExplicitHumanWinsOverMalformedEnvironment),
        ("explicit JSON output wins over environment", ExplicitJsonWinsOverEnvironment),
        ("environment output classification is case insensitive", EnvironmentClassificationIsCaseInsensitive),
        ("missing environment uses requested default", MissingEnvironmentUsesRequestedDefault),
        ("empty environment uses requested default", EmptyEnvironmentUsesRequestedDefault),
        ("malformed environment is invalid", MalformedEnvironmentIsInvalid),
        ("whitespace environment is not silently accepted", WhitespaceEnvironmentIsInvalid),
        ("unknown explicit output mode is rejected", UnknownExplicitOutputModeIsRejected),
        ("unknown default output mode is rejected", UnknownDefaultOutputModeIsRejected),
        ("higher precedence sources ignore an unknown default", HigherPrecedenceSourcesIgnoreUnknownDefault),
        ("grammar corpus has a portable contract shape", CorpusContractTests.ValidateGrammarCorpus),
        ("output corpus agrees with the public classifier", CorpusContractTests.ValidateOutputClassificationCorpus),
        ("command-line corpus fixture IDs are globally unique", CorpusContractTests.ValidateGlobalFixtureIds),
    ];

    public static int Main()
    {
        var failures = 0;

        foreach ((string name, Action test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception.Message);
            }
        }

        Console.WriteLine($"Executed {Tests.Length} contract tests; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ProtocolIdentityIsStable()
    {
        Assert.Equal("webuitoolkit.cli/1", CliProtocol.Identity);
    }

    private static void ExitCategoryNamesAreStable()
    {
        Assert.SequenceEqual(
            [
                nameof(CommandExitCategory.Success),
                nameof(CommandExitCategory.Usage),
                nameof(CommandExitCategory.Validation),
                nameof(CommandExitCategory.Cancelled),
                nameof(CommandExitCategory.Unavailable),
                nameof(CommandExitCategory.CommandFailure),
                nameof(CommandExitCategory.HostFailure),
            ],
            Enum.GetNames<CommandExitCategory>());
    }

    private static void DefaultExitCodesAreStable()
    {
        AssertExitCode(CommandExitCategory.Success, CommandExitCodes.Success, 0);
        AssertExitCode(CommandExitCategory.Usage, CommandExitCodes.Usage, 2);
        AssertExitCode(CommandExitCategory.Validation, CommandExitCodes.Validation, 3);
        AssertExitCode(CommandExitCategory.Cancelled, CommandExitCodes.Cancelled, 4);
        AssertExitCode(CommandExitCategory.Unavailable, CommandExitCodes.Unavailable, 5);
        AssertExitCode(CommandExitCategory.CommandFailure, CommandExitCodes.CommandFailure, 10);
        AssertExitCode(CommandExitCategory.HostFailure, CommandExitCodes.HostFailure, 70);
    }

    private static void SuccessOutcomeHasNoFault()
    {
        CommandOutcome<string> outcome = CommandOutcome.Success("payload");

        Assert.True(outcome.IsSuccess);
        Assert.Equal(CommandExitCategory.Success, outcome.ExitCategory);
        Assert.Equal("payload", outcome.Value);
        Assert.Null(outcome.Fault);
    }

    private static void NullableSuccessPayloadIsSupported()
    {
        CommandOutcome<string?> outcome = CommandOutcome.Success<string?>(null);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(CommandExitCategory.Success, outcome.ExitCategory);
        Assert.Null(outcome.Value);
        Assert.Null(outcome.Fault);
    }

    private static void OutcomesCannotBypassInvariantFactories()
    {
        Assert.Equal(0, typeof(CommandOutcome<string>).GetConstructors().Length);
    }

    private static void UnknownExitCategoryHasNoDefault()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandExitCodes.GetDefault((CommandExitCategory)int.MaxValue));
    }

    private static void FailureOutcomesHaveNoValue()
    {
        CommandFault fault = CreateFault();
        CommandExitCategory[] failureCategories =
        [
            CommandExitCategory.Usage,
            CommandExitCategory.Validation,
            CommandExitCategory.Cancelled,
            CommandExitCategory.Unavailable,
            CommandExitCategory.CommandFailure,
            CommandExitCategory.HostFailure,
        ];

        foreach (CommandExitCategory category in failureCategories)
        {
            CommandOutcome<string> outcome = CommandOutcome.Failure<string>(category, fault);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(category, outcome.ExitCategory);
            Assert.Null(outcome.Value);
            Assert.Same(fault, outcome.Fault!);
        }
    }

    private static void SuccessCannotBeConstructedAsFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutcome.Failure<string>(CommandExitCategory.Success, CreateFault()));
    }

    private static void UnknownCategoryCannotBeConstructedAsFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutcome.Failure<string>((CommandExitCategory)int.MaxValue, CreateFault()));
    }

    private static void FailureRequiresFault()
    {
        Assert.Throws<ArgumentNullException>(
            () => CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, null!));
    }

    private static void FaultRequiresStableCode()
    {
        Assert.Throws<ArgumentException>(() => _ = new CommandFault(string.Empty, "message"));
        Assert.Throws<ArgumentException>(() => _ = new CommandFault(" ", "message"));
    }

    private static void FaultRequiresSafeMessage()
    {
        Assert.Throws<ArgumentException>(() => _ = new CommandFault("WUTCLI2001", string.Empty));
        Assert.Throws<ArgumentException>(() => _ = new CommandFault("WUTCLI2001", " "));
    }

    private static void FaultDetailsAreCopiedAndObservable()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameter"] = "format",
        };
        var fault = new CommandFault("WUTCLI2001", "Invalid value.", details, retryable: true);
        details["parameter"] = "mutated";

        Assert.Equal("WUTCLI2001", fault.Code);
        Assert.Equal("Invalid value.", fault.Message);
        Assert.True(fault.Retryable);
        Assert.Equal("format", fault.Details["parameter"]);
    }

    private static void FaultDetailsRejectBlankKeys()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" "] = "value",
        };

        Assert.Throws<ArgumentException>(
            () => _ = new CommandFault("WUTCLI2001", "Invalid value.", details));
    }

    private static void FaultDetailsRejectNullValues()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameter"] = null!,
        };

        Assert.Throws<ArgumentException>(
            () => _ = new CommandFault("WUTCLI2001", "Invalid value.", details));
    }

    private static void OutputEnvironmentNameIsStable()
    {
        Assert.Equal("WEBUITOOLKIT_CLI_OUTPUT", CommandOutputClassifier.EnvironmentVariableName);
    }

    private static void OutputModeNamesAreStable()
    {
        Assert.SequenceEqual(
            [nameof(CommandOutputMode.Human), nameof(CommandOutputMode.Json)],
            Enum.GetNames<CommandOutputMode>());
        Assert.SequenceEqual(
            [
                nameof(CommandOutputModeSource.Default),
                nameof(CommandOutputModeSource.Environment),
                nameof(CommandOutputModeSource.ExplicitArgument),
            ],
            Enum.GetNames<CommandOutputModeSource>());
    }

    private static void ExplicitHumanWinsOverMalformedEnvironment()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            CommandOutputMode.Human,
            "not-a-mode",
            CommandOutputMode.Json);

        AssertValidClassification(result, CommandOutputMode.Human, CommandOutputModeSource.ExplicitArgument);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    private static void ExplicitJsonWinsOverEnvironment()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            CommandOutputMode.Json,
            "human");

        AssertValidClassification(result, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    private static void EnvironmentClassificationIsCaseInsensitive()
    {
        CommandOutputClassification human = CommandOutputClassifier.Classify(null, "HuMaN");
        CommandOutputClassification json = CommandOutputClassifier.Classify(null, "JsOn");

        AssertValidClassification(human, CommandOutputMode.Human, CommandOutputModeSource.Environment);
        AssertValidClassification(json, CommandOutputMode.Json, CommandOutputModeSource.Environment);
    }

    private static void MissingEnvironmentUsesRequestedDefault()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            null,
            null,
            CommandOutputMode.Json);

        AssertValidClassification(result, CommandOutputMode.Json, CommandOutputModeSource.Default);
    }

    private static void EmptyEnvironmentUsesRequestedDefault()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            null,
            string.Empty,
            CommandOutputMode.Human);

        AssertValidClassification(result, CommandOutputMode.Human, CommandOutputModeSource.Default);
    }

    private static void MalformedEnvironmentIsInvalid()
    {
        const string invalidValue = "xml";
        CommandOutputClassification result = CommandOutputClassifier.Classify(null, invalidValue);

        Assert.False(result.IsValid);
        Assert.Null(result.Mode);
        Assert.Null(result.Source);
        Assert.Equal(invalidValue, result.InvalidEnvironmentValue);
    }

    private static void WhitespaceEnvironmentIsInvalid()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(null, " ");

        Assert.False(result.IsValid);
        Assert.Equal(" ", result.InvalidEnvironmentValue);
    }

    private static void UnknownExplicitOutputModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutputClassifier.Classify((CommandOutputMode)int.MaxValue, "json"));
    }

    private static void UnknownDefaultOutputModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutputClassifier.Classify(null, null, (CommandOutputMode)int.MaxValue));
    }

    private static void HigherPrecedenceSourcesIgnoreUnknownDefault()
    {
        CommandOutputMode invalidDefault = (CommandOutputMode)int.MaxValue;

        CommandOutputClassification explicitResult = CommandOutputClassifier.Classify(
            CommandOutputMode.Json,
            "malformed",
            invalidDefault);
        CommandOutputClassification environmentResult = CommandOutputClassifier.Classify(
            null,
            "human",
            invalidDefault);

        AssertValidClassification(
            explicitResult,
            CommandOutputMode.Json,
            CommandOutputModeSource.ExplicitArgument);
        AssertValidClassification(
            environmentResult,
            CommandOutputMode.Human,
            CommandOutputModeSource.Environment);
    }

    private static void AssertExitCode(CommandExitCategory category, int declaredCode, int expectedCode)
    {
        Assert.Equal(expectedCode, declaredCode);
        Assert.Equal(expectedCode, CommandExitCodes.GetDefault(category));
    }

    private static void AssertValidClassification(
        CommandOutputClassification result,
        CommandOutputMode expectedMode,
        CommandOutputModeSource expectedSource)
    {
        Assert.True(result.IsValid);
        Assert.Equal(expectedMode, result.Mode);
        Assert.Equal(expectedSource, result.Source);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    private static CommandFault CreateFault()
    {
        CommandFault fault = new("WUTCLI3001", "The command failed.");
        Assert.Equal(0, fault.Details.Count);
        Assert.False(fault.Retryable);
        return fault;
    }
}

internal static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but found false.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected false, but found true.");
        }
    }

    public static void Null<T>(T? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, but found '{value}'.");
        }
    }

    public static void Same<T>(T expected, T actual)
        where T : class
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("Expected both values to reference the same instance.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but found '{actual}'.");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        Equal(expected.Count, actual.Count);

        for (var index = 0; index < expected.Count; index++)
        {
            Equal(expected[index], actual[index]);
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, but found {exception.GetType().Name}.",
                exception);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}
