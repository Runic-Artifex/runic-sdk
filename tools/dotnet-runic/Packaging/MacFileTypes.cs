using System.Collections.Generic;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// How the macOS bundle declares one of its file types: the uniform type identifier, whether the application owns and
/// exports it, and whether the bundle imports its declaration because macOS may not declare it.
/// </summary>
internal sealed record MacFileType(PackageFileType Type, string Identifier, bool Owned, bool Imported, string ConformsTo);

/// <summary>
/// The file types' uniform type identifiers. An extension with a system type, such as <c>.txt</c> (<c>public.plain-text</c>)
/// or <c>.png</c> (<c>public.png</c>), uses it with the <c>Alternate</c> or <c>Default</c> rank
/// (<c>RunicMacFileTypeHandlerRank</c>): the application opens such files, it does not own them. Any other extension is the
/// application's own: it exports <c>&lt;ApplicationId&gt;.&lt;extension&gt;</c> with the <c>Owner</c> rank.
/// <c>RunicMacFileTypeIdentifiers</c> names the type of an extension this list does not know.
/// </summary>
internal static class MacFileTypes
{
    /// <summary>Extensions with a type that macOS declares, and Markdown, which the bundle imports.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        [".txt"] = "public.plain-text",
        [".text"] = "public.plain-text",
        [".md"] = "net.daringfireball.markdown",
        [".markdown"] = "net.daringfireball.markdown",
        [".log"] = "public.log",
        [".json"] = "public.json",
        [".xml"] = "public.xml",
        [".yaml"] = "public.yaml",
        [".yml"] = "public.yaml",
        [".csv"] = "public.comma-separated-values-text",
        [".tsv"] = "public.tab-separated-values-text",
        [".html"] = "public.html",
        [".htm"] = "public.html",
        [".rtf"] = "public.rtf",
        [".pdf"] = "com.adobe.pdf",
        [".js"] = "com.netscape.javascript-source",
        [".sh"] = "public.shell-script",
        [".py"] = "public.python-script",
        [".png"] = "public.png",
        [".jpg"] = "public.jpeg",
        [".jpeg"] = "public.jpeg",
        [".gif"] = "com.compuserve.gif",
        [".heic"] = "public.heic",
        [".tif"] = "public.tiff",
        [".tiff"] = "public.tiff",
        [".bmp"] = "com.microsoft.bmp",
        [".ico"] = "com.microsoft.ico",
        [".webp"] = "org.webmproject.webp",
        [".svg"] = "public.svg-image",
        [".mp3"] = "public.mp3",
        [".wav"] = "com.microsoft.waveform-audio",
        [".mp4"] = "public.mpeg-4",
        [".mov"] = "com.apple.quicktime-movie",
        [".zip"] = "public.zip-archive",
        [".vcf"] = "public.vcard",
        [".ics"] = "com.apple.ical.ics",
    };

    /// <summary>Known types that macOS may not declare, which the bundle imports, and what they conform to.</summary>
    private static readonly Dictionary<string, string> ImportedConformance = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        ["net.daringfireball.markdown"] = "public.plain-text",
    };

    /// <summary>How the bundle declares the file type.</summary>
    internal static MacFileType Resolve(PackageIdentity identity, PackageFileType type)
    {
        if (identity.MacFileTypeIdentifiers.TryGetValue(type.Extension, out string? named))
        {
            // A type the project names: imported unless it is one of the system's own.
            bool system = IsSystemDeclared(named);
            return new MacFileType(type, named, Owned: false, Imported: !system,
                ImportedConformance.TryGetValue(named, out string? conformsTo) ? conformsTo : "public.data");
        }
        if (Known.TryGetValue(type.Extension, out string? known))
        {
            bool imported = ImportedConformance.TryGetValue(known, out string? conformsTo);
            return new MacFileType(type, known, Owned: false, imported, conformsTo ?? "public.data");
        }
        return new MacFileType(type, PackageIdentity.TypeIdentifier(identity.Id, type.Extension), Owned: true, Imported: false, "public.data");
    }

    private static bool IsSystemDeclared(string identifier)
    {
        if (ImportedConformance.ContainsKey(identifier)) return false;
        foreach (string known in Known.Values)
            if (known == identifier) return true;
        return false;
    }
}
