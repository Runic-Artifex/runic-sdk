using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using RunicMarkup;
using RunicToolkit.Samples.Cwhtml.Components;

namespace RunicToolkit.Samples.Cwhtml.Components.Tests;

internal static class Program
{
    private static int _failures;

    public static int Main()
    {
        Run("Bootstrap form and validation components encode application text", FormAndValidation);
        Run("Bootstrap navigation, modal, and toast expose accessible semantics", CompositePatterns);
        Run("styling-neutral component composes from compiled cwhtml", StylingNeutralCwhtml);
        Run("sample components do not reference frontend asset packages", AssetDependenciesRemainLocal);

        Console.WriteLine(_failures == 0
            ? "All 4 cwhtml sample component tests passed."
            : $"{_failures} cwhtml sample component test(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void FormAndValidation()
    {
        IHtmlRenderable input = Html.Fragment(
            "Ada",
            static (string value, ref Utf8HtmlWriter writer, TemplateContext _) =>
            {
                writer.BeginElement("input"u8);
                writer.WriteAttribute("id"u8, "display-name");
                writer.WriteAttribute("class"u8, "form-control");
                writer.WriteAttribute("value"u8, value);
            });
        var field = new BootstrapFormGroup(
            "display-name",
            "Display name",
            input,
            "Shown to collaborators.",
            "Use <two> or more characters.");
        string html = Render(field);

        Contains(html, "class=\"mb-3\"");
        Contains(html, "for=\"display-name\"");
        Contains(html, "value=\"Ada\"");
        Contains(html, "class=\"form-text\"");
        Contains(html, "role=\"alert\"");
        Contains(html, "Use &lt;two&gt; or more characters.");
    }

    private static void CompositePatterns()
    {
        True(HtmlUrl.TryCreate("#tasks", out HtmlUrl tasks));
        True(HtmlUrl.TryCreate("#settings", out HtmlUrl settings));
        var navigation = new BootstrapNavigation(
            "Workspace",
            "Todo",
            [
                new BootstrapNavigationItem("Tasks", tasks, isCurrent: true),
                new BootstrapNavigationItem("Settings", settings),
            ]);
        string navigationHtml = Render(navigation);
        Contains(navigationHtml, "<nav");
        Contains(navigationHtml, "aria-current=\"page\"");
        Contains(navigationHtml, "href=\"#settings\"");

        var modal = new BootstrapModal(
            "confirm-delete",
            "Delete task?",
            ComponentContent.Text("This cannot be undone."),
            ComponentContent.Text("Choose cancel or delete."));
        string modalHtml = Render(modal);
        Contains(modalHtml, "role=\"dialog\"");
        Contains(modalHtml, "aria-modal=\"true\"");
        Contains(modalHtml, "aria-labelledby=\"confirm-delete-title\"");

        string toastHtml = Render(new BootstrapToast("Saved", "Your changes are ready."));
        Contains(toastHtml, "class=\"toast show\"");
        Contains(toastHtml, "role=\"status\"");
        Contains(toastHtml, "aria-live=\"polite\"");
    }

    private static void StylingNeutralCwhtml()
    {
        var view = new StylingNeutralExampleView(
            new StylingNeutralExampleModel("Draft saved."));
        string html = Render(view);

        Contains(html, "class=\"paper-panel\"");
        Contains(html, "class=\"paper-status\"");
        Contains(html, "role=\"status\"");
        Contains(html, "Draft saved.");
        DoesNotContain(html, "bootstrap");
        DoesNotContain(html, "btn-");
        DoesNotContain(html, "fa-");
    }

    private static void AssetDependenciesRemainLocal()
    {
        AssemblyName[] references = typeof(BootstrapToast).Assembly.GetReferencedAssemblies();
        True(!references.Any(static reference =>
            reference.Name?.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) == true));
        True(!references.Any(static reference =>
            reference.Name?.Contains("FontAwesome", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static string Render(IHtmlRenderable component)
    {
        var context = new TemplateContext(CultureInfo.InvariantCulture);
        string? baseline = null;
        for (int repetition = 0; repetition < 3; repetition++)
        {
            var output = new ArrayBufferWriter<byte>();
            var writer = new Utf8HtmlWriter(output);
            component.Render(ref writer, context);
            writer.Complete();
            string candidate = Encoding.UTF8.GetString(output.WrittenSpan);
            if (baseline is not null &&
                !string.Equals(baseline, candidate, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Rendering was nondeterministic.");
            }

            baseline = candidate;
        }

        return baseline!;
    }

    private static void Run(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL {name}");
            Console.Error.WriteLine(exception);
        }
    }

    private static void Contains(string actual, string expected)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected rendered HTML to contain: {expected}");
        }
    }

    private static void DoesNotContain(string actual, string unexpected)
    {
        if (actual.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Rendered HTML unexpectedly contained: {unexpected}");
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }
}
