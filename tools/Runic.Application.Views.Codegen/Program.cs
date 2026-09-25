using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using Runic.Application.Views;
using Runic.Application.Views.Codegen.Toolkit;
using Runic.Application.Views.Codegen.ReactiveUI;

try
{
    if (args.Length == 3 && args[0] == "--composition-stub")
    {
        GenerateCompositionStub(args[2], args[1]);
    }
    else if (args.Length >= 1 && args[0] == "--generate")
    {
        var aot = false;
        var registerGlobally = true;
        string? compositionType = null;
        var values = new List<string> { args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--aot": aot = true; break;
                case "--no-registry": registerGlobally = false; break;
                case "--di-composition" when i + 1 < args.Length:
                    compositionType = args[++i];
                    break;
                default: values.Add(args[i]); break;
            }
        }
        var positional = values.ToArray();
        if (positional.Length != 4)
            throw new ArgumentException("Usage: BridgeCodegen --generate <model.dll> <C# output dir> <TypeScript output dir> [--aot] [--no-registry] [--di-composition <namespace.type>]");
        if (compositionType is not null && registerGlobally)
            throw new ArgumentException("--di-composition requires --no-registry.");

        var assembly = Assembly.LoadFrom(Path.GetFullPath(positional[1]));
        var viewTypes = new Dictionary<Type, List<Type>>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract) continue;
            var model = ViewModelFor(type);
            if (model is null) continue;
            if (!type.IsClass || !type.IsPublic || type.IsNested)
                throw new InvalidOperationException($"{type.FullName}: a Runic Window or View must be a public, top-level concrete class.");
            if (!viewTypes.TryGetValue(model, out var variants))
                viewTypes.Add(model, variants = []);
            var contract = ContractFor(type);
            if (variants.Any(existing => string.Equals(ContractFor(existing), contract, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{model.FullName}: duplicate Runic View contract '{contract ?? "default"}'.");
            variants.Add(type);
        }
        var models = viewTypes.Keys.Select(type => (Model: type, Name: PublicName(type))).ToArray();
        if (models.Length == 0)
            throw new InvalidOperationException($"{assembly.GetName().Name}: no Runic Window/View classes found.");
        if (models.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != models.Length)
            throw new InvalidOperationException("Bridge ViewModel names must be unique within an application.");
        var presentationKinds = models.SelectMany(entry => viewTypes.TryGetValue(entry.Model, out var variants)
            ? variants.Select(view => PageKind(entry.Name, ContractFor(view)))
            : [PageKind(entry.Name, null)]).ToArray();
        if (presentationKinds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != presentationKinds.Length)
            throw new InvalidOperationException("Bridge presentation kinds must be unique across ViewModels and View contracts.");
        if (aot)
        {
            var unsupported = models.FirstOrDefault(entry => ToolkitCommandInspector.HasUnsupportedAotValidation(entry.Model));
            if (unsupported.Model is not null)
                throw new AotValidationException($"{unsupported.Model.FullName} inherits CommunityToolkit.Mvvm.ObservableValidator. Its validation failed in this prototype's Native AOT probe; use a framework-dependent publish until an AOT-safe validator is verified.");
        }

        Directory.CreateDirectory(positional[2]);
        Directory.CreateDirectory(positional[3]);
        var expectedCsharp = models.Select(entry => Path.GetFullPath(Path.Combine(positional[2], $"{entry.Name}Bridge.g.cs")))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var view in viewTypes.Values.SelectMany(variants => variants))
            expectedCsharp.Add(Path.GetFullPath(Path.Combine(positional[2], $"{view.Name}Bridge.g.cs")));
        var expectedTypescript = models.Select(entry => Path.GetFullPath(Path.Combine(positional[3], $"{LowerFirst(entry.Name)}.ts")))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(positional[2], "*Bridge.g.cs"))
            if (!expectedCsharp.Contains(Path.GetFullPath(path))) File.Delete(path);
        foreach (var path in Directory.GetFiles(positional[3], "*.ts"))
            if (!expectedTypescript.Contains(Path.GetFullPath(path))) File.Delete(path);
        foreach (var entry in models)
        {
            GenerateOne(entry.Model, Path.Combine(positional[2], $"{entry.Name}Bridge.g.cs"),
                Path.Combine(positional[3], $"{LowerFirst(entry.Name)}.ts"), entry.Name, registerGlobally, models, viewTypes);
            if (viewTypes.TryGetValue(entry.Model, out var views))
                foreach (var view in views)
                    GenerateViewPartial(Path.Combine(positional[2], $"{view.Name}Bridge.g.cs"), view, entry.Model, entry.Name);
        }
        var compositionPath = Path.Combine(positional[2], "RunicBridgeComposition.g.cs");
        if (compositionType is null)
        {
            if (File.Exists(compositionPath)) File.Delete(compositionPath);
        }
        else GenerateCompositionRegistration(compositionPath, compositionType, models);
    }
    else
    {
        if (args.Length is not (4 or 5))
            throw new ArgumentException("Usage: BridgeCodegen <model.dll> <ViewModel type> <output.cs> <output.ts> [public name]");
        var model = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType(args[1], throwOnError: true)!;
        GenerateOne(model, args[2], args[3], args.Length == 5 ? args[4] : PublicName(model));
    }
}
catch (AotValidationException error)
{
    Console.Error.WriteLine($"error RUNICBRIDGE002: {error.Message}");
    Environment.ExitCode = 1;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or FileNotFoundException or TypeLoadException)
{
    Console.Error.WriteLine($"error RUNICBRIDGE001: {error.Message}");
    Environment.ExitCode = 1;
}

static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal)
    ? model.Name[..^"ViewModel".Length] : model.Name;

static Type? ViewModelFor(Type view)
{
    for (var current = view.BaseType; current is not null; current = current.BaseType)
        if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Runic.Application.Views.RunicView`1")
            return current.GenericTypeArguments[0];
    return null;
}

static string? ContractFor(MemberInfo member)
{
    var attribute = member.CustomAttributes.FirstOrDefault(value =>
        value.AttributeType.FullName == "Runic.Application.Views.RunicViewContractAttribute");
    if (attribute is null) return null;
    var contract = attribute.ConstructorArguments.Single().Value as string;
    if (string.IsNullOrWhiteSpace(contract) || !char.IsLetter(contract[0])
        || !contract.All(char.IsLetterOrDigit))
        throw new InvalidOperationException($"{member.Name}: a View contract must contain only letters and digits and start with a letter.");
    return contract;
}

static string PageKind(string name, string? contract) =>
    LowerFirst(name) + (contract is null ? "" : char.ToUpperInvariant(contract[0]) + contract[1..]);

static int InheritanceDepth(Type type)
{
    var depth = 0;
    for (var current = type.BaseType; current is not null; current = current.BaseType) depth++;
    return depth;
}

static void GenerateViewPartial(string path, Type view, Type model, string shortName)
{
    if (view.ContainsGenericParameters || view.Namespace is null || model.Namespace is null)
        throw new InvalidOperationException($"{view.FullName}: a Runic View must be a closed, named class.");
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine($"namespace {view.Namespace};");
    cs.AppendLine($"public {(view.IsSealed ? "sealed " : "")}partial class {view.Name}");
    cs.AppendLine("{");
    cs.AppendLine("    internal global::System.IDisposable AttachRunicBridge(");
    cs.AppendLine("        global::Runic.Application.Views.IBridgeTransport transport, string route,");
    cs.AppendLine("        global::Runic.Application.Views.WindowContentSession content) =>");
    cs.AppendLine($"        new global::{model.Namespace}.{shortName}Bridge(transport,");
    cs.AppendLine($"            DataContext ?? throw new global::System.InvalidOperationException(\"{view.Name} has no DataContext.\"),");
    cs.AppendLine("            route, content);");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static void GenerateCompositionRegistration(string path, string compositionType, (Type Model, string Name)[] models)
{
    var parts = CompositionParts(compositionType);

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    cs.AppendLine("using Runic.Application.Views;");
    cs.AppendLine($"namespace {string.Join('.', parts[..^1])};");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    public static IServiceCollection AddRunicBridges(this IServiceCollection services)");
    cs.AppendLine("    {");
    cs.AppendLine("        ArgumentNullException.ThrowIfNull(services);");
    foreach (var (model, name) in models)
    {
        var modelType = $"global::{model.FullName}";
        var bridgeType = $"global::{model.Namespace}.{name}Bridge";
        var hasContent = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
            .Any(property => !property.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Runic.Application.Views.RunicIgnoreAttribute")
                && (property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                        ? property.PropertyType.GenericTypeArguments[0] : property.PropertyType) is { } candidate
                && candidate != typeof(object) && models.Any(entry =>
                    entry.Model != model && candidate.IsAssignableFrom(entry.Model)));
        if (!hasContent)
        {
            cs.AppendLine($"        services.AddScoped<global::System.Func<IBridgeTransport, {modelType}, global::System.IDisposable>>(");
            cs.AppendLine($"            _ => (transport, vm) => new {bridgeType}(transport, vm));");
        }
        cs.AppendLine($"        services.AddScoped<global::System.Func<IBridgeTransport, WindowContentSession, {modelType}, global::System.IDisposable>>(");
        cs.AppendLine($"            _ => (transport, content, vm) => new {bridgeType}(transport, vm, content: content));");
    }
    cs.AppendLine("        return services;");
    cs.AppendLine("    }");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static void GenerateCompositionStub(string path, string compositionType)
{
    var parts = CompositionParts(compositionType);
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    cs.AppendLine($"namespace {string.Join('.', parts[..^1])};");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    public static IServiceCollection AddRunicBridges(this IServiceCollection services) => services;");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static string[] CompositionParts(string compositionType)
{
    var parts = compositionType.Split('.');
    if (parts.Length < 2 || parts.Any(part => part.Length == 0 || !(char.IsLetter(part[0]) || part[0] == '_')
        || part.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_'))))
        throw new ArgumentException("DI composition needs a fully qualified C# class name.");
    return parts;
}

static void GenerateOne(Type model, string csharpPath, string typescriptPath, string shortName,
    bool registerGlobally = true, (Type Model, string Name)[]? knownModels = null,
    IReadOnlyDictionary<Type, List<Type>>? viewTypes = null)
{
    knownModels ??= [(model, shortName)];
    if (!typeof(INotifyPropertyChanged).IsAssignableFrom(model))
        throw new InvalidOperationException($"{model.FullName} must implement INotifyPropertyChanged.");
    if (model.IsNested || !model.IsClass || !model.IsPublic)
        throw new InvalidOperationException("This prototype supports public, top-level ViewModel classes.");

    var declared = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetIndexParameters().Length == 0
            && !property.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Runic.Application.Views.RunicIgnoreAttribute"))
        .OrderBy(property => property.MetadataToken)
        .ToArray();
    var commands = declared.Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)).ToArray();
    var properties = declared.Except(commands).ToArray();
    if (properties.Any(property => LowerFirst(property.Name) == "revision"))
        throw new NotSupportedException($"{model.Name}.Revision conflicts with the generated Bridge revision field.");
    var nullability = new NullabilityInfoContext();
    if (properties.Length == 0 && commands.Length == 0)
        throw new InvalidOperationException("A ViewModel needs at least one state property or command.");
    var contentProperties = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var contentCollections = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    foreach (var property in properties)
    {
        if (property.GetMethod is null) throw new NotSupportedException($"{property.Name}: a public getter is required.");
        // A routed interface or base property may admit several registered
        // ViewModels. Emit the most specific class first: a base pattern ahead
        // of its derived class would hide the derived View (or fail to compile).
        var contentModels = knownModels.Where(entry => entry.Model != model
            && property.PropertyType.IsAssignableFrom(entry.Model))
            .OrderByDescending(entry => InheritanceDepth(entry.Model))
            .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
            .ToArray();
        if (contentModels.Length > 0 && property.PropertyType != typeof(object))
        {
            if (property.SetMethod?.IsPublic == true)
                throw new NotSupportedException($"{property.Name}: ViewModel content must be set by .NET, not the web view.");
            contentProperties.Add(property, contentModels);
            continue;
        }
        if (property.PropertyType.IsGenericType
            && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var itemType = property.PropertyType.GenericTypeArguments[0];
            var itemModels = knownModels.Where(entry => entry.Model != model
                && itemType != typeof(object) && itemType.IsAssignableFrom(entry.Model))
                .OrderByDescending(entry => InheritanceDepth(entry.Model))
                .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
                .ToArray();
            if (itemModels.Length > 0)
            {
                if (property.SetMethod?.IsPublic == true)
                    throw new NotSupportedException($"{property.Name}: ViewModel collections must be set by .NET, not the web view.");
                contentCollections.Add(property, itemModels);
                continue;
            }
            if (itemType == typeof(object) || typeof(INotifyPropertyChanged).IsAssignableFrom(itemType))
                throw new NotSupportedException($"{model.Name}.{property.Name}: a ViewModel collection needs a specific item interface or base class with a registered View.");
        }
        if (property.PropertyType != typeof(int) && property.PropertyType != typeof(string)
            && property.PropertyType != typeof(bool)
            && !TryListItem(property.PropertyType, out _))
            throw new NotSupportedException($"{property.Name}: only int, string, bool, and read-only lists of simple records are supported.");
        if (property.SetMethod?.IsPublic == true && property.PropertyType != typeof(int)
            && property.PropertyType != typeof(string) && property.PropertyType != typeof(bool))
            throw new NotSupportedException($"{property.Name}: writable collections are not supported yet.");
    }
    var hasContent = contentProperties.Count > 0 || contentCollections.Count > 0;
    var contentBindings = contentProperties.Concat(contentCollections).ToArray();
    if (hasContent && registerGlobally)
        throw new InvalidOperationException($"{model.Name} contains ViewModel content. Generate it with --no-registry and attach it through a window content session.");
    var fullType = $"global::{model.FullName}";
    var commandPlans = new Dictionary<PropertyInfo, (bool HasStringArgument, bool IsAsync, string Descriptor)>();
    foreach (var command in commands)
    {
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new NotSupportedException($"{command.Name}: Bridge commands must end with Command.");
        var plan = ToolkitCommandInspector.Inspect(command, fullType)
            ?? ReactiveCommandInspector.Inspect(command, fullType)
            ?? throw new NotSupportedException($"{command.Name}: unsupported CommunityToolkit or ReactiveUI command shape.");
        commandPlans.Add(command, plan);
    }

    if (shortName.Length == 0 || !shortName.All(char.IsLetterOrDigit) || !char.IsLetter(shortName[0]))
        throw new ArgumentException("The public Bridge name must be a C#/TypeScript identifier.");
    var prefix = LowerFirst(shortName);
    var hasErrors = typeof(INotifyDataErrorInfo).IsAssignableFrom(model);
    var contractFingerprint = BridgeContractShape.Compute(model);
    // Checked writes require a WindowContentSession-owned provider. Keep the
    // established global Bridge surface direct until it gains an equivalent
    // explicit window owner. The window slice supports the scalar codecs that
    // the ordinary Bridge already serializes without reflection.
    var checkedProperties = !registerGlobally
        ? properties.Where(property => property.SetMethod?.IsPublic == true
            && (property.PropertyType == typeof(string)
                || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(bool))).ToArray()
        : [];
    var needsCheckedWriter = checkedProperties.Length > 0;

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Runic.Application.Views;");
    cs.AppendLine($"namespace {model.Namespace};");
    cs.AppendLine($"internal sealed class {shortName}Bridge : ViewModelBridge<{fullType}>");
    cs.AppendLine("{");
    if (hasContent)
    {
        cs.AppendLine("    private readonly WindowContentSession _content;");
        cs.AppendLine($"    private readonly {fullType} _owner;");
    }
    cs.AppendLine($"    internal {shortName}Bridge(IBridgeTransport transport, {fullType} vm, string? routePrefix = null, WindowContentSession? content = null) : base(");
    cs.AppendLine($"        transport, vm, routePrefix ?? \"{prefix}\",");
    cs.AppendLine(needsCheckedWriter
        ? "        CreateWriter(content),"
        : hasContent
        ? "        CreateWriter(content),"
        : "        WriteSnapshot,");
    cs.AppendLine("        [");
    foreach (var property in properties)
    {
        var setter = property.SetMethod?.IsPublic == true
            ? property.PropertyType == typeof(int)
                ? $"(vm, e) => {{ var value = e.GetInt64(); if (value is < int.MinValue or > int.MaxValue) throw new global::System.ArgumentOutOfRangeException(nameof(value)); vm.{property.Name} = (int)value; }}"
                : property.PropertyType == typeof(bool)
                    ? $"(vm, e) => vm.{property.Name} = e.GetBoolean()"
                : IsNullableString(property)
                    ? $"(vm, e) => vm.{property.Name} = global::Runic.Application.Views.BridgeJson.ReadNullableString(e.GetString())"
                    : $"(vm, e) => vm.{property.Name} = e.GetString()"
            : "null";
        cs.AppendLine($"            new PropertyDescriptor<{fullType}>(\"{property.Name}\", vm => vm.{property.Name}, {setter}),");
    }
    cs.AppendLine("        ],");
    if (needsCheckedWriter)
    {
        cs.AppendLine("        [");
        foreach (var property in checkedProperties)
        {
            var kind = property.PropertyType == typeof(int) ? "Int32"
                : property.PropertyType == typeof(bool) ? "Boolean"
                : IsNullableString(property) ? "NullableString" : "String";
            var setter = property.PropertyType == typeof(int) ? $"(vm, value) => vm.{property.Name} = (int)value!"
                : property.PropertyType == typeof(bool) ? $"(vm, value) => vm.{property.Name} = (bool)value!"
                : IsNullableString(property) ? $"(vm, value) => vm.{property.Name} = (string?)value"
                : $"(vm, value) => vm.{property.Name} = (string)value!";
            cs.AppendLine($"            new CheckedPropertyDescriptor<{fullType}>(\"{property.Name}\", \"{LowerFirst(property.Name)}\", CheckedFieldValueKind.{kind}, vm => vm.{property.Name}, {setter}),");
        }
        cs.AppendLine("        ],");
    }
    cs.AppendLine("        [");
    foreach (var command in commands)
        cs.AppendLine($"            {commandPlans[command].Descriptor},");
    if (hasContent)
        cs.AppendLine($"        ], \"{contractFingerprint}\", content) {{ _content = content!; _owner = vm; }}");
    else cs.AppendLine($"        ], \"{contractFingerprint}\", content) {{ }}");
    cs.AppendLine();
    if (hasContent || needsCheckedWriter)
    {
        cs.AppendLine(needsCheckedWriter
            ? $"    private static global::Runic.Application.Views.BridgeSnapshotWriter<{fullType}> CreateWriter(WindowContentSession? content)"
            : $"    private static global::System.Action<global::System.Text.Json.Utf8JsonWriter, {fullType}, long> CreateWriter(WindowContentSession? content)");
        cs.AppendLine("    {");
        if (hasContent) cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(content);");
        cs.AppendLine(needsCheckedWriter
            ? (hasContent
                ? "        return (writer, current, revision, writeFieldMetadata) => WriteSnapshot(writer, current, revision, content, writeFieldMetadata);"
                : "        return (writer, current, revision, writeFieldMetadata) => WriteSnapshot(writer, current, revision, writeFieldMetadata);")
            : "        return (writer, current, revision) => WriteSnapshot(writer, current, revision, content);");
        cs.AppendLine("    }");
        cs.AppendLine();
    }
    cs.AppendLine($"    private static void WriteSnapshot(global::System.Text.Json.Utf8JsonWriter writer, {fullType} vm, long revision{(hasContent ? ", WindowContentSession content" : "")}{(needsCheckedWriter ? ", global::System.Action<global::System.Text.Json.Utf8JsonWriter> writeFieldMetadata" : "")})");
    cs.AppendLine("    {");
    cs.AppendLine("        writer.WriteStartObject();");
    cs.AppendLine("        writer.WriteNumber(\"revision\", revision);");
    foreach (var property in properties)
    {
        var jsonName = LowerFirst(property.Name);
        if (contentProperties.TryGetValue(property, out var pageModels))
        {
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        if (vm.{property.Name} is null) {{ content.Clear(vm, \"{property.Name}\"); writer.WriteNullValue(); }}");
            cs.AppendLine("        else");
            cs.AppendLine("        {");
            cs.AppendLine($"            switch (vm.{property.Name})");
            cs.AppendLine("            {");
            foreach (var (pageModel, pageName) in pageModels)
            {
                var pageType = $"global::{pageModel.FullName}";
                var bridgeType = $"global::{pageModel.Namespace}.{pageName}Bridge";
                Type? selectedView = null;
                var contract = ContractFor(property);
                if (viewTypes is not null && viewTypes.TryGetValue(pageModel, out var variants))
                {
                    selectedView = variants.FirstOrDefault(view => ContractFor(view) == contract);
                    if (selectedView is null)
                        throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.");
                }
                else if (contract is not null)
                    throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.");
                var pageKind = PageKind(pageName, contract);
                var contractArgument = contract is null ? "" : $", contract: \"{contract}\"";
                cs.AppendLine($"                case {pageType} page:");
                cs.AppendLine("                {");
                if (selectedView is not null)
                    cs.AppendLine($"                    var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    cs.AppendLine($"                    var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                cs.AppendLine("                    writer.WriteStartObject();");
                cs.AppendLine("                    writer.WriteString(\"kind\", reference.Kind);");
                cs.AppendLine("                    writer.WriteString(\"id\", reference.Id);");
                cs.AppendLine("                    writer.WriteEndObject();");
                cs.AppendLine("                    break;");
                cs.AppendLine("                }");
            }
            cs.AppendLine($"                default: throw new global::System.NotSupportedException(\"{property.Name} contains an unregistered ViewModel type.\");");
            cs.AppendLine("            }");
            cs.AppendLine("        }");
        }
        else if (contentCollections.TryGetValue(property, out var collectionModels))
        {
            var activeName = $"active{property.Name}Ids";
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        var {activeName} = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            cs.AppendLine($"        if (vm.{property.Name} is null) {{ content.PruneCollection(vm, \"{property.Name}\", {activeName}); writer.WriteNullValue(); }}");
            cs.AppendLine("        else");
            cs.AppendLine("        {");
            cs.AppendLine("            writer.WriteStartArray();");
            cs.AppendLine($"            foreach (var item in vm.{property.Name})");
            cs.AppendLine("            {");
            cs.AppendLine("                if (item is null) { writer.WriteNullValue(); continue; }");
            cs.AppendLine("                switch (item)");
            cs.AppendLine("                {");
            foreach (var (pageModel, pageName) in collectionModels)
            {
                var pageType = $"global::{pageModel.FullName}";
                var bridgeType = $"global::{pageModel.Namespace}.{pageName}Bridge";
                var contract = ContractFor(property);
                Type? selectedView = null;
                if (viewTypes is not null && viewTypes.TryGetValue(pageModel, out var variants))
                {
                    selectedView = variants.SingleOrDefault(view => ContractFor(view) == contract);
                    if (selectedView is null)
                        throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.");
                }
                else if (contract is not null)
                    throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.");
                var pageKind = PageKind(pageName, contract);
                var contractArgument = contract is null ? "" : $", contract: \"{contract}\"";
                cs.AppendLine($"                    case {pageType} page:");
                cs.AppendLine("                    {");
                if (selectedView is not null)
                    cs.AppendLine($"                        var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    cs.AppendLine($"                        var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                cs.AppendLine($"                        {activeName}.Add(reference.Id);");
                cs.AppendLine("                        writer.WriteStartObject();");
                cs.AppendLine("                        writer.WriteString(\"kind\", reference.Kind);");
                cs.AppendLine("                        writer.WriteString(\"id\", reference.Id);");
                cs.AppendLine("                        writer.WriteEndObject();");
                cs.AppendLine("                        break;");
                cs.AppendLine("                    }");
            }
            cs.AppendLine($"                    default: throw new global::System.NotSupportedException(\"{property.Name} contains an unregistered ViewModel type.\");");
            cs.AppendLine("                }");
            cs.AppendLine("            }");
            cs.AppendLine($"            content.PruneCollection(vm, \"{property.Name}\", {activeName});");
            cs.AppendLine("            writer.WriteEndArray();");
            cs.AppendLine("        }");
        }
        else if (TryListItem(property.PropertyType, out var item))
        {
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        if (vm.{property.Name} is null) writer.WriteNullValue();");
            cs.AppendLine("        else");
            cs.AppendLine("        {");
            cs.AppendLine("            writer.WriteStartArray();");
            cs.AppendLine($"            foreach (var item in vm.{property.Name})");
            cs.AppendLine("            {");
            cs.AppendLine("                if (item is null) { writer.WriteNullValue(); continue; }");
            cs.AppendLine("                writer.WriteStartObject();");
            foreach (var member in ListMembers(item!))
                cs.AppendLine($"                writer.{WriterMethod(member.PropertyType)}(\"{LowerFirst(member.Name)}\", item.{member.Name});");
            cs.AppendLine("                writer.WriteEndObject();");
            cs.AppendLine("            }");
            cs.AppendLine("            writer.WriteEndArray();");
            cs.AppendLine("        }");
        }
        else cs.AppendLine($"        writer.{WriterMethod(property.PropertyType)}(\"{jsonName}\", vm.{property.Name});");
        if (hasErrors)
            cs.AppendLine($"        global::Runic.Application.Views.BridgeJson.WriteErrors(writer, vm, \"{property.Name}\", \"{jsonName}Errors\");");
    }
    foreach (var command in commands)
        if (!commandPlans[command].HasStringArgument)
            cs.AppendLine($"        writer.WriteBoolean(\"can{command.Name[..^"Command".Length]}\", ((global::System.Windows.Input.ICommand)vm.{command.Name}).CanExecute(null));");
    if (needsCheckedWriter) cs.AppendLine("        writeFieldMetadata(writer);");
    cs.AppendLine("        writer.WriteEndObject();");
    cs.AppendLine("    }");
    if (hasContent)
    {
        cs.AppendLine("    public override void Dispose()");
        cs.AppendLine("    {");
        cs.AppendLine("        base.Dispose();");
        cs.AppendLine("        _content.ClearOwner(_owner);");
        cs.AppendLine("    }");
    }
    cs.AppendLine("}");
    if (registerGlobally)
    {
        cs.AppendLine($"internal static class {shortName}BridgeRegistration");
        cs.AppendLine("{");
        cs.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        cs.AppendLine($"    internal static void Register() => global::Runic.Application.Views.Bridge.Register<{fullType}>((transport, vm) => new {shortName}Bridge(transport, vm));");
        cs.AppendLine("}");
    }

    var ts = new StringBuilder();
    ts.AppendLine("// <auto-generated />");
    foreach (var (pageName, contracts) in contentBindings.SelectMany(entry => entry.Value.Select(page => (page.Name, Contract: ContractFor(entry.Key))))
        .GroupBy(entry => entry.Name).Select(group => (group.Key, Contracts: group.Select(entry => entry.Contract).Distinct())))
    {
        var members = string.Join(", ", contracts.SelectMany(contract =>
        {
            var variant = char.ToUpperInvariant(PageKind(pageName, contract)[0]) + PageKind(pageName, contract)[1..];
            return new[] { $"page{variant}", $"type {variant}PageReference" };
        }));
        ts.AppendLine($"import {{ {members} }} from \"./{LowerFirst(pageName)}.js\";");
    }
    if (hasContent) ts.AppendLine();
    foreach (var list in properties.Where(property => !contentCollections.ContainsKey(property))
        .Select(property => property.PropertyType).Where(type => TryListItem(type, out _)))
    {
        TryListItem(list, out var item);
        ts.AppendLine($"export interface {item!.Name} {{");
        foreach (var member in ListMembers(item))
        {
            ts.AppendLine($"  readonly {LowerFirst(member.Name)}: {TsPropertyType(member)};");
        }
        ts.AppendLine("}");
        ts.AppendLine();
    }
    ts.AppendLine($"export interface {shortName}State {{");
    ts.AppendLine("  readonly revision: number;");
    foreach (var property in properties)
    {
        var propertyType = contentProperties.TryGetValue(property, out var pages)
            ? string.Join(" | ", pages.Select(page =>
                $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                + (nullability.Create(property).ReadState == NullabilityState.Nullable ? " | null" : "")
            : contentCollections.TryGetValue(property, out var collectionPages)
                ? "readonly (" + string.Join(" | ", collectionPages.Select(page =>
                    $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                    + ")[]" + (nullability.Create(property).ReadState == NullabilityState.Nullable ? " | null" : "")
            : TsPropertyType(property);
        ts.AppendLine($"  readonly {LowerFirst(property.Name)}: {propertyType};");
        if (hasErrors) ts.AppendLine($"  readonly {LowerFirst(property.Name)}Errors: readonly string[];");
    }
    foreach (var command in commands)
        if (!commandPlans[command].HasStringArgument)
            ts.AppendLine($"  readonly can{command.Name[..^"Command".Length]}: boolean;");
    ts.AppendLine("}");
    ts.AppendLine();
    if (needsCheckedWriter)
    {
        ts.AppendLine("export type FieldBaseline<T> = { readonly value: T; readonly version: number };");
        ts.AppendLine("export type FieldWriteOptions<T> = { readonly requestId: string; readonly baseline: FieldBaseline<T> };");
        ts.AppendLine("export type FieldWriteReceipt<T> =");
        ts.AppendLine("  | { readonly kind: \"applied\"; readonly snapshot: FieldBaseline<T>; readonly validation?: string }");
        ts.AppendLine("  | { readonly kind: \"committed-with-error\"; readonly snapshot: FieldBaseline<T>; readonly message: string }");
        ts.AppendLine("  | { readonly kind: \"rejected\"; readonly message: string }");
        ts.AppendLine("  | { readonly kind: \"conflict\"; readonly incoming: FieldBaseline<T>; readonly message: string };");
        ts.AppendLine($"export interface {shortName}CheckedFields {{");
        foreach (var property in checkedProperties)
            ts.AppendLine($"  readonly {LowerFirst(property.Name)}: {TsPropertyType(property)};");
        ts.AppendLine("}");
        ts.AppendLine();
    }
    ts.AppendLine($"export interface {shortName}View {{");
    ts.AppendLine($"  readonly snapshot: {shortName}State;");
    ts.AppendLine($"  subscribe(listener: (state: {shortName}State) => void): () => void;");
    ts.AppendLine("  dispose(): void;");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
        ts.AppendLine($"  set{property.Name}(value: {TsPropertyType(property)}): Promise<{shortName}State>;");
    foreach (var property in checkedProperties)
        ts.AppendLine($"  write{property.Name}(value: {TsPropertyType(property)}, options: FieldWriteOptions<{TsPropertyType(property)}>): Promise<FieldWriteReceipt<{TsPropertyType(property)}>>;");
    if (needsCheckedWriter)
        ts.AppendLine($"  fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): FieldBaseline<{shortName}CheckedFields[K]>;");
    foreach (var command in commands)
        ts.AppendLine($"  {LowerFirst(command.Name[..^"Command".Length])}({(commandPlans[command].HasStringArgument ? "argument: string" : "")}): Promise<{shortName}State>;");
    foreach (var command in commands.Where(command => !commandPlans[command].HasStringArgument && commandPlans[command].IsAsync))
    {
        var operationName = command.Name[..^"Command".Length];
        ts.AppendLine($"  start{operationName}(): Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine($"  start{operationName}WithRequestId(requestId: string): Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine($"  recover{operationName}WithRequestId(requestId: string): Promise<{shortName}{operationName}Operation>;");
    }
    ts.AppendLine("}");
    ts.AppendLine();
    var ownContracts = viewTypes is not null && viewTypes.TryGetValue(model, out var ownViews)
        ? ownViews.Select(ContractFor).ToArray() : [null];
    foreach (var contract in ownContracts)
    {
        var ownKind = PageKind(shortName, contract);
        var functionName = char.ToUpperInvariant(ownKind[0]) + ownKind[1..];
        var referenceType = $"{functionName}PageReference";
        var mapName = $"page{functionName}References";
        var finalizerName = $"page{functionName}Finalizer";
        ts.AppendLine($"export interface {referenceType} {{");
        ts.AppendLine($"  readonly kind: \"{ownKind}\";");
        ts.AppendLine($"  connect(): Promise<{shortName}View>;");
        ts.AppendLine("}");
        ts.AppendLine($"const {mapName} = new Map<string, WeakRef<{referenceType}>>();");
        ts.AppendLine($"const {finalizerName} = new FinalizationRegistry<{{ id: string; reference: WeakRef<{referenceType}> }}>(entry => {{");
        ts.AppendLine($"  if ({mapName}.get(entry.id) === entry.reference) {mapName}.delete(entry.id);");
        ts.AppendLine("});");
        ts.AppendLine($"export function page{functionName}(id: string): {referenceType} {{");
        ts.AppendLine($"  let reference = {mapName}.get(id)?.deref();");
        ts.AppendLine("  if (reference) return reference;");
        ts.AppendLine($"  reference = {{ kind: \"{ownKind}\", connect: () => connect{shortName}At(`content${{id}}`, true) }};");
        ts.AppendLine("  const weak = new WeakRef(reference);");
        ts.AppendLine($"  {mapName}.set(id, weak);");
        ts.AppendLine($"  {finalizerName}.register(reference, {{ id, reference: weak }});");
        ts.AppendLine("  return reference;");
        ts.AppendLine("}");
        ts.AppendLine();
    }
    ts.AppendLine("export type BridgeErrorKind = \"rejected\" | \"cancelled\" | \"failed\" | \"disconnected\" | \"timeout\";");
    ts.AppendLine("export class BridgeError extends Error {");
    ts.AppendLine("  constructor(readonly kind: BridgeErrorKind, message: string) { super(message); this.name = \"BridgeError\"; }");
    ts.AppendLine("}");
    ts.AppendLine("export type BridgeOperationStatusKind = \"running\" | \"succeeded\" | \"failed\" | \"cancelled\" | \"expired\" | \"unknown\";");
    ts.AppendLine("export interface BridgeOperationStatus { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationStatusKind; readonly error?: { readonly kind: \"failed\"; readonly message: string }; }");
    ts.AppendLine("export type BridgeOperationCancelKind = \"cancellation-requested\" | \"not-running\" | \"unknown\" | \"expired\";");
    ts.AppendLine("export interface BridgeOperationCancelResult { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationCancelKind; }");
    ts.AppendLine("export class BridgeOperationUncertainError extends Error {");
    ts.AppendLine("  constructor(readonly contract: string, readonly requestId: string, message: string) { super(message); this.name = \"BridgeOperationUncertainError\"; }");
    ts.AppendLine("}");
    foreach (var command in commands.Where(command => !commandPlans[command].HasStringArgument && commandPlans[command].IsAsync))
    {
        var operationName = command.Name[..^"Command".Length];
        ts.AppendLine($"export interface {shortName}{operationName}Operation {{");
        ts.AppendLine("  readonly requestId: string;");
        ts.AppendLine("  status(): Promise<BridgeOperationStatus>;");
        ts.AppendLine("  readonly completion: Promise<BridgeOperationStatus>;");
        ts.AppendLine("  wait(): Promise<BridgeOperationStatus>;");
        ts.AppendLine("  cancel(): Promise<BridgeOperationCancelResult>;");
        ts.AppendLine("}");
    }
    if (hasContent || needsCheckedWriter)
    {
        var names = contentBindings.Select(entry => $"\"{LowerFirst(entry.Key.Name)}\"").ToList();
        if (names.Count == 0) names.Add("never");
        ts.AppendLine($"type WireState = Omit<{shortName}State, {string.Join(" | ", names)}> & {{");
        foreach (var (property, pages) in contentProperties)
        {
            var rawType = string.Join(" | ", pages.Select(page => $"{{ readonly kind: \"{PageKind(page.Name, ContractFor(property))}\"; readonly id: string }}"));
            if (nullability.Create(property).ReadState == NullabilityState.Nullable) rawType += " | null";
            ts.AppendLine($"  readonly {LowerFirst(property.Name)}: {rawType};");
        }
        foreach (var (property, pages) in contentCollections)
        {
            var rawType = "readonly (" + string.Join(" | ", pages.Select(page =>
                $"{{ readonly kind: \"{PageKind(page.Name, ContractFor(property))}\"; readonly id: string }}")) + ")[]";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable) rawType += " | null";
            ts.AppendLine($"  readonly {LowerFirst(property.Name)}: {rawType};");
        }
        if (needsCheckedWriter)
        {
            ts.AppendLine("  readonly __runicFields: {");
            foreach (var property in checkedProperties)
                ts.AppendLine($"    readonly {LowerFirst(property.Name)}: {{ readonly version: number }};");
            ts.AppendLine("  };");
        }
        ts.AppendLine("};");
        ts.AppendLine($"function hydrate(wire: WireState): {shortName}State {{");
        if (needsCheckedWriter) ts.AppendLine("  const { __runicFields: _runicFields, ...state } = wire;");
        ts.AppendLine("  return {");
        ts.AppendLine(needsCheckedWriter ? "    ...state," : "    ...wire,");
        foreach (var (property, pages) in contentProperties)
        {
            var field = LowerFirst(property.Name);
            var expression = string.Join(" : ", pages.Select(page =>
                $"wire.{field}.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}(wire.{field}.id)"));
            expression += $" : (() => {{ throw new BridgeError(\"failed\", \"Unknown {field} kind.\"); }})()";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable)
                expression = $"wire.{field} === null ? null : {expression}";
            ts.AppendLine($"    {field}: {expression},");
        }
        foreach (var (property, pages) in contentCollections)
        {
            var field = LowerFirst(property.Name);
            var expression = string.Join(" : ", pages.Select(page =>
                $"item.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}(item.id)"));
            expression += $" : (() => {{ throw new BridgeError(\"failed\", \"Unknown {field} kind.\"); }})()";
            var hydrated = $"wire.{field}.map(item => {expression})";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable)
                hydrated = $"wire.{field} === null ? null : {hydrated}";
            ts.AppendLine($"    {field}: {hydrated},");
        }
        ts.AppendLine("  };");
        ts.AppendLine("}");
    }
    else
    {
        ts.AppendLine($"type WireState = {shortName}State;");
        ts.AppendLine($"function hydrate(wire: WireState): {shortName}State {{ return wire; }}");
    }
    ts.AppendLine($"interface BridgeReply {{ readonly ok: boolean; readonly state: WireState | null; readonly error: {{ readonly kind: BridgeErrorKind; readonly message: string }} | null; }}");
    if (needsCheckedWriter)
        ts.AppendLine("interface FieldWriteReply<T> extends BridgeReply { readonly receipt: FieldWriteReceipt<T> | null; }");
    ts.AppendLine("interface RunicBridgeClient {");
    ts.AppendLine("  isConnected(): boolean;");
    ts.AppendLine("  call(name: string, ...args: unknown[]): Promise<string>;");
    ts.AppendLine("}");
    ts.AppendLine("type BridgeWindow = Window & { __runicBridge?: RunicBridgeClient };");
    ts.AppendLine();
    ts.AppendLine("async function waitForBridge(timeoutMilliseconds = 5_000): Promise<RunicBridgeClient> {");
    ts.AppendLine("  const hostWindow = window as BridgeWindow;");
    ts.AppendLine("  const deadline = Date.now() + timeoutMilliseconds;");
    ts.AppendLine("  while (!hostWindow.__runicBridge?.isConnected()) {");
    ts.AppendLine("    if (Date.now() >= deadline) throw new BridgeError(\"timeout\", \"The Bridge did not connect to .NET in time.\");");
    ts.AppendLine("    await new Promise<void>((resolve) => window.setTimeout(resolve, 50));");
    ts.AppendLine("  }");
    ts.AppendLine("  return hostWindow.__runicBridge;");
    ts.AppendLine("}");
    ts.AppendLine();
    ts.AppendLine("interface SharedLease {");
    ts.AppendLine("  disposed: boolean;");
    ts.AppendLine("  current: unknown | undefined;");
    ts.AppendLine("  readonly listeners: Set<(state: unknown) => void>;");
    ts.AppendLine("  mounted: boolean;");
    ts.AppendLine("  readonly mountToken: string | undefined;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedEntry {");
    ts.AppendLine("  readonly contract: string;");
    ts.AppendLine("  readonly route: string;");
    ts.AppendLine("  readonly bridge: RunicBridgeClient;");
    ts.AppendLine("  readonly routeEntry: SharedRoute;");
    ts.AppendLine("  readonly leases: Set<SharedLease>;");
    ts.AppendLine("  readonly hydrate: (wire: unknown) => unknown;");
    ts.AppendLine("  readonly accept: (wire: unknown) => unknown;");
    ts.AppendLine("  current: unknown | undefined;");
    ts.AppendLine("  wire: unknown | undefined;");
    ts.AppendLine("  revision: number | undefined;");
    ts.AppendLine("  initializing: Promise<void> | undefined;");
    ts.AppendLine("  active: boolean;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedRoute {");
    ts.AppendLine("  readonly route: string;");
    ts.AppendLine("  readonly callbackName: string;");
    ts.AppendLine("  readonly bridge: RunicBridgeClient;");
    ts.AppendLine("  readonly generation: symbol;");
    ts.AppendLine("  readonly entries: Map<string, SharedEntry>;");
    ts.AppendLine("  readonly callback: (state: unknown) => unknown;");
    ts.AppendLine("  readonly previousCallback: unknown;");
    ts.AppendLine("  active: boolean;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedRuntime {");
    ts.AppendLine("  bridge: RunicBridgeClient | undefined;");
    ts.AppendLine("  generation: symbol;");
    ts.AppendLine("  mountSession: string;");
    ts.AppendLine("  readonly routes: Map<string, SharedRoute>;");
    ts.AppendLine("  readonly operations: Map<string, SharedOperation>;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedOperation {");
    ts.AppendLine("  readonly contract: string;");
    ts.AppendLine("  readonly requestId: string;");
    ts.AppendLine("  state: \"pending\" | \"accepted\" | \"uncertain\";");
    ts.AppendLine("  admission: Promise<unknown> | undefined;");
    ts.AppendLine("  handle: unknown | undefined;");
    ts.AppendLine("}");
    ts.AppendLine("const sharedRuntimeKey = Symbol.for(\"runic.views.generated-client-runtime\");");
    ts.AppendLine("function sharedRuntimeFor(bridge: RunicBridgeClient): SharedRuntime {");
    ts.AppendLine("  const hostWindow = window as unknown as Record<symbol, unknown>;");
    ts.AppendLine("  let runtime = hostWindow[sharedRuntimeKey] as SharedRuntime | undefined;");
    ts.AppendLine("  if (!runtime) {");
    ts.AppendLine("    runtime = { bridge, generation: Symbol(), mountSession: globalThis.crypto.randomUUID(), routes: new Map(), operations: new Map() };");
    ts.AppendLine("    hostWindow[sharedRuntimeKey] = runtime;");
    ts.AppendLine("    return runtime;");
    ts.AppendLine("  }");
    ts.AppendLine("  if (runtime.bridge === bridge) {");
    ts.AppendLine("    // HMR can retain a runtime created by an earlier generated client.");
    ts.AppendLine("    const legacy = runtime as SharedRuntime & { operations?: Map<string, SharedOperation> };");
    ts.AppendLine("    legacy.operations ??= new Map();");
    ts.AppendLine("    return runtime;");
    ts.AppendLine("  }");
    ts.AppendLine("  const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("  for (const route of runtime.routes.values()) {");
    ts.AppendLine("    route.active = false;");
    ts.AppendLine("    if (callbacks[route.callbackName] === route.callback) callbacks[route.callbackName] = route.previousCallback;");
    ts.AppendLine("  }");
    ts.AppendLine("  runtime.routes.clear();");
    ts.AppendLine("  runtime.operations.clear();");
    ts.AppendLine("  runtime.bridge = bridge;");
    ts.AppendLine("  runtime.generation = Symbol();");
    ts.AppendLine("  runtime.mountSession = globalThis.crypto.randomUUID();");
    ts.AppendLine("  return runtime;");
    ts.AppendLine("}");
    ts.AppendLine("function sharedRouteFor(runtime: SharedRuntime, bridge: RunicBridgeClient, route: string): SharedRoute {");
    ts.AppendLine("  const callbackName = `__${route}Changed`;");
    ts.AppendLine("  const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("  const existing = runtime.routes.get(route);");
    ts.AppendLine("  if (existing?.active && existing.bridge === bridge && existing.generation === runtime.generation) {");
    ts.AppendLine("    callbacks[callbackName] = existing.callback;");
    ts.AppendLine("    return existing;");
    ts.AppendLine("  }");
    ts.AppendLine("  let sharedRoute: SharedRoute;");
    ts.AppendLine("  sharedRoute = {");
    ts.AppendLine("    route, callbackName, bridge, generation: runtime.generation, entries: new Map(), previousCallback: callbacks[callbackName], active: true,");
    ts.AppendLine("    callback(state) {");
    ts.AppendLine("      let accepted: unknown;");
    ts.AppendLine("      for (const entry of sharedRoute.entries.values()) accepted = entry.accept(state);");
    ts.AppendLine("      return accepted;");
    ts.AppendLine("    },");
    ts.AppendLine("  };");
    ts.AppendLine("  runtime.routes.set(route, sharedRoute);");
    ts.AppendLine("  callbacks[callbackName] = sharedRoute.callback;");
    ts.AppendLine("  return sharedRoute;");
    ts.AppendLine("}");
    ts.AppendLine($"const bridgeContract = \"{model.FullName}:{contractFingerprint}\";");
    ts.AppendLine();
    ts.AppendLine($"export function connect{shortName}(): Promise<{shortName}View> {{ return connect{shortName}At(\"{prefix}\"); }}");
    ts.AppendLine($"async function connect{shortName}At(route: string, needsMount = false): Promise<{shortName}View> {{");
    ts.AppendLine("  const bridge = await waitForBridge();");
    ts.AppendLine("  const runtime = sharedRuntimeFor(bridge);");
    ts.AppendLine("  const contractId = `${bridgeContract}:${route}`;");
    ts.AppendLine("  const routeEntry = sharedRouteFor(runtime, bridge, route);");
    ts.AppendLine("  if (routeEntry.entries.size !== 0 && !routeEntry.entries.has(contractId))");
    ts.AppendLine("    throw new BridgeError(\"failed\", \"This route already has an incompatible Bridge contract.\");");
    ts.AppendLine("  let entry = routeEntry.entries.get(contractId);");
    ts.AppendLine("  if (!entry) {");
    ts.AppendLine("    let created: SharedEntry;");
    ts.AppendLine("    created = {");
    ts.AppendLine("      contract: contractId, route, bridge, routeEntry, leases: new Set(), hydrate: wire => hydrate(wire as WireState), current: undefined, wire: undefined, revision: undefined, initializing: undefined, active: true,");
    ts.AppendLine("      accept(wire) {");
    ts.AppendLine("        const next = wire as WireState;");
    ts.AppendLine("        if (!created.active || !routeEntry.active) return created.current ?? created.hydrate(next);");
    ts.AppendLine("        if (created.current === undefined || created.revision === undefined || next.revision >= created.revision) {");
    ts.AppendLine("          created.revision = next.revision;");
    ts.AppendLine("          created.wire = next;");
    ts.AppendLine("          created.current = created.hydrate(next);");
    ts.AppendLine("          for (const lease of created.leases) if (!lease.disposed) {");
    ts.AppendLine("            lease.current = created.current;");
    ts.AppendLine("            for (const listener of lease.listeners) listener(created.current);");
    ts.AppendLine("          }");
    ts.AppendLine("        }");
    ts.AppendLine("        return created.current;");
    ts.AppendLine("      },");
    ts.AppendLine("    };");
    ts.AppendLine("    routeEntry.entries.set(contractId, created);");
    ts.AppendLine("    const initialization = (async () => {");
    ts.AppendLine("      let reply: string;");
    ts.AppendLine("      try { reply = await bridge.call(`${route}Snapshot`); }");
    ts.AppendLine("      catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The Bridge call could not complete.\"); }");
    ts.AppendLine("      unpack(reply, created);");
    ts.AppendLine("    })();");
    ts.AppendLine("    created.initializing = initialization;");
    ts.AppendLine("    initialization.then(");
    ts.AppendLine("      () => { if (created.initializing === initialization) created.initializing = undefined; },");
    ts.AppendLine("      () => { if (created.initializing === initialization) created.initializing = undefined; },");
    ts.AppendLine("    );");
    ts.AppendLine("    entry = created;");
    ts.AppendLine("  }");
    ts.AppendLine("  const shared = entry;");
    ts.AppendLine("  const mountToken = needsMount ? `${runtime.mountSession}:${globalThis.crypto.randomUUID()}` : undefined;");
    ts.AppendLine("  const lease: SharedLease = { disposed: false, current: undefined, listeners: new Set(), mounted: false, mountToken };");
    ts.AppendLine("  shared.leases.add(lease);");
    ts.AppendLine("  function isLive(): boolean {");
    ts.AppendLine("    return shared.active && routeEntry.active && runtime.bridge === bridge && routeEntry.generation === runtime.generation;");
    ts.AppendLine("  }");
    ts.AppendLine($"  function unpack(json: string, target: SharedEntry = shared): {shortName}State {{");
    ts.AppendLine("    let reply: BridgeReply;");
    ts.AppendLine("    try { reply = JSON.parse(json) as BridgeReply; }");
    ts.AppendLine("    catch { throw new BridgeError(\"failed\", \"The Bridge returned an invalid response.\"); }");
    ts.AppendLine("    const state = reply.state === null ? undefined : target.accept(reply.state);");
    ts.AppendLine("    if (!reply.ok) throw new BridgeError(reply.error?.kind ?? \"failed\", reply.error?.message ?? \"The call failed.\");");
    ts.AppendLine("    if (state === undefined) throw new BridgeError(\"failed\", \"The Bridge returned no state.\");");
    ts.AppendLine($"    return state as {shortName}State;");
    ts.AppendLine("  }");
    ts.AppendLine($"  async function invoke(name: string, ...args: unknown[]): Promise<{shortName}State> {{");
    ts.AppendLine("    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
    ts.AppendLine("    let reply: string;");
    ts.AppendLine("    try { reply = await bridge.call(name, ...args); }");
    ts.AppendLine("    catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The Bridge call could not complete.\"); }");
    ts.AppendLine("    if (lease.disposed || !isLive()) throw new BridgeError(\"disconnected\", \"This view was disposed. Reconnect for the current state.\");");
    ts.AppendLine("    return unpack(reply);");
    ts.AppendLine("  }");
    if (needsCheckedWriter)
    {
        ts.AppendLine("  async function invokeFieldWrite<T>(name: string, payload: string): Promise<FieldWriteReceipt<T>> {");
        ts.AppendLine("    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
        ts.AppendLine("    let json: string;");
        ts.AppendLine("    try { json = await bridge.call(name, payload); }");
        ts.AppendLine("    catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The Bridge call could not complete.\"); }");
        ts.AppendLine("    if (lease.disposed || !isLive()) throw new BridgeError(\"disconnected\", \"This view was disposed. Reconnect for the current state.\");");
        ts.AppendLine("    let reply: FieldWriteReply<T>;");
        ts.AppendLine("    try { reply = JSON.parse(json) as FieldWriteReply<T>; }");
        ts.AppendLine("    catch { throw new BridgeError(\"failed\", \"The Bridge returned an invalid response.\"); }");
        ts.AppendLine("    if (reply.state !== null) shared.accept(reply.state);");
        ts.AppendLine("    if (!reply.ok) throw new BridgeError(reply.error?.kind ?? \"failed\", reply.error?.message ?? \"The checked write failed.\");");
        ts.AppendLine("    if (reply.receipt === null || typeof reply.receipt.kind !== \"string\") throw new BridgeError(\"failed\", \"The checked write returned no receipt.\");");
        ts.AppendLine("    return reply.receipt;");
        ts.AppendLine("  }");
    }
    ts.AppendLine("  function operationKey(requestId: string): string { return `${contractId.length}:${contractId}${requestId.length}:${requestId}`; }");
    ts.AppendLine("  function reserveOperation(key: string): void {");
    ts.AppendLine("    const maximumRetainedOperations = 128;");
    ts.AppendLine("    while (runtime.operations.size >= maximumRetainedOperations) {");
    ts.AppendLine("      const candidate = [...runtime.operations.entries()].find(([, operation]) => operation.state === \"accepted\");");
    ts.AppendLine("      if (!candidate)");
    ts.AppendLine("        throw new BridgeOperationUncertainError(contractId, key, \"Pending operation identities are at capacity. Reconcile them before starting another operation.\");");
    ts.AppendLine("      runtime.operations.delete(candidate[0]);");
    ts.AppendLine("    }");
    ts.AppendLine("  }");
    ts.AppendLine("  function parseOperationStatus(json: string, requestId: string): BridgeOperationStatus {");
    ts.AppendLine("    let status: BridgeOperationStatus;");
    ts.AppendLine("    try { status = JSON.parse(json) as BridgeOperationStatus; }");
    ts.AppendLine("    catch { throw new BridgeError(\"failed\", \"The operation service returned an invalid response.\"); }");
    ts.AppendLine("    if (status.contract !== contractId || status.requestId !== requestId) throw new BridgeError(\"failed\", \"The operation service returned a mismatched identity.\");");
    ts.AppendLine("    if (!([\"running\", \"succeeded\", \"failed\", \"cancelled\", \"expired\", \"unknown\"] as const).includes(status.kind)) throw new BridgeError(\"failed\", \"The operation service returned an unknown status.\");");
    ts.AppendLine("    return status;");
    ts.AppendLine("  }");
    ts.AppendLine("  function operationFromInline(value: unknown, requestId: string): BridgeOperationStatus | undefined {");
    ts.AppendLine("    if (value === null || typeof value !== \"object\") return undefined;");
    ts.AppendLine("    const status = value as BridgeOperationStatus;");
    ts.AppendLine("    if (status.contract !== contractId || status.requestId !== requestId) throw new BridgeError(\"failed\", \"The operation admission returned a mismatched terminal identity.\");");
    ts.AppendLine("    if (!([\"succeeded\", \"failed\", \"cancelled\"] as const).includes(status.kind as \"succeeded\" | \"failed\" | \"cancelled\")) throw new BridgeError(\"failed\", \"The operation admission returned an invalid terminal result.\");");
    ts.AppendLine("    return status;");
    ts.AppendLine("  }");
    ts.AppendLine("  async function operationStatus(requestId: string, wait: boolean): Promise<BridgeOperationStatus> {");
    ts.AppendLine("    const identity = JSON.stringify({ contract: contractId, requestId });");
    ts.AppendLine("    let reply: string;");
    ts.AppendLine("    try { reply = await bridge.call(wait ? \"__runicOperationWait\" : \"__runicOperationStatus\", identity); }");
    ts.AppendLine("    catch { throw new BridgeOperationUncertainError(contractId, requestId, \"The operation status could not be observed.\"); }");
    ts.AppendLine("    return parseOperationStatus(reply, requestId);");
    ts.AppendLine("  }");
    ts.AppendLine("  async function recoverAdmission(requestId: string): Promise<BridgeOperationStatus> {");
    ts.AppendLine("    const status = await operationStatus(requestId, false);");
    ts.AppendLine("    if (status.kind === \"unknown\" || status.kind === \"expired\")");
    ts.AppendLine("      throw new BridgeOperationUncertainError(contractId, requestId, \"The admission reply was lost. Do not start the operation again automatically.\");");
    ts.AppendLine("    return status;");
    ts.AppendLine("  }");
    foreach (var command in commands.Where(command => !commandPlans[command].HasStringArgument && commandPlans[command].IsAsync))
    {
        var operationName = command.Name[..^"Command".Length];
        var methodName = LowerFirst(operationName);
        ts.AppendLine($"  function {methodName}Operation(requestId: string, terminal?: BridgeOperationStatus): {shortName}{operationName}Operation {{");
        ts.AppendLine("    const completion = terminal === undefined ? operationStatus(requestId, true) : Promise.resolve(terminal);");
        ts.AppendLine("    return {");
        ts.AppendLine("      requestId,");
        ts.AppendLine("      status: () => terminal === undefined ? operationStatus(requestId, false) : Promise.resolve(terminal),");
        ts.AppendLine("      completion,");
        ts.AppendLine("      wait: () => completion,");
        ts.AppendLine("      async cancel() {");
        ts.AppendLine("        const identity = JSON.stringify({ contract: contractId, requestId });");
        ts.AppendLine("        let reply: string;");
        ts.AppendLine("        try { reply = await bridge.call(\"__runicOperationCancel\", identity); }");
        ts.AppendLine("        catch { throw new BridgeOperationUncertainError(contractId, requestId, \"The cancellation request could not be observed.\"); }");
        ts.AppendLine("        let result: BridgeOperationCancelResult;");
        ts.AppendLine("        try { result = JSON.parse(reply) as BridgeOperationCancelResult; }");
        ts.AppendLine("        catch { throw new BridgeError(\"failed\", \"The cancellation service returned an invalid response.\"); }");
        ts.AppendLine("        if (result.contract !== contractId || result.requestId !== requestId || !([\"cancellation-requested\", \"not-running\", \"unknown\", \"expired\"] as const).includes(result.kind)) throw new BridgeError(\"failed\", \"The cancellation service returned a mismatched result.\");");
        ts.AppendLine("        return result;");
        ts.AppendLine("      },");
        ts.AppendLine("    };");
        ts.AppendLine("  }");
        ts.AppendLine($"  async function start{operationName}WithRequestId(requestId: string): Promise<{shortName}{operationName}Operation> {{");
        ts.AppendLine("    if (requestId.length === 0) throw new RangeError(\"Operation requestId is required.\");");
        ts.AppendLine("    const key = operationKey(requestId);");
        ts.AppendLine("    const prior = runtime.operations.get(key);");
        ts.AppendLine("    if (prior?.state === \"uncertain\") throw new BridgeOperationUncertainError(contractId, requestId, \"Admission is uncertain. Recover this request ID without issuing Start again.\");");
        ts.AppendLine($"    if (prior?.admission) return prior.admission as Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine("    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
        ts.AppendLine("    reserveOperation(key);");
        ts.AppendLine("    const tracked: SharedOperation = { contract: contractId, requestId, state: \"pending\", admission: undefined, handle: undefined };");
        ts.AppendLine("    runtime.operations.set(key, tracked);");
        ts.AppendLine("    const admission = (async () => {");
        ts.AppendLine("      let reply: string;");
        ts.AppendLine($"      try {{ reply = await bridge.call(`${{route}}Start{operationName}`, requestId); }}");
        ts.AppendLine("      catch { try { const recoveredStatus = await recoverAdmission(requestId); const recovered = " + methodName + "Operation(requestId, recoveredStatus.kind === \"succeeded\" || recoveredStatus.kind === \"failed\" || recoveredStatus.kind === \"cancelled\" ? recoveredStatus : undefined); tracked.state = \"accepted\"; tracked.handle = recovered; return recovered; } catch (error) { tracked.state = \"uncertain\"; throw error; } }");
        ts.AppendLine("      let result: { readonly contract?: string; readonly requestId?: string; readonly kind?: string; readonly status?: string; readonly reason?: string; readonly terminal?: unknown };");
        ts.AppendLine("      try { result = JSON.parse(reply) as { readonly contract?: string; readonly requestId?: string; readonly kind?: string; readonly status?: string; readonly reason?: string; readonly terminal?: unknown }; }");
        ts.AppendLine("      catch { tracked.state = \"uncertain\"; throw new BridgeOperationUncertainError(contractId, requestId, \"The operation admission returned invalid JSON. Recover this request ID without starting it again.\"); }");
        ts.AppendLine("      const requiresIdentity = result.kind === \"accepted\" || result.kind === \"duplicate\" || result.kind === \"expired\" || result.kind === \"unknown\";");
        ts.AppendLine("      if ((requiresIdentity && (result.contract !== contractId || result.requestId !== requestId)) || ((result.contract !== undefined || result.requestId !== undefined) && (result.contract !== contractId || result.requestId !== requestId))) { tracked.state = \"uncertain\"; throw new BridgeOperationUncertainError(contractId, requestId, \"The operation admission returned a mismatched identity. Recover this request ID without starting it again.\"); }");
        ts.AppendLine("      if (result.kind === \"accepted\" || result.kind === \"duplicate\") {");
        ts.AppendLine("        let terminal: BridgeOperationStatus | undefined; try { terminal = operationFromInline(result.terminal, requestId); } catch (error) { tracked.state = \"uncertain\"; throw error; } const handle = " + methodName + "Operation(requestId, terminal); tracked.state = \"accepted\"; tracked.handle = handle; return handle;");
        ts.AppendLine("      }");
        ts.AppendLine("      if (result.kind === \"expired\" || result.kind === \"unknown\")");
        ts.AppendLine("        { tracked.state = \"uncertain\"; throw new BridgeOperationUncertainError(contractId, requestId, \"The operation admission is no longer observable. Do not start it again automatically.\"); }");
        ts.AppendLine("      if (runtime.operations.get(key) === tracked) runtime.operations.delete(key);");
        ts.AppendLine("      throw new BridgeError(result.kind === \"disconnected\" ? \"disconnected\" : result.kind === \"cancelled\" ? \"cancelled\" : result.kind === \"rejected\" ? \"rejected\" : \"failed\", result.reason ?? \"The operation was not accepted.\");");
        ts.AppendLine("    })();");
        ts.AppendLine("    tracked.admission = admission;");
        ts.AppendLine("    return admission;");
        ts.AppendLine("  }");
        ts.AppendLine($"  async function recover{operationName}WithRequestId(requestId: string): Promise<{shortName}{operationName}Operation> {{");
        ts.AppendLine("    if (requestId.length === 0) throw new RangeError(\"Operation requestId is required.\");");
        ts.AppendLine("    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
        ts.AppendLine("    const status = await recoverAdmission(requestId);");
        ts.AppendLine("    const terminal = status.kind === \"succeeded\" || status.kind === \"failed\" || status.kind === \"cancelled\" ? status : undefined;");
        ts.AppendLine("    const handle = " + methodName + "Operation(requestId, terminal);");
        ts.AppendLine("    const key = operationKey(requestId);");
        ts.AppendLine("    runtime.operations.set(key, { contract: contractId, requestId, state: \"accepted\", admission: Promise.resolve(handle), handle });");
        ts.AppendLine("    return handle;");
        ts.AppendLine("  }");
    }
    ts.AppendLine("  function dispose(): void {");
    ts.AppendLine("    if (lease.disposed) return;");
    ts.AppendLine("    lease.disposed = true;");
    ts.AppendLine("    if (lease.mounted && lease.mountToken) void bridge.call(`${route}Unmount`, lease.mountToken).catch(() => {});");
    ts.AppendLine("    lease.listeners.clear();");
    ts.AppendLine("    shared.leases.delete(lease);");
    ts.AppendLine("    if (shared.leases.size !== 0 || routeEntry.entries.get(contractId) !== shared) return;");
    ts.AppendLine("    shared.active = false;");
    ts.AppendLine("    routeEntry.entries.delete(contractId);");
    ts.AppendLine("    if (routeEntry.entries.size !== 0) return;");
    ts.AppendLine("    routeEntry.active = false;");
    ts.AppendLine("    if (runtime.routes.get(route) === routeEntry) runtime.routes.delete(route);");
    ts.AppendLine("    const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("    if (callbacks[routeEntry.callbackName] === routeEntry.callback) callbacks[routeEntry.callbackName] = routeEntry.previousCallback;");
    ts.AppendLine("  }");
    ts.AppendLine("  try { await shared.initializing; }");
    ts.AppendLine("  catch (error) { dispose(); throw error; }");
    ts.AppendLine("  if (!isLive()) { dispose(); throw new BridgeError(\"disconnected\", \"The Bridge session changed during connection.\"); }");
    ts.AppendLine("  lease.current = shared.current;");
    ts.AppendLine("  if (mountToken) {");
    ts.AppendLine("    try {");
    ts.AppendLine("      lease.mounted = true;");
    ts.AppendLine("      const acknowledged = await bridge.call(`${route}Mount`, mountToken);");
    ts.AppendLine("      if (acknowledged !== \"ok\") throw new BridgeError(\"disconnected\", \"The View mount was not accepted.\");");
    ts.AppendLine("    } catch (cause) {");
    ts.AppendLine("      dispose();");
    ts.AppendLine("      throw cause instanceof BridgeError ? cause : new BridgeError(\"disconnected\", \"The View mount could not complete.\");");
    ts.AppendLine("    }");
    ts.AppendLine("  }");
    ts.AppendLine("  if (!isLive()) { dispose(); throw new BridgeError(\"disconnected\", \"The Bridge session changed during connection.\"); }");
    if (needsCheckedWriter)
    {
        ts.AppendLine($"  function fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): FieldBaseline<{shortName}CheckedFields[K]> {{");
        ts.AppendLine("    if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
        ts.AppendLine("    switch (field) {");
        foreach (var property in checkedProperties)
        {
            var wireName = LowerFirst(property.Name);
            ts.AppendLine($"      case \"{wireName}\": {{");
            ts.AppendLine($"        const version = (shared.wire as WireState | undefined)?.__runicFields?.{wireName}?.version;");
            ts.AppendLine("        if (typeof version !== \"number\" || !Number.isSafeInteger(version) || version < 0) throw new BridgeError(\"failed\", \"The checked field baseline is unavailable for this connection.\");");
            ts.AppendLine($"        return {{ value: (lease.current as {shortName}State).{wireName}, version }} as FieldBaseline<{shortName}CheckedFields[K]>;");
            ts.AppendLine("      }");
        }
        ts.AppendLine("    }");
        ts.AppendLine("    throw new BridgeError(\"failed\", \"The requested checked field is unavailable.\");");
        ts.AppendLine("  }");
    }
    ts.AppendLine("  return {");
    ts.AppendLine("    get snapshot() {");
    ts.AppendLine("      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
    ts.AppendLine($"      return lease.current as {shortName}State;");
    ts.AppendLine("    },");
    ts.AppendLine("    subscribe(listener) {");
    ts.AppendLine("      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
    ts.AppendLine($"      const typed = listener as (state: unknown) => void;");
    ts.AppendLine("      lease.listeners.add(typed);");
    ts.AppendLine("      typed(lease.current);");
    ts.AppendLine("      return () => lease.listeners.delete(typed);");
    ts.AppendLine("    },");
    ts.AppendLine("    dispose,");
    if (needsCheckedWriter)
    {
        ts.AppendLine("    fieldBaseline,");
    }
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
    {
        ts.AppendLine($"    async set{property.Name}(value) {{");
        if (property.PropertyType == typeof(int))
            ts.AppendLine("      if (!Number.isSafeInteger(value)) throw new RangeError(\"Value must be an integer.\");");
        var argument = IsNullableString(property) ? "JSON.stringify(value)" : "value";
        ts.AppendLine($"      return invoke(`${{route}}Set{property.Name}`, {argument});");
        ts.AppendLine("    },");
    }
    foreach (var property in checkedProperties)
    {
        ts.AppendLine($"    async write{property.Name}(value, options) {{");
        ts.AppendLine("      if (typeof options?.requestId !== \"string\" || options.requestId.trim().length === 0) throw new RangeError(\"A checked field requestId is required.\");");
        ts.AppendLine("      const baseline = options.baseline;");
        var check = property.PropertyType == typeof(int)
            ? "typeof value !== \"number\" || !Number.isSafeInteger(value) || !baseline || typeof baseline.value !== \"number\" || !Number.isSafeInteger(baseline.value)"
            : property.PropertyType == typeof(bool)
                ? "typeof value !== \"boolean\" || !baseline || typeof baseline.value !== \"boolean\""
                : IsNullableString(property)
                    ? "(value !== null && typeof value !== \"string\") || !baseline || (baseline.value !== null && typeof baseline.value !== \"string\")"
                    : "typeof value !== \"string\" || !baseline || typeof baseline.value !== \"string\"";
        ts.AppendLine($"      if ({check} || !Number.isSafeInteger(baseline.version) || baseline.version < 0) throw new RangeError(\"A checked field value and baseline are required.\");");
        ts.AppendLine($"      return invokeFieldWrite<{TsPropertyType(property)}>(`${{route}}Write{property.Name}`, JSON.stringify({{ requestId: options.requestId, expectedVersion: baseline.version, expectedValue: baseline.value, value }}));");
        ts.AppendLine("    },");
    }
    foreach (var command in commands)
    {
        var name = command.Name[..^"Command".Length];
        var hasStringArgument = commandPlans[command].HasStringArgument;
        ts.AppendLine($"    async {LowerFirst(name)}({(hasStringArgument ? "argument" : "")}) {{");
        ts.AppendLine($"      return invoke(`${{route}}{name}`{(hasStringArgument ? ", JSON.stringify(argument)" : "")});");
        ts.AppendLine("    },");
    }
    foreach (var command in commands.Where(command => !commandPlans[command].HasStringArgument && commandPlans[command].IsAsync))
    {
        var operationName = command.Name[..^"Command".Length];
        ts.AppendLine($"    start{operationName}() {{");
        ts.AppendLine($"      return start{operationName}WithRequestId(globalThis.crypto.randomUUID());");
        ts.AppendLine("    },");
        ts.AppendLine($"    start{operationName}WithRequestId(requestId) {{");
        ts.AppendLine($"      return start{operationName}WithRequestId(requestId);");
        ts.AppendLine("    },");
        ts.AppendLine($"    recover{operationName}WithRequestId(requestId) {{");
        ts.AppendLine($"      return recover{operationName}WithRequestId(requestId);");
        ts.AppendLine("    },");
    }
    ts.AppendLine("  };");
    ts.AppendLine("}");

    WriteIfChanged(csharpPath, cs.ToString());
    WriteIfChanged(typescriptPath, ts.ToString());
    Console.WriteLine($"Generated {shortName} bridge from compiled {model.Name}: {properties.Length} properties, {commands.Length} commands.");

    string TsPropertyType(PropertyInfo property) =>
        TsType(property.PropertyType) + (IsNullableString(property) ? " | null" : "");

    bool IsNullableString(PropertyInfo property) =>
        property.PropertyType == typeof(string)
        && nullability.Create(property).ReadState == NullabilityState.Nullable;

    static string WriterMethod(Type type) => type == typeof(int) ? "WriteNumber"
        : type == typeof(bool) ? "WriteBoolean" : "WriteString";

    static PropertyInfo[] ListMembers(Type item) =>
        item.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
            .Where(member => member.GetIndexParameters().Length == 0)
            .Select(member =>
            {
                if (member.GetMethod is null || member.PropertyType != typeof(int)
                    && member.PropertyType != typeof(string) && member.PropertyType != typeof(bool))
                    throw new NotSupportedException($"{item.Name}.{member.Name}: only readable int, string, and bool record fields are supported.");
                return member;
            }).ToArray();
}

static string LowerFirst(string text) => char.ToLowerInvariant(text[0]) + text[1..];

static bool TryListItem(Type type, out Type? item)
{
    item = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        ? type.GenericTypeArguments[0] : null;
    return item is { IsClass: true, IsPublic: true } && item != typeof(string);
}

static string TsType(Type type)
{
    if (type == typeof(int)) return "number";
    if (type == typeof(string)) return "string";
    if (type == typeof(bool)) return "boolean";
    if (TryListItem(type, out var item)) return $"readonly {item!.Name}[]";
    throw new NotSupportedException($"Unsupported TypeScript member type: {type.FullName}");
}

static void WriteIfChanged(string path, string content)
{
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    if (File.Exists(fullPath) && File.ReadAllText(fullPath) == content) return;
    File.WriteAllText(fullPath, content);
}

sealed class AotValidationException(string message) : Exception(message);
