using System.Collections.Generic;
using System.Text;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class StandaloneHtmlNoticeRenderer
{
    public static byte[] Render(DependencyNoticeDocument document)
    {
        string artifact = RenderingUtilities.HtmlEncode(document.ArtifactName);
        List<DependencyNotice> dependencies = NoticeOrdering.Dependencies(document.Dependencies);
        SortedDictionary<string, NoticeAsset> evidence = new(System.StringComparer.Ordinal);
        StringBuilder builder = new();
        _ = builder.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<title>Third-party notices — ").Append(artifact).Append("</title>\n")
            .Append("<style>\n")
            .Append(Styles)
            .Append("</style>\n</head>\n<body>\n<a class=\"skip\" href=\"#content\">Skip to notices</a>\n")
            .Append("<header><h1>Third-party notices</h1><p><strong>Artifact:</strong> ").Append(artifact);
        if (document.ArtifactVersion is not null)
        {
            _ = builder.Append(" <strong>Version:</strong> ").Append(RenderingUtilities.HtmlEncode(document.ArtifactVersion));
        }

        _ = builder.Append("</p><p>").Append(dependencies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" components</p></header>\n")
            .Append("<main id=\"content\">\n<section aria-labelledby=\"components-heading\"><h2 id=\"components-heading\">Components</h2>\n");

        int index = 0;
        foreach (DependencyNotice dependency in dependencies)
        {
            index++;
            string headingId = "component-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = builder.Append("<article aria-labelledby=\"").Append(headingId).Append("\"><h3 id=\"").Append(headingId).Append("\">")
                .Append(RenderingUtilities.HtmlEncode(dependency.Name)).Append(" <span>")
                .Append(RenderingUtilities.HtmlEncode(dependency.Version)).Append("</span></h3>\n")
                .Append("<table><caption>Component details</caption><tbody>")
                .Append("<tr><th scope=\"row\">Package URL</th><td><code>").Append(RenderingUtilities.HtmlEncode(dependency.PackageUrl)).Append("</code></td></tr>")
                .Append("<tr><th scope=\"row\">Observed license</th><td><code>").Append(RenderingUtilities.HtmlEncode(dependency.ObservedLicenseExpression)).Append("</code></td></tr>")
                .Append("<tr><th scope=\"row\">Effective license</th><td><code>").Append(RenderingUtilities.HtmlEncode(dependency.EffectiveLicenseExpression)).Append("</code></td></tr>");
            if (dependency.SelectedLicenseExpression is not null)
            {
                _ = builder.Append("<tr><th scope=\"row\">Selected license</th><td><code>")
                    .Append(RenderingUtilities.HtmlEncode(dependency.SelectedLicenseExpression)).Append("</code></td></tr>");
            }

            _ = builder.Append("<tr><th scope=\"row\">Scope</th><td>").Append(RenderingUtilities.EnumToken(dependency.Scope)).Append("</td></tr>")
                .Append("<tr><th scope=\"row\">Relationship</th><td>").Append(dependency.IsDirect ? "Direct" : "Transitive").Append("</td></tr>")
                .Append("</tbody></table>\n");
            if (dependency.IsModified && dependency.ModificationNotice is not null)
            {
                _ = builder.Append("<p><strong>Modification:</strong> ").Append(RenderingUtilities.HtmlEncode(dependency.ModificationNotice)).Append("</p>\n");
            }

            _ = builder.Append("<h4>Evidence</h4><ul>");
            foreach (NoticeAsset asset in NoticeOrdering.Assets(dependency.Assets))
            {
                string digest = RenderingUtilities.HtmlEncode(asset.Sha256);
                _ = builder.Append("<li>").Append(RenderingUtilities.HtmlEncode(RenderingUtilities.EnumToken(asset.Kind)))
                    .Append(": <a href=\"#evidence-").Append(digest).Append("\"><code>sha256:")
                    .Append(digest).Append("</code></a>; origin: ")
                    .Append(RenderingUtilities.HtmlEncode(asset.Origin)).Append("</li>");
                if (!evidence.ContainsKey(asset.Sha256))
                {
                    evidence.Add(asset.Sha256, asset);
                }
            }

            _ = builder.Append("</ul></article>\n");
        }

        _ = builder.Append("</section>\n<section aria-labelledby=\"evidence-heading\"><h2 id=\"evidence-heading\">Evidence appendix</h2>\n");
        foreach (KeyValuePair<string, NoticeAsset> pair in evidence)
        {
            string digest = RenderingUtilities.HtmlEncode(pair.Key);
            _ = builder.Append("<article class=\"evidence\" id=\"evidence-").Append(digest).Append("\"><h3><code>sha256:")
                .Append(digest).Append("</code></h3><dl><dt>Kind</dt><dd>")
                .Append(RenderingUtilities.HtmlEncode(RenderingUtilities.EnumToken(pair.Value.Kind))).Append("</dd><dt>Media type</dt><dd>")
                .Append(RenderingUtilities.HtmlEncode(pair.Value.MediaType)).Append("</dd><dt>Origin</dt><dd>")
                .Append(RenderingUtilities.HtmlEncode(pair.Value.Origin)).Append("</dd></dl><pre>")
                .Append(RenderingUtilities.HtmlEncode(pair.Value.Text)).Append("</pre></article>\n");
        }

        _ = builder.Append("</section>\n</main>\n<footer><p>Generated from pinned dependency-notice evidence.</p></footer>\n</body>\n</html>\n");
        return RenderingUtilities.Utf8NoBom.GetBytes(builder.ToString());
    }

    private const string Styles = """
:root{color-scheme:light dark;font:16px/1.5 system-ui,sans-serif}body{max-width:72rem;margin:auto;padding:0 1rem}header,footer{padding:2rem 0}article{border-top:1px solid;padding:1rem 0}table{border-collapse:collapse;width:100%}caption{text-align:left;font-weight:700}th,td{padding:.35rem;text-align:left;vertical-align:top}th{width:12rem}pre{border:1px solid;padding:1rem;overflow-wrap:anywhere;white-space:pre-wrap}.skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem}code{overflow-wrap:anywhere}@media print{a{color:inherit;text-decoration:none}article,.evidence{break-inside:avoid}body{max-width:none;font-size:10pt}}
""";
}
