using System;
using WebUIToolkit.MVVM.Html;

namespace WebUIToolkit.Samples.Cwhtml.Components;

/// <summary>Controls how updates in an accessible live region are announced.</summary>
public enum LiveRegionPoliteness
{
    /// <summary>Announces the update after the current assistive-technology output.</summary>
    Polite,

    /// <summary>Announces the update immediately.</summary>
    Assertive,
}

/// <summary>
/// A styling-neutral status region that depends only on semantic HTML and ARIA.
/// </summary>
public sealed class AccessibleStatusRegion : IHtmlRenderable
{
    private readonly string _message;
    private readonly LiveRegionPoliteness _politeness;
    private readonly string? _id;
    private readonly string? _className;

    /// <summary>Creates a styling-neutral live status region.</summary>
    public AccessibleStatusRegion(
        string message,
        LiveRegionPoliteness politeness = LiveRegionPoliteness.Polite,
        string? id = null,
        string? className = null)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        _politeness = politeness;
        _id = id;
        _className = className;
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        writer.BeginElement("div"u8);
        if (!string.IsNullOrEmpty(_id))
        {
            writer.WriteAttribute("id"u8, _id);
        }

        if (!string.IsNullOrEmpty(_className))
        {
            writer.WriteAttribute("class"u8, _className);
        }

        writer.WriteAttribute("role"u8, "status");
        writer.WriteAttribute(
            "aria-live"u8,
            _politeness == LiveRegionPoliteness.Assertive ? "assertive" : "polite");
        writer.WriteAttribute("aria-atomic"u8, "true");
        writer.WriteText(_message);
        writer.EndElement("div"u8);
    }
}
