using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using RunicMarkup;

namespace RunicArtifex.Examples.Markup;

internal static class Program
{
    public static int Main()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new Utf8HtmlWriter(output);
        new GreetingView(new GreetingModel("Ada & Bob")).Render(
            ref writer,
            new TemplateContext(CultureInfo.InvariantCulture));
        writer.Complete();

        const string cwhtmlExpected = "<main id=\"greeting\">Hello, Ada &amp; Bob</main>";
        if (!StringComparer.Ordinal.Equals(cwhtmlExpected, Encoding.UTF8.GetString(output.WrittenSpan)))
        {
            return 1;
        }

        output.Clear();
        writer = new Utf8HtmlWriter(output);
        GreetingCsharpMarkup.Render("Ada & Bob").Render(
            ref writer,
            new TemplateContext(CultureInfo.InvariantCulture));
        writer.Complete();

        const string csharpExpected =
            "<aside class=\"csharp-markup\">Package-only hello, Ada &amp; Bob</aside>";
        if (!StringComparer.Ordinal.Equals(csharpExpected, Encoding.UTF8.GetString(output.WrittenSpan)))
        {
            return 2;
        }

        Console.WriteLine("Runic Markup package-only cwhtml, C# markup, and NativeAOT canary passed.");
        return 0;
    }
}
