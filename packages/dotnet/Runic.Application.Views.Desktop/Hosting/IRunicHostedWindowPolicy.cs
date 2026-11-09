using System.Diagnostics.CodeAnalysis;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// A host's rules for what a guest application may do with the windows the host shows for it (the Runic host protocol,
/// <c>runic-host/1</c>): which windows open, with which native options, and which presentation requests apply.
/// </summary>
/// <remarks>
/// <para>
/// Every member has a default that allows the request unchanged, so a policy implements only what it restricts. A later
/// release can add members for later protocol features, such as menus and dialogs, with defaults that allow them. A
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

    /// <summary>The permissions the guest's page asks for; <see cref="WindowOptions"/> grants them unless the policy changes it.</summary>
    public DesktopPermissionGrant Permissions { get; }

    /// <summary>
    /// The options the host opens the window with unless the policy decides others: <see cref="RunicDesktopHost.WindowOptions"/>
    /// with the guest's size, minimum size, owner, modality and permissions.
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
    /// Its <see cref="RunicHostedPresentationRequest.Window"/> and <see cref="RunicHostedPresentationRequest.Kind"/> must be the guest's.
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
