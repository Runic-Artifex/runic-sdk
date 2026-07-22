using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Runtime;

public enum NoticeGroupBy
{
    Ecosystem,
    Scope,
    EffectiveLicenseExpression,
}

public sealed record NoticeGroup(string Key, IReadOnlyList<NoticeDependency> Dependencies);

public sealed record NoticeFilter(
    NoticeEcosystem? Ecosystem = null,
    NoticeDependencyScope? Scope = null,
    bool? IsDirect = null,
    bool? IsModified = null);

public sealed class NoticeCatalog
{
    private readonly NoticeDocument _document;

    public NoticeCatalog(NoticeDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public NoticeDocument Document => _document;

    public IReadOnlyList<NoticeDependency> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<NoticeDependency> results = [];
        foreach (NoticeDependency dependency in _document.Dependencies)
        {
            if (Contains(dependency.Name, query)
                || Contains(dependency.PackageUrl, query)
                || Contains(dependency.Version, query)
                || Contains(dependency.EffectiveLicenseExpression, query))
            {
                results.Add(dependency);
            }
        }

        return results.AsReadOnly();
    }

    public IReadOnlyList<NoticeDependency> Filter(NoticeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        List<NoticeDependency> results = [];
        foreach (NoticeDependency dependency in _document.Dependencies)
        {
            if ((filter.Ecosystem is null || dependency.Ecosystem == filter.Ecosystem)
                && (filter.Scope is null || dependency.Scope == filter.Scope)
                && (filter.IsDirect is null || dependency.IsDirect == filter.IsDirect)
                && (filter.IsModified is null || dependency.IsModified == filter.IsModified))
            {
                results.Add(dependency);
            }
        }

        return results.AsReadOnly();
    }

    public IReadOnlyList<NoticeGroup> Group(NoticeGroupBy groupBy)
    {
        SortedDictionary<string, List<NoticeDependency>> grouped = new(StringComparer.Ordinal);
        foreach (NoticeDependency dependency in _document.Dependencies)
        {
            string key = groupBy switch
            {
                NoticeGroupBy.Ecosystem => FormatEcosystem(dependency.Ecosystem),
                NoticeGroupBy.Scope => FormatScope(dependency.Scope),
                NoticeGroupBy.EffectiveLicenseExpression => dependency.EffectiveLicenseExpression,
                _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, "Unknown grouping."),
            };

            if (!grouped.TryGetValue(key, out List<NoticeDependency>? members))
            {
                members = [];
                grouped.Add(key, members);
            }

            members.Add(dependency);
        }

        NoticeGroup[] results = new NoticeGroup[grouped.Count];
        int index = 0;
        foreach (KeyValuePair<string, List<NoticeDependency>> pair in grouped)
        {
            results[index++] = new NoticeGroup(pair.Key, pair.Value.AsReadOnly());
        }

        return Array.AsReadOnly(results);
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatEcosystem(NoticeEcosystem ecosystem) => ecosystem switch
    {
        NoticeEcosystem.Generic => "generic",
        NoticeEcosystem.NuGet => "nuget",
        NoticeEcosystem.Npm => "npm",
        _ => throw new ArgumentOutOfRangeException(nameof(ecosystem), ecosystem, "Unknown ecosystem."),
    };

    private static string FormatScope(NoticeDependencyScope scope) => scope switch
    {
        NoticeDependencyScope.Runtime => "runtime",
        NoticeDependencyScope.Development => "development",
        NoticeDependencyScope.Optional => "optional",
        NoticeDependencyScope.Peer => "peer",
        NoticeDependencyScope.Bundled => "bundled",
        NoticeDependencyScope.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope."),
    };
}
