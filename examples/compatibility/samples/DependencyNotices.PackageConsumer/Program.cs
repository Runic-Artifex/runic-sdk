using System;
using System.IO;
using WebUIToolkit.DependencyNotices;
using WebUIToolkit.DependencyNotices.Engine;
using WebUIToolkit.DependencyNotices.Rendering;
using WebUIToolkit.DependencyNotices.Runtime;

namespace DependencyNotices.PackageConsumer;

internal static class Program
{
    private const string ResourceName = "DependencyNotices.PackageConsumer.dependency-notices.json";

    public static int Main()
    {
        using Stream documentStream = typeof(Program).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");

        NoticeDocument document = NoticeDocumentLoader.Load(documentStream);
        NoticeCatalog catalog = new(document);

        // These compile-time references prove that all four library packages expose consumable APIs.
        Type[] packageApiMarkers =
        [
            typeof(PackageUrl),
            typeof(NetworkPolicy),
            typeof(CanonicalJsonNoticeRenderer),
            typeof(NoticeCatalog),
        ];

        if (document.ArtifactName != "DependencyNotices.PackageConsumer"
            || catalog.Search("not-present").Count != 0
            || packageApiMarkers.Length != 4)
        {
            return 1;
        }

        Console.WriteLine("Dependency Notices packed-package consumer passed.");
        return 0;
    }
}
