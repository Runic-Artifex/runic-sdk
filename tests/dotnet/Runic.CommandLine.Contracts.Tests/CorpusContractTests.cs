using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Runic.CommandLine;

namespace Runic.CommandLine.Contracts.Tests;

internal static class CorpusContractTests
{
    private const int CorpusFormatVersion = 1;

    public static void ValidateGrammarCorpus()
    {
        using JsonDocument document = LoadCorpus("grammar-corpus.json");
        JsonElement cases = ValidateHeader(document.RootElement);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement fixture in cases.EnumerateArray())
        {
            string id = ReadUniqueFixtureId(fixture, identifiers);
            ValidateStringArray(RequireArray(fixture, "args", id), id, "args");

            JsonElement expected = RequireObject(fixture, "expected", id);
            string kind = RequireNonEmptyString(expected, "kind", id);
            switch (kind)
            {
                case "invocation":
                    ValidateInvocation(expected, id);
                    break;
                case "help":
                    ValidateStringArray(RequireArray(expected, "commandPath", id), id, "expected.commandPath");
                    break;
                case "version":
                    break;
                case "error":
                    ValidateDiagnostics(expected, id);
                    break;
                default:
                    throw new InvalidOperationException($"Fixture '{id}' has unknown expected kind '{kind}'.");
            }
        }

        Assert.True(identifiers.Count > 0);
    }

    public static void ValidateOutputClassificationCorpus()
    {
        using JsonDocument document = LoadCorpus("output-classification-corpus.json");
        JsonElement root = document.RootElement;
        JsonElement cases = ValidateHeader(root);
        Assert.Equal(
            CommandOutputClassifier.EnvironmentVariableName,
            RequireNonEmptyString(root, "environmentVariable", "corpus"));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement fixture in cases.EnumerateArray())
        {
            string id = ReadUniqueFixtureId(fixture, identifiers);
            string[] args = ReadStringArray(RequireArray(fixture, "args", id), id, "args");
            JsonElement environment = RequireObject(fixture, "environment", id);
            string? environmentValue = ReadNullableString(
                environment,
                CommandOutputClassifier.EnvironmentVariableName,
                id);
            JsonElement expected = RequireObject(fixture, "expected", id);
            string expectedKind = RequireNonEmptyString(expected, "kind", id);

            ExplicitOutput explicitOutput = ReadExplicitOutput(args);
            if (explicitOutput.IsPresent && !explicitOutput.IsValid)
            {
                Assert.Equal("error", expectedKind);
                ValidateDiagnostics(expected, id);
                continue;
            }

            CommandOutputClassification actual = CommandOutputClassifier.Classify(
                explicitOutput.IsPresent ? explicitOutput.Mode : null,
                environmentValue);

            if (expectedKind == "error")
            {
                ValidateDiagnostics(expected, id);
                Assert.False(actual.IsValid);
                continue;
            }

            Assert.Equal("output-mode", expectedKind);
            Assert.True(actual.IsValid);
            Assert.Equal(ParseMode(RequireNonEmptyString(expected, "mode", id), id), actual.Mode);
            Assert.Equal(ParseSource(RequireNonEmptyString(expected, "source", id), id), actual.Source);
            Assert.Null(actual.InvalidEnvironmentValue);
        }

        Assert.True(identifiers.Count > 0);
    }

    public static void ValidateGlobalFixtureIds()
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        ValidateUniqueFixtureIds("grammar-corpus.json", identifiers);
        ValidateUniqueFixtureIds("output-classification-corpus.json", identifiers);
    }

    private static void ValidateUniqueFixtureIds(string fileName, HashSet<string> identifiers)
    {
        using JsonDocument document = LoadCorpus(fileName);
        JsonElement cases = ValidateHeader(document.RootElement);
        foreach (JsonElement fixture in cases.EnumerateArray())
        {
            _ = ReadUniqueFixtureId(fixture, identifiers);
        }
    }

    private static JsonDocument LoadCorpus(string fileName)
    {
        string? root = FindRepositoryRoot(Environment.CurrentDirectory) ?? FindRepositoryRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException("Could not find the repository root containing spec/command-line.");
        }

        string path = Path.Combine(root, "spec", "command-line", fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Required command-line corpus was not found: {path}");
        }

        using FileStream stream = File.OpenRead(path);
        return JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
    }

    private static string? FindRepositoryRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "spec", "command-line")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static JsonElement ValidateHeader(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Corpus root must be a JSON object.");
        }

        Assert.Equal(CorpusFormatVersion, RequireInt32(root, "formatVersion", "corpus"));
        Assert.Equal(CliProtocol.Identity, RequireNonEmptyString(root, "protocol", "corpus"));
        return RequireArray(root, "cases", "corpus");
    }

    private static string ReadUniqueFixtureId(JsonElement fixture, HashSet<string> identifiers)
    {
        if (fixture.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Every corpus fixture must be a JSON object.");
        }

        string id = RequireNonEmptyString(fixture, "id", "fixture");
        if (!identifiers.Add(id))
        {
            throw new InvalidOperationException($"Fixture ID '{id}' is duplicated.");
        }

        return id;
    }

    private static void ValidateInvocation(JsonElement expected, string fixtureId)
    {
        JsonElement commandPath = RequireArray(expected, "commandPath", fixtureId);
        ValidateStringArray(commandPath, fixtureId, "expected.commandPath");
        Assert.True(commandPath.GetArrayLength() > 0);
        ValidateBindings(RequireArray(expected, "options", fixtureId), fixtureId, "options");
        ValidateBindings(RequireArray(expected, "arguments", fixtureId), fixtureId, "arguments");
    }

    private static void ValidateBindings(JsonElement bindings, string fixtureId, string propertyName)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixtureId}' expected.{propertyName} entries must be objects.");
            }

            string bindingId = RequireNonEmptyString(binding, "id", fixtureId);
            if (!identifiers.Add(bindingId))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixtureId}' repeats expected.{propertyName} ID '{bindingId}'.");
            }

            ValidateStringArray(
                RequireArray(binding, "values", fixtureId),
                fixtureId,
                $"expected.{propertyName}.values");
        }
    }

    private static void ValidateDiagnostics(JsonElement expected, string fixtureId)
    {
        JsonElement diagnostics = RequireArray(expected, "diagnostics", fixtureId);
        Assert.True(diagnostics.GetArrayLength() > 0);

        foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Fixture '{fixtureId}' diagnostics must be objects.");
            }

            string code = RequireNonEmptyString(diagnostic, "code", fixtureId);
            if (!code.StartsWith("RCLI", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixtureId}' diagnostic code '{code}' is outside the CLI identity range.");
            }

            _ = RequireNonEmptyString(diagnostic, "kind", fixtureId);
            int tokenIndex = RequireInt32(diagnostic, "tokenIndex", fixtureId);
            if (tokenIndex < 0)
            {
                throw new InvalidOperationException($"Fixture '{fixtureId}' has a negative diagnostic token index.");
            }
        }
    }

    private static ExplicitOutput ReadExplicitOutput(string[] args)
    {
        var found = false;
        CommandOutputMode? mode = null;

        for (var index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (token == "--")
            {
                break;
            }

            string? value = null;
            if (token == "--output")
            {
                if (index + 1 >= args.Length)
                {
                    return new ExplicitOutput(true, false, null);
                }

                value = args[++index];
            }
            else if (token.StartsWith("--output=", StringComparison.Ordinal))
            {
                value = token["--output=".Length..];
            }

            if (value is null)
            {
                continue;
            }

            if (found || !TryParseMode(value, out CommandOutputMode parsedMode))
            {
                return new ExplicitOutput(true, false, null);
            }

            found = true;
            mode = parsedMode;
        }

        return new ExplicitOutput(found, true, mode);
    }

    private static CommandOutputMode ParseMode(string value, string fixtureId)
    {
        if (TryParseMode(value, out CommandOutputMode mode))
        {
            return mode;
        }

        throw new InvalidOperationException($"Fixture '{fixtureId}' has unknown output mode '{value}'.");
    }

    private static bool TryParseMode(string value, out CommandOutputMode mode)
    {
        if (string.Equals(value, "human", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Human;
            return true;
        }

        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Json;
            return true;
        }

        mode = default;
        return false;
    }

    private static CommandOutputModeSource ParseSource(string value, string fixtureId)
    {
        return value switch
        {
            "default" => CommandOutputModeSource.Default,
            "environment" => CommandOutputModeSource.Environment,
            "explicit-argument" => CommandOutputModeSource.ExplicitArgument,
            _ => throw new InvalidOperationException(
                $"Fixture '{fixtureId}' has unknown output source '{value}'."),
        };
    }

    private static string[] ReadStringArray(JsonElement array, string context, string propertyName)
    {
        var values = new string[array.GetArrayLength()];
        var index = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"'{context}.{propertyName}' must contain only strings.");
            }

            values[index++] = value.GetString()!;
        }

        return values;
    }

    private static void ValidateStringArray(JsonElement array, string context, string propertyName)
    {
        _ = ReadStringArray(array, context, propertyName);
    }

    private static JsonElement RequireArray(JsonElement parent, string propertyName, string context)
    {
        JsonElement property = RequireProperty(parent, propertyName, context);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"'{context}.{propertyName}' must be an array.");
        }

        return property;
    }

    private static JsonElement RequireObject(JsonElement parent, string propertyName, string context)
    {
        JsonElement property = RequireProperty(parent, propertyName, context);
        if (property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"'{context}.{propertyName}' must be an object.");
        }

        return property;
    }

    private static string RequireNonEmptyString(JsonElement parent, string propertyName, string context)
    {
        JsonElement property = RequireProperty(parent, propertyName, context);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"'{context}.{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static string? ReadNullableString(JsonElement parent, string propertyName, string context)
    {
        JsonElement property = RequireProperty(parent, propertyName, context);
        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => throw new InvalidOperationException(
                $"'{context}.{propertyName}' must be a string or null."),
        };
    }

    private static int RequireInt32(JsonElement parent, string propertyName, string context)
    {
        JsonElement property = RequireProperty(parent, propertyName, context);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value))
        {
            throw new InvalidOperationException($"'{context}.{propertyName}' must be a 32-bit integer.");
        }

        return value;
    }

    private static JsonElement RequireProperty(JsonElement parent, string propertyName, string context)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement property))
        {
            throw new InvalidOperationException($"'{context}' is missing required property '{propertyName}'.");
        }

        return property;
    }

    private readonly record struct ExplicitOutput(
        bool IsPresent,
        bool IsValid,
        CommandOutputMode? Mode);
}
