using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RunicToolkit.MVVM.BindingCompiler.Tests;

internal static class CompilerCliContractTests
{
    private const string SettingsSource = """
        protocol runic.toolkit.mvvm/1;
        contract settings model Example.Settings {
          property 1 serverName: string => ServerName readwrite;
          command 2 save: none -> none => Save;
          validation serverNameErrors for serverName => ServerNameErrors;
        }
        """;

    private const string ProfileSource = """
        protocol runic.toolkit.mvvm/1;
        contract profile model Example.Profile {
          property 8 displayName: string => DisplayName readonly;
        }
        """;

    public static void Register(TestRunner runner)
    {
        runner.Add("help variants and missing-command usage are stable", HelpContractIsStable);
        runner.Add("version succeeds with a stable package version", VersionSucceeds);
        runner.Add("unknown command is a usage failure", UnknownCommandIsUsageFailure);
        runner.Add("valid input validates without output", ValidInputValidatesWithoutOutput);
        runner.Add("compile emits deterministic generated source", CompileEmitsGeneratedSource);
        runner.Add("multiple contracts validate and concatenate viable source", MultipleContractsProduceViableSource);
        runner.Add("input argument order cannot change output", InputOrderCannotChangeOutput);
        runner.Add("repeat compilation preserves output bytes", RepeatCompilationPreservesOutputBytes);
        runner.Add("diagnostics report exact one-based half-open spans", DiagnosticsReportExactSpans);
        runner.Add("cross-file duplicate contracts report the second exact span", CrossFileDuplicateContractsReportExactSpan);
        runner.Add("compile writes no standard output on language errors", CompileWritesNothingOnErrors);
        runner.Add("failed compilation preserves an existing output file", FailedCompilePreservesOutputFile);
        runner.Add("malformed UTF-8 is rejected as an input failure", MalformedUtf8IsRejected);
        runner.Add("UTF-8 BOM is accepted without changing generated output", Utf8BomIsAccepted);
        runner.Add("one MiB input byte limit is enforced", PerFileLimitIsEnforced);
        runner.Add("sixteen MiB aggregate input byte limit is enforced", AggregateLimitIsEnforced);
        runner.Add("input paths cannot escape the working directory", OutsidePathIsRejected);
        runner.Add("duplicate normalized paths are rejected", DuplicatePathIsRejected);
        runner.Add("argument count limit is enforced before file access", ArgumentCountLimitIsEnforced);
        runner.Add("input count limit is enforced before file access", InputCountLimitIsEnforced);
        runner.Add("option terminator admits dash-prefixed paths", OptionTerminatorAdmitsDashPath);
        runner.Add("output path cannot overwrite an input", OutputCannotOverwriteInput);
    }

    private static void HelpContractIsStable()
    {
        using TestWorkspace workspace = new();
        CompilerResult missing = CompilerProcess.Run(workspace.Root);
        CompilerResult help = CompilerProcess.Run(workspace.Root, "help");
        CompilerResult longHelp = CompilerProcess.Run(workspace.Root, "--help");
        CompilerResult shortHelp = CompilerProcess.Run(workspace.Root, "-h");

        Assert.Equal(2, missing.ExitCode);
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(0, longHelp.ExitCode);
        Assert.Equal(0, shortHelp.ExitCode);
        Assert.Contains("runic-toolkit-bindings compile", missing.StandardOutput);
        Assert.Equal(missing.StandardOutput, help.StandardOutput);
        Assert.Equal(help.StandardOutput, longHelp.StandardOutput);
        Assert.Equal(help.StandardOutput, shortHelp.StandardOutput);
        Assert.Empty(missing.StandardError);
        Assert.Empty(help.StandardError);
        Assert.Empty(longHelp.StandardError);
        Assert.Empty(shortHelp.StandardError);
    }

    private static void VersionSucceeds()
    {
        using TestWorkspace workspace = new();
        CompilerResult result = CompilerProcess.Run(workspace.Root, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.0.0" + Environment.NewLine, result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    private static void UnknownCommandIsUsageFailure()
    {
        using TestWorkspace workspace = new();
        CompilerResult result = CompilerProcess.Run(workspace.Root, "explode");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Unknown command 'explode'.", result.StandardError);
    }

    private static void ValidInputValidatesWithoutOutput()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("Bindings/settings.rtkmvvm", SettingsSource);

        CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", "Bindings/settings.rtkmvvm");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    private static void CompileEmitsGeneratedSource()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("settings.rtkmvvm", SettingsSource);

        CompilerResult result = CompilerProcess.Run(workspace.Root, "compile", "settings.rtkmvvm");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("GeneratedCodeAttribute", result.StandardOutput);
        Assert.Contains("TryGetMemberId", result.StandardOutput);
        Assert.Contains("DispatchAsync", result.StandardOutput);
        Assert.False(result.StandardOutput.Contains('\r'), "Generated output must use canonical LF newlines.");
        Assert.False(result.StandardOutput.StartsWith('\uFEFF'), "Generated output must not contain a BOM.");
    }

    private static void InputOrderCannotChangeOutput()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("z-settings.rtkmvvm", SettingsSource);
        workspace.WriteText("a-profile.rtkmvvm", ProfileSource);

        CompilerResult first = CompilerProcess.Run(
            workspace.Root, "compile", "z-settings.rtkmvvm", "a-profile.rtkmvvm");
        CompilerResult second = CompilerProcess.Run(
            workspace.Root, "compile", "a-profile.rtkmvvm", "z-settings.rtkmvvm");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal(first.StandardError, second.StandardError);
    }

    private static void MultipleContractsProduceViableSource()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("settings.rtkmvvm", SettingsSource);
        workspace.WriteText("profile.rtkmvvm", ProfileSource);

        CompilerResult validation = CompilerProcess.Run(
            workspace.Root, "validate", "settings.rtkmvvm", "profile.rtkmvvm");
        CompilerResult compilation = CompilerProcess.Run(
            workspace.Root, "compile", "settings.rtkmvvm", "profile.rtkmvvm");

        Assert.Equal(0, validation.ExitCode);
        Assert.Empty(validation.StandardOutput);
        Assert.Empty(validation.StandardError);
        Assert.Equal(0, compilation.ExitCode);
        Assert.Empty(compilation.StandardError);
        Assert.Equal(2, CountOccurrences(compilation.StandardOutput, "GeneratedCodeAttribute"),
            "Expected one generated artifact for each contract.");
        Assert.Equal(0, compilation.StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries)
            .Count(static line => line.StartsWith("namespace ", StringComparison.Ordinal) && line.EndsWith(';')),
            "Concatenated output contains multiple file-scoped namespace declarations.");
    }

    private static void RepeatCompilationPreservesOutputBytes()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("settings.rtkmvvm", SettingsSource);
        string output = Path.Combine(workspace.Root, "obj", "bindings.g.cs");

        CompilerResult first = CompilerProcess.Run(
            workspace.Root, "compile", "--output", output, "settings.rtkmvvm");
        byte[] firstBytes = File.ReadAllBytes(output);
        DateTime sentinelTimestamp = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(output, sentinelTimestamp);
        CompilerResult second = CompilerProcess.Run(
            workspace.Root, "compile", "--output", output, "settings.rtkmvvm");
        byte[] secondBytes = File.ReadAllBytes(output);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Empty(first.StandardOutput);
        Assert.Empty(second.StandardOutput);
        Assert.True(firstBytes.SequenceEqual(secondBytes), "No-op compilation changed output bytes.");
        Assert.Equal(sentinelTimestamp, File.GetLastWriteTimeUtc(output),
            "No-op compilation replaced an already identical output file.");
    }

    private static void DiagnosticsReportExactSpans()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText(
            "invalid.rtkmvvm",
            "protocol runic.toolkit.mvvm/1;\ncontract sample model Example.Sample {\n  =\n}\n");

        CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", "invalid.rtkmvvm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            "invalid.rtkmvvm(3,3,3,4): error RTKMVVM1001: Unexpected character U+003D." + Environment.NewLine,
            result.StandardError);
    }

    private static void CompileWritesNothingOnErrors()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("invalid.rtkmvvm", "protocol runic.toolkit.mvvm/2;\n");

        CompilerResult result = CompilerProcess.Run(workspace.Root, "compile", "invalid.rtkmvvm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("RTKMVVM2002", result.StandardError);
    }

    private static void CrossFileDuplicateContractsReportExactSpan()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText(
            "first.rtkmvvm",
            "protocol runic.toolkit.mvvm/1;\ncontract duplicate model Example.First {}\n");
        workspace.WriteText(
            "second.rtkmvvm",
            "protocol runic.toolkit.mvvm/1;\ncontract duplicate model Example.Second {}\n");
        string output = workspace.WriteText("obj/bindings.g.cs", "sentinel");
        string expectedDiagnostic =
            "second.rtkmvvm(2,10,2,19): error RTKMVVM2013: " +
            "Contract identity 'duplicate' is declared more than once." + Environment.NewLine;

        CompilerResult validation = CompilerProcess.Run(
            workspace.Root, "validate", "second.rtkmvvm", "first.rtkmvvm");
        CompilerResult compilation = CompilerProcess.Run(
            workspace.Root, "compile", "--output", output, "second.rtkmvvm", "first.rtkmvvm");

        Assert.Equal(1, validation.ExitCode);
        Assert.Empty(validation.StandardOutput);
        Assert.Equal(expectedDiagnostic, validation.StandardError);
        Assert.Equal(1, compilation.ExitCode);
        Assert.Empty(compilation.StandardOutput);
        Assert.Equal(expectedDiagnostic, compilation.StandardError);
        Assert.Equal("sentinel", File.ReadAllText(output));
    }

    private static void FailedCompilePreservesOutputFile()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("invalid.rtkmvvm", "protocol runic.toolkit.mvvm/2;\n");
        string output = workspace.WriteText("obj/bindings.g.cs", "sentinel");

        CompilerResult result = CompilerProcess.Run(
            workspace.Root, "compile", "--output", output, "invalid.rtkmvvm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("sentinel", File.ReadAllText(output));
    }

    private static void MalformedUtf8IsRejected()
    {
        using TestWorkspace workspace = new();
        workspace.WriteBytes("invalid.rtkmvvm", [0x70, 0x80, 0x71]);

        CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", "invalid.rtkmvvm");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("not valid UTF-8", result.StandardError);
        Assert.False(result.StandardError.Contains("DecoderFallbackException", StringComparison.Ordinal),
            "Encoding failure leaked an implementation exception.");
    }

    private static void Utf8BomIsAccepted()
    {
        using TestWorkspace workspace = new();
        byte[] source = Encoding.UTF8.GetBytes(SettingsSource);
        byte[] withBom = Encoding.UTF8.GetPreamble().Concat(source).ToArray();
        workspace.WriteBytes("with-bom.rtkmvvm", withBom);
        workspace.WriteBytes("without-bom.rtkmvvm", source);

        CompilerResult withBomResult = CompilerProcess.Run(workspace.Root, "compile", "with-bom.rtkmvvm");
        CompilerResult withoutBomResult = CompilerProcess.Run(workspace.Root, "compile", "without-bom.rtkmvvm");

        Assert.Equal(0, withBomResult.ExitCode);
        Assert.Equal(0, withoutBomResult.ExitCode);
        Assert.Empty(withBomResult.StandardError);
        Assert.Empty(withoutBomResult.StandardError);
        Assert.Equal(withoutBomResult.StandardOutput, withBomResult.StandardOutput);
    }

    private static void PerFileLimitIsEnforced()
    {
        using TestWorkspace workspace = new();
        workspace.WriteBytes("large.rtkmvvm", new byte[(1024 * 1024) + 1]);

        CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", "large.rtkmvvm");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("exceeds the 1048576 byte limit", result.StandardError);
    }

    private static void AggregateLimitIsEnforced()
    {
        using TestWorkspace workspace = new();
        byte[] oneMiB = new byte[1024 * 1024];
        var arguments = new List<string> { "validate" };
        for (int index = 0; index < 17; index++)
        {
            string relativePath = $"inputs/{index:D2}.rtkmvvm";
            workspace.WriteBytes(relativePath, oneMiB);
            arguments.Add(relativePath);
        }

        CompilerResult result = CompilerProcess.Run(workspace.Root, arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("exceed the 16777216 total byte limit", result.StandardError);
    }

    private static void OutsidePathIsRejected()
    {
        using TestWorkspace workspace = new();
        string outsideDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        string outsideFile = Path.Combine(outsideDirectory, "outside.rtkmvvm");
        try
        {
            File.WriteAllText(outsideFile, SettingsSource, new UTF8Encoding(false));
            CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", outsideFile);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("outside the current project directory", result.StandardError);
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private static void DuplicatePathIsRejected()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("same.rtkmvvm", SettingsSource);

        CompilerResult result = CompilerProcess.Run(
            workspace.Root, "validate", "same.rtkmvvm", "." + Path.DirectorySeparatorChar + "same.rtkmvvm");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Duplicate input path 'same.rtkmvvm'.", result.StandardError);
    }

    private static void ArgumentCountLimitIsEnforced()
    {
        using TestWorkspace workspace = new();
        string[] arguments = Enumerable.Repeat("missing.rtkmvvm", 513).ToArray();

        CompilerResult result = CompilerProcess.Run(workspace.Root, arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("No more than 512 arguments are accepted.", result.StandardError);
    }

    private static void InputCountLimitIsEnforced()
    {
        using TestWorkspace workspace = new();
        var arguments = new List<string> { "validate" };
        arguments.AddRange(Enumerable.Range(0, 257).Select(index => $"missing-{index}.rtkmvvm"));

        CompilerResult result = CompilerProcess.Run(workspace.Root, arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("No more than 256 input files are accepted.", result.StandardError);
    }

    private static void OptionTerminatorAdmitsDashPath()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("-settings.rtkmvvm", SettingsSource);

        CompilerResult result = CompilerProcess.Run(workspace.Root, "validate", "--", "-settings.rtkmvvm");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    private static void OutputCannotOverwriteInput()
    {
        using TestWorkspace workspace = new();
        workspace.WriteText("settings.rtkmvvm", SettingsSource);

        CompilerResult result = CompilerProcess.Run(
            workspace.Root, "compile", "--output", "settings.rtkmvvm", "settings.rtkmvvm");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("output path cannot be one of the input paths", result.StandardError);
        Assert.Equal(SettingsSource, File.ReadAllText(Path.Combine(workspace.Root, "settings.rtkmvvm")));
    }

    private static int CountOccurrences(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }
}
