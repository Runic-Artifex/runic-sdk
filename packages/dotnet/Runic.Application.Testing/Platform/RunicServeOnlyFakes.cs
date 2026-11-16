using System.Text;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// Scripts the file dialogs of a serve-only application (<see cref="RunicServeOnlyTestPlatform.FileDialogs"/>), with
/// the names of <see cref="RunicTestFileDialogs"/>.
/// </summary>
public sealed class RunicServeOnlyFileDialogs
{
    private readonly RunicServeOnlyTestPlatform _platform;

    internal RunicServeOnlyFileDialogs(RunicServeOnlyTestPlatform platform) => _platform = platform;

    /// <summary>Queues the selection of an existing file for the next open-file or open-document dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="content">Its bytes.</param>
    public void EnqueueOpen(string name, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _platform.Append("fileDialogs", "open", ("name", name), ("content", Convert.ToBase64String(content)));
    }

    /// <summary>Queues the selection of a file with UTF-8 text for the next open dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="text">Its text.</param>
    public void EnqueueOpen(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnqueueOpen(name, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Queues a new save destination for the next save dialog; its commits are recorded with their content.</summary>
    /// <param name="name">The name the user chose.</param>
    public void EnqueueSave(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _platform.Append("fileDialogs", "save", ("name", name));
    }

    /// <summary>Queues the selection of directories for the next directory dialog.</summary>
    /// <param name="paths">Fully qualified paths.</param>
    public void EnqueueOpenDirectory(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0) throw new ArgumentException("Select at least one directory.", nameof(paths));
        _platform.Append("fileDialogs", "openDirectory", ("path", string.Join('\n', paths)));
    }

    /// <summary>Queues a dismissal of the next file dialog.</summary>
    public void EnqueueDismissed() => _platform.Append("fileDialogs", "dismissed");

    /// <summary>Queues unavailability for the next file dialog.</summary>
    /// <param name="reason">The reason it reports.</param>
    public void EnqueueUnavailable(PlatformUnavailableReason reason) =>
        _platform.Append("fileDialogs", "unavailable", ("reason", RunicServeOnlyTestPlatform.Defined(reason).ToString()));

    /// <summary>Queues a failure for the next file dialog.</summary>
    /// <param name="code">The failure it reports.</param>
    public void EnqueueFailed(PlatformFailureCode code) =>
        _platform.Append("fileDialogs", "failed", ("code", RunicServeOnlyTestPlatform.Defined(code).ToString()));
}

/// <summary>
/// Scripts the message boxes of a serve-only application (<see cref="RunicServeOnlyTestPlatform.MessageBoxes"/>), with
/// the names of <see cref="RunicTestMessageBoxes"/>.
/// </summary>
public sealed class RunicServeOnlyMessageBoxes
{
    private readonly RunicServeOnlyTestPlatform _platform;

    internal RunicServeOnlyMessageBoxes(RunicServeOnlyTestPlatform platform) => _platform = platform;

    /// <summary>Queues the button the user chooses in the next message box.</summary>
    /// <param name="buttonId">The button's id, or <see langword="null"/> for a box closed without a button.</param>
    public void EnqueueAnswer(string? buttonId) => _platform.Append("messageBoxes", "answer", ("button", buttonId));
}

/// <summary>
/// Scripts the context menus of a serve-only application (<see cref="RunicServeOnlyTestPlatform.ContextMenus"/>), with
/// the names of <see cref="RunicTestContextMenus"/>.
/// </summary>
public sealed class RunicServeOnlyContextMenus
{
    private readonly RunicServeOnlyTestPlatform _platform;

    internal RunicServeOnlyContextMenus(RunicServeOnlyTestPlatform platform) => _platform = platform;

    /// <summary>Queues the item the user chooses in the next context menu.</summary>
    /// <param name="commandId">The item's command id.</param>
    /// <param name="commandParameter">The item's command parameter, or <see langword="null"/> for the first item with the id.</param>
    public void EnqueueChoice(string commandId, string? commandParameter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        _platform.Append("contextMenus", "choose", ("commandId", commandId), ("commandParameter", commandParameter));
    }

    /// <summary>Queues a dismissal of the next context menu.</summary>
    public void EnqueueDismissed() => _platform.Append("contextMenus", "dismissed");
}

/// <summary>
/// Scripts the clipboard of a serve-only application (<see cref="RunicServeOnlyTestPlatform.Clipboard"/>), with the
/// names of <see cref="RunicTestClipboard"/>.
/// </summary>
public sealed class RunicServeOnlyClipboard
{
    private readonly RunicServeOnlyTestPlatform _platform;

    internal RunicServeOnlyClipboard(RunicServeOnlyTestPlatform platform) => _platform = platform;

    /// <summary>Replaces the clipboard's content, as another application's copy would, before the application's next clipboard call.</summary>
    /// <param name="content">The content, or <see langword="null"/> to empty the clipboard.</param>
    public void SetContent(DesktopClipboardContent? content)
    {
        if (content is null)
        {
            _platform.Append("clipboard", "clear");
            return;
        }
        _platform.Append("clipboard", "set", ("text", content.Text), ("html", content.Html), ("rtf", content.Rtf),
            ("image", content.Image.IsEmpty ? null : Convert.ToBase64String(content.Image.Span)),
            ("files", content.Files.IsDefaultOrEmpty ? null : string.Join('\n', content.Files)));
    }

    /// <summary>Replaces the clipboard's content with text.</summary>
    /// <param name="text">The text.</param>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetContent(new DesktopClipboardContent { Text = text });
    }
}
