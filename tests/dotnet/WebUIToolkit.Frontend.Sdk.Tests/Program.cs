using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace WebUIToolkit.Frontend.Sdk.Tests;

internal static class Program
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ContractTool = Path.Combine(
        RepositoryRoot,
        "src",
        "WebUIToolkit.Frontend.Sdk",
        "tools",
        "generate-contracts.mjs");
    private static readonly string SdkProps = Path.Combine(
        RepositoryRoot,
        "src",
        "WebUIToolkit.Frontend.Sdk",
        "buildTransitive",
        "WebUIToolkit.Frontend.Sdk.props");
    private static readonly string SdkTargets = Path.Combine(
        RepositoryRoot,
        "src",
        "WebUIToolkit.Frontend.Sdk",
        "buildTransitive",
        "WebUIToolkit.Frontend.Sdk.targets");

    public static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("generation is deterministic", GenerationIsDeterministic),
            ("verify accepts current outputs", VerifyAcceptsCurrentOutputs),
            ("verify rejects stale outputs", VerifyRejectsStaleOutputs),
            ("malformed contracts fail validation", MalformedContractsFailValidation),
            ("SDK validation accepts complete configuration", SdkValidationAcceptsCompleteConfiguration),
            ("SDK validation accepts a Node-free cwhtml pipeline", SdkValidationAcceptsCwhtmlOnlyConfiguration),
            ("SDK validation rejects a missing pipeline", SdkValidationRejectsMissingPipeline),
            ("SDK validation rejects incomplete contract outputs", SdkValidationRejectsIncompleteContractOutputs),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} frontend SDK tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void GenerationIsDeterministic()
    {
        using TestWorkspace workspace = new();
        ContractFixture fixture = workspace.CreateContractFixture();

        ProcessResult first = RunContractTool(workspace.Root, fixture);
        AssertSuccess(first, "Initial contract generation");
        byte[] firstCSharp = File.ReadAllBytes(fixture.CSharpOutput);
        byte[] firstTypeScript = File.ReadAllBytes(fixture.TypeScriptOutput);

        ProcessResult second = RunContractTool(workspace.Root, fixture);
        AssertSuccess(second, "Repeated contract generation");

        AssertSequenceEqual(
            firstCSharp,
            File.ReadAllBytes(fixture.CSharpOutput),
            "C# output changed between identical compiler invocations.");
        AssertSequenceEqual(
            firstTypeScript,
            File.ReadAllBytes(fixture.TypeScriptOutput),
            "TypeScript output changed between identical compiler invocations.");
    }

    private static void VerifyAcceptsCurrentOutputs()
    {
        using TestWorkspace workspace = new();
        ContractFixture fixture = workspace.CreateContractFixture();

        AssertSuccess(RunContractTool(workspace.Root, fixture), "Contract generation");
        AssertSuccess(RunContractTool(workspace.Root, fixture, verify: true), "Contract verification");
    }

    private static void VerifyRejectsStaleOutputs()
    {
        using TestWorkspace workspace = new();
        ContractFixture fixture = workspace.CreateContractFixture();

        AssertSuccess(RunContractTool(workspace.Root, fixture), "Contract generation");
        File.AppendAllText(fixture.TypeScriptOutput, "// stale\n", new UTF8Encoding(false));

        ProcessResult result = RunContractTool(workspace.Root, fixture, verify: true);
        AssertFailure(result, "Stale contract verification");
        AssertContains(
            result.CombinedOutput,
            "Generated frontend contract is stale",
            "Stale verification did not identify the generated artifact.");
    }

    private static void MalformedContractsFailValidation()
    {
        using TestWorkspace workspace = new();
        ContractFixture fixture = workspace.CreateContractFixture(
            """
            {
              "$schema": "webuitoolkit.mvvm.frontend-contract/1",
              "csharp": {
                "namespace": "Tests.Generated",
                "className": "FixtureContracts"
              },
              "contracts": [
                {
                  "name": "fixture",
                  "client": "Fixture",
                  "csharp": {
                    "modelType": "global::Tests.FixtureModel"
                  },
                  "members": [
                    {
                      "id": 0,
                      "name": "title",
                      "kind": "property",
                      "type": "string",
                      "access": "readwrite",
                      "csharp": {
                        "sourceMember": "Title",
                        "binding": "property",
                        "jsonTypeInfo": "global::Tests.Json.String"
                      }
                    }
                  ]
                }
              ]
            }
            """);

        ProcessResult result = RunContractTool(workspace.Root, fixture);
        AssertFailure(result, "Malformed contract generation");
        AssertContains(
            result.CombinedOutput,
            "must be positive integers",
            "Validation failure was not actionable.");
        AssertFalse(File.Exists(fixture.CSharpOutput), "Malformed input unexpectedly emitted C#.");
        AssertFalse(File.Exists(fixture.TypeScriptOutput), "Malformed input unexpectedly emitted TypeScript.");
    }

    private static void SdkValidationAcceptsCompleteConfiguration()
    {
        using TestWorkspace workspace = new();
        string project = workspace.WriteText(
            "valid.proj",
            CreateValidationProject(
                """
                <WebUIToolkitFrontendWorkspace>fixture</WebUIToolkitFrontendWorkspace>
                <WebUIToolkitFrontendPackageDirectory>frontend</WebUIToolkitFrontendPackageDirectory>
                """));

        ProcessResult result = Run(
            ResolveDotNetHost(),
            workspace.Root,
            ["msbuild", project, "-nologo", "-verbosity:minimal", "-target:WebUIToolkitFrontendValidate"]);
        AssertSuccess(result, "SDK target validation");
    }

    private static void SdkValidationRejectsIncompleteContractOutputs()
    {
        using TestWorkspace workspace = new();
        string project = workspace.WriteText(
            "invalid.proj",
            CreateValidationProject(
                """
                <WebUIToolkitFrontendWorkspace>fixture</WebUIToolkitFrontendWorkspace>
                <WebUIToolkitFrontendPackageDirectory>frontend</WebUIToolkitFrontendPackageDirectory>
                <WebUIToolkitFrontendContractSource>contract.json</WebUIToolkitFrontendContractSource>
                <WebUIToolkitFrontendContractCSharpOutput>Contract.g.cs</WebUIToolkitFrontendContractCSharpOutput>
                """));

        ProcessResult result = Run(
            ResolveDotNetHost(),
            workspace.Root,
            ["msbuild", project, "-nologo", "-verbosity:minimal", "-target:WebUIToolkitFrontendValidate"]);
        AssertFailure(result, "Incomplete SDK target validation");
        AssertContains(
            result.CombinedOutput,
            "Both frontend contract output paths are required",
            "SDK validation did not explain the incomplete contract configuration.");
    }

    private static void SdkValidationAcceptsCwhtmlOnlyConfiguration()
    {
        using TestWorkspace workspace = new();
        string project = workspace.WriteText(
            "cwhtml.proj",
            CreateValidationProject(
                """
                <WebUIToolkitFrontendNodeEnabled>false</WebUIToolkitFrontendNodeEnabled>
                <WebUIToolkitFrontendCwhtmlEnabled>true</WebUIToolkitFrontendCwhtmlEnabled>
                """));

        ProcessResult result = Run(
            ResolveDotNetHost(),
            workspace.Root,
            ["msbuild", project, "-nologo", "-verbosity:minimal", "-target:WebUIToolkitFrontendValidate"]);
        AssertSuccess(result, "cwhtml-only SDK target validation");
    }

    private static void SdkValidationRejectsMissingPipeline()
    {
        using TestWorkspace workspace = new();
        string project = workspace.WriteText(
            "missing-pipeline.proj",
            CreateValidationProject(
                """
                <WebUIToolkitFrontendNodeEnabled>false</WebUIToolkitFrontendNodeEnabled>
                <WebUIToolkitFrontendCwhtmlEnabled>false</WebUIToolkitFrontendCwhtmlEnabled>
                """));

        ProcessResult result = Run(
            ResolveDotNetHost(),
            workspace.Root,
            ["msbuild", project, "-nologo", "-verbosity:minimal", "-target:WebUIToolkitFrontendValidate"]);
        AssertFailure(result, "Missing SDK pipeline validation");
        AssertContains(
            result.CombinedOutput,
            "Enable at least one WebUIToolkit frontend pipeline",
            "SDK validation did not explain that no frontend pipeline was enabled.");
    }

    private static ProcessResult RunContractTool(
        string workingDirectory,
        ContractFixture fixture,
        bool verify = false)
    {
        List<string> arguments =
        [
            ContractTool,
            "--source",
            fixture.Source,
            "--csharp",
            fixture.CSharpOutput,
            "--typescript",
            fixture.TypeScriptOutput,
        ];
        if (verify)
        {
            arguments.Add("--verify");
        }

        return Run("node", workingDirectory, arguments);
    }

    private static string CreateValidationProject(string properties)
    {
        string escapedProps = SecurityElement.Escape(SdkProps)
            ?? throw new InvalidOperationException("Could not XML-escape the SDK props path.");
        string escapedTargets = SecurityElement.Escape(SdkTargets)
            ?? throw new InvalidOperationException("Could not XML-escape the SDK targets path.");

        return $"""
            <Project>
              <Import Project="{escapedProps}" />
              <PropertyGroup>
                <WebUIToolkitFrontendEnabled>true</WebUIToolkitFrontendEnabled>
                {properties}
              </PropertyGroup>
              <Import Project="{escapedTargets}" />
            </Project>
            """;
    }

    private static ProcessResult Run(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"'{executable}' exceeded the 30 second test timeout.");
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static void AssertSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with exit code {result.ExitCode}.{Environment.NewLine}{result.CombinedOutput}");
        }
    }

    private static void AssertFailure(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            throw new InvalidOperationException($"{operation} unexpectedly succeeded.");
        }
    }

    private static void AssertContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message}{Environment.NewLine}Expected: {expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
        }
    }

    private static void AssertSequenceEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertFalse(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WebUIToolkit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find WebUIToolkit.slnx from '{AppContext.BaseDirectory}'.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
    }

    private sealed record ContractFixture(string Source, string CSharpOutput, string TypeScriptOutput);

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "WebUIToolkit.Frontend.Sdk.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public ContractFixture CreateContractFixture(string? contract = null)
        {
            string source = WriteText("contract.json", contract ?? ValidContract);
            string output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(output);
            return new ContractFixture(
                source,
                Path.Combine(output, "Contracts.g.cs"),
                Path.Combine(output, "contract.g.ts"));
        }

        public string WriteText(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup failure must not hide the test result.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IOException case above.
            }
        }

        private const string ValidContract =
            """
            {
              "$schema": "webuitoolkit.mvvm.frontend-contract/1",
              "csharp": {
                "namespace": "Tests.Generated",
                "className": "FixtureContracts"
              },
              "contracts": [
                {
                  "name": "fixture",
                  "client": "Fixture",
                  "csharp": {
                    "modelType": "global::Tests.FixtureModel"
                  },
                  "types": {
                    "FixtureItem": {
                      "id": "string",
                      "title": "string"
                    }
                  },
                  "members": [
                    {
                      "id": 1,
                      "name": "title",
                      "kind": "property",
                      "type": "string",
                      "access": "readwrite",
                      "validation": true,
                      "csharp": {
                        "sourceMember": "Title",
                        "binding": "property",
                        "jsonTypeInfo": "global::Tests.Json.String"
                      }
                    },
                    {
                      "id": 2,
                      "name": "items",
                      "kind": "collection",
                      "type": "FixtureItem",
                      "csharp": {
                        "sourceMember": "Items",
                        "binding": "collection",
                        "jsonTypeInfo": "global::Tests.Json.FixtureItems"
                      }
                    },
                    {
                      "id": 3,
                      "name": "save",
                      "kind": "command",
                      "csharp": {
                        "sourceMember": "SaveCommand",
                        "binding": "command"
                      }
                    },
                    {
                      "id": 4,
                      "name": "remove",
                      "kind": "command",
                      "argument": "string",
                      "csharp": {
                        "sourceMember": "RemoveCommand",
                        "binding": "asyncCommand",
                        "jsonTypeInfo": "global::Tests.Json.String"
                      }
                    },
                    {
                      "id": 5,
                      "name": "summary",
                      "kind": "property",
                      "type": "string",
                      "access": "readonly",
                      "csharp": {
                        "sourceMember": "Summary",
                        "binding": "readOnlyProperty",
                        "jsonTypeInfo": "global::Tests.Json.String"
                      }
                    }
                  ]
                }
              ]
            }
            """;
    }
}
