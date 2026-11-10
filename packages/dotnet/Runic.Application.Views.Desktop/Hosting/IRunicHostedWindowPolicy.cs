using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// A host's rules for what a guest application may do with the windows the host shows for it (the Runic host protocol,
/// <c>runic-host/1</c>): which windows open, with which native options, which presentation requests apply, and which
/// menus and dialogs the guest shows.
/// </summary>
/// <remarks>
/// <para>
/// Every member has a default that allows the request unchanged, so a policy implements only what it restricts. A later
/// release can add members for later protocol features, such as window services, with defaults that allow them. A
/// policy's members can run concurrently, for different windows and requests.
/// </para>
/// <code>
/// sealed class OneWindowPolicy : IRunicHostedWindowPolicy
/// {
///     public ValueTask&lt;RunicHostedWindowDecision&gt; OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =&gt;
///         ValueTask.FromResult(request.Application.Windows.Count == 0
///             ? RunicHostedWindowDecision.Allow(request.WindowOptions with { Placement = SavedPlacement(request) })
///             : RunicHostedWindowDecision.Refuse("This host shows one window of each tool."));
///
///     public RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request) =&gt;
///         request.Kind == RunicHostedPresentationKind.SetFullScreen ? RunicHostedPresentationDecision.Refuse() : RunicHostedPresentationDecision.Allow();
/// }
/// </code>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public interface IRunicHostedWindowPolicy
{
    /// <summary>
    /// Decides whether a window the guest asks for opens, and with which options. The default opens it with
    /// <see cref="RunicHostedWindowRequest.WindowOptions"/>.
    /// </summary>
    /// <param name="request">The guest's request and the options the host would open the window with.</param>
    /// <param name="cancellationToken">Cancelled when the guest goes away before the decision.</param>
    /// <returns>The decision. A refusal fails the guest's open with <c>hosted-window-refused</c>.</returns>
    /// <remarks>A policy that throws refuses the window as failed; the guest's open fails with <c>hosted-window-failed</c>.</remarks>
    ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RunicHostedWindowDecision.Allow());

    /// <summary>
    /// Decides one presentation request of the guest for an open window, such as presenting it, entering full screen or
    /// resizing it. The default applies it unchanged.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>
    /// The decision: apply it, apply a changed request (a clamp, such as a smaller size), or refuse it, which the guest
    /// receives as <see cref="PlatformFailureCode.PermissionDenied"/>.
    /// </returns>
    RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request) => RunicHostedPresentationDecision.Allow();

    /// <summary>
    /// Decides the menus a guest shows in one of its windows (<c>menus/1</c>): its menu bar, each time it or an item's
    /// state changes, and each context menu. The default shows them unchanged.
    /// </summary>
    /// <param name="request">The menus as the host would show them.</param>
    /// <returns>
    /// The decision: show them, show changed menus (<see cref="RunicHostedMenuDecision.Apply"/>), such as without an item
    /// or with an item of the host's own, or refuse them, which the guest receives as
    /// <see cref="PlatformFailureCode.PermissionDenied"/>; a refused menu bar shows no menus of the guest.
    /// </returns>
    /// <remarks>Called on any thread, and for a bar on each change of an item's state, so keep it cheap.</remarks>
    RunicHostedMenuDecision Menu(RunicHostedMenuRequest request) => RunicHostedMenuDecision.Allow();

    /// <summary>
    /// Decides a file dialog or message box a guest asks to show over one of its windows (<c>dialogs/1</c>). The default
    /// shows it unchanged.
    /// </summary>
    /// <param name="request">The dialog.</param>
    /// <param name="cancellationToken">Cancelled when the guest gives the dialog up, the window closes or the guest goes away.</param>
    /// <returns>
    /// The decision: show it, show a changed dialog of the same kind (<see cref="RunicHostedDialogDecision.Apply"/>),
    /// such as with another initial directory, or refuse it, which the guest receives as
    /// <see cref="PlatformFailureCode.PermissionDenied"/>.
    /// </returns>
    /// <remarks>A policy that throws fails the guest's dialog as failed.</remarks>
    ValueTask<RunicHostedDialogDecision> DialogAsync(RunicHostedDialogRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RunicHostedDialogDecision.Allow());
}

/// <summary>Which menus of a guest a <see cref="RunicHostedMenuRequest"/> is.</summary>
/// <remarks>Open: a later release can add kinds. Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicHostedMenuKind
{
    /// <summary>The menu bar of the window (<c>set-menu-bar</c>, <c>update-menu-items</c>).</summary>
    MenuBar,

    /// <summary>A context menu over the window (<c>show-context-menu</c>).</summary>
    ContextMenu,
}

/// <summary>Menus a guest application shows in one of its windows: its menu bar or a context menu.</summary>
/// <remarks>
/// <para>
/// <see cref="Entries"/> are the menus as the host shows them. A guest's item runs the guest's command: its
/// <see cref="DesktopMenuItem.CommandId"/> is <see cref="GuestCommandId"/> and <see cref="GuestItemId"/> reads the guest's
/// id from it. An item a policy adds with a command id of its own runs the host application's command, as an item of
/// <see cref="DesktopHost.MenuBar"/> does (<see cref="DesktopHost.MenuCommands"/>).
/// </para>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedMenuRequest
{
    internal RunicHostedMenuRequest(RunicHostedWindow window, RunicHostedMenuKind kind, ImmutableArray<DesktopMenuEntry> entries)
    {
        Window = window;
        Kind = kind;
        Entries = entries;
    }

    /// <summary>The command id of the guest's items in <see cref="Entries"/>.</summary>
    public const string GuestCommandId = Hosting.HostedWire.ItemCommand;

    /// <summary>The window.</summary>
    public RunicHostedWindow Window { get; }

    /// <summary>Whether these are the window's menu bar or a context menu.</summary>
    public RunicHostedMenuKind Kind { get; }

    /// <summary>
    /// The entries: for <see cref="RunicHostedMenuKind.MenuBar"/> the bar's <see cref="DesktopMenu"/>s, in the guest's
    /// order, empty for none; for <see cref="RunicHostedMenuKind.ContextMenu"/> the menu's entries. Disabled items, check
    /// marks and titles are the guest's current ones.
    /// </summary>
    public ImmutableArray<DesktopMenuEntry> Entries { get; init; }

    /// <summary>Where a context menu opens, in CSS pixels of the page's viewport; <see langword="null"/> at the pointer and for a menu bar.</summary>
    public DesktopContextMenuOptions? Position { get; init; }

    /// <summary>Gets the guest's id of an item of <see cref="Entries"/>, or <see langword="null"/> for an item that is not the guest's.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The id.</returns>
    public static string? GuestItemId(DesktopMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Hosting.HostedWire.ItemId(item);
    }
}

/// <summary>A host policy's decision about a guest's menus.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedMenuDecision
{
    private static readonly RunicHostedMenuDecision Allowed = new(true, null, null);

    private RunicHostedMenuDecision(bool allowed, RunicHostedMenuRequest? request, string? message)
    {
        IsAllowed = allowed;
        Request = request;
        Message = message;
    }

    /// <summary>Whether the menus show.</summary>
    public bool IsAllowed { get; }

    /// <summary>The menus that show instead of the guest's, or <see langword="null"/> for the guest's.</summary>
    public RunicHostedMenuRequest? Request { get; }

    /// <summary>For a refusal, why, as the guest's failure message; it must not carry a secret.</summary>
    public string? Message { get; }

    /// <summary>Shows the guest's menus.</summary>
    /// <returns>The decision.</returns>
    public static RunicHostedMenuDecision Allow() => Allowed;

    /// <summary>
    /// Shows changed menus instead, such as <c>request with { Entries = … }</c> without an item. The host shows them as
    /// they are, in the request's window, which must be the guest's; a menu bar's entries must all be
    /// <see cref="DesktopMenu"/>s.
    /// </summary>
    /// <param name="request">The menus to show.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedMenuDecision Apply(RunicHostedMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(true, request, null);
    }

    /// <summary>Refuses the menus: the guest receives <see cref="PlatformFailureCode.PermissionDenied"/>.</summary>
    /// <param name="message">Why, for the guest; at most 1,024 characters are sent.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedMenuDecision Refuse(string? message = null) => new(false, null, message);
}

/// <summary>The kind of a guest's dialog (<c>dialogs/1</c>).</summary>
/// <remarks>Open: a later release can add kinds. Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicHostedDialogKind
{
    /// <summary>A file dialog that selects one existing file (<c>open</c>).</summary>
    OpenFile,

    /// <summary>A file dialog that selects one or more existing files (<c>open-multiple</c>).</summary>
    OpenFiles,

    /// <summary>A file dialog that selects a save destination (<c>save</c>).</summary>
    SaveFile,

    /// <summary>A dialog that selects one directory (<c>folder</c>).</summary>
    OpenFolder,

    /// <summary>A dialog that selects one or more directories (<c>folder-multiple</c>).</summary>
    OpenFolders,

    /// <summary>A message box (<c>message-box</c>).</summary>
    MessageBox,
}

/// <summary>
/// A file dialog or message box a guest application asks its host to show over one of its windows. Only the members of
/// its <see cref="Kind"/> are set.
/// </summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedDialogRequest
{
    internal RunicHostedDialogRequest(RunicHostedWindow window, RunicHostedDialogKind kind)
    {
        Window = window;
        Kind = kind;
    }

    /// <summary>The window the dialog belongs to; the host shows it owned by that window.</summary>
    public RunicHostedWindow Window { get; }

    /// <summary>What the guest asks for.</summary>
    public RunicHostedDialogKind Kind { get; }

    /// <summary>A file dialog's title, or <see langword="null"/> for the platform's own.</summary>
    public string? Title { get; init; }

    /// <summary>The absolute directory a file dialog opens in, or <see langword="null"/> for the platform's choice.</summary>
    public string? InitialDirectory { get; init; }

    /// <summary>The file name a save dialog suggests.</summary>
    public string? SuggestedName { get; init; }

    /// <summary>The file types an open or save dialog offers, the first selected; empty for every file.</summary>
    public ImmutableArray<FileDialogFilter> Filters { get; init; } = [];

    /// <summary>Whether a file dialog offers a choice between <see cref="Filters"/> where it does not by itself.</summary>
    public bool ShowFilterChoice { get; init; }

    /// <summary>The message box of <see cref="RunicHostedDialogKind.MessageBox"/>.</summary>
    public DesktopMessageBox? MessageBox { get; init; }
}

/// <summary>A host policy's decision about a guest's dialog.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedDialogDecision
{
    private static readonly RunicHostedDialogDecision Allowed = new(true, null, null);

    private RunicHostedDialogDecision(bool allowed, RunicHostedDialogRequest? request, string? message)
    {
        IsAllowed = allowed;
        Request = request;
        Message = message;
    }

    /// <summary>Whether the dialog shows.</summary>
    public bool IsAllowed { get; }

    /// <summary>The dialog that shows instead of the guest's, or <see langword="null"/> for the guest's.</summary>
    public RunicHostedDialogRequest? Request { get; }

    /// <summary>For a refusal, why, as the guest's failure message; it must not carry a secret.</summary>
    public string? Message { get; }

    /// <summary>Shows the guest's dialog.</summary>
    /// <returns>The decision.</returns>
    public static RunicHostedDialogDecision Allow() => Allowed;

    /// <summary>
    /// Shows a changed dialog instead, such as <c>request with { InitialDirectory = … }</c>. It keeps the request's
    /// <see cref="RunicHostedDialogRequest.Kind"/> and window; another kind or window fails the guest's dialog.
    /// </summary>
    /// <param name="request">The dialog to show.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedDialogDecision Apply(RunicHostedDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(true, request, null);
    }

    /// <summary>Refuses the dialog: the guest receives <see cref="PlatformFailureCode.PermissionDenied"/>.</summary>
    /// <param name="message">Why, for the guest; at most 1,024 characters are sent.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedDialogDecision Refuse(string? message = null) => new(false, null, message);
}

/// <summary>A window a guest application asks its host to open (<c>window-open</c>).</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedWindowRequest
{
    internal RunicHostedWindowRequest(RunicHostedApplication application, string windowId, string type, string? title,
        RunicHostedWindow? owner, bool isModal, bool guardsClose, DesktopPermissionGrant permissions, DesktopWindowOptions windowOptions)
    {
        Application = application;
        WindowId = windowId;
        Type = type;
        Title = title;
        Owner = owner;
        IsModal = isModal;
        GuardsClose = guardsClose;
        Permissions = permissions;
        WindowOptions = windowOptions;
    }

    /// <summary>The guest that asks.</summary>
    public RunicHostedApplication Application { get; }

    /// <summary>The guest's id for the window, unique for the guest.</summary>
    public string WindowId { get; }

    /// <summary>The guest's Window type, such as <c>MyApp.DocumentWindow`1[Report]</c>, or an empty string. Informational.</summary>
    public string Type { get; }

    /// <summary>The title the window shows until its page sets one, or <see langword="null"/>.</summary>
    public string? Title { get; }

    /// <summary>The open window of the same guest that owns the new one, or <see langword="null"/>.</summary>
    public RunicHostedWindow? Owner { get; }

    /// <summary>Whether the new window is modal to <see cref="Owner"/>.</summary>
    public bool IsModal { get; }

    /// <summary>Whether the guest decides about the window's close, so that a user's close asks it first.</summary>
    public bool GuardsClose { get; }

    /// <summary>
    /// The permissions the guest's page asks for. <see cref="WindowOptions"/> grants only those that the host grants its
    /// own windows (<see cref="RunicDesktopHost.WindowOptions"/>): a host that grants nothing grants the guest nothing,
    /// and the page cannot pass a prompt the host's own pages would see. A policy grants more explicitly, with
    /// <c>RunicHostedWindowDecision.Allow(request.WindowOptions with { AllowedPermissions = request.Permissions })</c>.
    /// </summary>
    public DesktopPermissionGrant Permissions { get; }

    /// <summary>
    /// The options the host opens the window with unless the policy decides others: <see cref="RunicDesktopHost.WindowOptions"/>
    /// with the guest's size, minimum size, owner and modality, and the guest's permissions that the host grants its own
    /// windows (<see cref="Permissions"/>).
    /// </summary>
    /// <remarks>
    /// Whatever the policy returns, the host opens the window embedded, without a browser fallback, routes its close
    /// through the guest (<see cref="DesktopWindowOptions.ConfirmCloseAsync"/>) and loads the page with the session's own
    /// data store.
    /// </remarks>
    public DesktopWindowOptions WindowOptions { get; }
}

/// <summary>A host policy's decision about a window a guest asked for.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedWindowDecision
{
    private RunicHostedWindowDecision(bool allowed, DesktopWindowOptions? windowOptions, string? message)
    {
        IsAllowed = allowed;
        WindowOptions = windowOptions;
        Message = message;
    }

    /// <summary>Whether the window opens.</summary>
    public bool IsAllowed { get; }

    /// <summary>The options the window opens with, or <see langword="null"/> for the request's.</summary>
    public DesktopWindowOptions? WindowOptions { get; }

    /// <summary>For a refusal, why, as the guest's failure message; it must not carry a secret.</summary>
    public string? Message { get; }

    /// <summary>Opens the window.</summary>
    /// <param name="windowOptions">
    /// The options to open it with, typically <see cref="RunicHostedWindowRequest.WindowOptions"/> changed with a
    /// <see langword="with"/> expression, such as a saved placement; <see langword="null"/> for the request's.
    /// </param>
    /// <returns>The decision.</returns>
    public static RunicHostedWindowDecision Allow(DesktopWindowOptions? windowOptions = null) => new(true, windowOptions, null);

    /// <summary>Refuses the window: the guest's open fails with <c>hosted-window-refused</c>.</summary>
    /// <param name="message">Why, for the guest's failure message; at most 1,024 characters are sent.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedWindowDecision Refuse(string? message = null) => new(false, null, message);
}

/// <summary>The kind of a guest's presentation request (the Runic host protocol's presentation requests).</summary>
/// <remarks>
/// Open: a later release can add kinds for later protocol requests; a policy treats a kind it does not know as it sees
/// fit. Experimental; see <c>docs/experimental.md</c>.
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicHostedPresentationKind
{
    /// <summary><c>present</c>: show the window, restore it and bring it to the front.</summary>
    Present,

    /// <summary><c>minimize</c>.</summary>
    Minimize,

    /// <summary><c>set-visible</c>: <see cref="RunicHostedPresentationRequest.Value"/> shows or hides the window.</summary>
    SetVisible,

    /// <summary><c>set-full-screen</c>: <see cref="RunicHostedPresentationRequest.Value"/> enters or leaves full screen.</summary>
    SetFullScreen,

    /// <summary><c>toggle-maximized</c>.</summary>
    ToggleMaximized,

    /// <summary><c>resize</c>: <see cref="RunicHostedPresentationRequest.Width"/> and <see cref="RunicHostedPresentationRequest.Height"/>.</summary>
    Resize,

    /// <summary><c>move</c>: <see cref="RunicHostedPresentationRequest.X"/> and <see cref="RunicHostedPresentationRequest.Y"/>.</summary>
    Move,

    /// <summary><c>set-document-edited</c>: <see cref="RunicHostedPresentationRequest.Value"/>.</summary>
    SetDocumentEdited,

    /// <summary><c>set-represented-file</c>: <see cref="RunicHostedPresentationRequest.Path"/>.</summary>
    SetRepresentedFile,

    /// <summary><c>set-color-scheme</c>: <see cref="RunicHostedPresentationRequest.ColorScheme"/>.</summary>
    SetColorScheme,

    /// <summary><c>set-layout-direction</c>: <see cref="RunicHostedPresentationRequest.LayoutDirection"/>.</summary>
    SetLayoutDirection,

    /// <summary><c>announce</c>: <see cref="RunicHostedPresentationRequest.Text"/> with <see cref="RunicHostedPresentationRequest.Priority"/>.</summary>
    Announce,
}

/// <summary>
/// A guest's presentation request for one of its windows. Only the members of its <see cref="Kind"/> are set; a policy
/// clamps a request by returning a copy with other values (<see cref="RunicHostedPresentationDecision.Apply"/>).
/// </summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedPresentationRequest
{
    internal RunicHostedPresentationRequest(RunicHostedWindow window, RunicHostedPresentationKind kind)
    {
        Window = window;
        Kind = kind;
    }

    /// <summary>The window.</summary>
    public RunicHostedWindow Window { get; }

    /// <summary>What the guest asks for.</summary>
    public RunicHostedPresentationKind Kind { get; }

    /// <summary>The flag of <see cref="RunicHostedPresentationKind.SetVisible"/>, <see cref="RunicHostedPresentationKind.SetFullScreen"/> and <see cref="RunicHostedPresentationKind.SetDocumentEdited"/>.</summary>
    public bool? Value { get; init; }

    /// <summary>The content width of <see cref="RunicHostedPresentationKind.Resize"/>, in device-independent pixels.</summary>
    public int? Width { get; init; }

    /// <summary>The content height of <see cref="RunicHostedPresentationKind.Resize"/>, in device-independent pixels.</summary>
    public int? Height { get; init; }

    /// <summary>The horizontal position of <see cref="RunicHostedPresentationKind.Move"/>, in the host's desktop coordinates.</summary>
    public int? X { get; init; }

    /// <summary>The vertical position of <see cref="RunicHostedPresentationKind.Move"/>, in the host's desktop coordinates.</summary>
    public int? Y { get; init; }

    /// <summary>The absolute path of <see cref="RunicHostedPresentationKind.SetRepresentedFile"/>, or <see langword="null"/> for none.</summary>
    public string? Path { get; init; }

    /// <summary>The scheme of <see cref="RunicHostedPresentationKind.SetColorScheme"/>.</summary>
    public DesktopColorScheme? ColorScheme { get; init; }

    /// <summary>The direction of <see cref="RunicHostedPresentationKind.SetLayoutDirection"/>.</summary>
    public DesktopLayoutDirection? LayoutDirection { get; init; }

    /// <summary>The text of <see cref="RunicHostedPresentationKind.Announce"/>.</summary>
    public string? Text { get; init; }

    /// <summary>The priority of <see cref="RunicHostedPresentationKind.Announce"/>.</summary>
    public DesktopAnnouncementPriority? Priority { get; init; }
}

/// <summary>A host policy's decision about a guest's presentation request.</summary>
/// <remarks>Experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedPresentationDecision
{
    private static readonly RunicHostedPresentationDecision Allowed = new(true, null, null);

    private RunicHostedPresentationDecision(bool allowed, RunicHostedPresentationRequest? request, string? message)
    {
        IsAllowed = allowed;
        Request = request;
        Message = message;
    }

    /// <summary>Whether the request applies.</summary>
    public bool IsAllowed { get; }

    /// <summary>The request that applies instead of the guest's, or <see langword="null"/> for the guest's.</summary>
    public RunicHostedPresentationRequest? Request { get; }

    /// <summary>For a refusal, why, as the guest's failure message; it must not carry a secret.</summary>
    public string? Message { get; }

    /// <summary>Applies the guest's request.</summary>
    /// <returns>The decision.</returns>
    public static RunicHostedPresentationDecision Allow() => Allowed;

    /// <summary>
    /// Applies a changed request instead, such as a clamped size: <c>request with { Width = Math.Min(request.Width ?? 0, 1600) }</c>.
    /// The host applies it as it is, to its <see cref="RunicHostedPresentationRequest.Window"/>, which must be a window of
    /// the same guest; another guest's window fails the guest's request.
    /// </summary>
    /// <param name="request">The request to apply.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedPresentationDecision Apply(RunicHostedPresentationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(true, request, null);
    }

    /// <summary>Refuses the request: the guest receives <see cref="PlatformFailureCode.PermissionDenied"/>.</summary>
    /// <param name="message">Why, for the guest; at most 1,024 characters are sent.</param>
    /// <returns>The decision.</returns>
    public static RunicHostedPresentationDecision Refuse(string? message = null) => new(false, null, message);
}
