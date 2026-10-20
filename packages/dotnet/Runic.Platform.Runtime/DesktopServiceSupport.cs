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
    private int _readers;
    private bool _disposed;
    private TaskCompletionSource? _disposal;
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
    /// <remarks>Called before each watched read, so a change during that read completes the returned task.</remarks>
    protected virtual Task? NextChange() => null;
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
        }
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
                if (changed is null) await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
                else await changed.WaitAsync(linked.Token).ConfigureAwait(false);
            }
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
        Menu(state.Menu, new HashSet<string>(StringComparer.Ordinal), 0, ref count);
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
    private static void Menu(ImmutableArray<DesktopMenuItem> items, HashSet<string> ids, int depth, ref int count)
    {
        if (items.IsDefault) throw new ArgumentException("Status icon menus must be initialized.");
        if (depth > 4) throw new ArgumentException("Status icon menus nest at most four submenus deep.");
        foreach (var item in items)
        {
            if (++count > 256) throw new ArgumentException("A status icon menu has at most 256 entries.");
            switch (item)
            {
                case DesktopMenuCommand command:
                    Identifier(command.Id);
                    if (!ids.Add(command.Id)) throw new ArgumentException("Status icon menu command IDs must be unique.");
                    Text(command.Label, 128, "Menu label");
                    break;
                case DesktopMenuSubmenu submenu:
                    Text(submenu.Label, 128, "Menu label");
                    Menu(submenu.Items, ids, depth + 1, ref count);
                    break;
                case DesktopMenuSeparator:
                    break;
                default:
                    throw new ArgumentNullException(nameof(items), "Status icon menus cannot contain null entries.");
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
