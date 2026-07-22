using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WebUIToolkit.MVVM.Build.Compiler;
using WebUIToolkit.MVVM.Build.Generation;

namespace WebUIToolkit.MVVM.BindingCompiler;

internal static class Program
{
    private const int Success = 0;
    private const int CompilationFailure = 1;
    private const int UsageFailure = 2;
    private const int MaximumGeneratedOutputBytes = 64 * 1024 * 1024;
    private const string ToolVersion = "1.0.0";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            CommandLine commandLine = CommandLine.Parse(arguments);
            return commandLine.Command switch
            {
                CommandKind.Help => WriteHelp(arguments.Length == 0),
                CommandKind.Version => WriteVersion(),
                CommandKind.Compile or CommandKind.Validate => Compile(commandLine),
                _ => throw new InvalidOperationException("The parsed command is not supported."),
            };
        }
        catch (CommandLineException exception)
        {
            WriteToolError(exception.Message);
            WriteUsage(Console.Error);
            return UsageFailure;
        }
        catch (InputLimitException exception)
        {
            WriteToolError(exception.Message);
            return UsageFailure;
        }
        catch (InvalidInputEncodingException exception)
        {
            WriteToolError(exception.Message);
            return UsageFailure;
        }
        catch (UnauthorizedAccessException)
        {
            WriteToolError("Access to an input or output path was denied.");
            return UsageFailure;
        }
        catch (IOException)
        {
            WriteToolError("An input or output path could not be read or written.");
            return UsageFailure;
        }
        catch (ArgumentException exception)
        {
            WriteToolError(exception.Message);
            return UsageFailure;
        }
        catch (InvalidDataException exception)
        {
            WriteToolError(exception.Message);
            return UsageFailure;
        }
    }

    private static int Compile(CommandLine commandLine)
    {
        IReadOnlyList<CompilerInput> inputs = InputReader.ReadAll(
            commandLine.InputPaths,
            Environment.CurrentDirectory);
        var models = new List<BindingSemanticModel>(inputs.Count);
        bool hasErrors = false;
        foreach (CompilerInput input in inputs)
        {
            BindingSemanticResult result =
                global::WebUIToolkit.MVVM.Build.Compiler.BindingCompiler.Compile(input.Source, input.LogicalPath);
            DiagnosticWriter.Write(Console.Error, result.Diagnostics);
            hasErrors |= result.HasErrors;
            if (result.Model is not null)
            {
                models.Add(result.Model);
            }
        }

        if (hasErrors)
        {
            return CompilationFailure;
        }

        if (!EnsureContractsAreUnique(models))
        {
            return CompilationFailure;
        }

        if (commandLine.Command == CommandKind.Validate)
        {
            return Success;
        }

        var artifacts = new List<GeneratedBindingArtifacts>();
        foreach (BindingSemanticModel model in models)
        {
            artifacts.AddRange(SemanticModelGenerationAdapter.Generate(model));
        }

        GeneratedBindingArtifacts[] ordered = artifacts
            .OrderBy(static artifact => artifact.SourceHintName, StringComparer.Ordinal)
            .ToArray();
        EnsureArtifactNamesAreUnique(ordered);

        var output = new StringBuilder();
        int outputBytes = 0;
        foreach (GeneratedBindingArtifacts artifact in ordered)
        {
            outputBytes = checked(outputBytes + StrictUtf8.GetByteCount(artifact.Source));
            if (outputBytes > MaximumGeneratedOutputBytes)
            {
                throw new InputLimitException(
                    $"Generated output exceeds the {MaximumGeneratedOutputBytes.ToString(CultureInfo.InvariantCulture)} byte limit.");
            }

            output.Append(artifact.Source);
        }

        OutputWriter.Write(commandLine.OutputPath!, output.ToString(), commandLine.InputPaths);
        return Success;
    }

    private static bool EnsureContractsAreUnique(IEnumerable<BindingSemanticModel> models)
    {
        var contractNames = new Dictionary<string, BindingSourceSpan>(StringComparer.Ordinal);
        bool unique = true;
        foreach (BindingSemanticModel model in models)
        {
            foreach (BindingContractModel contract in model.Contracts)
            {
                if (!contractNames.TryAdd(contract.Name, contract.NameSpan))
                {
                    DiagnosticWriter.Write(
                        Console.Error,
                        contract.NameSpan,
                        BindingDiagnosticSeverity.Error,
                        BindingDiagnosticIds.DuplicateContract,
                        $"Contract identity '{contract.Name}' is declared more than once.");
                    unique = false;
                }
            }
        }

        return unique;
    }

    private static void EnsureArtifactNamesAreUnique(IEnumerable<GeneratedBindingArtifacts> artifacts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeneratedBindingArtifacts artifact in artifacts)
        {
            if (!names.Add(artifact.SourceHintName))
            {
                throw new InvalidDataException("Generation produced a duplicate source artifact name.");
            }
        }
    }

    private static int WriteHelp(bool missingCommand)
    {
        WriteUsage(Console.Out);
        return missingCommand ? UsageFailure : Success;
    }

    private static int WriteVersion()
    {
        Console.Out.WriteLine(ToolVersion);
        return Success;
    }

    private static void WriteToolError(string message) =>
        Console.Error.WriteLine($"wut-bindings: {TerminalText.Message(message)}");

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  wut-bindings compile [--output <path>|-] <bindings.wutmvvm> [...]");
        writer.WriteLine("  wut-bindings validate <bindings.wutmvvm> [...]");
        writer.WriteLine("  wut-bindings --version");
    }
}
