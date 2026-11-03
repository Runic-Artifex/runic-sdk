using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// RunicViewDriver.Present (A4): a test presentation answers the ViewModel's questions and interactions with the
// handlers the test registers, and takes its view requests, through the generated Bridges of ViewRequestCodegenFixture.
public sealed class TestPresentationTests
{
    [Test]
    public async Task ATestPresentationAnswersAQuestionAndTakesViewRequestsAsync()
    {
        var model = new ToolkitViewRequestViewModel();
        // Focus is NextMounted: a request raised before any page handles it waits for the first one.
        model.Focus.Raise(new ViewRequestFocusTarget("title", SelectAll: false));
        using var host = new RunicWindowTestHost<ToolkitViewRequestViewModel>(model,
            (transport, content, vm) => new ToolkitViewRequestBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "toolkitViewRequest", ViewLocator = new TestViewLocator() });

        using var page = await host.Root.PresentAsync();
        var kept = await page.NextViewRequestAsync();
        Require(kept.Name == "focus" && kept.Input<ViewRequestFocusTarget>() == new ViewRequestFocusTarget("title", false),
            $"The kept request did not reach the presentation: {kept}");

        page.Handle(vm => vm.ConfirmDiscard, request => request.Title == "Draft");
        (await host.Root.ExecuteAsync(vm => vm.DiscardCommand)).ThrowIfFailed();
        Require(model.Title.Length == 0, "The presentation's answer did not reach the ViewModel.");

        model.Focus.Raise(new ViewRequestFocusTarget("body", SelectAll: true));
        var focus = await page.NextViewRequestAsync();
        Require(focus.Route == "toolkitViewRequest" && focus.Presentation == page.Id
            && focus.InputJson == """{"field":"body","selectAll":true}""",
            $"The request is not the raised one: {focus}");
        Require(page.TakeViewRequests().Count == 0, "A request was delivered twice.");
    }

    [Test]
    public async Task AReactiveInteractionIsAnsweredWithExplicitTypesAsync()
    {
        var model = new ReactiveViewRequestViewModel();
        using var host = new RunicWindowTestHost<ReactiveViewRequestViewModel>(model,
            (transport, content, vm) => new ReactiveViewRequestBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactiveViewRequest", ViewLocator = new TestViewLocator() });
        using var page = await host.Root.PresentAsync();
        await page.HandleAsync<ViewRequestDiscard, bool>(vm => vm.ConfirmDiscard,
            (request, _) => ValueTask.FromResult(request.Title == "Draft"));
        (await host.Root.ExecuteAsync(vm => vm.DiscardCommand)).ThrowIfFailed();
        Require(model.Title.Length == 0, "The asynchronous answer did not reach the ViewModel.");

        model.Notify.Raise("Saved");
        Require((await page.NextViewRequestAsync()).Input<string>() == "Saved", "The broadcast did not reach the presentation.");

        // A presentation that Mount opened registers no requests, so with two pages the broadcast reaches only this one.
        await host.Root.MountAsync();
        model.Notify.Raise("Again");
        Require((await page.NextViewRequestAsync()).Input<string>() == "Again", "The broadcast did not reach the presentation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
