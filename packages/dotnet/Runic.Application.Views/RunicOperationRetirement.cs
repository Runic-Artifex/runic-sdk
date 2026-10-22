namespace Runic.Application.Views;

/// <summary>
/// What happens to a running operation when the navigation entry whose content started it retires. Declare it with
/// <see cref="RunicOperationRetirementAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// It applies to operations, the asynchronous commands that a generated client starts with <c>start…</c>, of content
/// that a <c>RunicNavigator</c> owns (an <c>Own</c>, <c>Create</c> or <c>Share</c> target) and of the ViewModels
/// presented under that content when the operation started. Retiring is final: Back, Replace, Reset or Clear removed
/// the entry. A retained entry, one that a push covers, keeps its operations whatever the policy. Borrowed content is
/// never retired by the navigator, so the policy has no effect there.
/// </para>
/// <para>This set is closed: it gains values only in a major release.</para>
/// </remarks>
public enum RunicOperationRetirement
{
    /// <summary>
    /// The operation keeps running after the entry retired, and the navigator disposes the content at once. Its status
    /// stays observable from the Window, like any operation. The default.
    /// </summary>
    Keep = 0,

    /// <summary>
    /// The operation's cancellation token is cancelled when the entry retires, as by the client's <c>cancel()</c>, and the
    /// navigator disposes the content, its entry scope and its model-context lease only after the operation has ended.
    /// An operation that completes despite the request still succeeds.
    /// </summary>
    Cancel = 1,

    /// <summary>
    /// The operation keeps running after the entry retired, and the navigator disposes the content, its entry scope and
    /// its model-context lease only after the operation has ended.
    /// </summary>
    Drain = 2,
}

/// <summary>
/// Declares the <see cref="RunicOperationRetirement"/> of operations: on a command property for that command, or on a
/// ViewModel class for each of its operations. A declaration on the property wins over the class's.
/// </summary>
/// <remarks>
/// For a CommunityToolkit <c>[RelayCommand]</c> method, forward it to the generated property with
/// <c>[property: RunicOperationRetirement(RunicOperationRetirement.Cancel)]</c>. On a member that has no operation, such
/// as a synchronous command or a state property, it has no effect and the Bridge generator warns (RUNICBRIDGE028). The
/// policy is .NET-side only: the generated TypeScript, contract hash and mocks do not change.
/// </remarks>
/// <param name="retirement">What happens to the operation when its entry retires.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RunicOperationRetirementAttribute(RunicOperationRetirement retirement) : Attribute
{
    /// <summary>Gets what happens to the operation when its entry retires.</summary>
    public RunicOperationRetirement Retirement { get; } = retirement;
}
