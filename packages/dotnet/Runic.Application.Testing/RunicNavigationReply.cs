using System.Text.Json;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>
/// The reply to a window's <c>navigation</c> request (<c>navigation-path/1</c>):
/// <see cref="RunicWindowTestHost{TViewModel}.NavigateBackAsync"/> or <see cref="RunicWindowTestHost{TViewModel}.OpenNavigationPathAsync"/>.
/// </summary>
public sealed record RunicNavigationReply
{
    internal RunicNavigationReply(string json)
    {
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Succeeded = root.GetProperty("ok").GetBoolean();
        if (root.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.Object)
            Outcome = new(Enum.Parse<NavigationOutcomeKind>(outcome.GetProperty("kind").GetString()!),
                outcome.GetProperty("rejection") is { ValueKind: JsonValueKind.String } rejection
                    ? Enum.Parse<NavigationRejection>(rejection.GetString()!) : null)
            {
                Arrival = Enum.Parse<NavigationArrivalState>(outcome.GetProperty("arrival").GetString()!),
            };
        if (root.TryGetProperty("path", out var path))
        {
            HasPath = true;
            Path = path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        }
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            ErrorReason = error.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
    }

    /// <summary>Gets the reply's JSON.</summary>
    public string Json { get; }

    /// <summary>Gets whether the window answered <c>ok</c>.</summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the request's outcome, or <see langword="null"/> when no route matched the opened path (nothing navigated) or
    /// the request failed.
    /// </summary>
    public NavigationOutcome? Outcome { get; }

    /// <summary>
    /// Gets the window's route path after the request: what a page's URL shows, and what it reverts to when the request
    /// did not commit. <see langword="null"/> when the path region is empty or its page has no route.
    /// </summary>
    public string? Path { get; }

    /// <summary>Gets whether the window has a path region (<see cref="RunicPresentation.SyncNavigationPath"/>), so the reply carries <see cref="Path"/>.</summary>
    public bool HasPath { get; }

    /// <summary>Gets the failure's reason, such as <c>unavailable</c>, or <see langword="null"/>.</summary>
    public string? ErrorReason { get; }
}

/// <summary>A <c>navigation-path</c> push (<c>navigation-path/1</c>).</summary>
/// <param name="Path">The window's route path, or <see langword="null"/>.</param>
/// <param name="Operation"><c>push</c>, <c>back</c> or <c>replace</c>: how the page changes its browser history.</param>
public sealed record RunicNavigationPathPush(string? Path, string Operation);
