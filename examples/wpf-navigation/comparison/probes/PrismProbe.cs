using Prism.Navigation;
using Prism.Navigation.Regions;

namespace Comparison.Probes;

// The real Prism.Core 9.0.537 RegionNavigationJournal driven by a port of the request/confirm/commit order of
// Prism.Wpf RegionNavigationService (src/Wpf/Prism.Wpf/Navigation/Regions/RegionNavigationService.cs @ 9.0.537):
//   :112      _currentNavigationContext = new NavigationContext(...)   (a later request replaces it)
//   :130-150  IConfirmNavigationRequest.ConfirmNavigationRequest(ctx, canNavigate => ...)
//   :138      continue only if _currentNavigationContext == navigationContext && canNavigate, else fail
//   :229      Journal.RecordNavigation(entry, persistInHistory)   before
//   :235      navigationCallback(new NavigationResult(ctx, true))
// RegionNavigationService itself needs WPF (FrameworkElement, IRegion), so it cannot run on Linux.
public sealed class RegionNavigationServiceModel : INavigateAsync
{
    private NavigationContext? _currentNavigationContext;

    public RegionNavigationServiceModel(RegionNavigationJournal journal)
    {
        Journal = journal;
        journal.NavigationTarget = this;
    }

    public RegionNavigationJournal Journal { get; }
    public string Current { get; private set; } = "";
    public HashSet<string> DirtyPages { get; } = [];
    public bool ModalConfirm { get; set; }
    public List<Action<bool>> PendingPrompts { get; } = [];

    public void RequestNavigate(Uri target, Action<NavigationResult> navigationCallback, INavigationParameters? navigationParameters)
    {
        var context = new NavigationContext(null!, target, navigationParameters);
        _currentNavigationContext = context;
        if (DirtyPages.Contains(Current))
        {
            PendingPrompts.Add(canNavigate =>
            {
                if (_currentNavigationContext == context && canNavigate)
                {
                    Execute(context, navigationCallback);
                }
                else
                {
                    navigationCallback(new NavigationResult(context, false));
                }
            });
            return;
        }

        Execute(context, navigationCallback);
    }

    private void Execute(NavigationContext context, Action<NavigationResult> navigationCallback)
    {
        DirtyPages.Remove(Current); // the guard's discard
        Current = context.Uri.OriginalString;
        Journal.RecordNavigation(new RegionNavigationJournalEntry { Uri = context.Uri, Parameters = context.Parameters }, true);
        navigationCallback(new NavigationResult(context, true));
    }

    public void Navigate(string page) => RequestNavigate(new Uri(page, UriKind.Relative), _ => { }, null);
}

public static class PrismProbe
{
    private static RegionNavigationServiceModel Setup(params string[] pages)
    {
        var service = new RegionNavigationServiceModel(new RegionNavigationJournal());
        foreach (var page in pages)
        {
            service.Navigate(page);
        }

        return service;
    }

    // Prism exposes no stack listing; walk it with the public API on a copy would navigate, so read it via reflection.
    private static string Journal(RegionNavigationServiceModel s)
    {
        string Names(string field) =>
            string.Join(", ", ((Stack<IRegionNavigationJournalEntry>)typeof(RegionNavigationJournal)
                .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(s.Journal)!).Reverse().Select(e => e.Uri.OriginalString));
        return $"shown {s.Current}; back [{Names("backStack")}], current {s.Journal.CurrentEntry?.Uri.OriginalString ?? "null"}, forward [{Names("forwardStack")}]";
    }

    public static IEnumerable<string> Run()
    {
        {
            var s = Setup("List", "A", "B");
            s.Journal.GoBack();
            s.Journal.GoBack();
            yield return $"P1 no pending guard, Journal.GoBack() twice from B: {Journal(s)}";
        }
        {
            var s = Setup("List", "A", "B");
            s.DirtyPages.Add("B");
            s.Journal.GoBack();
            s.Journal.GoBack(); // second click while the confirm for the first is open
            var prompts = s.PendingPrompts.Count;
            s.PendingPrompts[1](true); // newest prompt answered first (forced by stacked modal dialogs)
            s.PendingPrompts[0](true);
            yield return $"P2 dirty B, two GoBack() while confirming ({prompts} prompts), answered newest-first yes/yes: {Journal(s)}";
        }
        {
            var s = Setup("List", "A", "B");
            s.DirtyPages.Add("B");
            s.Journal.GoBack();
            s.Journal.GoBack();
            var prompts = s.PendingPrompts.Count;
            s.PendingPrompts[0](true); // oldest answered first (non-modal confirm), superseded -> fails
            s.PendingPrompts[1](true);
            yield return $"P3 dirty B, two GoBack() while confirming ({prompts} prompts), answered oldest-first yes/yes: {Journal(s)}";
        }
        {
            var s = Setup("List", "A", "B");
            s.DirtyPages.Add("B");
            s.Journal.GoBack();
            s.Journal.GoBack();
            s.PendingPrompts[0](false); // first prompt: Cancel
            s.PendingPrompts[1](true);
            yield return $"P4 dirty B, two GoBack(), first prompt Cancel then second OK: {Journal(s)}";
        }
        {
            var s = Setup("List", "A", "B");
            s.DirtyPages.Add("B");
            s.Journal.GoBack();
            s.Journal.GoBack();
            s.PendingPrompts[0](true);
            s.PendingPrompts[1](true);
            s.Journal.GoBack(); // the user presses Back again on the corrupted journal
            var third = Journal(s);
            s.Journal.GoBack();
            yield return $"P5 after P3, Back again: {third}; and again: {Journal(s)}";
        }
    }
}
