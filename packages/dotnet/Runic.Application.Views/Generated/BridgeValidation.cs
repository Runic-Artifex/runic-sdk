using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Buffers;
using System.Text.Json;

namespace Runic.Application.Views.Generated;

/// <summary>
/// One generated collection traversal entry. <see cref="PathSegment"/> must
/// be a string dictionary key or an integer list index. Generated metadata
/// supplies these entries without reflection so validation remains AOT-safe.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct BridgeValidationChild(object PathSegment, object? Value);

/// <summary>
/// Reads and observes the validation an object keeps outside <see cref="INotifyDataErrorInfo"/>, such as a
/// ReactiveUI.Validation <c>ValidationContext</c>. Created by generated bridges.
/// </summary>
/// <param name="readErrors">
/// Returns an object's errors, read as the entries of <see cref="INotifyDataErrorInfo.GetErrors(string?)"/> with
/// <see langword="null"/>, or <see langword="null"/> when the object keeps no such validation
/// (<see cref="BridgeValidation.Write(Utf8JsonWriter, object, IReadOnlyList{BridgeDataSubscriptionMember}, Func{object, IEnumerable?})"/>).
/// </param>
/// <param name="observe">
/// Calls its <see cref="Action"/> whenever the object's validation changes, until the result is disposed, or returns
/// <see langword="null"/> when the object keeps no such validation. The Bridge observes its ViewModel and every object
/// of its data graph, so a change of a rule that names no member still publishes the <c>validation</c> member.
/// </param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeValidationReader(Func<object, IEnumerable?> readErrors, Func<object, Action, IDisposable?> observe)
{
    /// <summary>Returns an object's errors, or <see langword="null"/> when it keeps no such validation.</summary>
    public Func<object, IEnumerable?> ReadErrors { get; } = readErrors ?? throw new ArgumentNullException(nameof(readErrors));

    /// <summary>Observes an object's validation, or returns <see langword="null"/> when it keeps no such validation.</summary>
    public Func<object, Action, IDisposable?> Observe { get; } = observe ?? throw new ArgumentNullException(nameof(observe));
}

/// <summary>
/// Writes the validation projection for generated bridge snapshots.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeValidation
{
    private const int MaximumNodes = 4_096;
    private const int MaximumTraversalEntries = 4_096;
    private const int MaximumErrors = 256;
    private const int MaximumPathSegments = 64;
    private const int MaximumStringLength = 2_048;
    // translation-text/1: a larger text reference is left out of its error, which then shows its message.
    private const int MaximumTextBytes = 16 * 1_024;

    /// <summary>Writes <c>{ hasErrors, truncated, errors }</c> for <paramref name="root"/>.</summary>
    public static void Write(Utf8JsonWriter writer, object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members) => WriteCore(writer, root, members, null);

    /// <summary>
    /// Writes <c>{ hasErrors, truncated, errors }</c> for <paramref name="root"/>, reading each object's
    /// object-level errors with <paramref name="readObjectErrors"/>.
    /// </summary>
    /// <param name="writer">The snapshot writer.</param>
    /// <param name="root">The ViewModel.</param>
    /// <param name="members">The ViewModel's generated member metadata.</param>
    /// <param name="readObjectErrors">
    /// Returns an object's errors in place of <see cref="INotifyDataErrorInfo.GetErrors(string?)"/> with
    /// <see langword="null"/>, or <see langword="null"/> to use that call. An entry is read as an entry of
    /// <c>GetErrors(null)</c>: a <see cref="ValidationResult"/> reports at the paths of its
    /// <see cref="ValidationResult.MemberNames"/> and any other entry at the object's path. Generated
    /// code passes a reader for ViewModels that keep their validations in a ReactiveUI.Validation
    /// <c>ValidationContext</c>.
    /// </param>
    public static void Write(Utf8JsonWriter writer, object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members, Func<object, IEnumerable?> readObjectErrors)
    {
        ArgumentNullException.ThrowIfNull(readObjectErrors);
        WriteCore(writer, root, members, readObjectErrors);
    }

    private static void WriteCore(Utf8JsonWriter writer, object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members, Func<object, IEnumerable?>? readObjectErrors)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(members);

        var bytes = new ArrayBufferWriter<byte>();
        bool hasErrors;
        Collector collector;
        using (var errors = new Utf8JsonWriter(bytes, BridgeJsonEncoder.WriterOptions))
        {
            errors.WriteStartArray();
            collector = new Collector(errors, readObjectErrors);
            collector.Visit(root, members);
            collector.ResolvePending();
            hasErrors = collector.HasErrors;
            errors.WriteEndArray();
        }

        writer.WriteStartObject();
        writer.WriteBoolean("hasErrors", hasErrors);
        writer.WriteBoolean("truncated", collector.Truncated);
        writer.WritePropertyName("errors");
        writer.WriteRawValue(bytes.WrittenSpan, skipInputValidation: true);
        // validation-pending/1: the paths whose validation is in progress, only when there are any, so a View
        // without pending validation writes the same validation as before.
        if (collector.Pending.Count > 0)
        {
            // Sorted, so a publication that changes no path writes the same value whatever order the model reports.
            collector.Pending.Sort(ComparePaths);
            writer.WriteStartArray("pending");
            foreach (var path in collector.Pending)
            {
                writer.WriteStartArray();
                foreach (var segment in path)
                {
                    if (segment is int index) writer.WriteNumberValue(index);
                    else writer.WriteStringValue((string)segment);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        // Pending paths left out or cut have their own flag: truncated describes errors.
        if (collector.PendingTruncated) writer.WriteBoolean("pendingTruncated", true);
        writer.WriteEndObject();
    }

    // Paths segment by segment: list indexes before member names and keys, indexes by value, names ordinally; a path
    // before the paths that continue it.
    private static int ComparePaths(object[] left, object[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var order = (left[index], right[index]) switch
            {
                (int a, int b) => a.CompareTo(b),
                (int, _) => -1,
                (_, int) => 1,
                (var a, var b) => string.CompareOrdinal((string)a, (string)b),
            };
            if (order != 0) return order;
        }
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// Calls <paramref name="changed"/> whenever the validation in progress of <paramref name="owner"/> changes, until
    /// the result is disposed, or returns <see langword="null"/> when it is no <see cref="IRunicPendingValidation"/>.
    /// </summary>
    internal static IDisposable? ObservePending(object owner, Action changed)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(changed);
        if (owner is not IRunicPendingValidation pending) return null;
        return new PendingObserver(pending, changed);
    }

    /// <summary>
    /// Observes an object's validation with <paramref name="observe"/> (a ReactiveUI.Validation context, from generated
    /// code) and its validation in progress (<see cref="IRunicPendingValidation"/>).
    /// </summary>
    internal static Func<object, Action, IDisposable?> ObserveWithPending(Func<object, Action, IDisposable?>? observe) =>
        (owner, changed) =>
        {
            var validation = observe?.Invoke(owner, changed);
            var pending = ObservePending(owner, changed);
            return validation is null ? pending : pending is null ? validation : new Both(validation, pending);
        };

    private sealed class PendingObserver : IDisposable
    {
        private readonly IRunicPendingValidation _source;
        private readonly Action _changed;
        private int _disposed;

        internal PendingObserver(IRunicPendingValidation source, Action changed)
        {
            _source = source;
            _changed = changed;
            source.PendingChanged += OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 0) _changed();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _source.PendingChanged -= OnChanged;
        }
    }

    private sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    /// <summary>
    /// Calls <paramref name="changed"/> for every value <paramref name="source"/> produces after the subscription, until
    /// the result is disposed. A value replayed while subscribing is not a change. Errors and completion end the
    /// observation; a source that was disposed returns <see langword="null"/>. Generated code observes a
    /// ReactiveUI.Validation <c>ValidationContext.ValidationStatusChange</c> with it.
    /// </summary>
    public static IDisposable? Observe<T>(IObservable<T> source, Action changed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changed);
        var observer = new ChangeObserver<T>(changed);
        try { observer.Subscription = source.Subscribe(observer); }
        catch (ObjectDisposedException) { return null; }
        observer.Subscribed();
        return observer;
    }

    /// <summary>
    /// Writes a member's errors, the entries of <see cref="INotifyDataErrorInfo.GetErrors(string?)"/>, as the JSON array
    /// of their messages (the <c>{name}Errors</c> state member). An empty message is <c>Invalid value.</c>
    /// </summary>
    public static void WriteMessages(Utf8JsonWriter writer, IEnumerable? errors)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        if (errors is not null)
            foreach (var error in errors) writer.WriteStringValue(MessageText(error));
        writer.WriteEndArray();
    }

    // The text of a validation message: an empty message is the default text.
    internal static string MessageText(string? message) =>
        string.IsNullOrWhiteSpace(message) ? DefaultMessage : message;

    // The text of one entry of GetErrors.
    internal static string MessageText(object? error) => MessageText(error switch
    {
        RunicValidationMessage message => message.Message,
        ValidationResult result => result.ErrorMessage,
        _ => error?.ToString(),
    });

    // The message of an invalid value without a text of its own.
    internal const string DefaultMessage = "Invalid value.";

    private sealed class ChangeObserver<T>(Action changed) : IObserver<T>, IDisposable
    {
        private int _state; // 0 subscribing, 1 observing, 2 ended
        internal IDisposable? Subscription { get; set; }

        internal void Subscribed()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 2) Subscription?.Dispose();
        }

        public void OnNext(T value)
        {
            if (Volatile.Read(ref _state) == 1) changed();
        }

        public void OnError(Exception error) => Volatile.Write(ref _state, 2);

        public void OnCompleted() => Volatile.Write(ref _state, 2);

        public void Dispose()
        {
            Volatile.Write(ref _state, 2);
            Subscription?.Dispose();
        }
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

    private sealed class Collector(Utf8JsonWriter writer, Func<object, IEnumerable?>? readObjectErrors)
    {
        private readonly Utf8JsonWriter _writer = writer;
        private readonly Func<object, IEnumerable?>? _readObjectErrors = readObjectErrors;
        private readonly HashSet<NodeKey> _ancestors = new(NodeKeyComparer.Instance);
        private readonly List<object> _path = [];
        private int _nodes;
        private int _entries;
        private int _errors;
        private bool _hasErrors;
        private bool _truncated;
        private readonly List<object[]> _pending = [];
        private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);
        private bool _pendingTruncated;
        private readonly List<(string[] Paths, Node Node)> _pendingRequests = [];

        // validation-pending/1: the pending paths of the objects visited, in visit order. Each path resolves with a
        // traversal budget of its own; a limit it reaches is pending's truncation, not the errors'.
        internal void ResolvePending()
        {
            var truncated = _truncated;
            var entries = _entries;
            foreach (var (paths, node) in _pendingRequests)
            {
                AddPending(paths, node);
                if (_pending.Count >= MaximumErrors && _pendingTruncated) break;
            }
            (_truncated, _entries) = (truncated, entries);
        }

        internal bool HasErrors => _hasErrors;
        internal List<object[]> Pending => _pending;
        internal bool PendingTruncated => _pendingTruncated;
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
            var node = new Node(owner, members, nodePath);
            try
            {
                var objectErrors = _readObjectErrors?.Invoke(owner);
                // An object the reader handles has the errors the reader and GetErrors(member) report: its
                // HasErrors can disagree with them, as a disposed ReactiveValidationObject's stays true.
                var readsHasErrors = objectErrors is null;
                if (owner is INotifyDataErrorInfo errors)
                {
                    if (readsHasErrors) _hasErrors |= errors.HasErrors;
                    // INotifyDataErrorInfo implementations such as ObservableValidator and
                    // ReactiveValidationObject return every property's errors for null as well.
                    // GetErrors(member) below reports a ValidationResult that names a presented
                    // member, so the object-level read does not repeat it.
                    AddErrors(objectErrors ?? errors.GetErrors(null), nodePath, node, skipPresentedMembers: true);
                }
                else AddErrors(objectErrors, nodePath, node, skipPresentedMembers: false);
                // translation-text/1: GetErrors(member) of an object the reader handles, such as a
                // ReactiveValidationObject, reports plain messages; the reader's entries carry their text.
                var memberTexts = objectErrors is null ? null : MemberTexts(objectErrors);
                // Resolved after the traversal, so they do not use the errors' traversal budget.
                if (owner is IRunicPendingValidation pending && pending.PendingMemberPaths is { Count: > 0 } paths)
                    _pendingRequests.Add((paths.ToArray(), node));

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
                            if (readsHasErrors) _hasErrors |= propertyErrors.HasErrors;
                            AddErrors(propertyErrors.GetErrors(member.PropertyName), _path, node, skipPresentedMembers: false,
                                memberTexts, member.PropertyName);
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

        // The text references of the reader's entries that name members, by member and message. GetErrors(member) of
        // such an object returns only strings, so a message is matched to its entry by its text: two entries of one
        // member with the same message but different references share the last one's text, and a formatter that
        // changes the message in GetErrors(member) leaves the error without its text.
        private static Dictionary<(string Member, string Message), RunicTextReference>? MemberTexts(IEnumerable entries)
        {
            Dictionary<(string, string), RunicTextReference>? texts = null;
            foreach (var entry in entries)
                if (entry is ValidationResult result && TextOf(result) is { } text)
                    foreach (var name in result.MemberNames)
                        if (!string.IsNullOrEmpty(name))
                            (texts ??= [])[(name, MessageText(result.ErrorMessage))] = text;
            return texts;
        }

        private void AddErrors(IEnumerable? values, IReadOnlyList<object> defaultPath, Node node,
            bool skipPresentedMembers, Dictionary<(string Member, string Message), RunicTextReference>? memberTexts = null,
            string? memberName = null)
        {
            if (values is null) return;
            foreach (var value in values)
            {
                if (_errors >= MaximumErrors) { _truncated = true; return; }
                switch (value)
                {
                    case RunicValidationMessage message:
                        AddMessage(MessageText(message.Message), message.Code, message.Severity, message.Text, message.MemberPaths, defaultPath, node);
                        break;
                    case ValidationResult result:
                        var memberNames = result.MemberNames;
                        if (skipPresentedMembers && !TryUnpresented(ref memberNames, node.Members)) break;
                        AddMessage(MessageText(result.ErrorMessage), null, null, TextOf(result), memberNames, defaultPath, node);
                        break;
                    default:
                        var plain = MessageText(value);
                        var text = TextOf(value);
                        if (text is null && memberName is not null && memberTexts is not null)
                            text = memberTexts.GetValueOrDefault((memberName, plain));
                        AddMessage(plain, null, null, text, null, defaultPath, node);
                        break;
                }
            }
        }

        // Keeps the member names that GetErrors(member) does not ask for. False when the result
        // names members and every one of them is presented, so the member read reports it.
        private static bool TryUnpresented(ref IEnumerable<string> memberNames,
            IReadOnlyList<BridgeDataSubscriptionMember> members)
        {
            List<string>? remaining = null;
            var any = false;
            foreach (var name in memberNames)
            {
                any = true;
                if (!string.IsNullOrWhiteSpace(name) && members.Any(member => !member.IsPathTransparent &&
                        string.Equals(member.PropertyName, name, StringComparison.Ordinal))) continue;
                (remaining ??= []).Add(name);
            }
            if (!any) return true;
            if (remaining is null) return false;
            memberNames = remaining;
            return true;
        }

        // translation-text/1: the text reference of an entry that has one (IRunicTextReferenceSource).
        private static RunicTextReference? TextOf(object? entry) =>
            entry is IRunicTextReferenceSource { Text: { } text } ? text : null;

        private void AddMessage(string message, string? code, string? severity, RunicTextReference? text,
            IEnumerable<string>? memberPaths, IReadOnlyList<object> defaultPath, Node node)
        {
            if (memberPaths is null)
            {
                WriteError(defaultPath, message, code, severity, text);
                return;
            }

            var found = false;
            foreach (var memberPath in memberPaths)
            {
                found = true;
                if (_errors >= MaximumErrors) { _truncated = true; return; }
                var resolved = ResolveMemberPath(memberPath, node);
                WriteError(resolved ?? defaultPath, message, code, severity, text);
            }
            if (!found) WriteError(defaultPath, message, code, severity, text);
        }

        // Resolves a CLR member path relative to its object, such as "Profile.PostalCode", "Items[0].Name",
        // "Lookup[primary]" or "Groups[0][1]": each name is a Bridge member's .NET name and each index a list index
        // or dictionary key of the collection before it. Returns null when the path names no presented member or
        // element, so the error reports at the object's path.
        private IReadOnlyList<object>? ResolveMemberPath(string? memberPath, Node node)
        {
            if (string.IsNullOrWhiteSpace(memberPath)) return node.Path;
            if (memberPath.Length > MaximumStringLength) { _truncated = true; return null; }
            var resolved = new List<object>(node.Path);
            var current = node.Members;
            object? value = node.Owner;
            foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TrySplitIndexers(segment, out var name, out var indexers)) return null;
                var member = current.FirstOrDefault(candidate => !candidate.IsPathTransparent &&
                    string.Equals(candidate.PropertyName, name, StringComparison.Ordinal));
                if (member is null) return null;
                if (resolved.Count >= MaximumPathSegments) { _truncated = true; return null; }
                resolved.Add(member.Name);
                current = member.Children;
                // An index needs the value of the collection it indexes, also after plain segments ("Profile.Items[0]").
                value = value is null ? null : member.Read(value);
                if (indexers.Count == 0) continue;
                var collection = member;
                for (var index = 0; index < indexers.Count; index++)
                {
                    // A nested collection's elements are described by its one path-transparent child.
                    if (index > 0)
                    {
                        if (current is not [{ IsPathTransparent: true } nested]) return null;
                        collection = nested;
                        current = nested.Children;
                    }
                    if (value is null || collection.EnumerateValidationChildren is not { } enumerate) return null;
                    if (resolved.Count >= MaximumPathSegments) { _truncated = true; return null; }
                    if (!TryFindChild(enumerate(value), indexers[index], out var pathSegment, out value)) return null;
                    resolved.Add(pathSegment);
                }
            }
            return resolved;
        }

        // "Items[0][1]" is the name "Items" and the indexers "0" and "1". A key may be quoted ("Lookup['a.b']" is not
        // supported: '.' separates members).
        private static bool TrySplitIndexers(string segment, out string name, out List<string> indexers)
        {
            indexers = [];
            var open = segment.IndexOf('[');
            name = open < 0 ? segment : segment[..open].TrimEnd();
            if (open < 0) return name.Length > 0;
            var rest = segment.AsSpan(open);
            while (!rest.IsEmpty)
            {
                if (rest[0] != '[') return false;
                var close = rest.IndexOf(']');
                if (close < 0) return false;
                var key = rest[1..close].Trim();
                if (key.Length >= 2 && (key[0] is '"' or '\'' ) && key[^1] == key[0]) key = key[1..^1];
                indexers.Add(key.ToString());
                rest = rest[(close + 1)..].TrimStart();
            }
            return name.Length > 0;
        }

        private bool TryFindChild(IEnumerable<BridgeValidationChild> children, string indexer,
            out object pathSegment, out object? value)
        {
            var isIndex = int.TryParse(indexer, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number);
            foreach (var child in children)
            {
                if (!TryConsumeEntry()) break;
                if (child.PathSegment is int position ? isIndex && position == number
                    : child.PathSegment is string key && string.Equals(key, indexer, StringComparison.Ordinal))
                {
                    pathSegment = child.PathSegment;
                    value = child.Value;
                    return true;
                }
            }
            pathSegment = null!;
            value = null;
            return false;
        }

        // validation-pending/1: each path once, resolved as an error's member path; at most MaximumErrors paths.
        private void AddPending(IReadOnlyCollection<string> memberPaths, Node node)
        {
            foreach (var memberPath in memberPaths)
            {
                (_entries, _truncated) = (0, false);
                var path = ResolveMemberPath(memberPath, node) ?? node.Path;
                if (_truncated) _pendingTruncated = true;
                if (path.Count > MaximumPathSegments) { _pendingTruncated = true; continue; }
                var segments = path.Select(segment => segment is string { Length: > MaximumStringLength } text
                    ? (object)LimitPending(text) : segment).ToArray();
                var key = string.Join('\u0000', segments.Select(segment => segment is int index
                    ? "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "$" + (string)segment));
                if (!_pendingKeys.Add(key)) continue;
                if (_pending.Count >= MaximumErrors) { _pendingTruncated = true; return; }
                _pending.Add(segments);
            }
        }

        private void WriteError(IReadOnlyList<object> path, string message, string? code, string? severity,
            RunicTextReference? text)
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
            // translation-text/1: the message as a text reference, which a page shows in the window's language. It is
            // part of the state whatever the connections' features; a client without the feature ignores it.
            // A text reference too large for an error is left out and logged; the error keeps its message, and the
            // projection is complete, so truncated is not set.
            if (text is { Utf8Json.Length: > MaximumTextBytes }) ViewsLog.ValidationTextTooLarge(BridgeSnapshotPublication.Logger, text.Key, text.Utf8Json.Length, MaximumTextBytes);
            else if (text is not null)
            {
                _writer.WritePropertyName("text");
                text.WriteTo(_writer);
            }
            _writer.WriteEndObject();
        }

        private string LimitPending(string value)
        {
            _pendingTruncated = true;
            return value[..MaximumStringLength];
        }

        private string Limit(string? value)
        {
            value ??= DefaultMessage;
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

        // The object being visited, its members and its path.
        private sealed record Node(object Owner, IReadOnlyList<BridgeDataSubscriptionMember> Members, IReadOnlyList<object> Path);

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
