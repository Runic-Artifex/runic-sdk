using System.Globalization;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// WindowContentSessionOptions.FlowCultureFromLocale (S6): a page's setters, commands and validation run in the culture of
// the window's locale when the window opts in, in both MVVM flavors, and in the process's culture otherwise.
public sealed class LocaleCultureTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ToolkitInvocationsRunInTheWindowsCultureWhenItOptsInAsync(bool flow)
    {
        await using var context = new RunicModelContext();
        var model = new CultureToolkitViewModel();
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<CultureToolkitViewModel>(model,
            (transport, content, vm) => new CultureToolkitBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions
            {
                RootRoute = "cultureToolkit", ModelContext = context, ViewLocator = new TestViewLocator(), FlowCultureFromLocale = flow,
            });
        using var invariant = new ProcessCulture(CultureInfo.InvariantCulture);
        host.Content.Locale = "de-CH-u-co-phonebk";
        (await host.Root.SetAsync(vm => vm.Amount, 1234)).ThrowIfFailed();
        (await host.Root.ExecuteAsync(vm => vm.FormatCommand)).ThrowIfFailed();
        var expected = flow ? "de-CH" : "";
        Require(model.SetterCulture == expected, $"The setter ran in '{model.SetterCulture}', not '{expected}'.");
        // The group separator of de-CH depends on the ICU version: ' or ’.
        var swiss = CultureInfo.GetCultureInfo("de-CH");
        string Swiss(int value) => value.ToString("N0", swiss);
        Require(model.Formatted == (flow ? $"{Swiss(1234)} de-CH" : "1,234 "), $"The command formatted '{model.Formatted}'.");
        Require(ErrorMessage(host) == (flow ? $"{Swiss(1234)} is more than {Swiss(1000)}." : "1,234 is more than 1,000."),
            $"Validation formatted '{ErrorMessage(host)}'.");
        Require(CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture), "The caller's culture was not restored.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReactiveInvocationsRunInTheWindowsCultureWhenItOptsInAsync(bool flow)
    {
        await using var context = new RunicModelContext();
        var model = new CultureReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<CultureReactiveViewModel>(model,
            (transport, content, vm) => new CultureReactiveBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions
            {
                RootRoute = "cultureReactive", ModelContext = context, ViewLocator = new TestViewLocator(), FlowCultureFromLocale = flow,
            });
        using var invariant = new ProcessCulture(CultureInfo.InvariantCulture);
        host.Content.Locale = "de";
        (await host.Root.SetAsync(vm => vm.Amount, 1234)).ThrowIfFailed();
        (await host.Root.ExecuteAsync(vm => vm.FormatCommand)).ThrowIfFailed();
        Require(model.SetterCulture == (flow ? "de" : ""), $"The setter ran in '{model.SetterCulture}'.");
        Require(model.Formatted == (flow ? "1.234 de" : "1,234 "), $"The command formatted '{model.Formatted}'.");
        Require(ErrorMessage(host) == (flow ? "1.234 is more than 1.000." : "1,234 is more than 1,000."),
            $"Validation formatted '{ErrorMessage(host)}'.");
    }

    [Test]
    public void ALocaleWithoutACultureKeepsTheProcessCulture()
    {
        Require(LocaleCultureScope.Resolve("de-CH-x-test")?.Name == "de-CH", "The nearest parent's culture was not used.");
        using var invariant = new ProcessCulture(CultureInfo.InvariantCulture);
        using (LocaleCultureScope.Enter(null))
            Require(CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture), "null changed the culture.");
        using (LocaleCultureScope.Enter("fr"))
            Require(CultureInfo.CurrentCulture.Name == "fr" && CultureInfo.CurrentUICulture.Name == "fr", "fr was not entered.");
        Require(CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture), "The culture was not restored.");
    }

    private static string? ErrorMessage<T>(RunicWindowTestHost<T> host) where T : class
    {
        using var snapshot = host.RawSnapshot();
        var errors = snapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
        return errors.GetArrayLength() == 0 ? null : errors[0].GetProperty("message").GetString();
    }

    // Sets the test thread's cultures, as a process in another culture runs.
    private sealed class ProcessCulture : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public ProcessCulture(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
