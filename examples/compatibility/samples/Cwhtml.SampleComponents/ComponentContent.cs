using System;
using WebUIToolkit.MVVM.Html;

namespace WebUIToolkit.Samples.Cwhtml.Components;

/// <summary>Small composition helpers used by the sample-only component library.</summary>
public static class ComponentContent
{
    /// <summary>Creates an encoded text component.</summary>
    public static IHtmlRenderable Text(string value) => new TextComponent(value);

    private sealed class TextComponent(string value) : IHtmlRenderable
    {
        private readonly string _value = value ??
            throw new ArgumentNullException(nameof(value));

        public void Render(ref Utf8HtmlWriter writer, TemplateContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            writer.WriteText(_value);
        }
    }
}
