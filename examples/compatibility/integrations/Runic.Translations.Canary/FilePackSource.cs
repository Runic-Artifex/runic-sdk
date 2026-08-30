using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Runic.Translations;

namespace RunicArtifex.Examples.Translations;

/// <summary>
/// Reads an external locale pack from a fixed file path on disk. Locales other than the
/// tag this source was created for yield <see langword="null"/> so the snapshot factory
/// falls back to the compiled catalog data for those locales.
/// </summary>
internal sealed class FilePackSource : IExternalTranslationSource
{
    private readonly string _localeTag;
    private readonly string _filePath;

    public FilePackSource(string localeTag, string filePath)
    {
        _localeTag = localeTag;
        _filePath = filePath;
    }

    public async ValueTask<ExternalTranslationPack?> LoadAsync(string catalog, string locale, CancellationToken cancellationToken)
    {
        if (!string.Equals(locale, _localeTag, StringComparison.Ordinal))
        {
            return null;
        }

        byte[] content = await File.ReadAllBytesAsync(_filePath, cancellationToken);
        return new ExternalTranslationPack(content);
    }
}
