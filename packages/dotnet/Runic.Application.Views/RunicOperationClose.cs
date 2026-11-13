using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

// sdk#900, W4a: the close drain's building blocks in Views (window-api-0.19/DESIGN.md §4.1–§4.2; D064.14–D064.20),
// experimental under RUNICDESK105 for one minor (D064.20).

/// <summary>
/// What a Window's close does to a running operation of the Window. Declare it with
/// <see cref="RunicOperationCloseAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// It applies to operations, the asynchronous commands that a generated client starts with <c>start…</c>, when their
/// Window closes: a close the application makes with <see cref="IBridgeWindow.CloseAsync"/>, a decided user close, the
/// Window's disposal and the host's shutdown. An operation without a declaration behaves as before 0.19, under every
/// <see cref="RunicWindowOptions.CloseDrain"/>.
/// </para>
/// <para>
/// It is independent of <see cref="RunicOperationRetirement"/>, which applies when a navigation entry retires:
/// <see cref="RunicOperationRetirement.Cancel"/> still cancels a <see cref="Durable"/> operation when its entry retires,
/// because that is the content's departure, not the Window's. The client's <c>cancel()</c> still cancels any operation.
/// </para>
/// <para>This set is closed: it gains values only in a major release.</para>
/// <para>Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicOperationClose
{
    /// <summary>
    /// The close waits for the operation up to its timeout, then asks it to cancel, and the host keeps the Window's scope
    /// until it ended. The default, and the behaviour of every operation before 0.19.
    /// </summary>
    Default = 0,

    /// <summary>
    /// A read-only operation, such as a query, refresh or preview: it is asked to cancel as soon as the close is decided.
    /// The close still waits for it to end, within its timeout.
    /// </summary>
    Cancel = 1,

    /// <summary>
    /// Durable work, such as a change to an external system that must not stop halfway: neither a Window's close nor the
    /// host's shutdown asks it to cancel. The close waits for it within its timeout or drain; after that the visible
    /// window may close, and the Window's scope stays until the operation ended (<see cref="RunicWindowCloseResult.RemainingDurableWork"/>).
    /// </summary>
    Durable = 2,
}

/// <summary>
/// Declares the <see cref="RunicOperationClose"/> of operations: on a command property for that command, or on a
/// ViewModel class for each of its operations. A declaration on the property wins over the class's.
/// </summary>
/// <remarks>
/// <para>
/// For a CommunityToolkit <c>[RelayCommand]</c> method, forward it to the generated property with
/// <c>[property: RunicOperationClose(RunicOperationClose.Durable)]</c>. On a member that has no operation, such as a
/// synchronous command or a state property, it has no effect and the Bridge generator warns (RUNICBRIDGE038). The policy
/// is .NET-side only: the generated TypeScript, contract hash and mocks do not change.
/// </para>
/// <para>Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.</para>
/// </remarks>
/// <param name="close">What a Window's close does to the operation.</param>
[Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RunicOperationCloseAttribute(RunicOperationClose close) : Attribute
{
    /// <summary>Gets what a Window's close does to the operation.</summary>
    public RunicOperationClose Close { get; } = close;
}

/// <summary>
/// How long a Window stays visible after its close was decided, while its accepted work drains: its operations and
/// awaited commands, and the <see cref="AcceptedWorkScope"/>s it drains (<see cref="IRunicWindowContext.DrainOnClose"/>).
/// Set it with <see cref="RunicWindowOptions.CloseDrain"/>.
/// </summary>
/// <remarks>
/// <para>
/// With a drain, a decided close (the user's close once the Window's guards agreed, or
/// <see cref="IRunicWindowContext.RequestCloseAsync"/>) leaves the window visible and its page connected. Operation
/// admission stops, the pages receive <c>closing</c> with <c>draining: true</c> and keep receiving the Window's state,
/// and operations declared <see cref="RunicOperationClose.Cancel"/> are asked to cancel. The window closes by itself when
/// the work has ended or the drain's timeout passed. A second user close meanwhile is ignored. The drain starts only
/// after every guard agreed, so a guard that keeps the Window open changes nothing.
/// </para>
/// <para>
/// No drain asks <see cref="RunicOperationClose.Durable"/> work to cancel. Operations without a declaration behave as
/// without a drain: they are waited for, and at a timeout asked to cancel.
/// </para>
/// <para>
/// 0.19 drains in <c>RunicWindowTestHost</c>; Runic Desktop's embedded windows follow in the next part of sdk#900.
/// Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.
/// </para>
/// </remarks>
[Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicCloseDrain
{
    private RunicCloseDrain(TimeSpan timeout) => Timeout = timeout;

    /// <summary>No drain: the visible window closes as soon as its close is decided, as before 0.19.</summary>
    public static RunicCloseDrain None { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// The window stays visible until its accepted work has ended, without a timeout. Only the application's own
    /// <see cref="IBridgeWindow.CloseAsync"/> or disposal, or the host's shutdown, ends it earlier.
    /// </summary>
    public static RunicCloseDrain UntilDrained { get; } = new(System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>
    /// The window stays visible until its accepted work has ended or <paramref name="timeout"/> passed. Then operations
    /// without a declaration are asked to cancel and the visible window closes; durable work keeps the Window's scope.
    /// </summary>
    /// <param name="timeout">A positive time, <see cref="TimeSpan.Zero"/> (as <see cref="None"/>) or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> (as <see cref="UntilDrained"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not infinite.</exception>
    public static RunicCloseDrain For(TimeSpan timeout)
    {
        if (timeout == TimeSpan.Zero) return None;
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan) return UntilDrained;
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return new(timeout);
    }

    /// <summary>
    /// The longest the window stays visible: <see cref="TimeSpan.Zero"/> for <see cref="None"/>, a positive time, or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for <see cref="UntilDrained"/>.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>Whether the window stays visible after a decided close: every drain but <see cref="None"/>.</summary>
    public bool KeepsWindow => Timeout != TimeSpan.Zero;
}

// The registration of IRunicWindowContext.DrainOnClose's default implementation, which drains nothing.
internal static class RunicCloseDrains
{
    internal static IDisposable None { get; } = new NoDrain();

    private sealed class NoDrain : IDisposable
    {
        public void Dispose() { }
    }
}
