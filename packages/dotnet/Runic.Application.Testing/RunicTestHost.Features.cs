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
        JsonObject? reply = null;
        var cancelled = false;
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(window.Closing))
        {
            _userRequests[request.Id] = cancel;
            try
            {
                reply = request.Kind switch
                {
                    "file-dialog" => await FileDialogAsync(recorded, cancel.Token).ConfigureAwait(false),
                    "message-box" => new JsonObject { ["outcome"] = "success", ["button"] = await MessageBoxAsync(recorded, cancel.Token).ConfigureAwait(false) },
                    _ => new JsonObject
                    {
                        ["outcome"] = "success",
                        ["itemId"] = _options.ContextMenu is { } choose ? await choose(recorded, cancel.Token).ConfigureAwait(false) : null,
                    },
                };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
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
        else
            await request.ReplyAsync(reply).ConfigureAwait(false);
        return true;
    }

    private async Task<JsonObject> FileDialogAsync(RunicTestDialogRequest request, CancellationToken cancellationToken)
    {
        var paths = _options.FileDialog is { } answer ? await answer(request, cancellationToken).ConfigureAwait(false) : null;
        if (paths is null || paths.Count == 0) return new JsonObject { ["outcome"] = "failed", ["code"] = "user-dismissed" };
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException($"RunicTestHostOptions.FileDialog returned the relative path '{path}'.");
        }
        return new JsonObject
        {
            ["outcome"] = "success",
            ["paths"] = new JsonArray([.. paths.Select(static path => (JsonNode)path)]),
            ["filter"] = null,
        };
    }

    // The answer, or the box's cancel button (an Esc) without one.
    private async Task<string?> MessageBoxAsync(RunicTestDialogRequest request, CancellationToken cancellationToken)
    {
        if (_options.MessageBox is { } answer) return await answer(request, cancellationToken).ConfigureAwait(false);
        if (!request.Message.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return null;
        foreach (var button in buttons.EnumerateArray())
        {
            if (button.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "cancel")
                return button.GetProperty("id").GetString();
        }
        return null;
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
    public async Task<JsonElement> WaitForMenuBarAsync(Func<JsonElement, bool> match, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
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
