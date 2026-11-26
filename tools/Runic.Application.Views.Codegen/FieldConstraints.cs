using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>A bound of a <c>[Range]</c>: a number, or the invariant or ISO text of a decimal, date or time bound.</summary>
/// <param name="Text">The number as .NET writes it invariantly, or the text.</param>
internal sealed record FieldConstraintBound(string Text, bool IsNumber);

/// <summary>
/// C8: the DataAnnotations of a field that a page can mirror on its input (<c>required</c>, <c>maxlength</c>,
/// <c>min</c>, <c>max</c>, <c>pattern</c>). They describe the C# validation; .NET still validates. The contract IR
/// carries them as values (<see cref="Json"/>), which the emitter writes into the generated field descriptors.
/// </summary>
internal sealed record FieldConstraintSet(bool Required, int? MinLength, int? MaxLength, FieldConstraintBound? Min, FieldConstraintBound? Max,
    bool MinExclusive, bool MaxExclusive, string? Pattern)
{
    /// <summary>The constraints of <paramref name="property"/>, or null when it has none.</summary>
    internal static FieldConstraintSet? Of(PropertyInfo property)
    {
        var required = property.GetCustomAttribute<RequiredAttribute>(true) is not null;
        var stringLength = property.GetCustomAttribute<StringLengthAttribute>(true);
        var minLength = property.GetCustomAttribute<MinLengthAttribute>(true)?.Length;
        if (stringLength is { MinimumLength: > 0 } && (minLength is null || stringLength.MinimumLength > minLength)) minLength = stringLength.MinimumLength;
        if (minLength is not > 0) minLength = null;
        var maxLength = property.GetCustomAttribute<MaxLengthAttribute>(true)?.Length;
        if (maxLength is <= 0) maxLength = null; // [MaxLength] without a length allows the most the store does.
        if (stringLength is not null && (maxLength is null || stringLength.MaximumLength < maxLength)) maxLength = stringLength.MaximumLength;
        FieldConstraintBound? min = null, max = null;
        var minExclusive = false;
        var maxExclusive = false;
        if (property.GetCustomAttribute<RangeAttribute>(true) is { } range)
        {
            min = Bound(range.Minimum, range.OperandType);
            max = Bound(range.Maximum, range.OperandType);
            minExclusive = range.MinimumIsExclusive;
            maxExclusive = range.MaximumIsExclusive;
        }
        var pattern = property.GetCustomAttribute<RegularExpressionAttribute>(true) is { } regex && SimplePattern(regex.Pattern)
            ? regex.Pattern : null;
        var result = new FieldConstraintSet(required, minLength, maxLength, min, max, minExclusive, maxExclusive, pattern);
        return result == new FieldConstraintSet(false, null, null, null, null, false, false, null) ? null : result;
    }

    // Numeric bounds are numbers. Decimal bounds (Range(typeof(decimal), "0", "9.99")) are their invariant text,
    // and date and time bounds their ISO form, the forms an input's min and max take. A bound that does not
    // read as its type is left out.
    private static FieldConstraintBound? Bound(object value, Type operand)
    {
        var invariant = CultureInfo.InvariantCulture;
        var text = Convert.ToString(value, invariant) ?? "";
        const DateTimeStyles styles = DateTimeStyles.None;
        return value switch
        {
            double d when double.IsInfinity(d) || double.IsNaN(d) => null,
            int or double => new(Convert.ToString(value, invariant)!, IsNumber: true),
            _ when operand == typeof(decimal) => decimal.TryParse(text, NumberStyles.Number, invariant, out var number)
                ? new(number.ToString(invariant), false) : null,
            _ when operand == typeof(DateOnly) => DateOnly.TryParse(text, invariant, styles, out var date)
                ? new(date.ToString("yyyy-MM-dd", invariant), false) : null,
            _ when operand == typeof(TimeOnly) => TimeOnly.TryParse(text, invariant, styles, out var time)
                ? new(time.ToString("HH:mm:ss", invariant), false) : null,
            _ when operand == typeof(DateTime) => DateTime.TryParse(text, invariant, styles, out var moment)
                ? new(moment.TimeOfDay == TimeSpan.Zero ? moment.ToString("yyyy-MM-dd", invariant)
                    : moment.ToString("yyyy-MM-ddTHH:mm:ss", invariant), false) : null,
            _ when operand == typeof(DateTimeOffset) => DateTimeOffset.TryParse(text, invariant, styles, out var instant)
                ? new(instant.ToString("yyyy-MM-ddTHH:mm:sszzz", invariant), false) : null,
            _ when operand == typeof(string) || operand == typeof(int) || operand == typeof(double) => new(text, false),
            _ => null,
        };
    }

    /// <summary>The constraints as the contract IR's <c>constraints</c> object: numbers as JSON numbers, the .NET text kept.</summary>
    internal JsonObject Json()
    {
        var result = new JsonObject();
        static JsonNode Bound(FieldConstraintBound bound) => bound.IsNumber ? JsonNode.Parse(bound.Text)! : JsonValue.Create(bound.Text);
        if (Required) result["required"] = true;
        if (MinLength is { } minLength) result["minLength"] = minLength;
        if (MaxLength is { } maxLength) result["maxLength"] = maxLength;
        if (Min is { } min) result["min"] = Bound(min);
        if (Max is { } max) result["max"] = Bound(max);
        if (MinExclusive) result["minExclusive"] = true;
        if (MaxExclusive) result["maxExclusive"] = true;
        if (Pattern is not null) result["pattern"] = Pattern;
        return result;
    }

    // A .NET pattern that a browser's pattern attribute reads the same way. Browsers compile it with the v flag, which
    // rejects ( ) [ { } / | and a lone - inside a character class, and doubled punctuators such as && or --. Rejected
    // too: inline options, named and balancing groups, lookbehind, the .NET-only anchors \A \Z \z \G, Unicode
    // categories (\p, \P), named backreferences (\k), and \d \w \b (Unicode in .NET, ASCII in JavaScript).
    internal static bool SimplePattern(string pattern)
    {
        var inClass = false;
        var classOpen = -1;
        // In a class: the previous item is one character, which a - can follow to make a range.
        var single = false;
        // In a class: the next character ends a range, so it cannot start another.
        var rangeEnd = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                if (++index >= pattern.Length || "AZzGpPkKdDwWbB".Contains(pattern[index], StringComparison.Ordinal)) return false;
                if (inClass)
                {
                    single = !rangeEnd && pattern[index] is not ('s' or 'S');
                    rangeEnd = false;
                }
                continue;
            }
            if (inClass)
            {
                if (character == '^' && index == classOpen + 1) continue;
                if (character == ']')
                {
                    // []…] is a literal ] in .NET and an empty class in JavaScript.
                    if (index == classOpen + 1 || index == classOpen + 2 && pattern[classOpen + 1] == '^') return false;
                    inClass = false;
                    continue;
                }
                if ("()[{}/|".Contains(character, StringComparison.Ordinal)) return false;
                if (index + 1 < pattern.Length && pattern[index + 1] == character
                    && "&!#$%*+,.:;<=>?@^`~-".Contains(character, StringComparison.Ordinal)) return false;
                if (character == '-')
                {
                    if (!single || index + 1 >= pattern.Length || pattern[index + 1] == ']') return false;
                    single = false;
                    rangeEnd = true;
                    continue;
                }
                single = !rangeEnd;
                rangeEnd = false;
                continue;
            }
            if (character == '[')
            {
                inClass = true;
                classOpen = index;
                single = rangeEnd = false;
                continue;
            }
            if (character == '(' && index + 1 < pattern.Length && pattern[index + 1] == '?'
                && !(index + 2 < pattern.Length && pattern[index + 2] is ':' or '=' or '!')) return false;
        }
        if (inClass) return false;
        try { _ = new System.Text.RegularExpressions.Regex(pattern); } catch (ArgumentException) { return false; }
        return true;
    }
}
