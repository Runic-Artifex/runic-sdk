using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;

namespace Runic.Application.Testing;

// menus/1 and dialogs/1 of the test host (host-protocol.md, Menus and Dialogs): each window's menu bar as the guest sent
// it, and the dialogs and context menus that RunicTestHostOptions answers, one dialog per window, given up by the guest's
// cancel push or the window's close.
public sealed partial class RunicTestHost
{
    // The guest's waiting requests that wait for the user, by request id, for its cancel push.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _userRequests = new();

    private void OnCancel(JsonObject push)
    {
        if (HostProtocol.Integer(push, "request") is { } id && _userRequests.TryGetValue(id, out var cancel))
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> OnFeatureRequestAsync(HostRequest request, string? id)
    {
        string feature;
        switch (request.Kind)
        {
            case "set-menu-bar" or "update-menu-items" or "show-context-menu":
                feature = HostFeatures.Menus;
                break;
            case "file-dialog" or "message-box":
                feature = HostFeatures.Dialogs;
                break;
            default:
                return false;
        }
        var message = request.Message;
        var reason = !Features.Contains(feature, StringComparer.Ordinal) ? "invalid-request" : request.Kind switch
        {
            "set-menu-bar" => message["menu"] is null ? null : HostFeatures.CheckMenu(message["menu"], bar: true),
            "update-menu-items" => HostFeatures.CheckMenuUpdates(message["items"]) ? null : "invalid-request",
            "show-context-menu" => HostFeatures.CheckPoint(message["point"]) ? HostFeatures.CheckMenu(message["menu"], bar: false) : "invalid-request",
            "file-dialog" => HostFeatures.CheckFileDialog(message) ? null : "invalid-request",
            _ => HostFeatures.CheckMessageBox(message) ? null : "invalid-request",
        };
        if (reason is null && FindWindow(id) is not { Refused: false }) reason = "invalid-request";
        if (reason is not null)
        {
            await request.FailAsync("rejected", reason, $"The '{request.Kind}' request is not valid here.").ConfigureAwait(false);
            return true;
        }
        var window = FindWindow(id)!;
        if (window.IsClosed)
        {
            await request.ReplyAsync(new JsonObject { ["outcome"] = "unavailable", ["reason"] = "owner-closed" }).ConfigureAwait(false);
            return true;
        }
        switch (request.Kind)
        {
            case "set-menu-bar":
                window.SetMenuBar((JsonArray?)message["menu"]?.DeepClone());
                await request.ReplyAsync(new JsonObject { ["outcome"] = "success" }).ConfigureAwait(false);
                return true;
            case "update-menu-items":
                window.UpdateMenuItems((JsonArray)message["items"]!);
                await request.ReplyAsync(new JsonObject { ["outcome"] = "success" }).ConfigureAwait(false);
                return true;
        }
        var dialog = request.Kind != "show-context-menu";
        if (dialog && !window.TryBeginDialog())
        {
            await request.FailAsync("rejected", "busy", "The window already shows a dialog.").ConfigureAwait(false);
            return true;
        }
        var recorded = new RunicTestDialogRequest(window.Id, request.Kind, JsonDocument.Parse(message.ToJsonString()).RootElement.Clone());
        window.RecordDialog(recorded);
        RunicTestDialogAnswer? answer = null;
        Exception? failure = null;
        var cancelled = false;
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(window.Closing))
        {
            _userRequests[request.Id] = cancel;
            try
            {
                var answers = request.Kind switch
                {
                    "file-dialog" => _options.FileDialog,
                    "message-box" => _options.MessageBox,
                    _ => _options.ContextMenu,
                };
                answer = answers is null ? DefaultAnswer(recorded) : await answers(recorded, cancel.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The answer is null.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
            finally
            {
                _userRequests.TryRemove(request.Id, out _);
                if (dialog) window.EndDialog();
            }
        }
        // The window closed: as for any request of a closed window. The guest's cancel: cancelled.
        if (cancelled && window.IsClosed)
            await request.ReplyAsync(new JsonObject { ["outcome"] = "unavailable", ["reason"] = "owner-closed" }).ConfigureAwait(false);
        else if (cancelled)
            await request.FailAsync("cancelled", null, "The guest gave the request up.").ConfigureAwait(false);
        else if (failure is null && Reply(recorded, answer!, out failure) is var (failKind, failReason, failText, reply) && failure is null)
        {
            if (reply is not null) await request.ReplyAsync(reply).ConfigureAwait(false);
            else await request.FailAsync(failKind!, failReason, failText!).ConfigureAwait(false);
        }
        else
        {
            // A callback that threw or answered what does not fit: the guest's dialog fails, and the test learns why.
            var name = request.Kind switch { "file-dialog" => "FileDialog", "message-box" => "MessageBox", _ => "ContextMenu" };
            RecordAnswerFailure(new InvalidOperationException($"RunicTestHostOptions.{name} failed for {recorded.DialogKind}: {failure!.Message}", failure));
            await request.FailAsync("failed", null, $"The test host's {name} answer failed.").ConfigureAwait(false);
        }
        return true;
    }

    // Without a callback: dismissed, or a message box's cancel button (an Esc).
    private static RunicTestDialogAnswer DefaultAnswer(RunicTestDialogRequest request)
    {
        if (request.DialogKind != RunicTestDialogKind.MessageBox) return RunicTestDialogAnswer.Dismiss();
        if (!request.Message.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return RunicTestDialogAnswer.Dismiss();
        foreach (var button in buttons.EnumerateArray())
        {
            if (button.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "cancel")
                return RunicTestDialogAnswer.Choose(button.GetProperty("id").GetString());
        }
        return RunicTestDialogAnswer.Dismiss();
    }

    // The reply of an answer, or the failure that it is (kind, reason, message); an answer that does not fit the request
    // sets failure.
    private static (string? Kind, string? Reason, string? Message, JsonObject? Reply) Reply(RunicTestDialogRequest request, RunicTestDialogAnswer answer,
        out Exception? failure)
    {
        failure = null;
        if (answer.Failure is var (kind, reason, message)) return (kind, reason, message, null);
        var file = request.DialogKind is not (RunicTestDialogKind.MessageBox or RunicTestDialogKind.ContextMenu);
        if (answer.Paths is { } paths)
        {
            if (!file)
            {
                failure = new ArgumentException("Select answers a file dialog; a message box or context menu takes Choose or Dismiss.");
                return default;
            }
            if (answer.Filter is { } index && index >= request.FilterNames.Count)
            {
                failure = new ArgumentOutOfRangeException(nameof(answer), $"The dialog has {request.FilterNames.Count} filters, not one of index {index}.");
                return default;
            }
            var single = request.DialogKind is RunicTestDialogKind.OpenFile or RunicTestDialogKind.SaveFile or RunicTestDialogKind.OpenFolder;
            return (null, null, null, new JsonObject
            {
                ["outcome"] = "success",
                ["paths"] = new JsonArray([.. (single ? paths.Take(1) : paths).Select(static path => (JsonNode)path)]),
                ["filter"] = answer.Filter,
            });
        }
        var reply = (JsonObject)answer.Reply!.DeepClone();
        switch (HostProtocol.String(reply, "outcome"))
        {
            case "dismissed":
                return (null, null, null, request.DialogKind switch
                {
                    RunicTestDialogKind.MessageBox => new JsonObject { ["outcome"] = "success", ["button"] = null },
                    RunicTestDialogKind.ContextMenu => new JsonObject { ["outcome"] = "success", ["itemId"] = null },
                    _ => new JsonObject { ["outcome"] = "failed", ["code"] = "user-dismissed" },
                });
            case "success":
                if (file)
                {
                    failure = new ArgumentException("Choose answers a message box or context menu; a file dialog takes Select or Dismiss.");
                    return default;
                }
                var chosen = HostProtocol.String(reply, "chosen");
                return (null, null, null, request.DialogKind == RunicTestDialogKind.MessageBox
                    ? new JsonObject { ["outcome"] = "success", ["button"] = chosen }
                    : new JsonObject { ["outcome"] = "success", ["itemId"] = chosen });
            default:
                return (null, null, null, reply);
        }
    }
}

public sealed partial class RunicTestHostedWindow
{
    private readonly object _menuGate = new();
    private JsonArray? _menuBar;
    private TaskCompletionSource _menuBarChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RunicTestDialogRequest> _dialogs = [];
    private readonly CancellationTokenSource _closing = new();
    private int _dialog;

    /// <summary>
    /// The window's menu bar as the guest sent it (<c>menus/1</c>): an array of submenu entries with their items' current
    /// titles, ids, shortcuts and states, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// An item entry has <c>kind</c> <c>item</c>, <c>id</c>, <c>title</c>, <c>shortcut</c>, <c>enabled</c> and
    /// <c>checked</c>; a submenu has <c>kind</c> <c>submenu</c>, <c>title</c>, <c>role</c>, <c>enabled</c> and
    /// <c>items</c>. <see cref="FindMenuItem(string[])"/> finds an item by its titles.
    /// </remarks>
    public JsonElement? MenuBar
    {
        get
        {
            lock (_menuGate) return _menuBar is null ? null : JsonDocument.Parse(_menuBar.ToJsonString()).RootElement.Clone();
        }
    }

    /// <summary>The file dialogs, message boxes and context menus the guest asked for in this window, in order.</summary>
    public IReadOnlyList<RunicTestDialogRequest> DialogRequests
    {
        get { lock (_dialogs) return [.. _dialogs]; }
    }

    internal CancellationToken Closing => _closing.Token;

    internal bool TryBeginDialog() => Interlocked.CompareExchange(ref _dialog, 1, 0) == 0;

    internal void EndDialog() => Volatile.Write(ref _dialog, 0);

    internal void RecordDialog(RunicTestDialogRequest request)
    {
        lock (_dialogs) _dialogs.Add(request);
    }

    // window-closed, or a refusal: the window's dialogs and context menus end with it.
    private void EndUserRequests()
    {
        try { _closing.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void SetMenuBar(JsonArray? menu)
    {
        lock (_menuGate) _menuBar = menu;
        MenuBarChanged();
    }

    internal void UpdateMenuItems(JsonArray items)
    {
        bool changed;
        lock (_menuGate) changed = _menuBar is { } bar && HostFeatures.ApplyUpdates(bar, items);
        if (changed) MenuBarChanged();
    }

    private void MenuBarChanged() =>
        Interlocked.Exchange(ref _menuBarChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Waits until <paramref name="match"/> accepts the window's menu bar, such as one with a German title.</summary>
    /// <param name="match">Decides about the bar, <see cref="MenuBar"/>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The bar that matched.</returns>
    /// <exception cref="TimeoutException">No bar matched in time.</exception>
    /// <exception cref="InvalidOperationException">An answer of <see cref="RunicTestHostOptions"/> failed before.</exception>
    public async Task<JsonElement> WaitForMenuBarAsync(Func<JsonElement, bool> match, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        _host.ThrowIfAnswerFailed();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            var changed = Volatile.Read(ref _menuBarChanged).Task;
            if (MenuBar is { } bar && match(bar)) return bar;
            try { await changed.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The window's menu bar did not match within {timeout}: {(MenuBar is { } current ? current.GetRawText() : "none")}");
            }
        }
    }

    /// <summary>Finds the id of an item of the window's menu bar by the titles of its menus and its own, such as ("File", "Save").</summary>
    /// <param name="titles">The titles, from the bar's menu to the item.</param>
    /// <returns>The item's id, or <see langword="null"/> when the bar has no such item.</returns>
    public string? FindMenuItem(params string[] titles) => MenuBar is { } bar ? FindMenuItem(bar, titles) : null;

    /// <summary>Finds the id of an item of a menu, such as a context menu's <c>menu</c> member, by its titles.</summary>
    /// <param name="menu">An array of menu entries.</param>
    /// <param name="titles">The titles, from the first level to the item.</param>
    /// <returns>The item's id, or <see langword="null"/> when the menu has no such item.</returns>
    public static string? FindMenuItem(JsonElement menu, params string[] titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var entries = menu;
        for (var index = 0; index < titles.Length; index++)
        {
            if (entries.ValueKind != JsonValueKind.Array) return null;
            JsonElement? found = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String &&
                    title.GetString() == titles[index])
                {
                    found = entry;
                    break;
                }
            }
            if (found is not { } match) return null;
            if (index == titles.Length - 1)
                return match.TryGetProperty("kind", out var kind) && kind.GetString() == "item" ? match.GetProperty("id").GetString() : null;
            if (!match.TryGetProperty("items", out entries)) return null;
        }
        return null;
    }

    /// <summary>
    /// Chooses an item of the window's menu bar as the host's user would, or presses its shortcut: pushes
    /// <c>menu-invoke</c>, and the guest runs the item's command through the Window's command path.
    /// </summary>
    /// <param name="itemId">The item's id, such as from <see cref="FindMenuItem(string[])"/>.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task InvokeMenuItemAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        return _host.PushAsync(new JsonObject { ["kind"] = "menu-invoke", ["window"] = Id, ["itemId"] = itemId }, cancellationToken);
    }
}
