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
public sealed record RunicValidationMessage(
    string Message,
    string? Code = null,
    string? Severity = null,
    IReadOnlyList<string>? MemberPaths = null);
