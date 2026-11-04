namespace Runic.Navigation;

// The ambient interaction invocation of Runic.Application.Views (RunicInteractionInvocation), which Runic.Navigation does
// not reference. A transition captures the invocation of the flow that starts it, such as a page's command that calls
// BackAsync without awaiting it, and its phases run in an invocation with the same identity until the transition ends,
// so a leave guard's question reaches the page whose command started the navigation, also after that command ended.
// Runic.Application.Views sets both delegates before any window session exists.
internal static class NavigationInvocationFlow
{
    // The current invocation, or null.
    internal static Func<object?>? Capture;

    // Enters an invocation with the captured one's identity on the current flow, until the returned scope is disposed.
    internal static Func<object, IDisposable>? Enter;
}
