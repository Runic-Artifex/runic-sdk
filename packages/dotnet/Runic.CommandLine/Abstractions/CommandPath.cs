using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>
/// Identifies a command by its ordered, canonical path segments.
/// </summary>
/// <remarks>
/// Path matching and equality are ordinal and case-sensitive. The empty path
/// represents the catalog root and is useful for root help requests.
/// </remarks>
public sealed class CommandPath : IEquatable<CommandPath>
{
    private static readonly string[] EmptySegments = [];
    private static readonly IReadOnlyList<string> EmptySegmentView =
        new ReadOnlyCollection<string>(EmptySegments);

    private readonly string[] _segments;
    private readonly IReadOnlyList<string> _segmentView;

    /// <summary>Gets the catalog root path.</summary>
    public static CommandPath Root { get; } = new(EmptySegments);

    /// <summary>
    /// Initializes a command path from canonical command-name segments.
    /// </summary>
    /// <param name="segments">The ordered path segments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A segment is not a portable canonical command name.
    /// </exception>
    public CommandPath(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var copy = new List<string>();
        foreach (string segment in segments)
        {
            ValidateSegment(segment, nameof(segments));
            copy.Add(segment);
        }

        _segments = copy.Count == 0 ? EmptySegments : [.. copy];
        _segmentView = copy.Count == 0
            ? EmptySegmentView
            : new ReadOnlyCollection<string>(_segments);
    }

    /// <summary>Gets the immutable ordered path segments.</summary>
    public IReadOnlyList<string> Segments => _segmentView;

    /// <summary>Gets the number of path segments.</summary>
    public int Count => _segments.Length;

    /// <summary>Gets a path segment by its zero-based index.</summary>
    /// <param name="index">The zero-based segment index.</param>
    public string this[int index] => _segments[index];

    /// <summary>Returns the canonical space-separated command path.</summary>
    public override string ToString() => string.Join(' ', _segments);

    /// <inheritdoc />
    public bool Equals(CommandPath? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || _segments.Length != other._segments.Length)
        {
            return false;
        }

        for (int index = 0; index < _segments.Length; index++)
        {
            if (!string.Equals(_segments[index], other._segments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CommandPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string segment in _segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Determines whether two command paths are equal.</summary>
    public static bool operator ==(CommandPath? left, CommandPath? right) =>
        EqualityComparer<CommandPath?>.Default.Equals(left, right);

    /// <summary>Determines whether two command paths are unequal.</summary>
    public static bool operator !=(CommandPath? left, CommandPath? right) => !(left == right);

    private static void ValidateSegment(string segment, string parameterName)
    {
        if (string.IsNullOrEmpty(segment) || segment[0] is < 'a' or > 'z')
        {
            throw new ArgumentException(
                "Command path segments must match [a-z][a-z0-9-]*.",
                parameterName);
        }

        for (int index = 1; index < segment.Length; index++)
        {
            char character = segment[index];
            if (character != '-' &&
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9'))
            {
                throw new ArgumentException(
                    "Command path segments must match [a-z][a-z0-9-]*.",
                    parameterName);
            }
        }
    }
}
