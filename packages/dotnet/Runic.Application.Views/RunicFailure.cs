using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("BridgeCodegen")]

namespace Runic.Application.Views;

/// <summary>
/// Declares the expected failure type of a bridged command. Put it on the command
/// property or on a CommunityToolkit <c>[RelayCommand]</c> method. The command signals
/// the failure by throwing <see cref="RunicFailureException"/> with a value of this
/// type; the generated client receives it as a typed failure instead of an error.
/// </summary>
/// <remarks>
/// A command declares one failure type. Several cases use <c>[RunicUnion]</c>.
/// Synchronous commands report only failures thrown synchronously from
/// <c>Execute</c>; operations exist only for asynchronous Toolkit and ReactiveUI commands.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RunicFailureAttribute(Type failure) : Attribute
{
    /// <summary>The CLR type of the command's declared failure.</summary>
    public Type Failure { get; } = failure ?? throw new ArgumentNullException(nameof(failure));
}

/// <summary>
/// Signals a declared failure from a command that has <see cref="RunicFailureAttribute"/>.
/// The Bridge sends <see cref="Failure"/> to the client as a <c>domain-failed</c> reply.
/// A value that is not the declared type is reported as an unexpected failure.
/// </summary>
public sealed class RunicFailureException : Exception
{
    /// <summary>Creates the exception for <paramref name="failure"/>.</summary>
    public RunicFailureException(object failure)
        : this(failure, null, null)
    {
    }

    /// <summary>Creates the exception for <paramref name="failure"/> with a log message.</summary>
    public RunicFailureException(object failure, string? message)
        : this(failure, message, null)
    {
    }

    /// <summary>Creates the exception for <paramref name="failure"/> with a log message and the exception that caused it.</summary>
    public RunicFailureException(object failure, string? message, Exception? innerException)
        : base(message ?? $"The command failed with {failure?.GetType().Name ?? "a failure"}.", innerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>The failure value, an instance of the command's declared failure type.</summary>
    public object Failure { get; }
}

// Thrown by the operation observer to the registry after a declared failure
// was encoded. The registry stays unaware of RunicFailureException.
internal sealed class BridgeDomainFailedException(string encodedJson, string failureType, Exception original)
    : Exception("The operation failed with its declared failure.", original)
{
    public string EncodedJson { get; } = encodedJson;
    public string FailureType { get; } = failureType;
}

// Thrown by the operation observer when a RunicFailureException could not be
// sent as the declared failure. The registry reports it as failed and logs 1008.
internal sealed class BridgeDomainFailureNotEncodedException(string failureType, string reason, Exception original)
    : Exception("The operation's failure could not be sent as its declared failure.", original)
{
    public string FailureType { get; } = failureType;
    public string Reason { get; } = reason;
}

internal static class BridgeDomainFailures
{
    internal const int MaximumEncodedBytes = 4096;

    // A single-inner AggregateException (for example from Task.Wait or a
    // Task.WhenAll of one task) carries the command's own exception.
    internal static RunicFailureException? Find(Exception error)
    {
        while (error is AggregateException { InnerExceptions.Count: 1 } aggregate)
            error = aggregate.InnerExceptions[0];
        return error as RunicFailureException;
    }

    internal static string TypeName(RunicFailureException failure) =>
        failure.Failure.GetType().FullName ?? failure.Failure.GetType().Name;

    // Returns the encoded failure, or null with the reason it falls back to failed.
    internal static string? TryEncode(Func<object, string?>? encode, RunicFailureException failure, out string reason)
    {
        if (encode is null)
        {
            reason = "the command declares no failure type";
            return null;
        }
        string? json;
        try { json = encode(failure.Failure); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = $"the failure could not be encoded ({error.GetType().Name})";
            return null;
        }
        if (json is null)
        {
            reason = "the failure is not the declared type";
            return null;
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
        {
            reason = $"the encoded failure exceeds {MaximumEncodedBytes} bytes";
            return null;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            reason = "the failure encoder wrote invalid JSON";
            return null;
        }
        reason = "";
        return json;
    }
}

// Where a command declares its failure: CommunityToolkit generates SaveCommand
// from [RelayCommand] Save, SaveAsync or OnSave, and the declaration may sit on
// that method. Shared by the code generator and BridgeContractShape.
internal static class BridgeCommandSources
{
    [RequiresUnreferencedCode("Reflects over the non-public methods of a ViewModel; used only by code generation and Hot Reload.")]
    internal static MethodInfo? ToolkitSourceMethod(Type model, PropertyInfo command)
    {
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal)) return null;
        var name = command.Name[..^"Command".Length];
        return (command.DeclaringType ?? model).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "CommunityToolkit.Mvvm.Input.RelayCommandAttribute"))
            .OrderBy(method => method.MetadataToken)
            .FirstOrDefault(method =>
            {
                var candidate = method.Name;
                if (candidate.Length > 2 && candidate.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(candidate[2]))
                    candidate = candidate[2..];
                if (candidate.Length > "Async".Length && candidate.EndsWith("Async", StringComparison.Ordinal))
                    candidate = candidate[..^"Async".Length];
                return candidate == name;
            });
    }

    // The failure declarations of a command: on the property, then on its
    // [RelayCommand] method. More than one is a codegen diagnostic.
    [RequiresUnreferencedCode("Reflects over the non-public methods of a ViewModel; used only by code generation and Hot Reload.")]
    internal static IReadOnlyList<(string DeclaredOn, Type Failure)> FailureDeclarations(Type model, PropertyInfo command)
    {
        var declarations = new List<(string, Type)>();
        foreach (var attribute in command.GetCustomAttributes<RunicFailureAttribute>(true))
            declarations.Add(("property", attribute.Failure));
        if (ToolkitSourceMethod(model, command) is { } method)
            foreach (var attribute in method.GetCustomAttributes<RunicFailureAttribute>(true))
                declarations.Add(("method", attribute.Failure));
        return declarations;
    }
}
