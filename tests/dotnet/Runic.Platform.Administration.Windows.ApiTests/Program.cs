using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Runic.Platform.Administration.Windows;

// Managed-only contract snapshot: reflection is deliberately outside the NativeAOT consumer.
var assembly = typeof(WindowsAdministrationException).Assembly;
var lines = new List<string> { "# Public signatures, constants, parameter defaults and init accessors. Review intentional changes." };
const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
foreach (var type in assembly.ExportedTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
{
    lines.Add($"type {type.FullName} : {type.BaseType} abstract={type.IsAbstract} sealed={type.IsSealed}");
    lines.AddRange(type.GetInterfaces().Select(type => "  interface " + type).Order(StringComparer.Ordinal));
    foreach (var member in type.GetMembers(flags).OrderBy(Describe, StringComparer.Ordinal))
    {
        if (member is Type || member is MethodInfo { IsSpecialName: true }) continue;
        lines.Add("  " + Describe(member));
    }
}
var actual = string.Join('\n', lines) + "\n";
var directory = new DirectoryInfo(AppContext.BaseDirectory);
while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) directory = directory.Parent;
if (directory is null) throw new InvalidOperationException("Run this verification from its repository build output.");
var path = Path.Combine(directory.FullName, "tests/dotnet/Runic.Platform.Administration.Windows.ApiTests/PublicApi.txt");
if (args is ["--write-baseline"]) File.WriteAllText(path, actual);
else if (args.Length != 0) throw new ArgumentException("Only --write-baseline is supported.");
else if (File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) != actual)
    throw new InvalidOperationException("Public API changed. Review the signatures before regenerating PublicApi.txt.");
Console.WriteLine($"PASS Administration public API: {assembly.ExportedTypes.Count()} exported types.");

static string Describe(MemberInfo member) => member switch
{
    ConstructorInfo constructor => $"ctor ({Parameters(constructor.GetParameters())})",
    MethodInfo method => $"method {(method.IsStatic ? "static " : "")}{method.ReturnType} {method.Name}({Parameters(method.GetParameters())})",
    PropertyInfo property => $"property {property.PropertyType} {property.Name} {{ {(property.GetMethod?.IsPublic == true ? "get; " : "")}{Setter(property)} }}",
    FieldInfo field => $"field {field.FieldType} {field.Name}" + (field.IsLiteral ? " = " + Constant(field.GetRawConstantValue()) : ""),
    EventInfo item => $"event {item.EventHandlerType} {item.Name}",
    _ => member.ToString() ?? member.Name
};

static string Setter(PropertyInfo property) =>
    property.SetMethod?.IsPublic != true ? "" :
    property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init;" : "set;";

static string Parameters(ParameterInfo[] values) => string.Join(", ", values.Select(parameter =>
    $"{(parameter.IsOut ? "out " : parameter.IsIn ? "in " : "")}{parameter.ParameterType} {parameter.Name}" +
    (parameter.HasDefaultValue ? " = " + Constant(parameter.DefaultValue) : "")));

static string Constant(object? value) => value switch
{
    null => "null",
    string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString() ?? ""
};
