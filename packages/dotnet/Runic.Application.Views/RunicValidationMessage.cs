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
/// <remarks>
/// Set <see cref="Text"/> to a text reference, and the page shows the message in the window's language, with
/// <see cref="Message"/> as the fallback:
/// <code>
/// new RunicValidationMessage("Title must be at most 80 characters.", "title-too-long")
/// {
///     Text = RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference),
/// }
/// </code>
/// </remarks>
public sealed record RunicValidationMessage(
    string Message,
    string? Code = null,
    string? Severity = null,
    IReadOnlyList<string>? MemberPaths = null) : IRunicTextReferenceSource
{
    /// <summary>
    /// The message as a text reference that the page shows in the window's language, or <see langword="null"/> to show
    /// <see cref="Message"/>. <see cref="Message"/> stays the fallback (the <c>text</c> of a validation error).
    /// </summary>
    public RunicTextReference? Text { get; init; }
}
