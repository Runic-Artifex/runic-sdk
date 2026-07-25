using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Text;
using System.Text.Json;
using WebUIToolkit.MVVM.Build.Compiler;
using WebUIToolkit.MVVM.Build.Generation;
using WebUIToolkit.MVVM.Build.Symbols;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class GeneratedMemberContractTests
{
    private const string FixtureTypeName = "WebUIToolkit.MVVM.Build.Tests.Fixtures.GeneratedMemberViewModel";
    private const string TitleFixtureId = "communitytoolkit.generated-member.title.v1";
    private const string CommandFixtureId = "communitytoolkit.generated-member.submit-command.v1";

    public static void Register(TestRunner runner)
    {
        runner.Add(TitleFixtureId, TitleGeneratedMemberIsObservedAndExecuted);
        runner.Add(CommandFixtureId, SubmitCommandGeneratedMemberIsObservedAndExecuted);
        runner.Add("generated-member compiler reports stable metadata diagnostics", ReportsStableDiagnostics);
        runner.Add("generated-member compiler rejects hostile identifiers and escapes JSON controls", RejectsHostileIdentifiersAndEscapesJsonControls);
        runner.Add("generated-member compiler output is root, culture, and enumeration deterministic", OutputIsDeterministic);
    }

    private static void TitleGeneratedMemberIsObservedAndExecuted()
    {
        GeneratedBindingArtifacts artifact = CompileFixtureArtifact();
        Assert.Contains("Get_Title", artifact.Source);
        Assert.Contains("Set_Title", artifact.Source);
        Assert.Contains("viewModel.Title", artifact.Source);
        Assert.Equal(0, RunConsumer(artifact));
    }

    private static void SubmitCommandGeneratedMemberIsObservedAndExecuted()
    {
        GeneratedBindingArtifacts artifact = CompileFixtureArtifact();
        Assert.Contains("CanExecute_SubmitCommand", artifact.Source);
        Assert.Contains("Execute_SubmitCommand", artifact.Source);
        Assert.Contains("viewModel.SubmitCommand", artifact.Source);
        Assert.Equal(0, RunConsumer(artifact));
    }

    private static void ReportsStableDiagnostics()
    {
        AssertDiagnostic(
            new GeneratedMemberContractRequest("missing.dll", FixtureTypeName, Requirements()),
            BindingDiagnosticIds.GeneratedMemberAssemblyNotFound);
        AssertDiagnostic(
            new GeneratedMemberContractRequest(ProducerAssemblyPath(), "Missing.Type", Requirements()),
            BindingDiagnosticIds.GeneratedMemberTypeNotFound);
        AssertDiagnostic(
            new GeneratedMemberContractRequest(
                ProducerAssemblyPath(),
                FixtureTypeName,
                [new GeneratedMemberRequirement("missing", "Missing", GeneratedMemberKind.Property, "System.String")]),
            BindingDiagnosticIds.GeneratedMemberMissing);
        AssertDiagnostic(
            new GeneratedMemberContractRequest(
                ProducerAssemblyPath(),
                FixtureTypeName,
                [new GeneratedMemberRequirement("title", "Title", GeneratedMemberKind.Property, "System.Int32")]),
            BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        AssertDiagnostic(
            new GeneratedMemberContractRequest(
                ProducerAssemblyPath(),
                FixtureTypeName,
                [
                    new GeneratedMemberRequirement("title", "Title", GeneratedMemberKind.Property, "System.String"),
                    new GeneratedMemberRequirement("title", "Title", GeneratedMemberKind.Property, "System.String"),
                ]),
            BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate);
    }

    private static void RejectsHostileIdentifiersAndEscapesJsonControls()
    {
        GeneratedMemberContractResult escapedKeyword = GeneratedMemberContractCompiler.Compile(new GeneratedMemberContractRequest(
            ProducerAssemblyPath(),
            FixtureTypeName,
            [new GeneratedMemberRequirement("keyword", "class", GeneratedMemberKind.Property, "System.String")]));
        Assert.Contains("viewModel.@class", Assert.Single(escapedKeyword.Artifacts).Source);

        GeneratedMemberContractResult hostile = GeneratedMemberContractCompiler.Compile(new GeneratedMemberContractRequest(
            ProducerAssemblyPath(),
            FixtureTypeName,
            [new GeneratedMemberRequirement("hostile", "Title;System.Console.WriteLine()", GeneratedMemberKind.Property, "System.String")]));
        Assert.Equal(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible, Assert.Single(hostile.Diagnostics).Id);
        Assert.Equal(0, hostile.Artifacts.Count, "Unsafe metadata-derived identifiers must not produce C# output.");

        GeneratedMemberContractResult escaped = GeneratedMemberContractCompiler.Compile(new GeneratedMemberContractRequest(
            ProducerAssemblyPath(),
            FixtureTypeName,
            [new GeneratedMemberRequirement("title\u0001control", "Title", GeneratedMemberKind.Property, "System.String")]));
        GeneratedBindingArtifacts artifact = Assert.Single(escaped.Artifacts);
        Assert.Equal(0, escaped.Diagnostics.Count);
        Assert.False(artifact.Manifest.Contains('\u0001'), "JSON manifests must never contain raw control characters.");
        Assert.Contains("\\u0001", artifact.Manifest);
        using JsonDocument document = JsonDocument.Parse(artifact.Manifest);
        Assert.Equal("title\u0001control", document.RootElement.GetProperty("members")[0].GetProperty("bindingMemberId").GetString());
    }

    private static void OutputIsDeterministic()
    {
        string root = Path.Combine(Path.GetTempPath(), "WebUIToolkit.MVVM.GeneratedMember", Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        try
        {
            string firstAssembly = BuildProducer(firstRoot, CultureInfo.InvariantCulture, reverseReferences: false);
            string secondAssembly = BuildProducer(secondRoot, CultureInfo.GetCultureInfo("tr-TR"), reverseReferences: true);
            Guid firstMvid = ReadModuleVersionId(firstAssembly);
            Guid secondMvid = ReadModuleVersionId(secondAssembly);
            Assert.False(firstMvid == secondMvid,
                "The clean producer builds must carry distinct MVIDs so the output comparison proves MVID independence.");

            GeneratedMemberContractResult first = CompileUnderCulture(firstAssembly, CultureInfo.InvariantCulture);
            GeneratedMemberContractResult second = CompileUnderCulture(secondAssembly, CultureInfo.GetCultureInfo("tr-TR"));
            GeneratedBindingArtifacts firstArtifact = Assert.Single(first.Artifacts);
            GeneratedBindingArtifacts secondArtifact = Assert.Single(second.Artifacts);

            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(firstArtifact.Source))),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secondArtifact.Source))),
                "Generated source bytes must match across clean roots, cultures, MVIDs, and reference order.");
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(firstArtifact.Manifest))),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secondArtifact.Manifest))),
                "Generated manifest bytes must match across clean roots, cultures, MVIDs, and reference order.");
            Assert.Equal(firstArtifact.Fingerprint, secondArtifact.Fingerprint);
            Assert.Equal(firstArtifact.SourceHintName, secondArtifact.SourceHintName);
            Assert.Equal(firstArtifact.ManifestFileName, secondArtifact.ManifestFileName);
            Assert.Equal(NormalizeDiagnostics(first.Diagnostics), NormalizeDiagnostics(second.Diagnostics));
            PostGeneratorSemanticTests.AssertDeterministic(
                firstAssembly,
                secondAssembly,
                firstRoot,
                secondRoot,
                firstMvid,
                secondMvid);

            GeneratedMemberContractResult firstMissing = CompileMissingMemberUnderCulture(
                firstAssembly, CultureInfo.InvariantCulture);
            GeneratedMemberContractResult secondMissing = CompileMissingMemberUnderCulture(
                secondAssembly, CultureInfo.GetCultureInfo("tr-TR"));
            Assert.Equal(
                NormalizeDiagnostics(firstMissing.Diagnostics),
                NormalizeDiagnostics(secondMissing.Diagnostics),
                "Normalized diagnostics must match across clean roots, cultures, MVIDs, and reference order.");

            string[] forbiddenState =
            [
                firstRoot,
                secondRoot,
                firstAssembly,
                secondAssembly,
                firstMvid.ToString(),
                secondMvid.ToString(),
                "tr-TR",
                Environment.MachineName,
                Environment.UserName,
            ];
            foreach (string forbidden in forbiddenState.Where(static value => !string.IsNullOrEmpty(value)))
            {
                Assert.False(firstArtifact.Source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Generated source must exclude absolute roots, timestamps, MVIDs, locale, and machine state.");
                Assert.False(firstArtifact.Manifest.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Generated manifests must exclude absolute roots, timestamps, MVIDs, locale, and machine state.");
                Assert.False(NormalizeDiagnostics(firstMissing.Diagnostics).Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Normalized diagnostics must exclude absolute roots, timestamps, MVIDs, locale, and machine state.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GeneratedBindingArtifacts CompileFixtureArtifact()
    {
        GeneratedMemberContractResult result = GeneratedMemberContractCompiler.Compile(new GeneratedMemberContractRequest(
            ProducerAssemblyPath(),
            FixtureTypeName,
            Requirements()));
        Assert.Equal(0, result.Diagnostics.Count);
        return Assert.Single(result.Artifacts);
    }

    private static GeneratedMemberRequirement[] Requirements() =>
    [
        new GeneratedMemberRequirement("title", "Title", GeneratedMemberKind.Property, "System.String"),
        new GeneratedMemberRequirement("submit-command", "SubmitCommand", GeneratedMemberKind.Command, "CommunityToolkit.Mvvm.Input.IRelayCommand"),
    ];

    private static string ProducerAssemblyPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WebUIToolkit.MVVM.Build.Tests.dll");
        Assert.True(File.Exists(path), "The real CommunityToolkit producer PE was not copied beside the test executable.");
        return path;
    }

    private static void AssertDiagnostic(GeneratedMemberContractRequest request, string expectedId)
    {
        GeneratedMemberContractResult result = GeneratedMemberContractCompiler.Compile(request);
        Assert.Equal(0, result.Artifacts.Count);
        BindingDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
        Assert.Equal("generated-member-contract", diagnostic.Span.LogicalPath);
    }

    private static int RunConsumer(GeneratedBindingArtifacts artifact)
    {
        string root = Path.Combine(Path.GetTempPath(), "WebUIToolkit.MVVM.GeneratedMemberConsumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string producerAssembly = ProducerAssemblyPath();
            string toolkitAssembly = Path.Combine(AppContext.BaseDirectory, "CommunityToolkit.Mvvm.dll");
            Assert.True(File.Exists(toolkitAssembly), "CommunityToolkit.Mvvm 8.4.2 was not copied to the producer output.");
            File.WriteAllText(Path.Combine(root, "Adapter.g.cs"), artifact.Source, new UTF8Encoding(false));
            string adapterTypeName = "GeneratedMemberContractAdapter_" + artifact.Fingerprint[..16];
            File.WriteAllText(Path.Combine(root, "Program.cs"), $$"""
                using WebUIToolkit.MVVM.Build.Tests.Fixtures;
                using WebUIToolkit.MVVM.Generated;

                GeneratedMemberViewModel viewModel = new();
                {{adapterTypeName}}.Set_Title(viewModel, "generated title");
                if (!string.Equals((string?){{adapterTypeName}}.Get_Title(viewModel), "generated title", System.StringComparison.Ordinal))
                {
                    return 10;
                }

                if (!{{adapterTypeName}}.CanExecute_SubmitCommand(viewModel))
                {
                    return 11;
                }

                {{adapterTypeName}}.Execute_SubmitCommand(viewModel);
                return viewModel.SubmissionCount == 1 ? 0 : 12;
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Consumer.csproj"), string.Create(CultureInfo.InvariantCulture, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="WebUIToolkit.MVVM.Build.Tests">
                      <HintPath>{SecurityElement.Escape(producerAssembly)}</HintPath>
                    </Reference>
                    <Reference Include="CommunityToolkit.Mvvm">
                      <HintPath>{SecurityElement.Escape(toolkitAssembly)}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """), new UTF8Encoding(false));

            RunDotNet(root, "build", "Consumer.csproj", "--nologo", "--verbosity", "quiet");
            return RunDotNet(root, "exec", Path.Combine(root, "bin", "Debug", "net10.0", "Consumer.dll"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string BuildProducer(string root, CultureInfo culture, bool reverseReferences)
    {
        Directory.CreateDirectory(root);
        string toolkitAssembly = Path.Combine(AppContext.BaseDirectory, "CommunityToolkit.Mvvm.dll");
        string buildAssembly = typeof(GeneratedMemberContractCompiler).Assembly.Location;
        string testAssembly = ProducerAssemblyPath();
        string packageRoot = Path.Combine(FindRepositoryRoot(), ".packages", "nuget");
        Assert.True(File.Exists(toolkitAssembly), "CommunityToolkit.Mvvm must be available for the clean producer build.");
        Assert.True(File.Exists(buildAssembly), "The build assembly must be available as the second ordered metadata reference.");
        Assert.True(Directory.Exists(packageRoot), "The repository-local NuGet package root must exist.");

        (string Name, string Path)[] references =
        [
            ("WebUIToolkit.MVVM.Build", buildAssembly),
            ("WebUIToolkit.MVVM.Build.Tests", testAssembly),
        ];
        if (reverseReferences)
        {
            Array.Reverse(references);
        }

        string referenceItems = string.Join(
            Environment.NewLine,
            references.Select(static reference => string.Create(
                CultureInfo.InvariantCulture,
                $"    <Reference Include=\"{reference.Name}\"><HintPath>{SecurityElement.Escape(reference.Path)}</HintPath><Private>false</Private></Reference>")));
        File.WriteAllText(Path.Combine(root, "Producer.csproj"), string.Create(CultureInfo.InvariantCulture, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>GeneratedMemberProducer</AssemblyName>
                <RootNamespace>WebUIToolkit.MVVM.Build.Tests.Fixtures</RootNamespace>
                <Deterministic>false</Deterministic>
                <DebugType>none</DebugType>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                <RestorePackagesPath>{SecurityElement.Escape(packageRoot)}</RestorePackagesPath>
              </PropertyGroup>
              <ItemGroup>
            {referenceItems}
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
              </ItemGroup>
            </Project>
            """), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "GeneratedMemberViewModel.cs"), """
            using CommunityToolkit.Mvvm.ComponentModel;
            using CommunityToolkit.Mvvm.Input;

            namespace WebUIToolkit.MVVM.Build.Tests.Fixtures;

            public partial class GeneratedMemberViewModel : ObservableValidator
            {
                [ObservableProperty]
                private string? title;

                [ObservableProperty]
                [NotifyDataErrorInfo]
                [System.ComponentModel.DataAnnotations.Required]
                private string? name = "valid";

                public string @class { get; set; } = string.Empty;

                public int SubmissionCount { get; private set; }

                [RelayCommand]
                private void Submit() => SubmissionCount++;

                public int MultipliedBy { get; private set; }

                public int ScaledBy { get; private set; }

                public System.Threading.Tasks.TaskCompletionSource LoadStarted { get; } =
                    new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

                public bool LoadCanceled { get; private set; }

                [RelayCommand(CanExecute = nameof(CanMultiply))]
                private void Multiply(int value) => MultipliedBy = value;

                private static bool CanMultiply(int value) => value > 0;

                [RelayCommand]
                private async System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken cancellationToken)
                {
                    LoadStarted.TrySetResult();
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(
                            System.Threading.Timeout.InfiniteTimeSpan,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        LoadCanceled = true;
                        throw;
                    }
                }

                [RelayCommand]
                private async System.Threading.Tasks.Task ScaleAsync(
                    int value,
                    System.Threading.CancellationToken cancellationToken)
                {
                    await System.Threading.Tasks.Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    ScaledBy = value;
                }
            }
            """, new UTF8Encoding(false));

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_UI_LANGUAGE"] = culture.Equals(CultureInfo.InvariantCulture) ? "en-US" : culture.Name,
            ["LANG"] = culture.Equals(CultureInfo.InvariantCulture) ? "C.UTF-8" : "tr_TR.UTF-8",
        };
        RunDotNetWithEnvironment(root, environment, "restore", "Producer.csproj", "--nologo", "--verbosity", "quiet");
        RunDotNetWithEnvironment(
            root, environment, "build", "Producer.csproj", "--configuration", "Release", "--no-restore", "--nologo", "--verbosity", "quiet");
        string assembly = Path.Combine(root, "bin", "Release", "net10.0", "GeneratedMemberProducer.dll");
        Assert.True(File.Exists(assembly), "The clean producer build did not emit its PE.");
        return assembly;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NuGet.config")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output.");
    }

    private static GeneratedMemberContractResult CompileUnderCulture(string assemblyPath, CultureInfo culture)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return GeneratedMemberContractCompiler.Compile(
                new GeneratedMemberContractRequest(assemblyPath, FixtureTypeName, Requirements()));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static GeneratedMemberContractResult CompileMissingMemberUnderCulture(
        string assemblyPath,
        CultureInfo culture)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return GeneratedMemberContractCompiler.Compile(new GeneratedMemberContractRequest(
                assemblyPath,
                FixtureTypeName,
                [new GeneratedMemberRequirement("missing", "Missing", GeneratedMemberKind.Property, "System.String")]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static Guid ReadModuleVersionId(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private static string NormalizeDiagnostics(IReadOnlyList<BindingDiagnostic> diagnostics) =>
        string.Join(
            "\n",
            diagnostics.Select(static diagnostic => string.Create(
                CultureInfo.InvariantCulture,
                $"{diagnostic.Id}|{diagnostic.Severity}|{diagnostic.Message}|{diagnostic.Span.LogicalPath}|{diagnostic.Span.Start.Offset}:{diagnostic.Span.End.Offset}")));

    private static int RunDotNet(string workingDirectory, params string[] arguments)
        => RunDotNetWithEnvironment(workingDirectory, null, arguments);

    private static int RunDotNetWithEnvironment(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the consumer process.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The generated-member consumer timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The generated-member consumer failed: {standardOutput}{standardError}");
        }

        return process.ExitCode;
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }
}
