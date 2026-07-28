using WebUIToolkit.MVVM.Html;
using WebUIToolkit.Samples.Cwhtml.Components;

namespace WebUIToolkit.Samples.Cwhtml.Components.Tests;

public sealed class StylingNeutralExampleModel
{
    public StylingNeutralExampleModel(string status)
    {
        Status = new AccessibleStatusRegion(
            status,
            id: "save-status",
            className: "paper-status");
    }

    public IHtmlRenderable Status { get; }
}
