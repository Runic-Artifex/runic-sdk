using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal static partial class FrontendDevelopmentDocument
{
    internal static void Write(
        DevProjectConfiguration configuration,
        Uri origin,
        Uri inspectorEndpoint,
        string destinationDocument,
        string developmentDocument)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(inspectorEndpoint);
        ArgumentNullException.ThrowIfNull(developmentDocument);
        string document = BaseElement().IsMatch(developmentDocument)
            ? BaseElement().Replace(
                developmentDocument,
                "<base href=\"/\">",
                1)
            : HeadElement().Replace(
                developmentDocument,
                "<head><base href=\"/\">",
                1);
        document = DevelopmentAssetAttribute().Replace(document, match =>
        {
            string path = match.Groups["path"].Value;
            string normalized = path.StartsWith("./", StringComparison.Ordinal)
                ? path[1..]
                : path;
            if (string.Equals(normalized, "/webui.js", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            return match.Groups["prefix"].Value +
                new Uri(origin, path).AbsoluteUri +
                match.Groups["suffix"].Value;
        });
        document = RootModuleSpecifier().Replace(
            document,
            match =>
                match.Groups["prefix"].Value +
                new Uri(origin, match.Groups["path"].Value).AbsoluteUri +
                match.Groups["suffix"].Value);
        document = WebUiScript().Replace(
            document,
            "<script src=\"/webui.js\"></script>",
            1);
        if (!WebUiScript().IsMatch(document))
        {
            document = HeadElement().Replace(
                document,
                "<head><script src=\"/webui.js\"></script>",
                1);
        }
        string inspectorBootstrap =
            "<script>globalThis.__webuitoolkitMvvmDevelopment=Object.freeze({" +
            "endpoint:" + JsonSerializer.Serialize(inspectorEndpoint.AbsoluteUri) + "," +
            "projectDirectory:" + JsonSerializer.Serialize(configuration.ProjectDirectory) +
            "});</script>";
        document = HeadElement().Replace(
            document,
            match => match.Value + inspectorBootstrap,
            1);

        string destination = Path.GetFullPath(
            destinationDocument,
            configuration.RuntimeWebRoot);
        string relative = Path.GetRelativePath(configuration.RuntimeWebRoot, destination);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new DevUsageException(
                "WUTDEV1005",
                "The frontend development document escapes the runtime web root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(
            destination,
            document,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true));
        Console.WriteLine(
            $"[dev] Wrote native frontend bootstrap '{destination}'.");
    }

    [GeneratedRegex("<base\\s+[^>]*href\\s*=\\s*[\"'][^\"']*[\"'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseElement();

    [GeneratedRegex("<head(?:\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadElement();

    [GeneratedRegex("<script\\s+[^>]*src\\s*=\\s*[\"'](?:\\.?/)?webui\\.js[\"'][^>]*>\\s*</script>", RegexOptions.IgnoreCase)]
    private static partial Regex WebUiScript();

    [GeneratedRegex("(?<prefix><(?:script|link)\\b[^>]*?\\b(?:src|href)\\s*=\\s*[\"'])(?<path>(?![A-Za-z][A-Za-z0-9+.-]*:|//|#|data:)[^\"']+)(?<suffix>[\"'])", RegexOptions.IgnoreCase)]
    private static partial Regex DevelopmentAssetAttribute();

    [GeneratedRegex("(?<prefix>\\b(?:from|import)\\s*[\"'])(?<path>/[^\"']+)(?<suffix>[\"'])")]
    private static partial Regex RootModuleSpecifier();
}
