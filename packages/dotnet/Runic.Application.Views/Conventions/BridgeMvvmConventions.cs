using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

// The MVVM concepts the Bridge contract understands, independent of a library:
// a framework base type (its members are not DTO members), a typed command
// with an input and a result, a command with only an input, an asynchronous
// command, an interaction, and the attributed method a library generates a
// command from. ICommand, INotifyPropertyChanged, INotifyDataErrorInfo and the
// Runic attributes need no convention.
//
// Each supported library contributes one descriptor in its own file. They are
// metadata names, not references: this package depends on neither library.
// They stay in this package, rather than being registered by an adapter at run
// time, because the contract fingerprint must be identical in the generator
// and in a running application whichever adapter assemblies have loaded, and
// because CommunityToolkit needs no Runic adapter package at all (D017).
internal sealed record BridgeMvvmConvention(
    string Library,
    // Exact full names of base classes that end a DTO's public hierarchy.
    string[] FrameworkBaseTypes,
    // Root namespaces whose types are never application content.
    string[] FrameworkNamespaces,
    // Generic definitions closed over <TInput, TResult>.
    string[] TypedCommandContracts,
    // Generic definitions closed over <TInput>.
    string[] InputCommandContracts,
    // Non-generic types that mark a command as asynchronous.
    string[] AsyncCommandTypes,
    // Generic definitions closed over <TInput, TOutput>.
    string[] InteractionContracts,
    // The attribute on a method the library turns into a command property,
    // and the command base name the library derives from that method's name.
    string? CommandSourceAttribute = null,
    Func<string, string>? CommandSourceName = null);

[RequiresUnreferencedCode("Reflects over the interfaces of arbitrary model member types; used only by code generation and Hot Reload.")]
internal static class BridgeMvvmConventions
{
    // ReactiveUI is the primary MVVM path and CommunityToolkit is supported
    // (D018.9). Their names do not overlap, so the order is not significant.
    internal static readonly BridgeMvvmConvention[] All = [ReactiveUIConventions.Descriptor, CommunityToolkitConventions.Descriptor];

    private static readonly HashSet<string> FrameworkBaseTypes = Names(convention => convention.FrameworkBaseTypes);
    private static readonly HashSet<string> TypedCommandContracts = Names(convention => convention.TypedCommandContracts);
    private static readonly HashSet<string> InputCommandContracts = Names(convention => convention.InputCommandContracts);
    private static readonly HashSet<string> AsyncCommandTypes = Names(convention => convention.AsyncCommandTypes);
    private static readonly HashSet<string> InteractionContracts = Names(convention => convention.InteractionContracts);

    internal static bool IsFrameworkBase(Type type) => type.FullName is { } name && FrameworkBaseTypes.Contains(name);

    internal static bool IsFrameworkNamespace(string ns) => All.SelectMany(convention => convention.FrameworkNamespaces)
        .Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal));

    /// <summary>The <c>&lt;TInput, TResult&gt;</c> command contract <paramref name="type"/> implements, or null.</summary>
    internal static Type? TypedCommand(Type type) => GenericContract(type, TypedCommandContracts);

    /// <summary>The <c>&lt;TInput&gt;</c> command contract <paramref name="type"/> implements, or null.</summary>
    internal static Type? InputCommand(Type type) => GenericContract(type, InputCommandContracts);

    /// <summary>The <c>&lt;TInput, TOutput&gt;</c> interaction contract <paramref name="type"/> implements, or null.</summary>
    internal static Type? Interaction(Type type) => GenericContract(type, InteractionContracts);

    internal static bool IsAsyncCommand(Type type) =>
        type.GetInterfaces().Append(type).Any(candidate => candidate.FullName is { } name && AsyncCommandTypes.Contains(name));

    /// <summary>The convention whose command-source attribute is <paramref name="attribute"/>, or null.</summary>
    internal static BridgeMvvmConvention? CommandSource(Type attribute) =>
        All.FirstOrDefault(convention => convention.CommandSourceAttribute == attribute.FullName);

    private static Type? GenericContract(Type type, HashSet<string> names) => type.GetInterfaces().Append(type)
        .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition().FullName is { } name && names.Contains(name));

    private static HashSet<string> Names(Func<BridgeMvvmConvention, string[]> select) =>
        All.SelectMany(select).ToHashSet(StringComparer.Ordinal);
}
