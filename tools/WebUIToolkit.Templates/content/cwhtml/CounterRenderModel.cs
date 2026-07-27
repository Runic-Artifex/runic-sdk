using System;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkitStarter;

public sealed record CounterRenderModel(int Count)
{
    internal static CounterRenderModel Initial(CounterViewModel model) =>
        new(model.Count);

    internal static CounterRenderModel Response(
        CounterViewModel model,
        HtmxRenderContext _) =>
        new(model.Count);
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
