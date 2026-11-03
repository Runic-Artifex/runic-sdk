using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Runic.Application.Views;

/// <summary>
/// A text reference of Runic Translations, held as its wire JSON: a catalog, a message key, typed arguments and an
/// optional fallback text. The page shows it in the window's language with the Translations ESM runtime, so text that a
/// ViewModel produces follows a language switch without a call per string (the <c>text-reference</c> wire type).
/// </summary>
/// <remarks>
/// <para>
/// A ViewModel member, DTO member or command result of type <c>Runic.Translations.TranslationReference</c> crosses the
/// Bridge without this type; the generator recognizes it by name, so the Views packages do not depend on Runic
/// Translations. Use <see cref="RunicTextReference"/> where Views code needs a reference, such as
/// <see cref="RunicValidationMessage.Text"/> and <see cref="RunicValidationResult.Text"/>, or where the application only has the
/// JSON:
/// </para>
/// <code>
/// var text = RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference);
/// </code>
/// <para>
/// Reading checks the shape of the reference, as the Translations readers do: an object with an integer
/// <c>version</c> of at least 1, a <c>catalog</c>, a <c>contractFingerprint</c>, a nonempty <c>key</c>, an
/// <c>arguments</c> object and, when present, a string <c>fallbackText</c> (or <see langword="null"/> in version 1), at
/// most 256 KiB of UTF-8. A later version is accepted: the Translations runtime that shows the reference reports a
/// version it does not read, and shows the fallback text, as it does for a reference that does not match its catalog.
/// </para>
/// </remarks>
public sealed class RunicTextReference : IEquatable<RunicTextReference>
{
    /// <summary>The largest reference in UTF-8 bytes, as the Translations transport allows.</summary>
    public const int MaximumUtf8Bytes = 256 * 1024;

    private readonly byte[] _utf8;

    private RunicTextReference(byte[] utf8, int version, string catalog, string contractFingerprint, string key,
        string? fallbackText)
    {
        _utf8 = utf8;
        Version = version;
        Catalog = catalog;
        ContractFingerprint = contractFingerprint;
        Key = key;
        FallbackText = fallbackText;
    }

    /// <summary>The transport version: 1, 2, or a later one that the page's Translations runtime may not read.</summary>
    public int Version { get; }

    /// <summary>The catalog id.</summary>
    public string Catalog { get; }

    /// <summary>The writer's catalog contract fingerprint.</summary>
    public string ContractFingerprint { get; }

    /// <summary>The message key, such as <c>title_too_long</c>.</summary>
    public string Key { get; }

    /// <summary>The text to show when the page cannot translate the reference, if the writer stored one.</summary>
    public string? FallbackText { get; }

    /// <summary>The reference's JSON, as UTF-8.</summary>
    public ReadOnlyMemory<byte> Utf8Json => _utf8;

    /// <summary>Reads a reference from its JSON text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The text is not one JSON value of a text reference's shape.</exception>
    public static RunicTextReference Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumUtf8Bytes) throw TooLarge();
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Reads a reference from its UTF-8 JSON.</summary>
    /// <exception cref="JsonException">The text is not one JSON value of a text reference's shape.</exception>
    public static RunicTextReference Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumUtf8Bytes) throw TooLarge();
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 64 });
        using var document = JsonDocument.ParseValue(ref reader);
        if (reader.Read()) throw new JsonException("A text reference is one JSON value.");
        return Read(document.RootElement);
    }

    /// <summary>Reads a reference from a JSON value, such as a member of a message.</summary>
    /// <exception cref="JsonException">The value does not have a text reference's shape.</exception>
    public static RunicTextReference Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("A text reference is a JSON object.");
        // Any later transport version is carried as it is: the page's Translations runtime reports a version it does not
        // read and shows the fallback text, so the rest of the state still decodes. An integer token, as the
        // Translations readers require (2.0 is not a version).
        var version = value.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.Number
            && versionElement.TryGetInt32(out var number) && number >= 1
            && versionElement.GetRawText() == number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? number : throw new JsonException("A text reference's version is an integer of at least 1.");
        var catalog = RequiredString(value, "catalog");
        var contractFingerprint = RequiredString(value, "contractFingerprint");
        var key = RequiredString(value, "key");
        if (key.Length == 0) throw new JsonException("A text reference's key is not empty.");
        if (!value.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new JsonException("A text reference has an arguments object.");
        string? fallbackText = null;
        // Version 1 readers take a null fallback as none; later versions leave the member out instead.
        if (value.TryGetProperty("fallbackText", out var fallback) && !(version == 1 && fallback.ValueKind == JsonValueKind.Null))
            fallbackText = fallback.ValueKind == JsonValueKind.String ? fallback.GetString()
                : throw new JsonException("A text reference's fallbackText is a string (or null in version 1).");

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, BridgeJsonEncoder.WriterOptions)) value.WriteTo(writer);
        if (buffer.WrittenCount > MaximumUtf8Bytes) throw TooLarge();
        return new(buffer.WrittenSpan.ToArray(), version, catalog, contractFingerprint, key, fallbackText);
    }

    /// <summary>
    /// Creates a reference from a value that its source-generated JSON metadata writes as a text reference, such as a
    /// <c>Runic.Translations.TranslationReference</c> with <c>TranslationReferenceJsonContext.Default.TranslationReference</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The value is not written as a text reference.</exception>
    public static RunicTextReference Create<T>(T reference, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Parse(JsonSerializer.SerializeToUtf8Bytes(reference, typeInfo));
    }

    /// <summary>Writes the reference as one JSON value.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(_utf8, skipInputValidation: true);
    }

    /// <summary>The reference's JSON text.</summary>
    public string ToJson() => Encoding.UTF8.GetString(_utf8);

    /// <summary>The fallback text, or the key when the reference has none.</summary>
    public override string ToString() => FallbackText ?? Key;

    /// <summary>Whether both references have the same JSON.</summary>
    public bool Equals(RunicTextReference? other) =>
        other is not null && (ReferenceEquals(this, other) || _utf8.AsSpan().SequenceEqual(other._utf8));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RunicTextReference);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_utf8);
        return hash.ToHashCode();
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new JsonException($"A text reference has a string {name}.");

    private static JsonException TooLarge() =>
        new($"A text reference is at most {MaximumUtf8Bytes} bytes of UTF-8.");
}

/// <summary>
/// An entry of validation that carries a text reference for the page to show in the window's language. The Bridge
/// writes its <see cref="Text"/> next to the entry's message, which stays the fallback (the <c>text</c> of a
/// validation error, feature <c>translation-text/1</c>).
/// </summary>
/// <remarks>
/// <see cref="RunicValidationMessage"/> and <see cref="RunicValidationResult"/> implement it. A ReactiveUI.Validation
/// component, or its <c>IValidationText</c>, that implements it gives the validation it reports the same text.
/// Any other entry of <see cref="System.ComponentModel.INotifyDataErrorInfo.GetErrors(string?)"/> may implement it too.
/// </remarks>
public interface IRunicTextReferenceSource
{
    /// <summary>The text reference, or <see langword="null"/> to show the message.</summary>
    RunicTextReference? Text { get; }
}

/// <summary>
/// A <see cref="ValidationResult"/> with a text reference, for DataAnnotations and CommunityToolkit
/// <c>ObservableValidator</c>: return it from a <see cref="ValidationAttribute"/> or a
/// <see cref="CustomValidationAttribute"/> method, and the page shows <see cref="Text"/> in the window's language, with
/// <see cref="ValidationResult.ErrorMessage"/> as the fallback.
/// </summary>
public class RunicValidationResult : ValidationResult, IRunicTextReferenceSource
{
    /// <summary>Creates a result with a message, its text reference and the members it reports.</summary>
    /// <param name="errorMessage">The message, shown where the page cannot translate <paramref name="text"/>.</param>
    /// <param name="text">The text reference.</param>
    /// <param name="memberNames">The members the result reports, as for <see cref="ValidationResult"/>.</param>
    public RunicValidationResult(string? errorMessage, RunicTextReference? text, IEnumerable<string>? memberNames = null)
        : base(errorMessage, memberNames) => Text = text;

    /// <summary>Copies a result.</summary>
    protected RunicValidationResult(RunicValidationResult validationResult) : base(validationResult) =>
        Text = validationResult.Text;

    /// <inheritdoc />
    public RunicTextReference? Text { get; }
}
