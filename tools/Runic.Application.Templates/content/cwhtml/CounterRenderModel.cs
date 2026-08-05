using System;
using System.Collections.Generic;
using System.Linq;
using RunicMarkup;
using RunicMarkup.RunicToolkit.Htmx;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;

namespace RunicToolkitStarter;

public sealed record CounterRenderModel(
    int Count,
    int Step,
    string Summary,
    IReadOnlyList<int> History,
    IReadOnlyList<string> StepErrors)
{
    internal static CounterRenderModel Initial(CounterViewModel model) =>
        Create(model);

    internal static CounterRenderModel Response(
        CounterViewModel model,
        HtmxRenderContext _) =>
        Create(model);

    private static CounterRenderModel Create(CounterViewModel model) =>
        new(
            model.Count,
            model.Step,
            model.Summary,
            model.History.ToArray(),
            model.GetErrors(nameof(CounterViewModel.Step))
                .Select(static error => error?.ToString() ?? "The step is invalid.")
                .ToArray());
}

public sealed class CounterDocumentModel(
    IHtmlRenderable application,
    FrontendDevelopmentAssets assets)
{
    public IHtmlRenderable Application { get; } =
        application ?? throw new ArgumentNullException(nameof(application));

    public FrontendDevelopmentAssets Assets { get; } =
        assets ?? throw new ArgumentNullException(nameof(assets));
}
