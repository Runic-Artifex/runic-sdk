using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>One context menu the application showed and the item chosen.</summary>
/// <param name="Items">The menu's entries.</param>
/// <param name="Options">Where it opened, or <see langword="null"/> at the pointer.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Chosen">The chosen item, or <see langword="null"/>.</param>
/// <param name="Scripted">Whether a scripted choice ended it; <see langword="false"/> when the queue was empty.</param>
public sealed record RunicContextMenuCall(ImmutableArray<DesktopMenuEntry> Items, DesktopContextMenuOptions? Options,
    RunicTestOutcome Outcome, DesktopMenuItem? Chosen, bool Scripted);

/// <summary>
/// Scripted context menus for <see cref="IWindowPlatformServices.ShowContextMenuAsync"/> in tests: each menu takes the
/// next queued choice and is recorded.
/// </summary>
/// <remarks>
/// Queue the item the user chooses, by its command id, with <see cref="EnqueueChoice"/>. A menu shown with an empty
/// queue is dismissed. Choosing an item the menu does not have, or one that is disabled or in a disabled submenu,
/// throws <see cref="InvalidOperationException"/> from the call, since the user could not choose it. Menus are
/// checked with the rules of the real ones. The type is safe to use from any thread.
/// </remarks>
public sealed class RunicTestContextMenus
{
    private static readonly IWindowPlatformServices s_checks = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private readonly object _gate = new();
    private readonly Queue<Choice> _choices = new();
    private readonly List<RunicContextMenuCall> _calls = [];
    private readonly RunicTestPlatform _platform;

    internal RunicTestContextMenus(RunicTestPlatform platform) => _platform = platform;

    /// <summary>The menus shown so far, oldest first.</summary>
    public IReadOnlyList<RunicContextMenuCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>When set, every menu reports <see cref="PlatformResult{T}.Unavailable"/> with this reason, as a window without native context menus does.</summary>
    public PlatformUnavailableReason? UnavailableReason { get; set; }

    /// <summary>Queues the item the user chooses in the next menu.</summary>
    /// <param name="commandId">The item's <see cref="DesktopMenuItem.CommandId"/>.</param>
    /// <param name="commandParameter">The item's <see cref="DesktopMenuItem.CommandParameter"/> when several items share the command id; <see langword="null"/> matches the first.</param>
    public void EnqueueChoice(string commandId, string? commandParameter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        lock (_gate) _choices.Enqueue(new Choice(commandId, commandParameter));
    }

    /// <summary>Queues a dismissal of the next menu.</summary>
    public void EnqueueDismissed()
    {
        lock (_gate) _choices.Enqueue(new Choice(null, null));
    }

    internal async ValueTask<PlatformResult<DesktopMenuItem?>> ShowAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options, CancellationToken cancellationToken)
    {
        await s_checks.ShowContextMenuAsync(items, options, cancellationToken).ConfigureAwait(false);
        if ((_platform.IsClosed ? PlatformUnavailableReason.OwnerClosed : UnavailableReason) is { } reason)
            return Record(items, options, RunicTestOutcome.Unavailable, null, false, new PlatformResult<DesktopMenuItem?>.Unavailable(reason));
        _platform.Pump();
        bool scripted;
        Choice? choice;
        lock (_gate) scripted = _choices.TryDequeue(out choice);
        if (choice?.CommandId is not { } commandId)
            return Record(items, options, RunicTestOutcome.Dismissed, null, scripted, new PlatformResult<DesktopMenuItem?>.Success(null));
        var chosen = Find(items, commandId, choice.CommandParameter, enabled: true, out var disabled);
        if (chosen is null)
        {
            throw new InvalidOperationException(disabled
                ? $"The context menu item '{commandId}' is disabled, so the user cannot choose it."
                : $"The context menu has no item with the command id '{commandId}'" +
                  (choice.CommandParameter is null ? "." : $" and the parameter '{choice.CommandParameter}'."));
        }
        return Record(items, options, RunicTestOutcome.Selected, chosen, scripted, new PlatformResult<DesktopMenuItem?>.Success(chosen));
    }

    internal void EnqueueScripted(string operation, IReadOnlyDictionary<string, string?> values)
    {
        switch (operation)
        {
            case "choose": EnqueueChoice(RunicTestFileDialogs.Required(values, "commandId"), values.GetValueOrDefault("commandParameter")); break;
            case "dismissed": EnqueueDismissed(); break;
            default: throw new InvalidOperationException($"Unknown scripted context menu operation '{operation}'.");
        }
    }

    private static DesktopMenuItem? Find(ImmutableArray<DesktopMenuEntry> entries, string commandId, string? parameter, bool enabled, out bool disabled)
    {
        disabled = false;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DesktopMenuItem item when item.CommandId == commandId && (parameter is null || item.CommandParameter == parameter):
                    if (enabled && item.IsEnabled) return item;
                    disabled = true;
                    break;
                case DesktopMenu menu:
                    var found = Find(menu.Items, commandId, parameter, enabled && menu.IsEnabled, out var nestedDisabled);
                    if (found is not null) return found;
                    disabled |= nestedDisabled;
                    break;
            }
        }
        return null;
    }

    private PlatformResult<DesktopMenuItem?> Record(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions? options,
        RunicTestOutcome outcome, DesktopMenuItem? chosen, bool scripted, PlatformResult<DesktopMenuItem?> result)
    {
        lock (_gate) _calls.Add(new RunicContextMenuCall(items, options, outcome, chosen, scripted));
        _platform.Record("contextMenus", "show", [
            ("outcome", outcome.ToString()), ("commandId", chosen?.CommandId), ("commandParameter", chosen?.CommandParameter),
        ]);
        return result;
    }

    private sealed record Choice(string? CommandId, string? CommandParameter);
}
