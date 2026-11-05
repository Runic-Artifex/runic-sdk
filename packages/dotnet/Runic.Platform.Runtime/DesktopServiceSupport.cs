using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Runic.Platform;

namespace Runic.Platform.Runtime;

/// <summary>Bounded, non-overlapping preference observation, including backend recovery.</summary>
internal abstract class DesktopSettingsSource : IDesktopSettings
{
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers, _watches;
    private bool _disposed;
    private TaskCompletionSource? _disposal;
    // The native change notifications StartChanges started for the running watches; null while none run, or when
    // they could not start (the watches then poll).
    private readonly Lock _changesGate = new();
    private IDisposable? _changes;
    private TaskCompletionSource? _changed;
    private object? _changesGeneration;
    private bool _changesFailed;
    /// <summary>The clock of the once-per-second polling fallback; tests set a fake one.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <inheritdoc />
    public async ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            _readers++;
        }
        try { return await ReadCoreAsync(linked.Token).ConfigureAwait(false); }
        finally
        {
            linked.Dispose();
            lock (_gate) if (--_readers == 0 && _disposed) _drained.TrySetResult();
        }
    }
    /// <summary>Reads one native snapshot and drains any submitted native callback before returning.</summary>
    protected abstract ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken);
    /// <summary>Returns a task that completes after the next native change, or null to poll once per second.</summary>
    /// <remarks>
    /// Called before each watched read, so a change during that read completes the returned task. The default starts
    /// <see cref="StartChanges"/> for the first watch and stops it when the last watch ends; if it cannot start, the
    /// watches poll until they all end.
    /// </remarks>
    protected virtual Task? NextChange()
    {
        lock (_changesGate)
        {
            if (_changed is null && !_changesFailed)
            {
                var generation = new object();
                (_changed, _changesGeneration) = (new(TaskCreationOptions.RunContinuationsAsynchronously), generation);
                try { _changes = StartChanges(() => Changed(generation, stopped: false), () => Changed(generation, stopped: true)); }
                catch (Exception error) when (error is not OutOfMemoryException) { _changes = null; }
                if (_changes is null) (_changed, _changesGeneration, _changesFailed) = (null, null, true);
            }
            return _changed?.Task;
        }
    }
    /// <summary>
    /// Starts native change notifications that call <c>changed</c> after each change of a setting the source reads, on
    /// any thread, and <c>failed</c> if they stop on their own; null, or an exception, when they are not available.
    /// Disposing stops them without waiting.
    /// </summary>
    protected virtual IDisposable? StartChanges(Action changed, Action failed) => null;
    // Wakes the watches and arms the next change, unless the notifications stopped since. Notifications that failed
    // leave the watches polling until they all end.
    private void Changed(object generation, bool stopped)
    {
        TaskCompletionSource? woken;
        IDisposable? changes = null;
        lock (_changesGate)
        {
            if (!ReferenceEquals(_changesGeneration, generation)) return;
            woken = _changed;
            if (stopped) (changes, _changes, _changed, _changesGeneration, _changesFailed) = (_changes, null, null, null, true);
            else _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { changes?.Dispose(); }
        finally { woken?.TrySetResult(); }
    }
    // The last watch ended, or the source closed: stops the notifications, and wakes a watch that started meanwhile,
    // whose next NextChange starts them again.
    private void StopChanges()
    {
        IDisposable? changes;
        TaskCompletionSource? woken;
        lock (_changesGate)
        {
            (changes, woken) = (_changes, _changed);
            (_changes, _changed, _changesGeneration, _changesFailed) = (null, null, null, false);
        }
        try { changes?.Dispose(); }
        finally { woken?.TrySetResult(); }
    }
    /// <summary>Releases native observation after disposal has drained every read.</summary>
    protected virtual ValueTask CloseCoreAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public async IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            _watches++;
        }
        try
        {
            using (linked)
            {
                PlatformResult<DesktopAppearance>? previous = null;
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var changed = NextChange();
                    PlatformResult<DesktopAppearance> current;
                    try { current = await ReadAsync(linked.Token).ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (linked.IsCancellationRequested)
                    { throw new OperationCanceledException(linked.Token); }
                    if (current != previous) { previous = current; yield return current; }
                    if (changed is null) await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider, linked.Token).ConfigureAwait(false);
                    else await changed.WaitAsync(linked.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            bool last;
            lock (_gate) last = --_watches == 0;
            if (last) StopChanges();
        }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal.Task);
            completion = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposed = true;
            if (_readers == 0) _drained.TrySetResult();
        }
        _ = CloseAsync(completion);
        GC.SuppressFinalize(this);
        return new(completion.Task);
    }
    private async Task CloseAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await _closed.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        await _drained.Task.ConfigureAwait(false);
        try { StopChanges(); } catch (Exception error) { failure ??= error; }
        try { await CloseCoreAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        _closed.Dispose();
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }
}

/// <summary>Shared input rules applied before native side effects.</summary>
internal static class DesktopServiceValidation
{
    /// <summary>Validates and normalizes a trusted absolute local path.</summary>
    public static string FilePath(string path, DesktopFileOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Supply a fully qualified local path.", nameof(path));
        path = Path.GetFullPath(path);
        // UNC and \\?\ or \\.\ device paths reach shares and devices; a later colon names an alternate stream.
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0))
            throw new ArgumentException("Supply a local drive path, not a UNC, device or alternate stream path.", nameof(path));
        return path;
    }
    /// <summary>
    /// Validates and normalizes a trusted absolute local path to a file, directory or symbolic link that an operation
    /// acts on, such as moving it to the trash.
    /// </summary>
    /// <returns>The full path without a trailing separator; a final symbolic link is not resolved.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, relative or contains NUL, names a root directory, or on Windows is a device or alternate
    /// stream path.
    /// </exception>
    public static string ItemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Supply a fully qualified local path.", nameof(path));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(path) is { } root && Path.TrimEndingDirectorySeparator(root) == path)
            throw new ArgumentException("Supply a file or directory below a root directory.", nameof(path));
        // \\?\ and \\.\ device paths reach devices; a later colon names an alternate stream. UNC shares are allowed.
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0))
            throw new ArgumentException("Supply a drive or UNC path, not a device or alternate stream path.", nameof(path));
        return path;
    }
    /// <summary>Validates bounded notification content and routing identifiers.</summary>
    public static void Notification(DesktopNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Identifier(notification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentNullException.ThrowIfNull(notification.Body);
        if (notification.Title.Length > 256 || notification.Body.Length > 4096 || notification.Title.Contains('\0') || notification.Body.Contains('\0') || notification.Actions.IsDefault || notification.Actions.Length > 4)
            throw new ArgumentException("Notification content exceeds its bounds.", nameof(notification));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in notification.Actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            Identifier(action.Id);
            if (action.Id == "default" || !ids.Add(action.Id)) throw new ArgumentException("Notification action IDs must be unique and non-default.");
            ArgumentException.ThrowIfNullOrWhiteSpace(action.Label);
            if (action.Label.Length > 80 || action.Label.Contains('\0')) throw new ArgumentException("Notification action label is too long.");
        }
        if (notification.ActivationUri is { } uri && (!uri.IsAbsoluteUri || uri.IsFile || uri.AbsoluteUri.Length > 2048))
            throw new ArgumentException("Notification activation requires a bounded absolute non-file URI.");
    }
    /// <summary>Validates a status icon's bounded content, icons and menu.</summary>
    public static void StatusIcon(DesktopStatusIconState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Status)) throw new ArgumentOutOfRangeException(nameof(state), "Unknown status icon status.");
        Text(state.Title, 256, "Status icon title");
        if (state.Tooltip is not null && (state.Tooltip.Length > 1024 || state.Tooltip.Contains('\0')))
            throw new ArgumentException("Status icon tooltip exceeds its bounds.", nameof(state));
        Icon(state.Icon);
        if (state.AttentionIcon is not null) Icon(state.AttentionIcon);
        int count = 0;
        Menu(state.Menu, new HashSet<(string, string?)>(), 0, ref count);
    }
    private static void Icon(DesktopIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (icon.Images.IsDefault) throw new ArgumentException("Status icon images must be initialized.");
        if (icon.IconName is { } name && (name.Length is 0 or > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            throw new ArgumentException("Use an icon theme name of at most 128 ASCII letters, digits, dots, underscores or hyphens.");
        if (icon.IconName is null && icon.Images.Length == 0) throw new ArgumentException("A status icon needs an icon name or an image.");
        if (icon.Images.Length > 8) throw new ArgumentException("Supply at most eight status icon images.");
        foreach (var image in icon.Images)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width is < 1 or > 512 || image.Height is < 1 or > 512 || image.Rgba.IsDefault || image.Rgba.Length != image.Width * image.Height * 4)
                throw new ArgumentException("Status icon images need 1 to 512 pixels on each side and four RGBA bytes per pixel.");
        }
    }
    // Items are unique by command id and parameter, so an activation names the chosen item.
    private static void Menu(ImmutableArray<DesktopMenuEntry> entries, HashSet<(string, string?)> ids, int depth, ref int count)
    {
        if (entries.IsDefault) throw new ArgumentException("Status icon menus must be initialized.");
        if (depth > 4) throw new ArgumentException("Status icon menus nest at most four submenus deep.");
        foreach (var entry in entries)
        {
            if (++count > 256) throw new ArgumentException("A status icon menu has at most 256 entries.");
            switch (entry)
            {
                case null:
                    throw new ArgumentNullException(nameof(entries), "Status icon menus cannot contain null entries.");
                case DesktopMenuItem item:
                    DesktopMenuRules.Title(item.Title, "A status icon menu item");
                    DesktopMenuRules.Item(item, $"the status icon menu item '{item.Title}'");
                    if (!ids.Add((item.CommandId, item.CommandParameter)))
                        throw new ArgumentException(item.CommandParameter is null
                            ? $"The command id '{item.CommandId}' appears twice without a parameter; status icon menu items must be unique by command id and parameter."
                            : $"The command id '{item.CommandId}' appears twice with the same parameter; status icon menu items must be unique by command id and parameter.");
                    break;
                case DesktopMenu submenu:
                    DesktopMenuRules.Title(submenu.Title, "A status icon submenu");
                    DesktopMenuRules.AccessKey(submenu.AccessKey, $"the status icon submenu '{submenu.Title}'");
                    Menu(submenu.Items, ids, depth + 1, ref count);
                    break;
                default:
                    // A separator, or an entry kind of a later release that providers skip.
                    break;
            }
        }
    }
    private static void Text(string text, int maximum, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > maximum || text.Contains('\0')) throw new ArgumentException($"{what} exceeds its bounds.");
    }
    /// <summary>Validates an application-owned routing identifier.</summary>
    public static void Identifier(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Use an identifier of at most 64 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
    }
}
