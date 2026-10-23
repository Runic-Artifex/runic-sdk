using System.Threading.Channels;

// What the probe needs from a platform accessibility API: the calls a screen reader makes to read the page and to
// act on it, and the events it listens to.
internal abstract class AccessibilityClient : IAsyncDisposable
{
    private readonly Channel<AccessibilityEvent> _events = Channel.CreateUnbounded<AccessibilityEvent>();

    /// <summary>The API's name in the receipt, e.g. "AT-SPI".</summary>
    internal abstract string Api { get; }

    internal ChannelReader<AccessibilityEvent> Events => _events.Reader;

    protected void Raise(AccessibilityEvent value)
    {
        Console.WriteLine($"  event {value.Kind} from '{value.Source}' ({value.Role}): {value.Detail}");
        _events.Writer.TryWrite(value);
    }

    /// <summary>The fixture's window or frame, as the API names it.</summary>
    internal abstract Task<string> WindowTitleAsync(CancellationToken cancellationToken);

    /// <summary>Finds the only control of the kind with the accessible name; null when there is none.</summary>
    internal abstract Task<Control?> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken);

    /// <summary>The value, minimum and maximum of a range control, each null when the API does not expose it.</summary>
    internal abstract Task<RangeState> ReadRangeAsync(Control control, CancellationToken cancellationToken);

    /// <summary>Changes a range control towards <paramref name="value"/> as a screen reader user would, by setting it or with
    /// increments.</summary>
    internal abstract Task ChangeRangeAsync(Control control, double value, CancellationToken cancellationToken);

    /// <summary>One increment (<paramref name="direction"/> 1) or decrement (-1), as a screen reader's commands do.</summary>
    internal abstract Task StepAsync(Control control, int direction, CancellationToken cancellationToken);

    internal abstract Task PressAsync(Control control, CancellationToken cancellationToken);

    /// <summary>The names of the selected items of a list box.</summary>
    internal abstract Task<IReadOnlyList<string>> ReadSelectionAsync(Control control, CancellationToken cancellationToken);

    internal abstract Task SelectAsync(Control control, string item, CancellationToken cancellationToken);

    internal abstract Task FocusAsync(Control control, CancellationToken cancellationToken);

    /// <summary>Whether <see cref="StepWithKeysAsync"/> and <see cref="SelectWithKeysAsync"/> press keys, which reach the
    /// control only once the screen reader has settled on it.</summary>
    internal virtual bool ActsWithKeys => false;

    /// <summary>One increment or decrement of the focused control as a screen reader user makes it, for the speech
    /// checks. The API's own action (<see cref="StepAsync"/>) unless the screen reader's users press keys instead.</summary>
    internal virtual Task StepWithKeysAsync(Control control, int direction, CancellationToken cancellationToken)
        => StepAsync(control, direction, cancellationToken);

    /// <summary>Selects an item of the focused list box as a screen reader user does, for the speech checks.</summary>
    internal virtual Task SelectWithKeysAsync(Control control, string item, CancellationToken cancellationToken)
        => SelectAsync(control, item, cancellationToken);

    /// <summary>A short description of the native menu bar, or null when the host has none.</summary>
    internal virtual Task<string?> MenuBarAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>The tree as the API exposes it (role and name per line), for the log when a control is missing.</summary>
    internal virtual Task<string> DumpAsync(CancellationToken cancellationToken) => Task.FromResult("");

    public abstract ValueTask DisposeAsync();
}

internal enum ControlKind
{
    Button,
    Slider,
    SpinButton,
    ListBox,
}

internal enum EventKind
{
    ValueChanged,
    SelectionChanged,
    Announcement,
    LiveRegion,
    FocusChanged,
}

/// <param name="Handle">The API's element.</param>
/// <param name="Role">The role the API reports, for the receipt.</param>
internal sealed record Control(ControlKind Kind, string Name, string Role, object Handle);

internal readonly record struct RangeState(double? Value, double? Minimum, double? Maximum)
{
    public override string ToString() => $"value {Format(Value)}, minimum {Format(Minimum)}, maximum {Format(Maximum)}";

    private static string Format(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not exposed";
}

/// <param name="Source">The accessible name of the element the event came from, or its text.</param>
/// <param name="Detail">The event's own data, such as the announced text or the new value.</param>
internal sealed record AccessibilityEvent(EventKind Kind, string Source, string Role, string Detail);
