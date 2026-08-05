using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RunicToolkit.MVVM.Build.Compiler;
using RunicToolkit.MVVM.Build.Generation;

namespace RunicToolkit.MVVM.Build;

internal static class BuildHost
{
    private const int MaximumInputCount = 4_096;
    private const int MaximumInputListBytes = 1024 * 1024;
    private const long MaximumTotalInputBytes = 16 * 1024 * 1024;
    private const int MaximumBuildDiagnostics = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
            {
                Console.Out.WriteLine(BindingGenerationContract.GeneratorVersion);
                return 0;
            }

            if (TryReadCleanArguments(args, out BuildHostCleanOptions? cleanOptions))
            {
                return Clean(cleanOptions!);
            }

            if (!TryReadArguments(args, out BuildHostOptions? options))
            {
                WriteUsage();
                return 2;
            }

            return Compile(options!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or EncoderFallbackException or DecoderFallbackException)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"error RTKMVVM0903: {SanitizeMessage(exception.Message)}"));
            return 2;
        }
    }

    private static int Compile(BuildHostOptions options)
    {
        string projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        string intermediateDirectory = Path.GetFullPath(options.IntermediateDirectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        string inputList = Path.GetFullPath(options.InputList);
        EnsureOutputIsInsideIntermediate(intermediateDirectory, outputDirectory);

        var inputListInfo = new FileInfo(inputList);
        if (!inputListInfo.Exists || inputListInfo.Length > MaximumInputListBytes)
        {
            throw new IOException($"The binding input list is missing or exceeds {MaximumInputListBytes} bytes.");
        }

        string[] physicalPaths = File.ReadAllLines(inputList, StrictUtf8);
        if (physicalPaths.Length is 0 or > MaximumInputCount)
        {
            throw new IOException($"The binding input list must contain between 1 and {MaximumInputCount} paths.");
        }

        var inputs = new List<BindingInput>(physicalPaths.Length);
        var logicalPaths = new HashSet<string>(StringComparer.Ordinal);
        long totalInputBytes = 0;
        foreach (string physicalPath in physicalPaths)
        {
            if (string.IsNullOrWhiteSpace(physicalPath) || physicalPath.Any(char.IsControl))
            {
                throw new IOException("The binding input list contains an empty or unsupported path.");
            }

            string fullPath = Path.GetFullPath(physicalPath);
            string relativePath = Path.GetRelativePath(projectDirectory, fullPath);
            if (Path.IsPathRooted(relativePath) || IsParentTraversal(relativePath))
            {
                throw new IOException("Every binding input must be contained by the project directory.");
            }

            string logicalPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (!logicalPaths.Add(logicalPath))
            {
                throw new IOException($"The binding input '{logicalPath}' is listed more than once.");
            }

            var inputInfo = new FileInfo(fullPath);
            totalInputBytes = checked(totalInputBytes + inputInfo.Length);
            if (totalInputBytes > MaximumTotalInputBytes)
            {
                throw new IOException($"The combined binding inputs exceed {MaximumTotalInputBytes} bytes.");
            }

            string source = File.ReadAllText(fullPath, StrictUtf8);
            inputs.Add(new BindingInput(logicalPath, source));
        }

        inputs.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));

        var models = new List<BindingSemanticModel>(inputs.Count);
        bool hasErrors = false;
        int diagnosticCount = 0;
        foreach (BindingInput input in inputs)
        {
            BindingSemanticResult result = BindingCompiler.Compile(input.Source, input.LogicalPath);
            foreach (BindingDiagnostic diagnostic in result.Diagnostics)
            {
                if (diagnosticCount == MaximumBuildDiagnostics)
                {
                    Console.Error.WriteLine("error RTKMVVM0003: The build diagnostic limit was exceeded; further diagnostics were suppressed.");
                    return 1;
                }

                WriteDiagnostic(diagnostic);
                diagnosticCount++;
            }

            hasErrors |= result.HasErrors;
            if (result.Model is not null)
            {
                models.Add(result.Model);
            }
        }

        if (hasErrors)
        {
            return 1;
        }

        if (!AreContractsUnique(models))
        {
            return 1;
        }
        var artifacts = new List<GeneratedBindingArtifacts>();
        foreach (BindingSemanticModel model in models)
        {
            artifacts.AddRange(SemanticModelGenerationAdapter.Generate(model));
        }

        artifacts.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.SourceHintName, right.SourceHintName));
        EnsureArtifactNamesAreUnique(artifacts);
        WriteArtifacts(outputDirectory, artifacts);
        return 0;
    }

    private static void WriteArtifacts(string outputDirectory, List<GeneratedBindingArtifacts> artifacts)
    {
        Directory.CreateDirectory(outputDirectory);
        var sourceNames = new List<string>(artifacts.Count);
        var artifactNames = new List<string>(artifacts.Count * 2);
        var stampBuilder = new StringBuilder();
        stampBuilder.Append("protocol=");
        stampBuilder.Append(BindingGenerationContract.ProtocolIdentity);
        stampBuilder.Append('\n');
        foreach (GeneratedBindingArtifacts artifact in artifacts)
        {
            WriteTextAtomically(Path.Combine(outputDirectory, artifact.SourceHintName), artifact.Source, true);
            WriteTextAtomically(Path.Combine(outputDirectory, artifact.ManifestFileName), artifact.Manifest, true);
            sourceNames.Add(artifact.SourceHintName);
            artifactNames.Add(artifact.SourceHintName);
            artifactNames.Add(artifact.ManifestFileName);
            stampBuilder.Append("artifact=");
            stampBuilder.Append(artifact.Fingerprint);
            stampBuilder.Append('\n');
        }

        string generatedFiles = string.Join("\n", sourceNames);
        if (generatedFiles.Length != 0)
        {
            generatedFiles += "\n";
        }

        ReconcileStaleArtifacts(outputDirectory, artifactNames);
        WriteTextAtomically(Path.Combine(outputDirectory, "generated-files.list"), generatedFiles, true);
        WriteTextAtomically(
            Path.Combine(outputDirectory, "generated-artifacts.list"),
            string.Join("\n", artifactNames) + (artifactNames.Count == 0 ? string.Empty : "\n"),
            true);
        string stamp = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(stampBuilder.ToString())))
            .ToLowerInvariant() + "\n";
        WriteTextAtomically(Path.Combine(outputDirectory, "bindings.stamp"), stamp, false);
    }

    private static void ReconcileStaleArtifacts(string outputDirectory, IReadOnlyCollection<string> currentNames)
    {
        string inventoryPath = Path.Combine(outputDirectory, "generated-artifacts.list");
        var current = new HashSet<string>(currentNames, FileNameComparer);
        string[] previousNames = File.Exists(inventoryPath)
            ? File.ReadAllLines(inventoryPath, StrictUtf8)
            : [];
        foreach (string previousName in previousNames)
        {
            if (!IsSafeArtifactName(previousName))
            {
                throw new IOException("The generated artifact inventory contains an unsafe file name.");
            }

            if (!current.Contains(previousName))
            {
                File.Delete(Path.Combine(outputDirectory, previousName));
            }
        }

        DeleteUnlistedGeneratedFiles(outputDirectory, current, "RunicToolkit.MVVM.*.g.cs");
        DeleteUnlistedGeneratedFiles(outputDirectory, current, "runic.toolkit.mvvm.*.contract.json");
    }

    private static StringComparer FileNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void DeleteUnlistedGeneratedFiles(
        string outputDirectory,
        HashSet<string> currentNames,
        string searchPattern)
    {
        foreach (string existingPath in Directory.EnumerateFiles(outputDirectory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            string existingName = Path.GetFileName(existingPath);
            if (IsSafeArtifactName(existingName) && !currentNames.Contains(existingName))
            {
                File.Delete(existingPath);
            }
        }
    }

    private static int Clean(BuildHostCleanOptions options)
    {
        string intermediateDirectory = Path.GetFullPath(options.IntermediateDirectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        EnsureOutputIsInsideIntermediate(intermediateDirectory, outputDirectory);

        string inventoryPath = Path.Combine(outputDirectory, "generated-artifacts.list");
        string[] artifactNames = File.Exists(inventoryPath)
            ? File.ReadAllLines(inventoryPath, StrictUtf8)
            : [];
        if (artifactNames.Any(static name => !IsSafeArtifactName(name)))
        {
            throw new IOException("The generated artifact inventory contains an unsafe file name.");
        }

        foreach (string artifactName in artifactNames)
        {
            File.Delete(Path.Combine(outputDirectory, artifactName));
        }

        var noCurrentNames = new HashSet<string>(FileNameComparer);
        DeleteUnlistedGeneratedFiles(outputDirectory, noCurrentNames, "RunicToolkit.MVVM.*.g.cs");
        DeleteUnlistedGeneratedFiles(outputDirectory, noCurrentNames, "runic.toolkit.mvvm.*.contract.json");

        File.Delete(Path.Combine(outputDirectory, "generated-files.list"));
        File.Delete(inventoryPath);
        File.Delete(Path.Combine(outputDirectory, "bindings.stamp"));
        File.Delete(Path.Combine(outputDirectory, "inputs.rsp"));
        return 0;
    }

    private static bool IsSafeArtifactName(string name)
    {
        const string sourcePrefix = "RunicToolkit.MVVM.";
        const string sourceSuffix = ".g.cs";
        const string manifestPrefix = "runic.toolkit.mvvm.";
        const string manifestSuffix = ".contract.json";
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(char.IsControl) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return false;
        }

        return HasLowerHexIdentity(name, sourcePrefix, sourceSuffix) ||
               HasLowerHexIdentity(name, manifestPrefix, manifestSuffix);
    }

    private static bool HasLowerHexIdentity(string name, string prefix, string suffix)
    {
        if (name.Length != prefix.Length + 64 + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> identity = name.AsSpan(prefix.Length, 64);
        foreach (char character in identity)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteTextAtomically(string path, string content, bool keepUnchanged)
    {
        if (keepUnchanged && File.Exists(path) && string.Equals(File.ReadAllText(path, StrictUtf8), content, StringComparison.Ordinal))
        {
            return;
        }

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temporaryPath, content, StrictUtf8);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool AreContractsUnique(IEnumerable<BindingSemanticModel> models)
    {
        var contractNames = new Dictionary<string, BindingSourceSpan>(StringComparer.Ordinal);
        foreach (BindingSemanticModel model in models)
        {
            foreach (BindingContractModel contract in model.Contracts)
            {
                if (contractNames.TryGetValue(contract.Name, out BindingSourceSpan firstSpan))
                {
                    WriteDiagnostic(new BindingDiagnostic(
                        BindingDiagnosticIds.DuplicateContract,
                        BindingDiagnosticSeverity.Error,
                        $"Contract identity '{contract.Name}' is declared in more than one binding input.",
                        contract.NameSpan,
                        firstSpan));
                    return false;
                }

                contractNames.Add(contract.Name, contract.NameSpan);
            }
        }

        return true;
    }

    private static void EnsureArtifactNamesAreUnique(IEnumerable<GeneratedBindingArtifacts> artifacts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeneratedBindingArtifacts artifact in artifacts)
        {
            if (!names.Add(artifact.SourceHintName) || !names.Add(artifact.ManifestFileName))
            {
                throw new ArgumentException("Generation produced a duplicate artifact name.");
            }
        }
    }

    private static void EnsureOutputIsInsideIntermediate(string intermediateDirectory, string outputDirectory)
    {
        string outputFromIntermediate = Path.GetRelativePath(intermediateDirectory, outputDirectory);
        if (string.Equals(outputFromIntermediate, ".", StringComparison.Ordinal) ||
            Path.IsPathRooted(outputFromIntermediate) || IsParentTraversal(outputFromIntermediate))
        {
            throw new ArgumentException("The generated output directory must be a child of the project's intermediate directory.");
        }
    }

    private static bool IsParentTraversal(string path) =>
        string.Equals(path, "..", StringComparison.Ordinal) ||
        path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        path.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static void WriteDiagnostic(BindingDiagnostic diagnostic)
    {
        BindingSourceSpan span = diagnostic.Span;
        string severity = diagnostic.Severity == BindingDiagnosticSeverity.Error ? "error" : "warning";
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{SanitizeMessage(span.LogicalPath)}({span.Start.Line + 1},{span.Start.Column + 1},{span.End.Line + 1},{span.End.Column + 1}): {severity} {diagnostic.Id}: {SanitizeMessage(diagnostic.Message)}"));
    }

    private static string SanitizeMessage(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (char character in message)
        {
            builder.Append(
                char.IsControl(character) ||
                character is '\u2028' or '\u2029' ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format
                    ? ' '
                    : character);
        }

        return builder.ToString();
    }

    private static bool TryReadArguments(string[] args, out BuildHostOptions? options)
    {
        options = null;
        if (args.Length != 8)
        {
            return false;
        }

        string? projectDirectory = null;
        string? intermediateDirectory = null;
        string? outputDirectory = null;
        string? inputList = null;
        for (int index = 0; index < args.Length; index += 2)
        {
            string value = args[index + 1];
            switch (args[index])
            {
                case "--project-directory":
                    projectDirectory = value;
                    break;
                case "--output-directory":
                    outputDirectory = value;
                    break;
                case "--intermediate-directory":
                    intermediateDirectory = value;
                    break;
                case "--input-list":
                    inputList = value;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(projectDirectory) ||
            string.IsNullOrWhiteSpace(intermediateDirectory) ||
            string.IsNullOrWhiteSpace(outputDirectory) ||
            string.IsNullOrWhiteSpace(inputList))
        {
            return false;
        }

        options = new BuildHostOptions(projectDirectory, intermediateDirectory, outputDirectory, inputList);
        return true;
    }

    private static bool TryReadCleanArguments(string[] args, out BuildHostCleanOptions? options)
    {
        options = null;
        if (args.Length != 7 || !string.Equals(args[0], "--clean", StringComparison.Ordinal))
        {
            return false;
        }

        string? projectDirectory = null;
        string? intermediateDirectory = null;
        string? outputDirectory = null;
        for (int index = 1; index < args.Length; index += 2)
        {
            string value = args[index + 1];
            switch (args[index])
            {
                case "--project-directory":
                    projectDirectory = value;
                    break;
                case "--intermediate-directory":
                    intermediateDirectory = value;
                    break;
                case "--output-directory":
                    outputDirectory = value;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(projectDirectory) ||
            string.IsNullOrWhiteSpace(intermediateDirectory) ||
            string.IsNullOrWhiteSpace(outputDirectory))
        {
            return false;
        }

        options = new BuildHostCleanOptions(projectDirectory, intermediateDirectory, outputDirectory);
        return true;
    }

    private static void WriteUsage() => Console.Error.WriteLine(
        "Usage: RunicToolkit.MVVM.Build --project-directory <path> --intermediate-directory <path> --output-directory <path> --input-list <path>");

    private sealed record BuildHostOptions(
        string ProjectDirectory,
        string IntermediateDirectory,
        string OutputDirectory,
        string InputList);

    private sealed record BuildHostCleanOptions(
        string ProjectDirectory,
        string IntermediateDirectory,
        string OutputDirectory);

    private sealed record BindingInput(string LogicalPath, string Source);
}
