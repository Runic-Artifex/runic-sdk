namespace Runic.Application.Views;

/// <summary>
/// How a <see cref="RunicAppendOnlyTextAttribute"/> value continues the value the page has: drop <see cref="Trim"/>
/// UTF-16 code units from the start of the <see cref="Length"/> code units the page has, then append
/// <see cref="Text"/>.
/// </summary>
internal readonly record struct BridgeTextDelta(int Length, int Trim, string Text)
{
    // A log with repeated lines can offer several places where the kept text
    // ends; past these, only the shorter kept texts are compared.
    private const int MaximumCandidates = 8;
    private const int ProbeLength = 64;

    /// <summary>
    /// The delta from <paramref name="baseline"/> to <paramref name="value"/> that keeps the longest suffix of
    /// <paramref name="baseline"/> that <paramref name="value"/> starts with, or <see langword="null"/> when the
    /// appended text would start inside a surrogate pair.
    /// </summary>
    internal static BridgeTextDelta? Between(string baseline, string value)
    {
        // The common case: text appended to everything the page has.
        if (value.StartsWith(baseline, StringComparison.Ordinal)) return Create(baseline, 0, value);
        var span = baseline.AsSpan();
        var text = value.AsSpan();
        // The kept text ends where the baseline ends, so a long one ends with
        // the baseline's last characters. Where they occur in the value, latest
        // first, gives the longest candidates; a log that drops its start keeps
        // all but a few lines.
        if (span.Length > ProbeLength)
        {
            var tail = span[^ProbeLength..];
            // Something is dropped, so at most all but one character is kept.
            var region = text[..Math.Min(text.Length, span.Length - 1)];
            for (var candidates = 0; candidates < MaximumCandidates; candidates++)
            {
                var found = region.LastIndexOf(tail, StringComparison.Ordinal);
                if (found < 0) break;
                var kept = found + ProbeLength;
                if (text.StartsWith(span[^kept..], StringComparison.Ordinal)) return Create(baseline, span.Length - kept, value);
                region = region[..(kept - 1)];
            }
        }
        // A kept text shorter than the probe, compared directly, longest first.
        for (var kept = Math.Min(Math.Min(ProbeLength, span.Length - 1), text.Length); kept > 0; kept--)
            if (text.StartsWith(span[^kept..], StringComparison.Ordinal)) return Create(baseline, span.Length - kept, value);
        // Nothing is kept: the value replaces the text.
        return Create(baseline, span.Length, value);
    }

    private static BridgeTextDelta? Create(string baseline, int trim, string value)
    {
        var kept = baseline.Length - trim;
        // JSON cannot carry half of a surrogate pair; a full state carries the value whole.
        if (kept < value.Length && char.IsLowSurrogate(value[kept])) return null;
        return new(baseline.Length, trim, value[kept..]);
    }
}
