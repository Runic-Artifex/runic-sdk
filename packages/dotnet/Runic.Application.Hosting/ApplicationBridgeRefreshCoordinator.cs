using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Runic.Assets;
using Runic.Application.Bridge;
using Runic.Translations;

namespace Runic.Application.Hosting;

/// <summary>
/// Forwards authoritative asset and translation changes into one existing application bridge session.
/// </summary>
public sealed class ApplicationBridgeRefreshCoordinator : IAsyncDisposable
{
    private const int MaxQueuedChanges = 64;
    private readonly ApplicationBridgeSession _session;
    private readonly IAssetSource _assets;
    private readonly IAssetSourceChangeNotifier _assetChanges;
    private readonly ITranslationManager _translations;
    private readonly ApplicationBridgeWebSocketTransport? _transport;
    private readonly Func<IBridgeEventPublisher, AssetSourceChangedEventArgs, CancellationToken, ValueTask> _publishAssetChange;
    private readonly Func<IBridgeEventPublisher, TranslationLocaleChangedEventArgs, CancellationToken, ValueTask> _publishTranslationChange;
    private readonly Channel<PendingChange> _changes = Channel.CreateBounded<PendingChange>(new BoundedChannelOptions(MaxQueuedChanges)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private readonly Task _delivery;
    private Task? _disposeTask;
    private int _replayRequested;
    private int _disposed;

    /// <summary>Subscribes to the supplied authoritative asset and translation sources.</summary>
    public ApplicationBridgeRefreshCoordinator(
        ApplicationBridgeSession session,
        IAssetSource assets,
        ITranslationManager translations,
        Func<IBridgeEventPublisher, AssetSourceChangedEventArgs, CancellationToken, ValueTask> publishAssetChange,
        Func<IBridgeEventPublisher, TranslationLocaleChangedEventArgs, CancellationToken, ValueTask> publishTranslationChange,
        ApplicationBridgeWebSocketTransport? transport = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _assetChanges = assets as IAssetSourceChangeNotifier ?? throw new ArgumentException("The asset source must publish authoritative change notifications.", nameof(assets));
        _translations = translations ?? throw new ArgumentNullException(nameof(translations));
        _publishAssetChange = publishAssetChange ?? throw new ArgumentNullException(nameof(publishAssetChange));
        _publishTranslationChange = publishTranslationChange ?? throw new ArgumentNullException(nameof(publishTranslationChange));
        _transport = transport;
        _delivery = DeliverAsync();
        _assetChanges.Changed += OnAssetsChanged;
        _translations.LocaleChanged += OnTranslationLocaleChanged;
        _transport?.Activated += OnTransportActivated;
    }

    /// <summary>Stops source subscriptions and waits until no queued or in-flight publication can continue.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Returns a stable SHA-256 identity for the supplied immutable asset manifest.</summary>
    public static string GetManifestFingerprint(AssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new StringBuilder("runic.application.refresh.asset-manifest/1\n");
        AppendComponent(canonical, manifest.Version);
        AppendComponent(canonical, manifest.Assets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (AssetDescriptor asset in manifest.Assets)
        {
            AppendComponent(canonical, asset.RelativePath);
            AppendComponent(canonical, asset.MediaType);
            AppendComponent(canonical, asset.Length.ToString(CultureInfo.InvariantCulture));
            AppendComponent(canonical, asset.Sha256);
            AppendComponent(canonical, asset.IsEntryPoint ? "1" : "0");
            AppendComponent(canonical, ((int)asset.CacheMode).ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private void OnAssetsChanged(object? sender, AssetSourceChangedEventArgs change) =>
        Enqueue(new PendingChange(token => _publishAssetChange(_session, change, token)));

    private void OnTranslationLocaleChanged(object? sender, TranslationLocaleChangedEventArgs change) =>
        Enqueue(new PendingChange(token => _publishTranslationChange(_session, change, token)));

    /// <summary>Queues a current-source replay for a non-WebSocket host after it has activated its client.</summary>
    public void RequestReplay()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _replayRequested, 1);
        _changes.Writer.TryWrite(PendingChange.Replay);
    }

    private void OnTransportActivated(object? sender, EventArgs change) => RequestReplay();

    private void Enqueue(PendingChange change)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _changes.Writer.WriteAsync(change, _shutdown.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0) { }
    }

    private async Task DeliverAsync()
    {
        try
        {
            await foreach (PendingChange change in _changes.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (change.IsReplay) await ReplayAsync().ConfigureAwait(false);
                else await DeliverAsync(change.Publish!).ConfigureAwait(false);
                if (Volatile.Read(ref _replayRequested) != 0 && !_changes.Reader.TryPeek(out _))
                    await ReplayAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ReplayAsync()
    {
        if (Interlocked.Exchange(ref _replayRequested, 0) == 0) return;
        AssetManifest assets = _assets.Manifest;
        ITranslationSnapshot translations = _translations.Current;
        await DeliverAsync(token => _publishAssetChange(_session, new AssetSourceChangedEventArgs(assets, assets), token)).ConfigureAwait(false);
        await DeliverAsync(token => _publishTranslationChange(_session, new TranslationLocaleChangedEventArgs(translations, translations), token)).ConfigureAwait(false);
    }

    private async Task DeliverAsync(Func<CancellationToken, ValueTask> publish)
    {
        while (true)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            try
            {
                await publish(_shutdown.Token).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _assetChanges.Changed -= OnAssetsChanged;
        _translations.LocaleChanged -= OnTranslationLocaleChanged;
        _transport?.Activated -= OnTransportActivated;
        _changes.Writer.TryComplete();
        _shutdown.Cancel();
        await _delivery.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static void AppendComponent(StringBuilder output, string value) =>
        output.Append(value.Length).Append(':').Append(value).Append('\n');

    private sealed record PendingChange(Func<CancellationToken, ValueTask>? Publish)
    {
        public static PendingChange Replay { get; } = new((Func<CancellationToken, ValueTask>?)null);
        public bool IsReplay => Publish is null;
    }
}
