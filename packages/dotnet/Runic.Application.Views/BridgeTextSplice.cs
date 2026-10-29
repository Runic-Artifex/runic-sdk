namespace Runic.Application.Views;

/// <summary>
/// One text splice of <c>text-splice/1</c> (<c>specs/application/state-changes.md</c>, "Text splices"): replace
/// <see cref="Delete"/> UTF-16 code units at <see cref="Index"/> of a base value with <see cref="Text"/>.
/// </summary>
internal readonly record struct BridgeTextSplice(int Index, int Delete, string Text)
{
    /// <summary>A splice is published, and sent, only when it keeps at least this many code units of its base.</summary>
    internal const int MinimumKept = 64;

    // Compares blocks of this many code units from the end before the last, partial block.
    private const int SuffixBlock = 64;

    /// <summary>
    /// The splices a producer publishes for a text member that changed from <paramref name="previous"/> to
    /// <paramref name="value"/>: one splice from the common prefix and suffix when it keeps at least half of the
    /// previous value, otherwise the "drop the start, append" pair when it keeps more; nothing (a set of the member)
    /// when the chosen shape keeps fewer than <see cref="MinimumKept"/> code units.
    /// </summary>
    /// <returns>The number of splices: 0 (publish a set), 1 or 2.</returns>
    internal static int Between(string previous, string value, out BridgeTextSplice first, out BridgeTextSplice second)
    {
        first = second = default;
        var single = Single(previous, value);
        var kept = previous.Length - single.Delete;
        if (kept * 2L < previous.Length && BridgeTextDelta.Between(previous, value) is { } pair
            && pair.Length - pair.Trim > kept && IsBoundary(previous, pair.Trim))
        {
            kept = pair.Length - pair.Trim;
            if (kept < MinimumKept) return 0;
            first = new(0, pair.Trim, "");
            if (pair.Text.Length == 0) return 1;
            second = new(kept, 0, pair.Text);
            return 2;
        }
        if (kept < MinimumKept) return 0;
        first = single;
        return 1;
    }

    /// <summary>
    /// The single splice from <paramref name="previous"/> to <paramref name="value"/>: the common prefix, then the
    /// common suffix of the rest, each shortened by one code unit where it would end inside a surrogate pair.
    /// </summary>
    internal static BridgeTextSplice Single(string previous, string value)
    {
        var prefix = previous.AsSpan().CommonPrefixLength(value);
        if (prefix > 0 && char.IsHighSurrogate(previous[prefix - 1])) prefix--;
        var suffix = CommonSuffixLength(previous.AsSpan(prefix), value.AsSpan(prefix));
        if (suffix > 0 && char.IsLowSurrogate(previous[^suffix])) suffix--;
        return new(prefix, previous.Length - prefix - suffix, value.Substring(prefix, value.Length - prefix - suffix));
    }

    /// <summary>
    /// Applies a splice to <paramref name="value"/>: false when <paramref name="length"/> is not its length, the range
    /// is outside it, or an end of the range lies between the two code units of a surrogate pair.
    /// </summary>
    internal static bool TryApply(string value, long length, long index, long delete, string text, out string result)
    {
        result = "";
        if (length != value.Length || index < 0 || delete < 0 || index > length || delete > length - index) return false;
        if (!IsBoundary(value, (int)index) || !IsBoundary(value, (int)(index + delete))) return false;
        var resultLength = length - delete + text.Length;
        if (resultLength > Array.MaxLength) return false;
        var start = (int)index;
        var end = (int)(index + delete);
        result = string.Create((int)resultLength, (value, start, end, text), static (span, state) =>
        {
            state.value.AsSpan(0, state.start).CopyTo(span);
            state.text.AsSpan().CopyTo(span[state.start..]);
            state.value.AsSpan(state.end).CopyTo(span[(state.start + state.text.Length)..]);
        });
        return true;
    }

    // Whether index is not between a high and a low surrogate of value.
    internal static bool IsBoundary(string value, int index) =>
        index <= 0 || index >= value.Length || !(char.IsHighSurrogate(value[index - 1]) && char.IsLowSurrogate(value[index]));

    // The length of the longest common suffix, block by block from the end.
    private static int CommonSuffixLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var maximum = Math.Min(left.Length, right.Length);
        var common = 0;
        while (maximum - common >= SuffixBlock
            && left[^(common + SuffixBlock)..^common].SequenceEqual(right[^(common + SuffixBlock)..^common]))
            common += SuffixBlock;
        while (common < maximum && left[^(common + 1)] == right[^(common + 1)]) common++;
        return common;
    }
}
