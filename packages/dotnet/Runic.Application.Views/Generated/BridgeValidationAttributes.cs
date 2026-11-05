using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Runic.Application.Views.Generated;

// translation-text/1 (RunicValidationOptions.TextFor): the property that generated metadata names for a member with
// validation attributes (BridgeDataSubscriptionMember.ValidationProperty), read once per member and process. A failure
// to read it is kept and logged once (event 1177).
internal sealed class BridgeValidationProperty
{
    private int _logged;

    private BridgeValidationProperty(PropertyInfo? property, Exception? error)
    {
        Property = property;
        Error = error;
    }

    internal PropertyInfo? Property { get; }
    internal Exception? Error { get; }

    internal static BridgeValidationProperty Read(Func<PropertyInfo?> read)
    {
        try { return new(read(), null); }
        // Such as an AmbiguousMatchException of hand-written metadata.
        catch (Exception error) { return new(null, error); }
    }

    // Whether this call is the first to report a failure of the member's attributes.
    internal bool FirstFailure() => Interlocked.Exchange(ref _logged, 1) == 0;
}

// translation-text/1: which validation attribute reported each of a member's errors, and the text reference that
// RunicValidationOptions.TextFor gives it.
//
// The Bridge never sees which attribute produced an error: Validator and ObservableValidator report plain
// ValidationResults. So it evaluates the member's attributes again, against the member's current value and with a
// ValidationContext of the member's owner, the way Validator does: [Required] first, and nothing else when it fails,
// then the others in their declared order. Whether an attribute fails does not depend on the culture, so the result
// is the same in every publication, whichever culture the turn that writes it runs in.
//
// Pairing: when the member's errors (every entry of GetErrors(member), including those with a text of their own) and
// its failing attributes line up one to one, the k-th error is the k-th failing attribute's. The error's message only
// overrides that pairing when, formatted in the current culture, it is the message of another of the member's
// attributes and not of the paired one: an error the ViewModel has not revalidated since the value changed. When the
// counts differ (an error the ViewModel adds itself, an IValidatableObject result that names the member, an error the
// ViewModel has not revalidated), the message alone decides: a failing attribute whose message it is, else the only
// attribute whose message it is, else none. That fallback depends on the culture the message was formatted in.
//
// The attributes are fresh instances per evaluation (GetCustomAttributes creates them), not ones cached per member:
// evaluating an attribute changes it. [Range] with string limits parses them on its first use and keeps them, and
// [Compare] keeps the other property's display name; a shared instance would keep the culture of whichever window
// used it first, and TextFor receives the instance. A [Range] with string limits parses them in the invariant culture
// (RangeAttribute.ParseLimitsInInvariantCulture), whatever it declares: the limits of [Range(typeof(decimal), "0.5",
// "9.5")] are C# literals, and the ViewModel's own validator, whose TypeDescriptor-cached instance parses them once in
// the culture of its first use, reads them the same way only in an invariant-like culture (in de-DE "0.5" does not
// parse at all). Declare ParseLimitsInInvariantCulture = true to keep both the same in every culture.
internal static class BridgeValidationAttributes
{
    // The texts of the member's entries, by index; null when none has one. messages[k] is the message of an entry that
    // may take an attribute's text, or null for one that has a text of its own or is a RunicValidationMessage.
    internal static RunicTextReference?[]? Texts(BridgeDataSubscriptionMember member, object owner, object? value,
        IReadOnlyList<string?> messages, bool complete,
        Func<ValidationAttribute, RunicValidationMember, RunicTextReference?> textFor,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        if (member.ValidationMetadata is not { } metadata || !messages.Any(message => !string.IsNullOrEmpty(message))) return null;
        var modelType = owner.GetType();
        if (metadata.Error is { } readError)
        {
            if (metadata.FirstFailure()) ViewsLog.ValidationAttributesFailed(logger, modelType.Name, member.PropertyName, readError);
            return null;
        }
        // A union's members include every case's: only the owner's own case has the property.
        if (metadata.Property is not { } property || property.DeclaringType?.IsInstanceOfType(owner) != true) return null;

        ValidationAttribute[] attributes;
        string[] displayNames;
        bool[] failing;
        try
        {
            attributes = [.. property.GetCustomAttributes<ValidationAttribute>(true)];
            if (attributes.Length == 0) return null;
            foreach (var attribute in attributes)
                if (attribute is RangeAttribute { Minimum: string } range) range.ParseLimitsInInvariantCulture = true;
            displayNames = DisplayNames(property);
            failing = Failing(attributes, owner, value, property.Name, displayNames[0]);
        }
        catch (Exception error)
        {
            // An attribute whose constructor or IsValid throws: the member's errors keep their messages.
            if (metadata.FirstFailure()) ViewsLog.ValidationAttributesFailed(logger, modelType.Name, member.PropertyName, error);
            return null;
        }

        var order = new List<int>(attributes.Length);
        for (var index = 0; index < attributes.Length; index++)
            if (failing[index]) order.Add(index);
        var positional = complete && order.Count == messages.Count;
        var used = new bool[attributes.Length];
        RunicTextReference?[]? texts = null;
        for (var entry = 0; entry < messages.Count; entry++)
        {
            if (messages[entry] is not { Length: > 0 } message) continue;
            if (Attribute(message, positional ? order[entry] : null, attributes, failing, used, displayNames)
                is not ({ } chosen, var displayName)) continue;
            used[chosen] = true;
            try
            {
                if (textFor(attributes[chosen], new(modelType, property.Name, displayName, message)) is { } text)
                    (texts ??= new RunicTextReference?[messages.Count])[entry] = text;
            }
            catch (Exception error)
            {
                ViewsLog.ValidationTextForFailed(logger, modelType.Name, property.Name, attributes[chosen].GetType().Name, error);
            }
        }
        return texts;
    }

    // Which attributes fail for the value, as Validator.TryValidateProperty evaluates them.
    private static bool[] Failing(ValidationAttribute[] attributes, object owner, object? value, string name, string displayName)
    {
        var failing = new bool[attributes.Length];
        // The display name is given, so the context reads no metadata of its own (trim-safe).
        var context = new ValidationContext(owner, displayName, null, null) { MemberName = name };
        var required = Array.FindIndex(attributes, attribute => attribute is RequiredAttribute);
        if (required >= 0 && attributes[required].GetValidationResult(value, context) is not null)
        {
            failing[required] = true;
            return failing;
        }
        for (var index = 0; index < attributes.Length; index++)
            if (index != required && attributes[index].GetValidationResult(value, context) is not null) failing[index] = true;
        return failing;
    }

    // The attribute of one error, and the display name its message used.
    private static (int? Attribute, string DisplayName) Attribute(string message, int? paired, ValidationAttribute[] attributes,
        bool[] failing, bool[] used, string[] displayNames)
    {
        if (paired is { } pairedIndex && Formats(attributes[pairedIndex], message, displayNames) is { } pairedName)
            return (pairedIndex, pairedName);
        int? failingMatch = null, otherMatch = null;
        string? failingName = null, otherName = null;
        var others = 0;
        for (var index = 0; index < attributes.Length; index++)
        {
            if (index == paired || Formats(attributes[index], message, displayNames) is not { } name) continue;
            if (failing[index])
            {
                // Of two failing attributes with the same message, the first one no other error took.
                if (failingMatch is null || used[failingMatch.Value] && !used[index]) (failingMatch, failingName) = (index, name);
            }
            else if (others++ == 0) (otherMatch, otherName) = (index, name);
        }
        if (failingMatch is { } found) return (found, failingName!);
        if (others == 1) return (otherMatch, otherName!);
        // No other attribute's message, or several: the pairing stands, or the error has no attribute.
        return others == 0 && paired is not null ? (paired, displayNames[0]) : (null, displayNames[0]);
    }

    // The display name with which the attribute formats the message in the current culture, if any.
    private static string? Formats(ValidationAttribute attribute, string message, string[] displayNames)
    {
        foreach (var displayName in displayNames)
        {
            string formatted;
            // An attribute that cannot format its message, such as a [Range] whose limits do not convert.
            try { formatted = attribute.FormatErrorMessage(displayName); }
            catch (Exception) { continue; }
            if (string.Equals(formatted, message, StringComparison.Ordinal)) return displayName;
        }
        return null;
    }

    // The display names a message may use: DataAnnotations' Validator takes [Display(Name)], then [DisplayName], then
    // the property name; CommunityToolkit's ObservableValidator [Display(Name)], then the property name. Read on each
    // evaluation, as [Display(ResourceType = …)] gives the name of the current UI culture.
    private static string[] DisplayNames(PropertyInfo property)
    {
        var names = new List<string>(3);
        try
        {
            if (property.GetCustomAttribute<DisplayAttribute>(true)?.GetName() is { Length: > 0 } display) names.Add(display);
        }
        catch (InvalidOperationException) { } // A [Display] whose resource it cannot read.
        if (property.GetCustomAttribute<DisplayNameAttribute>(true)?.DisplayName is { Length: > 0 } displayName
            && !names.Contains(displayName))
            names.Add(displayName);
        if (!names.Contains(property.Name)) names.Add(property.Name);
        return [.. names];
    }
}
