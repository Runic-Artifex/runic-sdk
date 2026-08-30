using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.Examples.Translations.Generated;
using Runic.Translations;

namespace RunicArtifex.Examples.Translations;

/// <summary>
/// Demo scaffolding for the hot-swap scenario, working around a known limitation of the pinned
/// Runic.Translations 0.1.0-preview.8.1: <see cref="ITranslationManager.SetLocaleAsync(string,
/// System.Threading.CancellationToken)"/> short-circuits when the requested locale equals the
/// current locale, and <see cref="CompiledTranslationProvider"/> memoizes successful snapshots, so
/// an in-place refresh of a replaced external pack cannot be observed through the public API.
/// Delegating every snapshot request to a freshly composed generated external provider defeats
/// that memoization. This is NOT a supported pattern; upstream needs a same-locale refresh API
/// (candidate follow-up lane in runic-translations).
/// </summary>
internal sealed class RotatingExternalProvider(IExternalTranslationSource initialSource) : ITranslationProvider
{
    private volatile IExternalTranslationSource _currentSource = initialSource;

    public void Rotate(IExternalTranslationSource source)
    {
        _currentSource = source;
    }

    public ValueTask<ITranslationSnapshot> GetSnapshotAsync(string requestedLocale, CancellationToken cancellationToken = default)
    {
        ITranslationProvider freshInnerProvider = CanaryTextCatalog.CreateExternalProvider(_currentSource);
        return freshInnerProvider.GetSnapshotAsync(requestedLocale, cancellationToken);
    }
}
