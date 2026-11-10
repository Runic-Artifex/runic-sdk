using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Desktop;

// The host's side of menus/1 and dialogs/1 (host-protocol.md, Menus and Dialogs): a guest's menu bar in its window's
// native bar, its context menus, file dialogs and message boxes owned by its window, and the guest's cancel pushes.
public sealed partial class RunicHostedApplication
{
    // The guest's requests that wait for the user, by request id, for its cancel push.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _userRequests = new();

    // Creates the dialogs of a hosted window; tests replace it, as a headless window shows none.
    internal static Func<DesktopWindow, HostDialogServices> CreateDialogs { get; set; } = DesktopPlatformServices.CreateHostDialogs;

    private bool InUse(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    // The request kinds of menus/1 and dialogs/1; false for another kind.
    private async Task<bool> OnFeatureRequestAsync(HostRequest request)
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
        // A kind of a feature that is not in use is not a request of this protocol (host-protocol.md, Messages).
        if (!InUse(feature))
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' belongs to {feature}, which is not in use.").ConfigureAwait(false);
            return true;
        }
        var message = request.Message;
        var reason = request.Kind switch
        {
            "set-menu-bar" => message["menu"] is null ? null : HostFeatures.CheckMenu(message["menu"], bar: true),
            "update-menu-items" => HostFeatures.CheckMenuUpdates(message["items"]) ? null : "invalid-request",
            "show-context-menu" => HostFeatures.CheckPoint(message["point"]) ? HostFeatures.CheckMenu(message["menu"], bar: false) : "invalid-request",
            "file-dialog" => HostFeatures.CheckFileDialog(message) ? null : "invalid-request",
            _ => HostFeatures.CheckMessageBox(message) ? null : "invalid-request",
        };
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(message, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        if (window is null) reason = "invalid-request";
        if (reason is not null)
        {
            await request.FailAsync("rejected", reason, reason == "capacity"
                ? "The menu is larger than the protocol allows."
                : $"The '{request.Kind}' request is missing a member, has one out of range or names no window this channel opened.").ConfigureAwait(false);
            return true;
        }
        // A request for a window the host closed is not an error: the guest may not have learned of the close yet.
        if (!window!.IsOpen)
        {
            await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))).ConfigureAwait(false);
            return true;
        }
        switch (request.Kind)
        {
            case "set-menu-bar":
                await SetMenuBarAsync(request, window).ConfigureAwait(false);
                break;
            case "update-menu-items":
                await UpdateMenuItemsAsync(request, window).ConfigureAwait(false);
                break;
            default:
                await UserRequestAsync(request, window).ConfigureAwait(false);
                break;
        }
        return true;
    }

    // ----- menus/1: the menu bar

    private async Task SetMenuBarAsync(HostRequest request, RunicHostedWindow window)
    {
        var menu = request.Message["menu"] as JsonArray;
        // Rules the shape check leaves to Runic's menus, such as a radio group name or two File menus.
        try { CheckBar(menu is null ? [] : HostedWire.ReadEntries(menu)); }
        catch (ArgumentException error)
        {
            await request.FailAsync("rejected", "invalid-request", Limit($"The menu bar is not valid: {error.Message}")).ConfigureAwait(false);
            return;
        }
        await window.MenuGate.WaitAsync().ConfigureAwait(false);
        try
        {
            window.GuestMenuBar = (JsonArray?)menu?.DeepClone();
            await ReplyMenuBarAsync(request, window).ConfigureAwait(false);
        }
        finally { window.MenuGate.Release(); }
    }

    private async Task UpdateMenuItemsAsync(HostRequest request, RunicHostedWindow window)
    {
        await window.MenuGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ids that name no item of the bar are ignored: the bar may have changed since (host-protocol.md, Menus).
            if (window.GuestMenuBar is not { } bar || !HostedWire.ApplyUpdates(bar, (JsonArray)request.Message["items"]!))
            {
                await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()))).ConfigureAwait(false);
                return;
            }
            await ReplyMenuBarAsync(request, window).ConfigureAwait(false);
        }
        finally { window.MenuGate.Release(); }
    }

    private static void CheckBar(ImmutableArray<DesktopMenuEntry> entries) =>
        _ = DesktopMenuBar.Check([.. entries.Cast<DesktopMenu>()], DesktopMenuPlanner.CurrentPlatform, DesktopMenuBarPresentation.Native, nativeBar: true);

    // Under MenuGate: the policy decides the guest's bar as it is now, and the window shows the result.
    private async Task ReplyMenuBarAsync(HostRequest request, RunicHostedWindow window)
    {
        var entries = window.GuestMenuBar is { } bar ? HostedWire.ReadEntries(bar) : [];
        RunicHostedMenuDecision decision;
        try { decision = Policy.Menu(new RunicHostedMenuRequest(window, RunicHostedMenuKind.MenuBar, entries)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            await request.FailAsync("failed", null, "The host's window policy failed.").ConfigureAwait(false);
            return;
        }
        IReadOnlyList<DesktopMenu> shown = [];
        if (decision.IsAllowed)
        {
            var applied = decision.Request;
            if (applied is not null && (!ReferenceEquals(applied.Window, window) || applied.Kind != RunicHostedMenuKind.MenuBar ||
                applied.Entries.IsDefault || applied.Entries.Any(static entry => entry is not DesktopMenu)))
            {
                DesktopLog.HostedWindowPolicyFailed(_logger, null, "InvalidMenu");
                await request.FailAsync("failed", null, "The host's window policy returned menus for another window or not a menu bar.").ConfigureAwait(false);
                return;
            }
            shown = [.. (applied?.Entries ?? entries).Cast<DesktopMenu>()];
            try { CheckBar([.. shown]); }
            catch (ArgumentException error)
            {
                DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
                await request.FailAsync("failed", null, "The host's window policy returned a menu bar that is not valid.").ConfigureAwait(false);
                return;
            }
        }
        // A refused bar shows no menus of the guest.
        window.ShowMenuBar(shown);
        var result = await ShowAsync(window, shown).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await request.FailAsync("rejected", "refused", Limit(decision.Message ?? "The host's window policy refused the menus.")).ConfigureAwait(false);
            return;
        }
        await request.ReplyAsync(Outcome(result)).ConfigureAwait(false);

        static async Task<PlatformResult<PlatformUnit>> ShowAsync(RunicHostedWindow window, IReadOnlyList<DesktopMenu> menus)
        {
            if (window.PresentationOrNull is not { IsOpen: true } presentation)
                return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            try
            {
                await presentation.SetHostedMenuBarAsync(menus).ConfigureAwait(false);
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }
            catch (ObjectDisposedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown,
                    new PlatformDiagnostic("Runic.Desktop", 0, error.GetType().Name));
            }
        }
    }

    // A guest's item was chosen in the window's menu bar or by its shortcut: menu-invoke (host-protocol.md, Menus).
    internal void InvokeMenuItem(RunicHostedWindow window, string itemId) =>
        _ = PushAsync(new JsonObject { ["kind"] = "menu-invoke", ["window"] = window.Id, ["itemId"] = itemId });

    // ----- Requests that wait for the user: context menus and dialogs

    // The cancel push: the guest gave up one of its requests that wait for the user.
    private void OnCancel(JsonObject push)
    {
        if (HostProtocol.Integer(push, "request") is { } id && _userRequests.TryGetValue(id, out var cancel))
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task UserRequestAsync(HostRequest request, RunicHostedWindow window)
    {
        var dialog = request.Kind != "show-context-menu";
        // One dialog per window (host-protocol.md, Dialogs); a context menu replaces the window's open one natively.
        if (dialog && !window.TryBeginDialog())
        {
            await request.FailAsync("rejected", "busy", "The window already shows a dialog.").ConfigureAwait(false);
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(window.Closing, _ending.Token);
        _userRequests[request.Id] = cancel;
        try
        {
            var reply = request.Kind switch
            {
                "show-context-menu" => await ContextMenuAsync(request.Message, window, cancel.Token).ConfigureAwait(false),
                "file-dialog" => await FileDialogAsync(request.Message, window, cancel.Token).ConfigureAwait(false),
                _ => await MessageBoxAsync(request.Message, window, cancel.Token).ConfigureAwait(false),
            };
            await request.ReplyAsync(reply).ConfigureAwait(false);
        }
        catch (RejectedException rejected)
        {
            await request.FailAsync(rejected.Kind, rejected.Reason, Limit(rejected.Message)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The window closed, or the guest went away: the guest learns it as for any request of a closed window. The
            // guest's own cancel: the request ends cancelled.
            if (!window.IsOpen || window.Closing.IsCancellationRequested || _ending.IsCancellationRequested)
                await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))).ConfigureAwait(false);
            else
                await request.FailAsync("cancelled", null, "The guest gave the request up.").ConfigureAwait(false);
        }
        finally
        {
            _userRequests.TryRemove(request.Id, out _);
            if (dialog) window.EndDialog();
        }
    }

    // A failed reply of a request that waits for the user.
    private sealed class RejectedException(string kind, string? reason, string message) : Exception(message)
    {
        internal string Kind { get; } = kind;
        internal string? Reason { get; } = reason;
    }

    private static RejectedException PolicyFailed() => new("failed", null, "The host's window policy failed.");

    private async Task<JsonObject> ContextMenuAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        ImmutableArray<DesktopMenuEntry> entries;
        try
        {
            entries = HostedWire.ReadEntries((JsonArray)message["menu"]!);
            DesktopMenuRules.ContextMenu(entries);
        }
        catch (ArgumentException error) { throw new RejectedException("rejected", "invalid-request", $"The context menu is not valid: {error.Message}"); }
        var position = message["point"] is JsonObject point
            ? new DesktopContextMenuOptions { X = point["x"]!.GetValue<double>(), Y = point["y"]!.GetValue<double>() }
            : null;
        RunicHostedMenuDecision decision;
        try { decision = Policy.Menu(new RunicHostedMenuRequest(window, RunicHostedMenuKind.ContextMenu, entries) { Position = position }); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            throw PolicyFailed();
        }
        if (!decision.IsAllowed) throw new RejectedException("rejected", "refused", decision.Message ?? "The host's window policy refused the menu.");
        if (decision.Request is { } applied)
        {
            if (!ReferenceEquals(applied.Window, window) || applied.Kind != RunicHostedMenuKind.ContextMenu || applied.Entries.IsDefaultOrEmpty)
            {
                DesktopLog.HostedWindowPolicyFailed(_logger, null, "InvalidMenu");
                throw new RejectedException("failed", null, "The host's window policy returned menus for another window or not a context menu.");
            }
            (entries, position) = (applied.Entries, applied.Position);
        }
        var presentation = window.PresentationOrNull!;
        PlatformResult<DesktopMenuItem?> result;
        try { result = await DesktopPlatformServices.ShowContextMenuAsync(presentation, entries, position, cancellationToken).ConfigureAwait(false); }
        catch (ArgumentException error)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            throw new RejectedException("failed", null, "The context menu could not be shown.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        switch (result)
        {
            case PlatformResult<DesktopMenuItem?>.Success(var item):
                // The window closed while the menu was open.
                if (!window.IsOpen) return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed));
                var itemId = item is null ? null : HostedWire.ItemId(item);
                // An item a policy added runs the host application's command, as its own menus do; the guest chose nothing.
                if (item is not null && itemId is null)
                {
                    try { _ = await presentation.TryExecuteMenuItemAsync(item, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { }
                }
                return new JsonObject { ["outcome"] = "success", ["itemId"] = itemId };
            case PlatformResult<DesktopMenuItem?>.Unavailable(var reason):
                return Outcome(new PlatformResult<PlatformUnit>.Unavailable(reason));
            case PlatformResult<DesktopMenuItem?>.Failed(var code, var diagnostic):
                return Outcome(new PlatformResult<PlatformUnit>.Failed(code, diagnostic));
            default:
                return Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown));
        }
    }

    private async Task<RunicHostedDialogRequest> DecideDialogAsync(RunicHostedDialogRequest proposed, CancellationToken cancellationToken)
    {
        RunicHostedDialogDecision decision;
        try { decision = await Policy.DialogAsync(proposed, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            throw PolicyFailed();
        }
        if (!decision.IsAllowed) throw new RejectedException("rejected", "refused", decision.Message ?? "The host's window policy refused the dialog.");
        var applied = decision.Request ?? proposed;
        if (!ReferenceEquals(applied.Window, proposed.Window) || applied.Kind != proposed.Kind ||
            applied.Kind == RunicHostedDialogKind.MessageBox && applied.MessageBox is null ||
            applied.Kind == RunicHostedDialogKind.SaveFile && string.IsNullOrEmpty(applied.SuggestedName))
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, null, "InvalidDialog");
            throw new RejectedException("failed", null, "The host's window policy returned another kind of dialog or another window.");
        }
        return applied;
    }

    private async Task<JsonObject> FileDialogAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        RunicHostedDialogKind kind;
        NativeFileDialogRequest native;
        try { (kind, native) = HostedWire.ReadFileDialog(message); }
        catch (ArgumentException error) { throw new RejectedException("rejected", "invalid-request", $"The file dialog is not valid: {error.Message}"); }
        var applied = await DecideDialogAsync(new RunicHostedDialogRequest(window, kind)
        {
            Title = native.Title,
            InitialDirectory = native.InitialDirectory,
            SuggestedName = native.SuggestedName,
            Filters = native.Filters,
            ShowFilterChoice = native.ShowFilterChoice,
        }, cancellationToken).ConfigureAwait(false);
        if (applied.InitialDirectory is { } directory && !Path.IsPathFullyQualified(directory))
            throw new RejectedException("failed", null, "The host's window policy returned a relative initial directory.");
        native = native with
        {
            Title = applied.Title,
            InitialDirectory = applied.InitialDirectory,
            SuggestedName = applied.Kind == RunicHostedDialogKind.SaveFile ? applied.SuggestedName : null,
            Filters = applied.Filters.IsDefault ? [] : applied.Filters,
            ShowFilterChoice = applied.ShowFilterChoice,
        };
        var dialogs = window.Dialogs(CreateDialogs);
        if (dialogs.Unavailable is { } unavailable || dialogs.Picker is not { } picker)
            return Outcome(new PlatformResult<PlatformUnit>.Unavailable(dialogs.Unavailable ?? PlatformUnavailableReason.BackendUnavailable));
        (string Path, IAsyncDisposable? Access, FileDialogFilter? Filter)[]? selected;
        try
        {
            selected = kind switch
            {
                RunicHostedDialogKind.OpenFolder or RunicHostedDialogKind.OpenFolders when picker is INativeDirectoryPicker folders =>
                    kind == RunicHostedDialogKind.OpenFolders
                        ? folders.SupportsMultipleDirectorySelection
                            ? (await folders.SelectDirectoriesAsync(native, cancellationToken).ConfigureAwait(false))?
                                .Select(static selection => (selection.Path, selection.Access, (FileDialogFilter?)null)).ToArray()
                            : throw new NativeBackendUnavailableException()
                        : await folders.SelectDirectoryAsync(native, cancellationToken).ConfigureAwait(false) is { } one
                            ? [(one.Path, one.Access, (FileDialogFilter?)null)]
                            : null,
                RunicHostedDialogKind.OpenFolder or RunicHostedDialogKind.OpenFolders => throw new NativeBackendUnavailableException(),
                RunicHostedDialogKind.OpenFiles when !picker.SupportsMultipleSelection => throw new NativeBackendUnavailableException(),
                _ => (await picker.SelectAsync(native, cancellationToken).ConfigureAwait(false))?
                    .Select(static selection => (selection.Path, (IAsyncDisposable?)selection.Access, selection.SelectedFilter)).ToArray(),
            };
        }
        catch (OwnerClosedException) { return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)); }
        catch (Exception error) when (error is NativeBackendUnavailableException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        }
        catch (UnauthorizedAccessException) { return Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied)); }
        catch (IOException) { return Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError)); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is null || selected.Length == 0) return Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed));
            // The guest opens the paths itself, with its own access: the host keeps no grant (host-protocol.md, Dialogs).
            var single = kind is RunicHostedDialogKind.OpenFile or RunicHostedDialogKind.SaveFile or RunicHostedDialogKind.OpenFolder;
            var paths = single ? selected.Take(1) : selected;
            var filter = selected[0].Filter is { } chosen && native.Filters.IndexOf(chosen) is >= 0 and var index ? index : (int?)null;
            return new JsonObject
            {
                ["outcome"] = "success",
                ["paths"] = new JsonArray([.. paths.Select(static selection => (JsonNode)selection.Path)]),
                ["filter"] = filter,
            };
        }
        finally
        {
            foreach (var selection in selected ?? [])
            {
                if (selection.Access is null) continue;
                try { await selection.Access.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }

    private async Task<JsonObject> MessageBoxAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        var box = HostedWire.ReadMessageBox(message);
        var applied = await DecideDialogAsync(new RunicHostedDialogRequest(window, RunicHostedDialogKind.MessageBox) { MessageBox = box },
            cancellationToken).ConfigureAwait(false);
        var dialogs = window.Dialogs(CreateDialogs);
        PlatformResult<DesktopMessageBoxButton?> result;
        try { result = await dialogs.MessageBoxes.ShowAsync(applied.MessageBox! with { OwnerPolicy = OwnerPolicy.RequireOwner }, cancellationToken).ConfigureAwait(false); }
        catch (ArgumentException error)
        {
            throw ReferenceEquals(applied.MessageBox, box)
                ? new RejectedException("rejected", "invalid-request", $"The message box is not valid: {error.Message}")
                : new RejectedException("failed", null, "The host's window policy returned a message box that is not valid.");
        }
        return result switch
        {
            PlatformResult<DesktopMessageBoxButton?>.Success(var button) => new JsonObject { ["outcome"] = "success", ["button"] = button?.Id },
            PlatformResult<DesktopMessageBoxButton?>.Unavailable(var reason) => Outcome(new PlatformResult<PlatformUnit>.Unavailable(reason)),
            PlatformResult<DesktopMessageBoxButton?>.Failed(var code, var diagnostic) => Outcome(new PlatformResult<PlatformUnit>.Failed(code, diagnostic)),
            _ => Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown)),
        };
    }

    private static string Limit(string message) => message.Length <= 1_024 ? message : message[..1_024];
}
