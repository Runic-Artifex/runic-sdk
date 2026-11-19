namespace Runic.Application.Views;

// sdk#1146: the lifetime of a bridged ReactiveUI Interaction, which the Bridge generator passes to
// ReactiveInteractionDescriptor.Create. A ViewQuestion takes its own in its constructor.

/// <summary>
/// Sets how long a page may take to answer a ReactiveUI <c>Interaction</c> that a Bridge exposes, or that it has no
/// deadline.
/// </summary>
/// <remarks>
/// <para>
/// Without it, a page has two minutes. When the time passes, the page's handler is aborted (its <c>signal</c>) and
/// <c>Interaction.Handle</c> throws <see cref="RunicInteractionTimeoutException"/>. Use
/// <see cref="System.Threading.Timeout.Infinite"/> for a question a person answers, such as a modal confirmation: the
/// question still ends when the asking command is cancelled, when its page unmounts or disconnects and when the Window
/// closes, and then <c>Handle</c> throws <see cref="RunicInteractionCancelledException"/>.
/// </para>
/// <code>
/// [RunicInteractionTimeout(Timeout.Infinite)]
/// public Interaction&lt;ConfirmationRequest, bool&gt; ConfirmAction { get; } = new();
///
/// [RunicInteractionTimeout(15 * 60 * 1000)] // fifteen minutes
/// public Interaction&lt;RenameRequest, string?&gt; Rename { get; } = new();
/// </code>
/// <para>
/// It applies to the property of a ReactiveUI <c>Interaction</c> (<c>IInteraction&lt;TInput, TOutput&gt;</c>). On a
/// <see cref="ViewQuestion{TInput, TOutput}"/> the Bridge generator reports RUNICBRIDGE035, because a view question
/// takes its timeout in its constructor; on another member it has no effect. A value that is neither
/// <see cref="System.Threading.Timeout.Infinite"/> nor between 1 and <see cref="MaximumMilliseconds"/> is reported as
/// RUNICBRIDGE011. The lifetime is .NET-side only: the generated TypeScript and contract hash do not change.
/// </para>
/// <para>
/// A page of <c>@runic-artifex/views</c> before 0.23 still aborts its handler after ten minutes, so with such a page a
/// longer question ends then with <see cref="RunicInteractionCancelledException"/>.
/// </para>
/// </remarks>
/// <param name="milliseconds">
/// How long a page may take to answer, in milliseconds, from 1 to <see cref="MaximumMilliseconds"/> (24 hours), or
/// <see cref="System.Threading.Timeout.Infinite"/> for no deadline.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RunicInteractionTimeoutAttribute(int milliseconds) : Attribute
{
    /// <summary>The longest finite timeout, in milliseconds: 24 hours.</summary>
    public const int MaximumMilliseconds = 86_400_000;

    /// <summary>Gets the timeout in milliseconds, or <see cref="System.Threading.Timeout.Infinite"/> for none.</summary>
    public int Milliseconds { get; } = milliseconds;

    /// <summary>
    /// Gets the timeout, <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for none, as
    /// <c>ReactiveInteractionDescriptor.Create</c> and <see cref="ViewQuestion{TInput, TOutput}"/> take it.
    /// </summary>
    public TimeSpan Timeout => Milliseconds == System.Threading.Timeout.Infinite
        ? System.Threading.Timeout.InfiniteTimeSpan
        : TimeSpan.FromMilliseconds(Milliseconds);
}

// The lifetime rules of an interaction request, shared by the router and ViewQuestion; ReactiveInteractionDescriptor
// repeats them (it has no access to internals).
internal static class InteractionLifetime
{
    internal static readonly TimeSpan Default = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(RunicInteractionTimeoutAttribute.MaximumMilliseconds);

    internal const string RangeMessage =
        "An interaction timeout must be positive and at most 24 hours, or Timeout.InfiniteTimeSpan for none.";

    // Whether the timeout is valid: null (the default), Timeout.InfiniteTimeSpan or a positive time of at most a day.
    internal static bool IsValid(TimeSpan? timeout) =>
        timeout is not { } value || value == Timeout.InfiniteTimeSpan || (value > TimeSpan.Zero && value <= Maximum);

    internal static TimeSpan Resolve(TimeSpan? timeout, string parameterName)
    {
        if (!IsValid(timeout)) throw new ArgumentOutOfRangeException(parameterName, timeout, RangeMessage);
        return timeout ?? Default;
    }
}
