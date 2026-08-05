using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal sealed class FrontendCompilerHotReloadCoordinator
{
    internal const string Contract = "runic-toolkit.frontend-compiler.hot-reload/1.0";

    private readonly string _sourcePath;
    private readonly string _readyPath;
    private readonly HostProcessController _host;
    private Snapshot _current;
    private int _acknowledgedHotReloadGeneration;

    private FrontendCompilerHotReloadCoordinator(
        string sourcePath,
        HostProcessController host,
        Snapshot current)
    {
        _sourcePath = sourcePath;
        _readyPath = sourcePath + ".ready";
        _host = host;
        _current = current;
        _acknowledgedHotReloadGeneration = host.HotReloadGeneration;
    }

    internal string ReadyPath => _readyPath;

    internal static FrontendCompilerHotReloadCoordinator Create(
        string sourcePath,
        HostProcessController host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(host);
        Snapshot current = Snapshot.Parse(File.ReadAllBytes(sourcePath));
        var coordinator = new FrontendCompilerHotReloadCoordinator(sourcePath, host, current);
        coordinator.PublishReadySnapshot();
        return coordinator;
    }

    internal Task WatchAsync(CancellationToken cancellationToken) =>
        FilePoller.WatchAsync(_sourcePath, ApplyAsync, cancellationToken);

    internal static ReloadDecision Compare(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current) =>
        Compare(Snapshot.Parse(previous), Snapshot.Parse(current));

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Snapshot next = Snapshot.Parse(File.ReadAllBytes(_sourcePath));
        ReloadDecision decision = Compare(_current, next);
        _current = next;
        if (decision.Kind == ReloadKind.None)
        {
            return;
        }

        if (decision.Kind == ReloadKind.Restart)
        {
            Console.WriteLine(
                $"[{_current.Kind}] Generated shape changed ({decision.Reason}); restarting the native host.");
            await _host.RestartAsync(cancellationToken).ConfigureAwait(false);
            _acknowledgedHotReloadGeneration = _host.HotReloadGeneration;
            return;
        }

        try
        {
            _acknowledgedHotReloadGeneration = await _host.WaitForHotReloadAsync(
                _acknowledgedHotReloadGeneration,
                cancellationToken).ConfigureAwait(false);
            PublishReadySnapshot();
            Console.WriteLine(
                $"[{_current.Kind}] Renderer hot reload applied; refreshing " +
                $"{decision.AffectedFragments.Count} affected fragment(s).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[{_current.Kind}] The managed Hot Reload acknowledgement timed out; restarting safely.");
            await _host.RestartAsync(cancellationToken).ConfigureAwait(false);
            _acknowledgedHotReloadGeneration = _host.HotReloadGeneration;
        }
    }

    private void PublishReadySnapshot()
    {
        byte[] content = File.ReadAllBytes(_sourcePath);
        string? directory = Path.GetDirectoryName(_readyPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _readyPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, _readyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static ReloadDecision Compare(Snapshot previous, Snapshot current)
    {
        if (!previous.Templates.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(current.Templates.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return ReloadDecision.Restart("the template set changed");
        }

        if (!StringComparer.Ordinal.Equals(previous.Contract, current.Contract))
        {
            return ReloadDecision.Restart("the language contract changed");
        }

        var affected = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string path, Template next) in current.Templates)
        {
            Template prior = previous.Templates[path];
            if (prior.CanRefreshFragments != next.CanRefreshFragments ||
                !StringComparer.Ordinal.Equals(
                    prior.CompatibilityFingerprint,
                    next.CompatibilityFingerprint) ||
                !prior.AffectedFragments.SequenceEqual(
                    next.AffectedFragments,
                    StringComparer.Ordinal))
            {
                return ReloadDecision.Restart($"'{path}' crossed its compatibility boundary");
            }

            if (StringComparer.Ordinal.Equals(prior.RendererFingerprint, next.RendererFingerprint))
            {
                continue;
            }

            if (!prior.CanRefreshFragments ||
                !next.CanRefreshFragments)
            {
                return ReloadDecision.Restart($"'{path}' crossed its compatibility boundary");
            }

            affected.UnionWith(next.AffectedFragments);
        }

        return affected.Count == 0
            ? ReloadDecision.None()
            : ReloadDecision.Refresh(affected);
    }

    private sealed record Snapshot(
        string Contract,
        string Kind,
        IReadOnlyDictionary<string, Template> Templates)
    {
        internal static Snapshot Parse(ReadOnlySpan<byte> content)
        {
            using JsonDocument document = JsonDocument.Parse(content.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("contract", out JsonElement contract) ||
                contract.GetString() != FrontendCompilerHotReloadCoordinator.Contract ||
                !root.TryGetProperty("templates", out JsonElement templates) ||
                templates.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Expected {FrontendCompilerHotReloadCoordinator.Contract}.");
            }

            var parsed = new Dictionary<string, Template>(StringComparer.Ordinal);
            foreach (JsonElement item in templates.EnumerateArray())
            {
                string path = RequiredString(item, "logicalPath");
                string[] fragments = item.GetProperty("affectedFragments")
                    .EnumerateArray()
                    .Select(static value => value.GetString() ??
                        throw new InvalidDataException("A fragment handle must be a string."))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var template = new Template(
                    RequiredString(item, "rendererFingerprint"),
                    RequiredString(item, "compatibilityFingerprint"),
                    item.GetProperty("canRefreshFragments").GetBoolean(),
                    fragments);
                if (!parsed.TryAdd(path, template))
                {
                    throw new InvalidDataException($"Duplicate markup template '{path}'.");
                }
            }

            return new Snapshot(
                contract.GetString()!,
                "frontend-compiler",
                parsed);
        }

        private static string RequiredString(JsonElement item, string name) =>
            item.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidDataException($"Hot-reload property '{name}' must be a string.");
    }

    private sealed record Template(
        string RendererFingerprint,
        string CompatibilityFingerprint,
        bool CanRefreshFragments,
        IReadOnlyList<string> AffectedFragments);
}

internal enum ReloadKind
{
    None,
    Refresh,
    Restart,
}

internal sealed record ReloadDecision(
    ReloadKind Kind,
    IReadOnlyList<string> AffectedFragments,
    string Reason)
{
    internal static ReloadDecision None() => new(ReloadKind.None, [], string.Empty);

    internal static ReloadDecision Refresh(IEnumerable<string> fragments) =>
        new(ReloadKind.Refresh, fragments.ToArray(), string.Empty);

    internal static ReloadDecision Restart(string reason) =>
        new(ReloadKind.Restart, [], reason);
}
