using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>The Runic Views host a project references.</summary>
internal enum RunicViewsHost
{
    Unknown,
    CsWebUi,
    Desktop,
    /// <summary>Runic.Application.Views.Wpf, a WPF child WebView2 presentation; the primary host only when it is the only one.</summary>
    Wpf,
}

internal sealed record DoctorProjectConfiguration(
    string ProjectPath,
    string ProjectDirectory,
    string TargetFramework,
    bool IsViewsWindowProject,
    string FrontendPackageDirectory,
    string ProjectAssetsFile,
    string RuntimeIdentifier,
    string ViteDevServerEntry,
    string ViteConfigurationPath,
    bool ViteDevServerEnabled,
    RunicViewsHost Host = RunicViewsHost.Unknown)
{
    internal const string CsWebUiPackage = "Runic.Application.Views.CsWebUi";
    internal const string DesktopPackage = "Runic.Application.Views.Desktop";
    internal const string DesktopGtk4Package = "Runic.Desktop.Gtk4";
    internal const string WpfPackage = "Runic.Application.Views.Wpf";

    private readonly IReadOnlyList<RunicViewsHost>? _hosts;

    /// <summary>
    /// Every Views host adapter the project references, in the order CS-WebUI, Runic Desktop, WPF. Doctor checks each
    /// one. Defaults to <see cref="Host"/> alone.
    /// </summary>
    internal IReadOnlyList<RunicViewsHost> Hosts
    {
        get => _hosts ?? (Host == RunicViewsHost.Unknown ? [] : [Host]);
        init => _hosts = value;
    }

    /// <summary>
    /// Vite's root, against which the entry module resolves: <c>RunicApplicationFrontendViteRoot</c>, by default the
    /// frontend package directory (empty here means that default).
    /// </summary>
    internal string ViteRoot { get; init; } = string.Empty;

    /// <summary>The semicolon-separated RuntimeIdentifiers property.</summary>
    internal string RuntimeIdentifiers { get; init; } = string.Empty;

    /// <summary>PublishAot, evaluated for the doctor target RID when one is given.</summary>
    internal bool PublishAot { get; init; }

    /// <summary>SelfContained, evaluated for the doctor target RID when one is given.</summary>
    internal bool SelfContained { get; init; }

    /// <summary>
    /// RunicDesktopServer as Runic.Desktop's targets resolved it: Kestrel (Runic.Desktop.Kestrel, which needs the ASP.NET
    /// Core runtime), Native (the experimental Runic.Desktop.NativeServer), None, or empty without a server package.
    /// </summary>
    internal string DesktopServer { get; init; } = string.Empty;

    /// <summary>The RollForward property, which the build writes to runtimeconfig.json; empty for the default (Minor).</summary>
    internal string RollForward { get; init; } = string.Empty;

    /// <summary>False when StripSymbols is explicitly false; Native AOT then needs no objcopy.</summary>
    internal bool StripSymbols { get; init; } = true;

    /// <summary>
    /// _RunicApplicationHostAdapter, which every adapter's build props set: true when a restored project gets a host
    /// adapter, also through another package or project.
    /// </summary>
    internal bool ReferencesHostAdapter { get; init; }

    /// <summary>True when the project references the optional GTK 4 Desktop provider.</summary>
    internal bool UsesGtk4 { get; init; }

    /// <summary>The evaluated ApplicationId, the application's reverse-DNS identity; empty when the project sets none.</summary>
    internal string ApplicationId { get; init; } = string.Empty;

    /// <summary>The package and project references the project declares directly, by package identity.</summary>
    internal IReadOnlyList<string> DirectReferences { get; init; } = [];

    /// <summary>
    /// The frontend's JavaScript workspace root, found as the Views targets find it (<see cref="JavaScriptWorkspace"/>);
    /// empty means the frontend directory, which is then its own root.
    /// </summary>
    internal string JavaScriptWorkspaceRoot { get; init; } = string.Empty;

    /// <summary>The workspace root, or the frontend directory when the frontend is its own root.</summary>
    internal string EffectiveWorkspaceRoot => JavaScriptWorkspaceRoot.Length == 0 ? FrontendPackageDirectory : JavaScriptWorkspaceRoot;

    /// <summary><c>RunicApplicationFrontendWorkspaceRoot</c> as the project sets it: a folder, <c>none</c>, or empty.</summary>
    internal string WorkspaceRootSetting { get; init; } = string.Empty;

    /// <summary><c>RunicApplicationFrontendPackageManager</c> when the project sets it, else empty.</summary>
    internal string PackageManagerName { get; init; } = string.Empty;

    /// <summary>
    /// The package manager that installs and builds the frontend, as the build chooses it: the property, then the
    /// workspace root's <c>packageManager</c> and lock file, then the frontend's own, then npm.
    /// </summary>
    internal (JavaScriptPackageManager Manager, string Source) ResolvePackageManager() =>
        JavaScriptPackageManager.ResolveWithSource(EffectiveWorkspaceRoot, FrontendPackageDirectory, PackageManagerName);

    /// <summary>TargetPlatformIdentifier, such as <c>windows</c> for net10.0-windows; empty for a portable framework.</summary>
    internal string TargetPlatformIdentifier { get; init; } = string.Empty;

    /// <summary>EnableWindowsTargeting, which lets the .NET SDK build a Windows target framework on Linux and macOS.</summary>
    internal bool EnableWindowsTargeting { get; init; }

    /// <summary>
    /// The frontend packages that the page depends on and the workspace facts of the frontend graph, read without a
    /// build; null when the project has no frontend package.json.
    /// </summary>
    internal DoctorFrontendGraph? FrontendGraph { get; init; }

    /// <summary>The project's <c>RunicApplicationFrontendClientModule</c> items: the client libraries whose generated clients the page imports.</summary>
    internal IReadOnlyList<DoctorClientModule> ClientModules { get; init; } = [];

    /// <summary>
    /// The global properties 'dotnet publish -r rid' sets, plus PublishAot and
    /// SelfContained for the --aot, --no-aot, --self-contained and --no-self-contained switches.
    /// </summary>
    internal static IReadOnlyList<string> PublishProperties(DoctorTargetRid target, bool? aot, bool? selfContained)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<string> properties = [$"RuntimeIdentifier={target.Value}", "_IsPublishing=true"];
        if (aot is { } publishAot) properties.Add(publishAot ? "PublishAot=true" : "PublishAot=false");
        // dotnet publish --self-contained true|false also sets _CommandLineDefinedSelfContained.
        if (selfContained is { } publishSelfContained)
            properties.AddRange([publishSelfContained ? "SelfContained=true" : "SelfContained=false", "_CommandLineDefinedSelfContained=true"]);
        return properties;
    }

    internal static async Task<DoctorProjectConfiguration> EvaluateAsync(
        string dotnetHost,
        string project,
        string configuration,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? publishProperties = null)
    {
        // A multi-targeted project stops in DevProjectConfiguration (RAPPDEV1005), so this evaluation has one
        // TargetFramework, and references conditioned on it are present.
        DevProjectConfiguration development = await DevProjectConfiguration.EvaluateAsync(
            dotnetHost, project, configuration, cancellationToken).ConfigureAwait(false);
        string properties = await ReadProjectPropertiesAsync(
            dotnetHost, development.ProjectPath, development.ProjectDirectory,
            configuration, publishProperties ?? [], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(properties);
        JsonElement values = document.RootElement.GetProperty("Properties");
        string Value(string name) => values.TryGetProperty(name, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
        IReadOnlyList<string> references = ReferenceIdentities(document.RootElement);
        DoctorFrontendGraph? graph = await DoctorFrontendGraph.ReadAsync(dotnetHost, development, configuration,
            NormalizeScript(Value("_RunicViewsFrontendGraphScript"), development.ProjectDirectory), Value("RunicApplicationFrontendCodegenRuntime").Trim(),
            cancellationToken).ConfigureAwait(false);
        return new(
            development.ProjectPath,
            development.ProjectDirectory,
            Value("TargetFramework"),
            development.IsViewsWindowProject,
            development.FrontendPackageDirectory,
            development.ProjectAssetsFile,
            string.IsNullOrWhiteSpace(Value("RuntimeIdentifier"))
                ? Value("NETCoreSdkRuntimeIdentifier")
                : Value("RuntimeIdentifier"),
            development.ViteDevServerEntry,
            development.ViteConfigurationPath,
            development.ViteDevServerEnabled,
            DetectHost(references))
        {
            Hosts = DetectHosts(references),
            ViteRoot = development.EffectiveViteRoot,
            ReferencesHostAdapter = IsTrue(Value("_RunicApplicationHostAdapter")),
            RuntimeIdentifiers = Value("RuntimeIdentifiers"),
            PublishAot = IsTrue(Value("PublishAot")),
            SelfContained = IsTrue(Value("SelfContained")),
            DesktopServer = Value("RunicDesktopServer"),
            RollForward = Value("RollForward"),
            StripSymbols = !Value("StripSymbols").Equals("false", StringComparison.OrdinalIgnoreCase),
            UsesGtk4 = references.Contains(DesktopGtk4Package, StringComparer.OrdinalIgnoreCase),
            DirectReferences = references,
            ApplicationId = Value("ApplicationId"),
            JavaScriptWorkspaceRoot = development.JavaScriptWorkspaceRoot,
            WorkspaceRootSetting = development.WorkspaceRootSetting,
            PackageManagerName = development.PackageManagerName,
            TargetPlatformIdentifier = Value("TargetPlatformIdentifier"),
            EnableWindowsTargeting = IsTrue(Value("EnableWindowsTargeting")),
            FrontendGraph = graph,
            ClientModules = ReadClientModules(document.RootElement),
        };
    }

    /// <summary>The evaluation's <c>RunicApplicationFrontendClientModule</c> items, with their Specifier metadata.</summary>
    internal static IReadOnlyList<DoctorClientModule> ReadClientModules(JsonElement evaluation)
    {
        if (!evaluation.TryGetProperty("Items", out JsonElement items) || items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty("RunicApplicationFrontendClientModule", out JsonElement modules) || modules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. modules.EnumerateArray()
            .Select(static module => new DoctorClientModule(
                module.TryGetProperty("Identity", out JsonElement identity) ? identity.GetString() ?? string.Empty : string.Empty,
                module.TryGetProperty("Specifier", out JsonElement specifier) ? specifier.GetString() ?? string.Empty : string.Empty))
            .Where(static module => module.Assembly.Length != 0)];
    }

    private static string NormalizeScript(string value, string directory) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value.Trim(), directory);

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the primary Views host from the evaluated package and project references, so an unrestored project
    /// still gets host-specific checks, and the --rid checks follow it. CS-WebUI wins when a project references
    /// both hosts because it may open a browser; WPF is primary only when it is the only host.
    /// </summary>
    internal static RunicViewsHost DetectHost(JsonElement evaluation) => DetectHost(ReferenceIdentities(evaluation));

    internal static RunicViewsHost DetectHost(IReadOnlyCollection<string> references) =>
        DetectHosts(references) is [var primary, ..] ? primary : RunicViewsHost.Unknown;

    /// <summary>Every Views host the evaluation references, in the order of <see cref="Hosts"/>.</summary>
    internal static IReadOnlyList<RunicViewsHost> DetectHosts(JsonElement evaluation) => DetectHosts(ReferenceIdentities(evaluation));

    internal static IReadOnlyList<RunicViewsHost> DetectHosts(IReadOnlyCollection<string> references)
    {
        bool Has(string name) => references.Contains(name, StringComparer.OrdinalIgnoreCase);
        List<RunicViewsHost> hosts = [];
        if (Has(CsWebUiPackage)) hosts.Add(RunicViewsHost.CsWebUi);
        if (Has(DesktopPackage)) hosts.Add(RunicViewsHost.Desktop);
        if (Has(WpfPackage)) hosts.Add(RunicViewsHost.Wpf);
        return hosts;
    }

    /// <summary>Whether the evaluation has a package or project reference to <paramref name="name"/>.</summary>
    internal static bool References(JsonElement evaluation, string name) =>
        ReferenceIdentities(evaluation).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The package identities of the evaluation's package references and referenced project names.</summary>
    internal static IReadOnlyList<string> ReferenceIdentities(JsonElement evaluation)
    {
        List<string> identities = [];
        if (!evaluation.TryGetProperty("Items", out JsonElement items) || items.ValueKind != JsonValueKind.Object) return identities;
        foreach (string itemType in new[] { "PackageReference", "ProjectReference" })
        {
            if (!items.TryGetProperty(itemType, out JsonElement references) || references.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement reference in references.EnumerateArray())
            {
                if (!reference.TryGetProperty("Identity", out JsonElement identityNode)) continue;
                string identity = identityNode.GetString() ?? string.Empty;
                if (itemType == "ProjectReference")
                {
                    identity = Path.GetFileNameWithoutExtension(identity.Replace('\\', '/').Split('/')[^1]);
                }
                if (identity.Length != 0) identities.Add(identity);
            }
        }
        return identities;
    }

    private static async Task<string> ReadProjectPropertiesAsync(
        string dotnetHost,
        string project,
        string projectDirectory,
        string configuration,
        IReadOnlyList<string> publishProperties,
        CancellationToken cancellationToken)
    {
        // For --rid, the global properties of 'dotnet publish -r' make RID- and
        // publish-conditioned PublishAot and SelfContained settings apply.
        List<string> arguments =
        [
            "msbuild", project, "-nologo", $"-property:Configuration={configuration}",
            "-getProperty:TargetFramework,TargetFrameworks,RuntimeIdentifier,RuntimeIdentifiers,NETCoreSdkRuntimeIdentifier,PublishAot,SelfContained,StripSymbols,RunicDesktopServer,RollForward,_RunicApplicationHostAdapter,ApplicationId,TargetPlatformIdentifier,EnableWindowsTargeting,_RunicViewsFrontendGraphScript,RunicApplicationFrontendCodegenRuntime",
            "-getItem:PackageReference,ProjectReference,RunicApplicationFrontendClientModule",
        ];
        arguments.AddRange(publishProperties.Select(static property => $"-property:{property}"));
        CommandResult result = await CommandRunner.RunAsync(
            dotnetHost,
            projectDirectory,
            arguments,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1003",
                "MSBuild could not evaluate the project.",
                $"Project: {project}\n{result.CombinedOutput.Trim()}\n");
        }
        return result.StandardOutput;
    }
}
