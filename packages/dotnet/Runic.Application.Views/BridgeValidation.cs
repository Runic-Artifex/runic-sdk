using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Buffers;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>
/// A structured validation error that a model may return from
/// <see cref="INotifyDataErrorInfo.GetErrors(string?)"/>. Member paths are
/// relative CLR property paths (for example <c>Address.Postcode</c>); the
/// bridge resolves them through generated metadata before placing them on the
/// wire. One message is emitted for each supplied member path.
/// </summary>
public sealed record BridgeValidationMessage(
    string Message,
    string? Code = null,
    string? Severity = null,
    IReadOnlyList<string>? MemberPaths = null);

/// <summary>
/// One generated collection traversal entry. <see cref="PathSegment"/> must
/// be a string dictionary key or an integer list index. Generated metadata
/// supplies these entries without reflection so validation remains AOT-safe.
/// </summary>
public readonly record struct BridgeValidationChild(object PathSegment, object? Value);

/// <summary>
/// Writes the validation projection for generated bridge snapshots.
/// </summary>
public static class BridgeValidation
{
    private const int MaximumNodes = 4_096;
    private const int MaximumTraversalEntries = 4_096;
    private const int MaximumErrors = 256;
    private const int MaximumPathSegments = 64;
    private const int MaximumStringLength = 2_048;

    /// <summary>Writes <c>{ hasErrors, truncated, errors }</c> for <paramref name="root"/>.</summary>
    public static void Write(Utf8JsonWriter writer, object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(members);

        var bytes = new ArrayBufferWriter<byte>();
        bool hasErrors;
        Collector collector;
        using (var errors = new Utf8JsonWriter(bytes))
        {
            errors.WriteStartArray();
            collector = new Collector(errors);
            collector.Visit(root, members);
            hasErrors = collector.HasErrors;
            errors.WriteEndArray();
        }

        writer.WriteStartObject();
        writer.WriteBoolean("hasErrors", hasErrors);
        writer.WriteBoolean("truncated", collector.Truncated);
        writer.WritePropertyName("errors");
        writer.WriteRawValue(bytes.WrittenSpan, skipInputValidation: true);
        writer.WriteEndObject();
    }

    /// <summary>Produces indexed entries for generated array/list metadata.</summary>
    public static IEnumerable<BridgeValidationChild> EnumerateIndexed(IEnumerable values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var index = 0;
        foreach (var value in values)
        {
            yield return new BridgeValidationChild(index, value);
            checked { index++; }
        }
    }

    /// <summary>Produces keyed entries for generated string dictionary metadata.</summary>
    public static IEnumerable<BridgeValidationChild> EnumerateDictionary<T>(
        IEnumerable<KeyValuePair<string, T>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var entry in values)
            yield return new BridgeValidationChild(entry.Key, entry.Value);
    }

    private sealed class Collector(Utf8JsonWriter writer)
    {
        private readonly Utf8JsonWriter _writer = writer;
        private readonly HashSet<NodeKey> _ancestors = new(NodeKeyComparer.Instance);
        private readonly List<object> _path = [];
        private int _nodes;
        private int _entries;
        private int _errors;
        private bool _hasErrors;
        private bool _truncated;

        internal bool HasErrors => _hasErrors;
        internal bool Truncated => _truncated;

        internal void Visit(object? owner, IReadOnlyList<BridgeDataSubscriptionMember> members)
        {
            if (!TryConsumeEntry() || owner is null) return;
            if (_path.Count > MaximumPathSegments) { _truncated = true; return; }
            if (_nodes >= MaximumNodes) { _truncated = true; return; }
            if (owner.GetType().IsValueType) return;
            var key = new NodeKey(owner, members);
            if (!_ancestors.Add(key)) return;
            _nodes++;
            var nodePath = _path.ToArray();
            try
            {
                if (owner is INotifyDataErrorInfo errors)
                {
                    _hasErrors |= errors.HasErrors;
                    AddErrors(errors.GetErrors(null), nodePath, members, nodePath);
                }

                foreach (var member in members)
                {
                    if (_errors >= MaximumErrors) { _truncated = true; return; }
                    if (!TryConsumeEntry()) return;
                    var visible = !member.IsPathTransparent;
                    if (visible) _path.Add(member.Name);
                    try
                    {
                        if (owner is INotifyDataErrorInfo propertyErrors)
                        {
                            _hasErrors |= propertyErrors.HasErrors;
                            AddErrors(propertyErrors.GetErrors(member.PropertyName), _path, members, nodePath);
                        }

                        var value = member.Read(owner);
                        VisitMember(value, member);
                    }
                    finally
                    {
                        if (visible) _path.RemoveAt(_path.Count - 1);
                    }
                }
            }
            finally { _ancestors.Remove(key); }
        }

        private void VisitMember(object? value, BridgeDataSubscriptionMember member)
        {
            if (value is null) return;
            if (_errors >= MaximumErrors) { _truncated = true; return; }
            if (member.EnumerateValidationChildren is { } enumerate)
            {
                foreach (var child in enumerate(value))
                {
                    if (_errors >= MaximumErrors || _path.Count >= MaximumPathSegments)
                    {
                        _truncated = true;
                        return;
                    }
                    if (!TryConsumeEntry()) return;
                    if (child.PathSegment is not string and not int) continue;
                    _path.Add(child.PathSegment);
                    try { Visit(child.Value, member.Children); }
                    finally { _path.RemoveAt(_path.Count - 1); }
                }
                return;
            }
            if (member.Children.Count > 0) Visit(value, member.Children);
        }

        private void AddErrors(IEnumerable? values, IReadOnlyList<object> defaultPath,
            IReadOnlyList<BridgeDataSubscriptionMember> members, IReadOnlyList<object> nodePath)
        {
            if (values is null) return;
            foreach (var value in values)
            {
                if (_errors >= MaximumErrors) { _truncated = true; return; }
                switch (value)
                {
                    case BridgeValidationMessage message:
                        AddMessage(message.Message, message.Code, message.Severity, message.MemberPaths, defaultPath, members, nodePath);
                        break;
                    case ValidationResult result:
                        AddMessage(result.ErrorMessage ?? "Invalid value.", null, null,
                            result.MemberNames, defaultPath, members, nodePath);
                        break;
                    default:
                        AddMessage(value?.ToString() ?? "Invalid value.", null, null, null, defaultPath, members, nodePath);
                        break;
                }
            }
        }

        private void AddMessage(string message, string? code, string? severity,
            IEnumerable<string>? memberPaths, IReadOnlyList<object> defaultPath,
            IReadOnlyList<BridgeDataSubscriptionMember> members, IReadOnlyList<object> nodePath)
        {
            if (memberPaths is null)
            {
                WriteError(defaultPath, message, code, severity);
                return;
            }

            var found = false;
            foreach (var memberPath in memberPaths)
            {
                found = true;
                if (_errors >= MaximumErrors) { _truncated = true; return; }
                var resolved = ResolveMemberPath(memberPath, members, nodePath);
                WriteError(resolved ?? defaultPath, message, code, severity);
            }
            if (!found) WriteError(defaultPath, message, code, severity);
        }

        private IReadOnlyList<object>? ResolveMemberPath(string? memberPath,
            IReadOnlyList<BridgeDataSubscriptionMember> members, IReadOnlyList<object> nodePath)
        {
            if (string.IsNullOrWhiteSpace(memberPath)) return nodePath;
            if (memberPath.Length > MaximumStringLength) { _truncated = true; return null; }
            var resolved = new List<object>(nodePath);
            var current = members;
            foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var member = current.FirstOrDefault(candidate =>
                    string.Equals(candidate.PropertyName, segment, StringComparison.Ordinal));
                if (member is null) return null;
                if (resolved.Count >= MaximumPathSegments) { _truncated = true; return null; }
                resolved.Add(member.Name);
                current = member.Children;
            }
            return resolved;
        }

        private void WriteError(IReadOnlyList<object> path, string message, string? code, string? severity)
        {
            _hasErrors = true;
            _errors++;
            if (_errors == MaximumErrors) _truncated = true;
            _writer.WriteStartObject();
            _writer.WritePropertyName("path");
            _writer.WriteStartArray();
            if (path.Count > MaximumPathSegments) _truncated = true;
            foreach (var segment in path.Take(MaximumPathSegments))
            {
                if (segment is int index) _writer.WriteNumberValue(index);
                else _writer.WriteStringValue(Limit((string)segment));
            }
            _writer.WriteEndArray();
            _writer.WriteString("message", Limit(message));
            if (!string.IsNullOrEmpty(code)) _writer.WriteString("code", Limit(code));
            if (!string.IsNullOrEmpty(severity)) _writer.WriteString("severity", Limit(severity));
            _writer.WriteEndObject();
        }

        private string Limit(string? value)
        {
            value ??= "Invalid value.";
            if (value.Length <= MaximumStringLength) return value;
            _truncated = true;
            return value[..MaximumStringLength];
        }

        private bool TryConsumeEntry()
        {
            if (_entries++ < MaximumTraversalEntries) return true;
            _truncated = true;
            return false;
        }

        private readonly record struct NodeKey(object Owner, IReadOnlyList<BridgeDataSubscriptionMember> Members);

        private sealed class NodeKeyComparer : IEqualityComparer<NodeKey>
        {
            internal static NodeKeyComparer Instance { get; } = new();
            public bool Equals(NodeKey left, NodeKey right) =>
                ReferenceEquals(left.Owner, right.Owner) && ReferenceEquals(left.Members, right.Members);
            public int GetHashCode(NodeKey value) => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Owner),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Members));
        }
    }
}
