using System.ComponentModel.DataAnnotations;

namespace Runic.Application.Views;

/// <summary>
/// How the Bridge writes a ViewModel's validation. Register one in the application's services
/// (<c>services.AddSingleton(new RunicValidationOptions { … })</c>); the Desktop, WPF, CS-WebUI and test hosts give it
/// to each window they open, or set <see cref="WindowContentSessionOptions.Validation"/>.
/// </summary>
public sealed class RunicValidationOptions
{
    /// <summary>
    /// Gives the message of a <see cref="ValidationAttribute"/>, such as <c>[Required]</c>, <c>[MaxLength]</c> or
    /// <c>[Range]</c>, a text reference that the page shows in the window's language, or returns <see langword="null"/>
    /// to show the message as it is (feature <c>translation-text/1</c>). The message stays the error's fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Bridge calls it for a validation error of a bridged member that has no text reference of its own: a
    /// <see cref="ValidationResult"/> or a string of <see cref="System.ComponentModel.INotifyDataErrorInfo.GetErrors(string?)"/>
    /// for the member, as CommunityToolkit's <c>ObservableValidator</c> and a hand-written <c>Validator</c> call report
    /// them. It finds the attribute by evaluating the member's attributes against its current value, as
    /// <see cref="Validator"/> does (<c>[Required]</c> first, then in declared order), and pairs the member's errors with
    /// the failing attributes in order when their counts match, so an error formatted in another culture, such as one
    /// kept from before a window language switch, keeps its attribute. Otherwise, or when the error is another
    /// attribute's message, the message decides: <see cref="ValidationAttribute.FormatErrorMessage(string)"/> of the
    /// member's display name (<c>[Display(Name)]</c>, <c>[DisplayName]</c> or the property name) in the current culture.
    /// A message that a ViewModel writes itself while the member's attributes pass stays as it is. The attributes are
    /// evaluated on fresh instances, with <see cref="RangeAttribute"/> string limits parsed in the invariant culture.
    /// </para>
    /// <para>
    /// It runs while the Bridge writes the state, on the window's model context: keep it, and the attributes, fast and
    /// free of side effects. An exception it throws is logged (event 1176), and the error shows its message; an attribute
    /// that cannot be evaluated leaves its member's errors without a text (event 1177, once per member).
    /// </para>
    /// <code>
    /// services.AddSingleton(new RunicValidationOptions
    /// {
    ///     TextFor = (attribute, member) => attribute switch
    ///     {
    ///         RequiredAttribute => Text(AppText.Required(member.DisplayName)),
    ///         MaxLengthAttribute maximum => Text(AppText.TooLong(member.DisplayName, maximum.Length)),
    ///         RangeAttribute range => Text(AppText.OutOfRange(member.DisplayName, $"{range.Minimum}", $"{range.Maximum}")),
    ///         _ => null,
    ///     },
    /// });
    ///
    /// static RunicTextReference Text(TranslationReference reference) =>
    ///     RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference);
    /// </code>
    /// </remarks>
    public Func<ValidationAttribute, RunicValidationMember, RunicTextReference?>? TextFor { get; init; }
}

/// <summary>The member whose validation attribute reported an error, for <see cref="RunicValidationOptions.TextFor"/>.</summary>
/// <param name="ModelType">The type of the object that has the member, such as the ViewModel or a DTO.</param>
/// <param name="Name">The .NET property name.</param>
/// <param name="DisplayName">The name the attribute's message used: <c>[Display(Name)]</c>, <c>[DisplayName]</c> or <paramref name="Name"/>, or the first of them when the Bridge paired the error with the attribute without matching its message, such as one formatted in another culture.</param>
/// <param name="Message">The error's message, which stays the fallback of the text reference.</param>
public sealed record RunicValidationMember(Type ModelType, string Name, string DisplayName, string Message);
