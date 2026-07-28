using System;
using System.Collections.Generic;
using System.Linq;
using WebUIToolkit.MVVM.Html;

namespace WebUIToolkit.Samples.Cwhtml.Components;

/// <summary>A Bootstrap 5.3 form group with caller-owned control markup.</summary>
public sealed class BootstrapFormGroup : IHtmlRenderable
{
    private readonly string _controlId;
    private readonly string _label;
    private readonly IHtmlRenderable _control;
    private readonly string? _helpText;
    private readonly string? _validationMessage;

    /// <summary>Creates a form group.</summary>
    public BootstrapFormGroup(
        string controlId,
        string label,
        IHtmlRenderable control,
        string? helpText = null,
        string? validationMessage = null)
    {
        _controlId = RequireText(controlId, nameof(controlId));
        _label = RequireText(label, nameof(label));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _helpText = helpText;
        _validationMessage = validationMessage;
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "mb-3");
        writer.BeginElement("label"u8);
        writer.WriteAttribute("class"u8, "form-label");
        writer.WriteAttribute("for"u8, _controlId);
        writer.WriteText(_label);
        writer.EndElement("label"u8);
        _control.Render(ref writer, context);

        if (!string.IsNullOrEmpty(_helpText))
        {
            writer.BeginElement("div"u8);
            writer.WriteAttribute("class"u8, "form-text");
            writer.WriteText(_helpText);
            writer.EndElement("div"u8);
        }

        if (!string.IsNullOrEmpty(_validationMessage))
        {
            new BootstrapValidationMessage(
                $"{_controlId}-validation",
                _validationMessage).Render(ref writer, context);
        }

        writer.EndElement("div"u8);
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>A Bootstrap 5.3 validation message announced by assistive technology.</summary>
public sealed class BootstrapValidationMessage : IHtmlRenderable
{
    private readonly string _id;
    private readonly string _message;

    /// <summary>Creates a validation message.</summary>
    public BootstrapValidationMessage(string id, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _id = id;
        _message = message;
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        writer.BeginElement("p"u8);
        writer.WriteAttribute("id"u8, _id);
        writer.WriteAttribute("class"u8, "invalid-feedback d-block");
        writer.WriteAttribute("role"u8, "alert");
        writer.WriteText(_message);
        writer.EndElement("p"u8);
    }
}

/// <summary>One validated same-origin item in a Bootstrap navigation bar.</summary>
public sealed class BootstrapNavigationItem
{
    /// <summary>Creates a navigation item.</summary>
    public BootstrapNavigationItem(string label, HtmlUrl href, bool isCurrent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Label = label;
        Href = href;
        IsCurrent = isCurrent;
    }

    /// <summary>Gets the encoded navigation label.</summary>
    public string Label { get; }

    /// <summary>Gets the validated link destination.</summary>
    public HtmlUrl Href { get; }

    /// <summary>Gets whether this item represents the current page.</summary>
    public bool IsCurrent { get; }
}

/// <summary>A compact Bootstrap 5.3 navigation landmark.</summary>
public sealed class BootstrapNavigation : IHtmlRenderable
{
    private readonly string _label;
    private readonly string _brand;
    private readonly IReadOnlyList<BootstrapNavigationItem> _items;

    /// <summary>Creates a navigation landmark.</summary>
    public BootstrapNavigation(
        string label,
        string brand,
        IReadOnlyList<BootstrapNavigationItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(brand);
        _label = label;
        _brand = brand;
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        writer.BeginElement("nav"u8);
        writer.WriteAttribute("class"u8, "navbar navbar-expand bg-body-tertiary");
        writer.WriteAttribute("aria-label"u8, _label);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "container-fluid");
        writer.BeginElement("span"u8);
        writer.WriteAttribute("class"u8, "navbar-brand");
        writer.WriteText(_brand);
        writer.EndElement("span"u8);
        writer.BeginElement("ul"u8);
        writer.WriteAttribute("class"u8, "navbar-nav");
        foreach (BootstrapNavigationItem item in _items)
        {
            writer.BeginElement("li"u8);
            writer.WriteAttribute("class"u8, "nav-item");
            writer.BeginElement("a"u8);
            writer.WriteAttribute(
                "class"u8,
                item.IsCurrent ? "nav-link active" : "nav-link");
            writer.WriteUrl("href"u8, item.Href);
            if (item.IsCurrent)
            {
                writer.WriteAttribute("aria-current"u8, "page");
            }

            writer.WriteText(item.Label);
            writer.EndElement("a"u8);
            writer.EndElement("li"u8);
        }

        writer.EndElement("ul"u8);
        writer.EndElement("div"u8);
        writer.EndElement("nav"u8);
    }
}

/// <summary>A Bootstrap 5.3 modal presentation with typed renderable content.</summary>
public sealed class BootstrapModal : IHtmlRenderable
{
    private readonly string _id;
    private readonly string _title;
    private readonly IHtmlRenderable _body;
    private readonly IHtmlRenderable? _footer;

    /// <summary>Creates an initially visible modal presentation.</summary>
    public BootstrapModal(
        string id,
        string title,
        IHtmlRenderable body,
        IHtmlRenderable? footer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _id = id;
        _title = title;
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _footer = footer;
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string titleId = $"{_id}-title";
        writer.BeginElement("div"u8);
        writer.WriteAttribute("id"u8, _id);
        writer.WriteAttribute("class"u8, "modal show");
        writer.WriteAttribute("tabindex"u8, "-1");
        writer.WriteAttribute("role"u8, "dialog");
        writer.WriteAttribute("aria-modal"u8, "true");
        writer.WriteAttribute("aria-labelledby"u8, titleId);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "modal-dialog");
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "modal-content");
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "modal-header");
        writer.BeginElement("h2"u8);
        writer.WriteAttribute("id"u8, titleId);
        writer.WriteAttribute("class"u8, "modal-title fs-5");
        writer.WriteText(_title);
        writer.EndElement("h2"u8);
        writer.EndElement("div"u8);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "modal-body");
        _body.Render(ref writer, context);
        writer.EndElement("div"u8);
        if (_footer is not null)
        {
            writer.BeginElement("div"u8);
            writer.WriteAttribute("class"u8, "modal-footer");
            _footer.Render(ref writer, context);
            writer.EndElement("div"u8);
        }

        writer.EndElement("div"u8);
        writer.EndElement("div"u8);
        writer.EndElement("div"u8);
    }
}

/// <summary>A Bootstrap 5.3 toast with an accessible status announcement.</summary>
public sealed class BootstrapToast : IHtmlRenderable
{
    private readonly string _title;
    private readonly string _message;

    /// <summary>Creates an initially visible toast.</summary>
    public BootstrapToast(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _title = title;
        _message = message;
    }

    /// <inheritdoc/>
    public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "toast show");
        writer.WriteAttribute("role"u8, "status");
        writer.WriteAttribute("aria-live"u8, "polite");
        writer.WriteAttribute("aria-atomic"u8, "true");
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "toast-header");
        writer.BeginElement("strong"u8);
        writer.WriteAttribute("class"u8, "me-auto");
        writer.WriteText(_title);
        writer.EndElement("strong"u8);
        writer.EndElement("div"u8);
        writer.BeginElement("div"u8);
        writer.WriteAttribute("class"u8, "toast-body");
        writer.WriteText(_message);
        writer.EndElement("div"u8);
        writer.EndElement("div"u8);
    }
}
