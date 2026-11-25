using System.Reflection;
using System.Text;

/// <summary>
/// Library mode (<c>--client-library</c>, RunicApplicationFrontendClientLibrary=true, #1318): the assembly that declares
/// a ViewModel's Views generates its TypeScript once, as a module that other frontends import. Besides the usual
/// output, the generator writes <c>runic-clients.ts</c>, a barrel of the client modules, the shared client modules and
/// the named types with <c>runicClientContracts</c>; <c>runic-mocks.ts</c>, a barrel of the mock modules, kept apart so
/// that production imports never reach the mock runtime; and <c>RunicClientLibrary.g.cs</c>, an assembly marker that
/// says the assembly was built in library mode.
/// <para>
/// Application mode (<c>--client-module A=S</c>, a RunicApplicationFrontendClientModule item): the application adds
/// the Views of the mapped client library <c>A</c> to its generation. The library owns their ViewModels' TypeScript:
/// the application still writes their C# Bridges, which its content switches construct, but discards their client
/// modules and imports the library's from the module specifier <c>S</c>. <c>runic-client-modules.ts</c> compares the
/// contract of each imported client with the one this application compiles against, as a TypeScript type check.
/// </para>
/// </summary>
/// <remarks>
/// The hyphenated module names cannot collide with a generated module, which is named after a ViewModel or shared
/// client and so is an identifier; <c>index.ts</c> would be an <c>IndexViewModel</c>'s.
/// </remarks>
internal static class ClientModules
{
    /// <summary>The barrel of the client modules, the shared client modules and the named types.</summary>
    internal const string ClientsModule = "runic-clients";

    /// <summary>The barrel of the mock modules.</summary>
    internal const string MocksModule = "runic-mocks";

    /// <summary>The C# file that marks the assembly as built in library mode.</summary>
    internal const string LibraryMarkerFile = "RunicClientLibrary.g.cs";

    /// <summary>The <see cref="System.Reflection.AssemblyMetadataAttribute"/> key of the library-mode marker.</summary>
    internal const string LibraryMetadataKey = "Runic.Application.Views.ClientLibrary";

    // "{ViewModel}:{fingerprint}" of each generated client, as the client module embeds it.
    private static readonly Dictionary<Type, string> Contracts = [];

    /// <summary>Records the contract that the client module of <paramref name="model"/> embeds.</summary>
    internal static void RecordContract(Type model, string contract) => Contracts[model] = contract;

    /// <summary>The TypeScript files library mode adds to the output list, with their owners.</summary>
    internal static (string Path, string Owner)[] LibraryTypeScriptOutputs() =>
    [
        ($"{ClientsModule}.ts", "the client library barrel"),
        ($"{MocksModule}.ts", "the client library mock barrel"),
    ];

    /// <summary>
    /// Writes the barrels into <paramref name="typescriptDirectory"/> and the marker into
    /// <paramref name="csharpDirectory"/>. Runs after the modules are written, so that <paramref name="namedTypes"/>
    /// says whether <c>types.ts</c> exists.
    /// </summary>
    internal static void WriteLibrary(string csharpDirectory, string typescriptDirectory, IReadOnlyList<(Type Model, string Module)> clients,
        IEnumerable<string> sharedModules, bool namedTypes)
    {
        var ordered = clients.OrderBy(client => client.Module, StringComparer.Ordinal).ToArray();
        var barrel = new StringBuilder();
        barrel.AppendLine(GeneratedOutput.Header);
        barrel.AppendLine("// The generated clients of this library (RunicApplicationFrontendClientLibrary), for applications and");
        barrel.AppendLine("// components that import them. The mocks are in runic-mocks.ts.");
        foreach (var module in ordered.Select(client => client.Module).Concat(sharedModules.Order(StringComparer.Ordinal)))
            barrel.AppendLine($"export * from \"./{module}.js\";");
        if (namedTypes) barrel.AppendLine($"export * from \"./{TypeScriptNamedTypes.ModuleName}.js\";");
        barrel.AppendLine();
        barrel.AppendLine("/**");
        barrel.AppendLine(" * The contract of each generated client: its ViewModel and the fingerprint of the ViewModel's Bridge contract,");
        barrel.AppendLine(" * computed from the assembly this module was generated from.");
        barrel.AppendLine(" */");
        barrel.AppendLine("export const runicClientContracts = {");
        foreach (var (model, module) in ordered)
            barrel.AppendLine($"  {module}: \"{Contracts[model]}\",");
        barrel.AppendLine("} as const;");
        GeneratedFile.WriteIfChanged(Path.Combine(typescriptDirectory, $"{ClientsModule}.ts"), barrel.ToString());

        var mocks = new StringBuilder();
        mocks.AppendLine(GeneratedOutput.Header);
        mocks.AppendLine("// The generated mocks of this library's clients (RunicApplicationFrontendClientLibrary), for tests and");
        mocks.AppendLine("// previews. Production code imports runic-clients.ts.");
        foreach (var (_, module) in ordered)
            mocks.AppendLine($"export * from \"./{module}.mock.js\";");
        GeneratedFile.WriteIfChanged(Path.Combine(typescriptDirectory, $"{MocksModule}.ts"), mocks.ToString());

        GeneratedFile.WriteIfChanged(Path.Combine(csharpDirectory, LibraryMarkerFile), $$"""
            // <auto-generated />
            // RunicApplicationFrontendClientLibrary=true: this assembly's Views own their generated TypeScript, which
            // applications import instead of generating copies.
            [assembly: global::System.Reflection.AssemblyMetadata("{{LibraryMetadataKey}}", "1")]

            """);
    }

    // Application mode.

    /// <summary>The module that checks the contracts of the clients this application imports from client libraries.</summary>
    internal const string CheckModule = "runic-client-modules";

    /// <summary>The import that brings <see cref="CheckModule"/> into the program of each module that imports a library.</summary>
    /// <remarks>
    /// Type-only and empty, so it is erased from the bundle; it reaches a program whose only root is <c>src/main.ts</c>,
    /// as Angular's.
    /// </remarks>
    internal const string CheckImport = $"import type {{}} from \"./{CheckModule}.js\";";

    // The owning client library of each ViewModel whose Views a mapped library declares, and of each client or shared
    // client module that the application imports from one.
    private static readonly Dictionary<Type, ClientModuleMapping> ModelOwners = [];
    private static readonly Dictionary<string, ClientModuleMapping> ModuleOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// True while the generator emits the TypeScript of a ViewModel or shared client that a client library owns. The
    /// module is checked like any other, but not written (<c>TypeScriptModules.Add</c>), and the named types it uses are
    /// declared only if one of the application's own modules uses them too (<see cref="TypeScriptNamedTypes"/>).
    /// </summary>
    internal static bool Discarding { get; set; }

    /// <summary>The client library that owns <paramref name="model"/>'s TypeScript, or null when the application does.</summary>
    internal static ClientModuleMapping? OwnerOf(Type model) => ModelOwners.GetValueOrDefault(model);

    /// <summary>True when a client library owns the generated module <paramref name="module"/>.</summary>
    internal static bool IsOwned(string module) => ModuleOwners.ContainsKey(module);

    /// <summary>
    /// The specifier that imports the generated module <paramref name="module"/>: the owning library's, or the module
    /// beside the importing one, <c>./{module}{suffix}</c>.
    /// </summary>
    internal static string Specifier(string module, string suffix) =>
        ModuleOwners.TryGetValue(module, out var owner) ? owner.Specifier : $"./{module}{suffix}";

    /// <summary>True when a module text imports from a client library.</summary>
    internal static bool ImportsLibrary(string text) =>
        ModuleOwners.Values.Any(owner => text.Contains($" from \"{owner.Specifier}\";", StringComparison.Ordinal));

    /// <summary>Parses the value of <c>--client-module</c>, <c>&lt;assembly&gt;=&lt;module specifier&gt;</c>.</summary>
    internal static ClientModuleMapping ParseMapping(string value)
    {
        var separator = value.IndexOf('=', StringComparison.Ordinal);
        var assembly = separator < 0 ? "" : value[..separator].Trim();
        var specifier = separator < 0 ? "" : value[(separator + 1)..].Trim();
        if (assembly.Length == 0 || specifier.Length == 0)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleItem,
                $"--client-module '{value}' must be <assembly>=<module specifier>, such as SharedUi=@acme/shared-ui/generated: a RunicApplicationFrontendClientModule item needs Include and Specifier.");
        if (IsRelative(specifier))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleItem,
                $"the RunicApplicationFrontendClientModule {assembly} has the relative Specifier '{specifier}', which every generated module would resolve from its own folder. Use a package specifier, such as @acme/shared-ui/generated, or a tsconfig paths alias.");
        return new ClientModuleMapping(assembly, specifier);
    }

    /// <summary>True for a relative module specifier, which RunicApplicationFrontendClientModule rejects.</summary>
    internal static bool IsRelative(string specifier) =>
        specifier is "." or ".." || specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)
        || specifier.StartsWith(".\\", StringComparison.Ordinal) || specifier.StartsWith("..\\", StringComparison.Ordinal);

    /// <summary>
    /// Adds the Views of each mapped client library to <paramref name="viewTypes"/>, which holds the application's own,
    /// and records the library as the owner of their ViewModels and client modules. Reports a library that is not
    /// referenced or not built in library mode, a library without Views, and a ViewModel whose Views are in two
    /// assemblies.
    /// </summary>
    internal static void Discover(Assembly application, string applicationPath, IReadOnlyList<ClientModuleMapping> mappings,
        Dictionary<Type, List<Type>> viewTypes, Func<Type, Type?> viewModelFor, Func<Type, string> moduleOf)
    {
        var applicationName = application.GetName().Name;
        var directory = Path.GetDirectoryName(Path.GetFullPath(applicationPath))!;
        var declaredBy = viewTypes.ToDictionary(entry => entry.Key, entry => (Owner: (ClientModuleMapping?)null, View: entry.Value[0]));
        var split = new HashSet<Type>();
        foreach (var mapping in mappings)
        {
            try
            {
                var library = Load(mapping, applicationName, directory);
                var views = new List<(Type Model, Type View)>();
                foreach (var type in BridgeLoading.InspectableTypes(library))
                {
                    if (type.IsAbstract || !type.IsClass || !type.IsPublic || type.IsNested) continue;
                    if (viewModelFor(type) is { } model) views.Add((model, type));
                }
                if (views.Count == 0)
                {
                    Diagnostics.Warn(BridgeDiagnosticCodes.ClientModuleEmpty,
                        $"{mapping.Assembly} is mapped to {mapping.Specifier} by RunicApplicationFrontendClientModule, but declares no Runic Window or View, so the mapping imports nothing. Declare the Views of its ViewModels in {mapping.Assembly}, or remove the item.",
                        null);
                    continue;
                }
                foreach (var (model, view) in views.OrderBy(entry => entry.View.FullName, StringComparer.Ordinal))
                {
                    if (declaredBy.TryGetValue(model, out var other) && other.Owner != mapping)
                    {
                        if (!split.Add(model)) continue;
                        var by = other.Owner?.Assembly ?? applicationName;
                        Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleViews,
                            other.Owner is null
                                ? $"{by} declares {other.View.Name} for {model.FullName}, whose Views and client {mapping.Assembly} owns (RunicApplicationFrontendClientModule {mapping.Assembly}). Delete {other.View.Name}, or remove the mapping."
                                : $"{by} and {mapping.Assembly} both declare Views for {model.FullName} ({other.View.FullName} and {view.FullName}), and both are mapped by RunicApplicationFrontendClientModule. All Views of a ViewModel must be in one assembly.",
                            other.Owner is null ? other.View : view));
                        continue;
                    }
                    declaredBy[model] = (mapping, view);
                    if (!viewTypes.TryGetValue(model, out var variants)) viewTypes.Add(model, variants = []);
                    variants.Add(view);
                    ModelOwners[model] = mapping;
                    ModuleOwners[moduleOf(model)] = mapping;
                }
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error);
            }
        }
    }

    // The mapped assembly, as the application resolves it: already loaded through the application's types, or beside
    // the inspected assembly, where the bootstrap build copies its references.
    private static Assembly Load(ClientModuleMapping mapping, string? applicationName, string directory)
    {
        if (string.Equals(mapping.Assembly, applicationName, StringComparison.OrdinalIgnoreCase))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleLibrary,
                $"{mapping.Assembly} is mapped to {mapping.Specifier} by RunicApplicationFrontendClientModule, but it is this application. Map a referenced client library's assembly.");
        var path = Path.Combine(directory, mapping.Assembly + ".dll");
        var library = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(loaded =>
                string.Equals(loaded.GetName().Name, mapping.Assembly, StringComparison.OrdinalIgnoreCase)
                && !loaded.IsDynamic && string.Equals(Path.GetDirectoryName(loaded.Location), directory, StringComparison.Ordinal))
            ?? (File.Exists(path) ? Assembly.LoadFrom(path) : null)
            ?? throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleLibrary,
                $"{mapping.Assembly} is mapped to {mapping.Specifier} by RunicApplicationFrontendClientModule, but {applicationName} does not reference it: {path} does not exist. Add a ProjectReference or PackageReference to {mapping.Assembly}, or correct the item's Include, which is the assembly's simple name.");
        var marked = library.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
            && attribute.ConstructorArguments is [{ Value: LibraryMetadataKey }, { Value: "1" }]);
        if (!marked)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleLibrary,
                $"{mapping.Assembly} is mapped to {mapping.Specifier} by RunicApplicationFrontendClientModule, but was not built with RunicApplicationFrontendClientLibrary=true, so it does not generate the module the application imports. Set the property in {mapping.Assembly}'s project, or remove the item.");
        return library;
    }

    /// <summary>
    /// Checks that a ViewModel that a client library owns presents only content of the same library: the library's
    /// client types its pages, and cannot name the application's or another library's.
    /// </summary>
    internal static void CheckContent(Type model, IEnumerable<KeyValuePair<PropertyInfo, (Type Model, string Name)[]>> content)
    {
        if (OwnerOf(model) is not { } owner) return;
        foreach (var (property, pages) in content)
            foreach (var (page, _) in pages)
                if (OwnerOf(page) != owner)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleOwner,
                        $"{model.Name}.{property.Name} presents {page.FullName}, whose client {Describe(OwnerOf(page))} owns, but {model.FullName}'s client is {owner.Assembly}'s (RunicApplicationFrontendClientModule {owner.Assembly}), which cannot type a page it does not own. "
                        + $"Declare the Views of {page.Name} in {owner.Assembly}, or present it from a ViewModel of {Describe(OwnerOf(page))}.",
                        property);
    }

    /// <summary>
    /// Assigns each shared client to the owner of its implementers, so that an application imports a library's shared
    /// client module from the library. Implementers with different owners are reported.
    /// </summary>
    internal static void AssignSharedClients(IEnumerable<SharedClientDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            var owners = declaration.Implementers.Select(implementer => OwnerOf(implementer.Model)).Distinct().ToArray();
            if (owners.Length > 1)
                Diagnostics.Report(new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleOwner,
                    $"{declaration.Interface.Name}: the shared client {declaration.Name} has implementers whose clients have different owners: "
                    + string.Join(", ", declaration.Implementers.Select(implementer => $"{implementer.Model.Name} ({Describe(OwnerOf(implementer.Model))})"))
                    + ". A shared client's module is generated once, so all its implementers' Views must be in one assembly.",
                    declaration.Interface));
            else if (owners[0] is { } owner) ModuleOwners[declaration.Module] = owner;
        }
    }

    private static string Describe(ClientModuleMapping? owner) => owner is null ? "the application" : owner.Assembly;

    /// <summary>The TypeScript files application mode adds to the output list, with their owners.</summary>
    internal static (string Path, string Owner)[] ApplicationTypeScriptOutputs() =>
        ModelOwners.Count == 0 ? [] : [($"{CheckModule}.ts", "the client module check")];

    /// <summary>
    /// Writes <c>runic-client-modules.ts</c>: for each ViewModel a library owns, a type that is an error unless the
    /// contract that the library's module exports is the one this application's Bridge carries. A stale library package
    /// is TS2344, naming both contracts; a library module without the client is TS2339.
    /// </summary>
    internal static void WriteCheck(string typescriptDirectory, IReadOnlyList<(Type Model, string Module)> clients)
    {
        var owned = clients.Where(client => OwnerOf(client.Model) is not null)
            .OrderBy(client => client.Module, StringComparer.Ordinal).ToArray();
        if (owned.Length == 0) return;
        var aliases = new Dictionary<ClientModuleMapping, string>();
        var taken = new HashSet<string>(["RunicSameContract", "RunicClientModuleCheck"], StringComparer.Ordinal);
        foreach (var owner in owned.Select(client => OwnerOf(client.Model)!).Distinct().OrderBy(owner => owner.Assembly, StringComparer.Ordinal))
        {
            var alias = Identifier(owner.Assembly);
            for (var suffix = 2; !taken.Add(alias); suffix++) alias = Identifier(owner.Assembly) + suffix;
            aliases.Add(owner, alias);
        }
        var check = new StringBuilder();
        check.AppendLine(GeneratedOutput.Header);
        check.AppendLine("// The clients that RunicApplicationFrontendClientModule imports from client libraries. A type error here means that a");
        check.AppendLine("// library's module was generated from another build of the assembly than this application compiles against:");
        check.AppendLine("// rebuild the library and its package.");
        foreach (var (owner, alias) in aliases)
            check.AppendLine($"import type {{ runicClientContracts as {alias} }} from \"{owner.Specifier}\";");
        check.AppendLine();
        check.AppendLine("type RunicSameContract<Package extends Application, Application extends string> = Package;");
        check.AppendLine();
        check.AppendLine("/** Each imported client's contract in its library's module, which must be the one this application's Bridge carries. */");
        check.AppendLine("export type RunicClientModuleCheck = [");
        foreach (var (model, module) in owned)
            check.AppendLine($"  RunicSameContract<typeof {aliases[OwnerOf(model)!]}.{module}, \"{Contracts[model]}\">,");
        check.AppendLine("];");
        GeneratedFile.WriteIfChanged(Path.Combine(typescriptDirectory, $"{CheckModule}.ts"), check.ToString());
    }

    private static string Identifier(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var character in name) result.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '$' ? character : '_');
        if (result.Length == 0 || char.IsAsciiDigit(result[0])) result.Insert(0, '_');
        return result.ToString();
    }
}

/// <summary>A RunicApplicationFrontendClientModule item: a client library's assembly and its <c>runic-clients.ts</c> specifier.</summary>
internal sealed record ClientModuleMapping(string Assembly, string Specifier);
