using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using WebUIToolkit.MVVM.Build.Compiler;
using WebUIToolkit.MVVM.Build.Generation;
using WebUIToolkit.MVVM.Build.Symbols;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class PostGeneratorSemanticTests
{
    private const string FixtureTypeName = "WebUIToolkit.MVVM.Build.Tests.Fixtures.GeneratedMemberViewModel";
    private const string RelayCommandOfInt =
        "CommunityToolkit.Mvvm.Input.IRelayCommand`1<System.Int32>";
    private const string AsyncRelayCommand =
        "CommunityToolkit.Mvvm.Input.IAsyncRelayCommand";
    private const string AsyncRelayCommandOfInt =
        "CommunityToolkit.Mvvm.Input.IAsyncRelayCommand`1<System.Int32>";
    private const PostGeneratorSemanticCapabilities AllCapabilities =
        PostGeneratorSemanticCapabilities.PropertyGet |
        PostGeneratorSemanticCapabilities.PropertySet |
        PostGeneratorSemanticCapabilities.CommandCanExecute |
        PostGeneratorSemanticCapabilities.CommandExecute |
        PostGeneratorSemanticCapabilities.AsyncCommandExecute |
        PostGeneratorSemanticCapabilities.AsyncCommandCancel |
        PostGeneratorSemanticCapabilities.AsyncCommandIsRunning |
        PostGeneratorSemanticCapabilities.AsyncCommandCanBeCanceled |
        PostGeneratorSemanticCapabilities.ValidationErrors |
        PostGeneratorSemanticCapabilities.SourceGeneratedSerializerMetadata;

    public static void Register(TestRunner runner)
    {
        runner.Add(
            "post-generator semantic artifact executes typed async cancellation and validation",
            ArtifactExecutesGeneratedSurface);
        runner.Add(
            "post-generator semantic contract rejects unsupported capability and hostile metadata",
            RejectsUnsupportedAndHostileRequests);
        runner.Add(
            "post-generator semantic artifact preserves non-nullable property annotations",
            PreservesNonNullablePropertyAnnotations);
        runner.Add(
            "post-generator semantic artifact preserves nested and command nullability",
            PreservesNestedAndCommandNullability);
    }

    public static void AssertDeterministic(
        string firstAssembly,
        string secondAssembly,
        string firstRoot,
        string secondRoot,
        Guid firstMvid,
        Guid secondMvid)
    {
        PostGeneratorSemanticResult first = Compile(
            firstAssembly,
            reverseRequirements: false,
            reverseReferences: false,
            CultureInfo.InvariantCulture);
        PostGeneratorSemanticResult second = Compile(
            secondAssembly,
            reverseRequirements: true,
            reverseReferences: true,
            CultureInfo.GetCultureInfo("tr-TR"));
        GeneratedBindingArtifacts firstArtifact = Assert.Single(first.Artifacts);
        GeneratedBindingArtifacts secondArtifact = Assert.Single(second.Artifacts);
        Assert.Equal(0, first.Diagnostics.Count);
        Assert.Equal(0, second.Diagnostics.Count);
        Assert.Equal(Hash(firstArtifact.Source), Hash(secondArtifact.Source));
        Assert.Equal(Hash(firstArtifact.Manifest), Hash(secondArtifact.Manifest));
        Assert.Equal(firstArtifact.Fingerprint, secondArtifact.Fingerprint);
        Assert.Equal(firstArtifact.SourceHintName, secondArtifact.SourceHintName);
        Assert.Equal(firstArtifact.ManifestFileName, secondArtifact.ManifestFileName);

        PostGeneratorSemanticResult firstMissing = CompileMissing(
            firstAssembly,
            reverseReferences: false,
            CultureInfo.InvariantCulture);
        PostGeneratorSemanticResult secondMissing = CompileMissing(
            secondAssembly,
            reverseReferences: true,
            CultureInfo.GetCultureInfo("tr-TR"));
        Assert.Equal(NormalizeDiagnostics(firstMissing.Diagnostics), NormalizeDiagnostics(secondMissing.Diagnostics));

        string[] forbidden =
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
        foreach (string value in forbidden.Where(static value => !string.IsNullOrEmpty(value)))
        {
            Assert.False(firstArtifact.Source.Contains(value, StringComparison.OrdinalIgnoreCase),
                "Post-generator source must exclude roots, MVIDs, culture, and machine state.");
            Assert.False(firstArtifact.Manifest.Contains(value, StringComparison.OrdinalIgnoreCase),
                "Post-generator manifests must exclude roots, MVIDs, culture, and machine state.");
            Assert.False(NormalizeDiagnostics(firstMissing.Diagnostics).Contains(value, StringComparison.OrdinalIgnoreCase),
                "Normalized post-generator diagnostics must exclude roots, MVIDs, culture, and machine state.");
        }
    }

    private static void ArtifactExecutesGeneratedSurface()
    {
        PostGeneratorSemanticResult result = Compile(
            ProducerAssemblyPath(),
            reverseRequirements: false,
            reverseReferences: false,
            CultureInfo.InvariantCulture);
        Assert.Equal(0, result.Diagnostics.Count);
        GeneratedBindingArtifacts artifact = Assert.Single(result.Artifacts);
        Assert.Contains("global::System.String? Get_Name", artifact.Source);
        Assert.Contains("global::System.Int32 parameter", artifact.Source);
        Assert.Contains("ExecuteAsync_LoadCommand", artifact.Source);
        Assert.Contains("Cancel_LoadCommand", artifact.Source);
        Assert.Contains("IsRunning_LoadCommand", artifact.Source);
        Assert.Contains("CanBeCanceled_LoadCommand", artifact.Source);
        Assert.Contains("GetErrors_Name", artifact.Source);
        Assert.False(artifact.Source.Contains("object?", StringComparison.Ordinal),
            "The semantic artifact must not fall back to object accessors.");
        Assert.False(artifact.Source.Contains("dynamic", StringComparison.Ordinal),
            "The semantic artifact must not emit dynamic access.");

        using JsonDocument manifest = JsonDocument.Parse(artifact.Manifest);
        Assert.Equal(PostGeneratorSemanticContract.SchemaVersion,
            manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement serializerRequirements =
            manifest.RootElement.GetProperty("serializerMetadataRequirements");
        Assert.Equal(3, serializerRequirements.GetArrayLength(),
            "Name, MultiplyCommand, and ScaleCommand require exact source-generated JsonTypeInfo<T> metadata.");
        Assert.Equal(0, RunConsumer(artifact));
    }

    private static void PreservesNonNullablePropertyAnnotations()
    {
        var request = new PostGeneratorSemanticRequest(
            PostGeneratorSemanticContract.SchemaVersion,
            ProducerAssemblyPath(),
            FixtureTypeName,
            new PostGeneratorAdapterCapabilities(
                "test.non-nullable/1",
                1,
                PostGeneratorSemanticCapabilities.PropertyGet |
                    PostGeneratorSemanticCapabilities.PropertySet,
                null),
            [ToolkitAssemblyPath()],
            [
                new PostGeneratorMemberRequirement(
                    "required-name",
                    "RequiredName",
                    PostGeneratorMemberKind.Property,
                    "System.String",
                    null,
                    false,
                    false),
            ]);
        PostGeneratorSemanticResult result = PostGeneratorSemanticCompiler.Compile(request);
        Assert.Equal(0, result.Diagnostics.Count);
        GeneratedBindingArtifacts artifact = Assert.Single(result.Artifacts);
        Assert.Contains("global::System.String Get_RequiredName", artifact.Source);
        Assert.Contains(
            "global::System.String value) => viewModel.RequiredName = value",
            artifact.Source);
        Assert.Equal(0, RunNonNullableConsumer(artifact));
    }

    private static void PreservesNestedAndCommandNullability()
    {
        var request = new PostGeneratorSemanticRequest(
            PostGeneratorSemanticContract.SchemaVersion,
            ProducerAssemblyPath(),
            FixtureTypeName,
            new PostGeneratorAdapterCapabilities(
                "test.nested-nullability/1",
                1,
                PostGeneratorSemanticCapabilities.PropertyGet |
                    PostGeneratorSemanticCapabilities.PropertySet |
                    PostGeneratorSemanticCapabilities.CommandCanExecute |
                    PostGeneratorSemanticCapabilities.CommandExecute,
                null),
            [ToolkitAssemblyPath()],
            [
                new PostGeneratorMemberRequirement(
                    "nullable-items",
                    "NullableItems",
                    PostGeneratorMemberKind.Property,
                    "System.Collections.Generic.IReadOnlyList`1<System.String>",
                    null,
                    false,
                    false),
                new PostGeneratorMemberRequirement(
                    "optional-command",
                    "OptionalCommand",
                    PostGeneratorMemberKind.Command,
                    "CommunityToolkit.Mvvm.Input.IRelayCommand",
                    null,
                    false,
                    false),
            ]);
        PostGeneratorSemanticResult result = PostGeneratorSemanticCompiler.Compile(request);
        Assert.Equal(0, result.Diagnostics.Count);
        GeneratedBindingArtifacts artifact = Assert.Single(result.Artifacts);
        Assert.Contains(
            "global::System.Collections.Generic.IReadOnlyList<global::System.String?> Get_NullableItems",
            artifact.Source);
        Assert.Contains(
            "global::CommunityToolkit.Mvvm.Input.IRelayCommand? Get_OptionalCommand",
            artifact.Source);
    }

    private static void RejectsUnsupportedAndHostileRequests()
    {
        PostGeneratorSemanticRequest valid = CreateRequest(
            ProducerAssemblyPath(),
            reverseRequirements: false,
            reverseReferences: false);

        AssertDiagnostic(valid with { SchemaVersion = 2 },
            BindingDiagnosticIds.PostGeneratorSemanticContractUnsupported);
        AssertDiagnostic(
            valid with
            {
                Adapter = valid.Adapter with
                {
                    Capabilities = valid.Adapter.Capabilities &
                        ~PostGeneratorSemanticCapabilities.AsyncCommandCancel,
                },
            },
            BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        AssertDiagnostic(
            valid with
            {
                Members =
                [
                    new PostGeneratorMemberRequirement(
                        "hostile",
                        "Name;global::System.Console.WriteLine()",
                        PostGeneratorMemberKind.Property,
                        "System.String",
                        null,
                        true,
                        true),
                ],
            },
            BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        AssertDiagnostic(
            valid with
            {
                Members =
                [
                    new PostGeneratorMemberRequirement(
                        "object-fallback",
                        "MultiplyCommand",
                        PostGeneratorMemberKind.Command,
                        RelayCommandOfInt,
                        "System.Object",
                        false,
                        false),
                ],
            },
            BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        AssertDiagnostic(
            valid with { Adapter = valid.Adapter with { Identity = "\uD800" } },
            BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);

        PostGeneratorSemanticResult escaped = PostGeneratorSemanticCompiler.Compile(
            valid with
            {
                Adapter = valid.Adapter with { Identity = "adapter\u0001identity" },
                Members =
                [
                    new PostGeneratorMemberRequirement(
                        "name\u0001binding",
                        "Name",
                        PostGeneratorMemberKind.Property,
                        "System.String",
                        null,
                        true,
                        true),
                ],
            });
        GeneratedBindingArtifacts escapedArtifact = Assert.Single(escaped.Artifacts);
        Assert.Equal(0, escaped.Diagnostics.Count);
        Assert.False(escapedArtifact.Manifest.Contains('\u0001'),
            "Hostile JSON controls must always be escaped.");
        Assert.Contains("\\u0001", escapedArtifact.Manifest);
        using JsonDocument document = JsonDocument.Parse(escapedArtifact.Manifest);
        Assert.Equal("adapter\u0001identity", document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(
            "name\u0001binding",
            document.RootElement.GetProperty("members")[0].GetProperty("bindingMemberId").GetString());
    }

    private static PostGeneratorSemanticResult Compile(
        string assemblyPath,
        bool reverseRequirements,
        bool reverseReferences,
        CultureInfo culture)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return PostGeneratorSemanticCompiler.Compile(
                CreateRequest(assemblyPath, reverseRequirements, reverseReferences));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static PostGeneratorSemanticResult CompileMissing(
        string assemblyPath,
        bool reverseReferences,
        CultureInfo culture)
    {
        PostGeneratorSemanticRequest request = CreateRequest(
            assemblyPath,
            reverseRequirements: false,
            reverseReferences);
        return CompileUnderCulture(
            request with
            {
                Members =
                [
                    new PostGeneratorMemberRequirement(
                        "missing",
                        "Missing",
                        PostGeneratorMemberKind.Property,
                        "System.String",
                        null,
                        true,
                        false),
                ],
            },
            culture);
    }

    private static PostGeneratorSemanticResult CompileUnderCulture(
        PostGeneratorSemanticRequest request,
        CultureInfo culture)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return PostGeneratorSemanticCompiler.Compile(request);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static PostGeneratorSemanticRequest CreateRequest(
        string assemblyPath,
        bool reverseRequirements,
        bool reverseReferences)
    {
        string[] references =
        [
            ToolkitAssemblyPath(),
            typeof(INotifyDataErrorInfo).Assembly.Location,
        ];
        PostGeneratorMemberRequirement[] requirements =
        [
            new(
                "name",
                "Name",
                PostGeneratorMemberKind.Property,
                "System.String",
                null,
                true,
                true),
            new(
                "multiply",
                "MultiplyCommand",
                PostGeneratorMemberKind.Command,
                RelayCommandOfInt,
                "System.Int32",
                false,
                false),
            new(
                "load",
                "LoadCommand",
                PostGeneratorMemberKind.AsyncCommand,
                AsyncRelayCommand,
                null,
                false,
                false),
            new(
                "scale",
                "ScaleCommand",
                PostGeneratorMemberKind.AsyncCommand,
                AsyncRelayCommandOfInt,
                "System.Int32",
                false,
                false),
        ];
        if (reverseReferences)
        {
            Array.Reverse(references);
        }

        if (reverseRequirements)
        {
            Array.Reverse(requirements);
        }

        return new PostGeneratorSemanticRequest(
            PostGeneratorSemanticContract.SchemaVersion,
            assemblyPath,
            FixtureTypeName,
            new PostGeneratorAdapterCapabilities(
                "test.adapter/1",
                1,
                AllCapabilities,
                "System.ComponentModel.INotifyDataErrorInfo"),
            references,
            requirements);
    }

    private static int RunConsumer(GeneratedBindingArtifacts artifact)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WebUIToolkit.MVVM.PostGeneratorConsumer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string producerAssembly = ProducerAssemblyPath();
            string toolkitAssembly = ToolkitAssemblyPath();
            string artifactTypeName = "PostGeneratorSemanticArtifact_" + artifact.Fingerprint[..16];
            File.WriteAllText(Path.Combine(root, "Semantic.g.cs"), artifact.Source, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Program.cs"), $$"""
                using System.Linq;
                using WebUIToolkit.MVVM.Build.Tests.Fixtures;
                using WebUIToolkit.MVVM.Generated;

                GeneratedMemberViewModel viewModel = new();
                {{artifactTypeName}}.Set_Name(viewModel, null);
                if (!{{artifactTypeName}}.HasErrors_Name(viewModel) ||
                    !{{artifactTypeName}}.GetErrors_Name(viewModel)!.Cast<object>().Any())
                {
                    return 10;
                }

                if (!{{artifactTypeName}}.CanExecute_MultiplyCommand(viewModel, 7))
                {
                    return 11;
                }

                {{artifactTypeName}}.Execute_MultiplyCommand(viewModel, 7);
                if (viewModel.MultipliedBy != 7)
                {
                    return 12;
                }

                global::System.Threading.Tasks.Task load =
                    {{artifactTypeName}}.ExecuteAsync_LoadCommand(viewModel);
                await viewModel.LoadStarted.Task.WaitAsync(global::System.TimeSpan.FromSeconds(5));
                if (!{{artifactTypeName}}.IsRunning_LoadCommand(viewModel) ||
                    !{{artifactTypeName}}.CanBeCanceled_LoadCommand(viewModel))
                {
                    return 13;
                }

                {{artifactTypeName}}.Cancel_LoadCommand(viewModel);
                try
                {
                    await load;
                }
                catch (global::System.OperationCanceledException)
                {
                }

                await {{artifactTypeName}}.ExecuteAsync_ScaleCommand(viewModel, 9);
                return viewModel.LoadCanceled && viewModel.ScaledBy == 9 ? 0 : 14;
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Consumer.csproj"), string.Create(
                CultureInfo.InvariantCulture,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
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
            return RunDotNet(
                root,
                "exec",
                Path.Combine(root, "bin", "Debug", "net10.0", "Consumer.dll"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int RunNonNullableConsumer(GeneratedBindingArtifacts artifact)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WebUIToolkit.MVVM.PostGeneratorNonNullableConsumer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string producerAssembly = ProducerAssemblyPath();
            string toolkitAssembly = ToolkitAssemblyPath();
            string artifactTypeName = "PostGeneratorSemanticArtifact_" + artifact.Fingerprint[..16];
            File.WriteAllText(Path.Combine(root, "Semantic.g.cs"), artifact.Source, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Program.cs"), $$"""
                using WebUIToolkit.MVVM.Build.Tests.Fixtures;
                using WebUIToolkit.MVVM.Generated;

                GeneratedMemberViewModel viewModel = new();
                {{artifactTypeName}}.Set_RequiredName(viewModel, "updated");
                return {{artifactTypeName}}.Get_RequiredName(viewModel) == "updated" ? 0 : 1;
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Consumer.csproj"), string.Create(
                CultureInfo.InvariantCulture,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
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
            return RunDotNet(
                root,
                "exec",
                Path.Combine(root, "bin", "Debug", "net10.0", "Consumer.dll"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertDiagnostic(PostGeneratorSemanticRequest request, string expectedId)
    {
        PostGeneratorSemanticResult result = PostGeneratorSemanticCompiler.Compile(request);
        Assert.Equal(0, result.Artifacts.Count);
        BindingDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
        Assert.Equal("post-generator-semantics", diagnostic.Span.LogicalPath);
    }

    private static string ProducerAssemblyPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WebUIToolkit.MVVM.Build.Tests.dll");
        Assert.True(File.Exists(path), "The CommunityToolkit test producer PE is missing.");
        return path;
    }

    private static string ToolkitAssemblyPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CommunityToolkit.Mvvm.dll");
        Assert.True(File.Exists(path), "CommunityToolkit.Mvvm 8.4.2 is missing.");
        return path;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeDiagnostics(IReadOnlyList<BindingDiagnostic> diagnostics) =>
        string.Join(
            "\n",
            diagnostics.Select(static diagnostic => string.Create(
                CultureInfo.InvariantCulture,
                $"{diagnostic.Id}|{diagnostic.Severity}|{diagnostic.Message}|{diagnostic.Span.LogicalPath}|{diagnostic.Span.Start.Offset}:{diagnostic.Span.End.Offset}")));

    private static int RunDotNet(string workingDirectory, params string[] arguments)
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

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start the semantic artifact consumer.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The semantic artifact consumer timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The semantic artifact consumer failed: {standardOutput}{standardError}");
        }

        return process.ExitCode;
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }
}
