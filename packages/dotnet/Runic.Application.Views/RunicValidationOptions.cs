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
    /// them. It finds the attribute by its message: one of the member's validation attributes whose
    /// <see cref="ValidationAttribute.FormatErrorMessage(string)"/> of the member's display name (<c>[Display(Name)]</c>,
    /// <c>[DisplayName]</c> or the property name) is the error's message, in the current UI culture. A message that a
    /// ViewModel formats itself, or that it produced in another UI culture, matches no attribute and stays as it is.
    /// </para>
    /// <para>
    /// It runs while the Bridge writes the state, on the window's model context: keep it fast and free of side effects.
    /// An exception it throws is logged (event 1176), and the error shows its message.
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
/// <param name="DisplayName">The name the attribute's message used: <c>[Display(Name)]</c>, <c>[DisplayName]</c> or <paramref name="Name"/>.</param>
/// <param name="Message">The error's message, which stays the fallback of the text reference.</param>
public sealed record RunicValidationMember(Type ModelType, string Name, string DisplayName, string Message);
