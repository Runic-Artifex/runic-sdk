using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Runic.Platform;
using Runic.Translations;

namespace Runic.Application.Views.Translations;

/// <summary>
/// Runic's own text from the <c>runic-views</c> catalog (English and German): validation messages of the built-in
/// DataAnnotations attributes, and the <see cref="DesktopPlatformTexts"/> that Runic shows in native UI. The page's field
/// errors come from the same catalog in the <c>@runic-artifex/views-translations</c> npm package.
/// </summary>
/// <remarks>
/// <para>
/// <c>services.AddRunicViewsTranslations()</c> wires everything for an application that registers its Translations
/// context with <c>Runic.Translations.Hosting</c>. Without dependency injection, add
/// <see cref="RunicViewsTextCatalog.Registration"/> to the application's <see cref="TranslationContext"/> builder, set
/// <see cref="ValidationText"/> as <see cref="RunicValidationOptions.TextFor"/>, and call <see cref="FollowPlatformTexts"/>.
/// </para>
/// <para>
/// An application changes single messages, or adds a locale, in its own <c>runic.json</c> (<c>"overrides"</c> of the
/// <c>runic-views</c> catalog from this package); the text it overrides follows the same locale.
/// </para>
/// </remarks>
public static class RunicViewsTranslations
{
    /// <summary>
    /// A <see cref="RunicValidationOptions.TextFor"/> that gives the built-in DataAnnotations attributes a text reference
    /// of the <c>runic-views</c> catalog, which the page shows in the window's language; <see langword="null"/> for an
    /// attribute it has no text for, or one with a message of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It covers <see cref="RequiredAttribute"/>, <see cref="MaxLengthAttribute"/>, <see cref="MinLengthAttribute"/>,
    /// <see cref="StringLengthAttribute"/>, <see cref="LengthAttribute"/>, <see cref="RangeAttribute"/> (inclusive limits),
    /// <see cref="EmailAddressAttribute"/>, <see cref="PhoneAttribute"/>, <see cref="UrlAttribute"/>,
    /// <see cref="CreditCardAttribute"/>, <see cref="RegularExpressionAttribute"/>, <see cref="CompareAttribute"/>,
    /// <see cref="FileExtensionsAttribute"/>, <see cref="Base64StringAttribute"/>, <see cref="AllowedValuesAttribute"/>,
    /// <see cref="DeniedValuesAttribute"/> and <see cref="EnumDataTypeAttribute"/>; types derived from them keep their
    /// message, as do attributes that set <see cref="ValidationAttribute.ErrorMessage"/> or a message resource, and a
    /// <see cref="RangeAttribute"/> with an exclusive limit.
    /// </para>
    /// <para>
    /// The field's name is <see cref="RunicValidationMember.DisplayName"/>, as the attribute's message writes it. To combine
    /// it with the application's own texts, call it last:
    /// <c>TextFor = (attribute, member) =&gt; AppText(attribute, member) ?? RunicViewsTranslations.ValidationText(attribute, member)</c>.
    /// The page resolves the reference with this catalog (<c>@runic-artifex/views-translations</c>); a page without it shows
    /// the reference's English fallback.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static RunicTextReference? ValidationText(ValidationAttribute attribute, RunicValidationMember member) =>
        ValidationReference(attribute, member) is { } reference
            ? RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference)
            : null;

    /// <summary>
    /// The <c>runic-views</c> reference that <see cref="ValidationText"/> gives an attribute, as a Translations
    /// <see cref="TranslationReference"/>, for code that resolves it in C# (<see cref="ITranslationContext.Resolve"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static TranslationReference? ValidationReference(ValidationAttribute attribute, RunicValidationMember member)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(member);
        if (HasOwnMessage(attribute)) return null;
        var field = member.DisplayName;
        // Exact types: a derived attribute may format its own message.
        return attribute switch
        {
            _ when attribute.GetType() == typeof(RequiredAttribute) => RunicViewsText.References.validation_required(field: field),
            StringLengthAttribute length when attribute.GetType() == typeof(StringLengthAttribute) => length.MinimumLength > 0
                ? RunicViewsText.References.validation_length_between(field: field, min: length.MinimumLength, max: length.MaximumLength)
                : RunicViewsText.References.validation_max_length(field: field, max: length.MaximumLength),
            // MaxLength(-1), the default, allows any length and never fails.
            MaxLengthAttribute maximum when attribute.GetType() == typeof(MaxLengthAttribute) && maximum.Length >= 0 =>
                RunicViewsText.References.validation_max_length(field: field, max: maximum.Length),
            MinLengthAttribute minimum when attribute.GetType() == typeof(MinLengthAttribute) =>
                RunicViewsText.References.validation_min_length(field: field, min: minimum.Length),
            LengthAttribute length when attribute.GetType() == typeof(LengthAttribute) =>
                RunicViewsText.References.validation_length_between(field: field, min: length.MinimumLength, max: length.MaximumLength),
            RangeAttribute range when attribute.GetType() == typeof(RangeAttribute) => Range(field, range),
            _ when attribute.GetType() == typeof(EmailAddressAttribute) => RunicViewsText.References.validation_email(field: field),
            _ when attribute.GetType() == typeof(PhoneAttribute) => RunicViewsText.References.validation_phone(field: field),
            _ when attribute.GetType() == typeof(UrlAttribute) => RunicViewsText.References.validation_url(field: field),
            _ when attribute.GetType() == typeof(CreditCardAttribute) => RunicViewsText.References.validation_credit_card(field: field),
            _ when attribute.GetType() == typeof(RegularExpressionAttribute) => RunicViewsText.References.validation_pattern(field: field),
            CompareAttribute compare when attribute.GetType() == typeof(CompareAttribute) =>
                RunicViewsText.References.validation_compare(field: field, other: compare.OtherPropertyDisplayName ?? compare.OtherProperty),
            FileExtensionsAttribute extensions when attribute.GetType() == typeof(FileExtensionsAttribute) =>
                RunicViewsText.References.validation_file_extensions(field: field, extensions: Extensions(extensions.Extensions)),
            _ when attribute.GetType() == typeof(Base64StringAttribute) => RunicViewsText.References.validation_base64(field: field),
            _ when attribute.GetType() == typeof(AllowedValuesAttribute) => RunicViewsText.References.validation_allowed_values(field: field),
            _ when attribute.GetType() == typeof(DeniedValuesAttribute) => RunicViewsText.References.validation_denied_values(field: field),
            _ when attribute.GetType() == typeof(EnumDataTypeAttribute) => RunicViewsText.References.validation_enum(field: field),
            _ => null,
        };
    }

    /// <summary>
    /// The <see cref="DesktopPlatformTexts"/> in the context's current locale: Linux dialog titles, the untitled file name,
    /// the macOS Choose an application title, the Linux keyring label, shortcut key names and a browser window's error pages.
    /// </summary>
    /// <param name="context">A Translations context with <see cref="RunicViewsTextCatalog.Registration"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The context has no <c>runic-views</c> catalog.</exception>
    public static DesktopPlatformTexts PlatformTexts(ITranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = new RunicViewsText(context).Messages;
        return new DesktopPlatformTexts
        {
            OpenFileTitle = text.platform_open_file_title,
            OpenFilesTitle = text.platform_open_files_title,
            OpenDirectoryTitle = text.platform_open_directory_title,
            OpenDirectoriesTitle = text.platform_open_directories_title,
            SaveFileTitle = text.platform_save_file_title,
            UntitledFileName = text.platform_untitled_file_name,
            ChooseApplicationTitle = text.platform_choose_application_title,
            SecretCollectionLabel = text.platform_secret_collection_label,
            ControlKeyName = text.platform_control_key,
            ShiftKeyName = text.platform_shift_key,
            AltKeyName = text.platform_alt_key,
            AccessDeniedTitle = text.platform_access_denied_title,
            ContentInUseMessage = text.platform_content_in_use,
            LaunchLinkInvalidMessage = text.platform_launch_link_invalid,
            ResourceUnavailableTitle = text.platform_resource_unavailable_title,
            ResourceUnavailableMessage = text.platform_resource_unavailable,
        };
    }

    /// <summary>
    /// Sets <see cref="DesktopPlatformTexts.Current"/> from the context now and whenever the <c>runic-views</c> catalog
    /// publishes new text: after a locale switch (the context follows the application's locale with
    /// <c>IRunicApplicationLocale.Follow(context.SetLocaleAsync)</c>) and after a refresh of its overlays.
    /// </summary>
    /// <remarks>
    /// The texts change before <see cref="IRunicApplicationLocale.LocaleChanged"/> is raised, as the context switches first.
    /// A text that <see cref="DesktopPlatformTexts"/> refuses, such as an overridden untitled file name with a <c>/</c>, keeps
    /// the previous texts and is passed to <paramref name="onError"/>; without it, the switch's thread sees the
    /// <see cref="ArgumentException"/>. Set the texts only through one follower: <see cref="DesktopPlatformTexts.Current"/>
    /// is process-wide.
    /// </remarks>
    /// <param name="context">A Translations context with <see cref="RunicViewsTextCatalog.Registration"/>.</param>
    /// <param name="onError">Receives a text the platform refused, or <see langword="null"/>.</param>
    /// <returns>A registration; dispose it to stop following. The current texts stay.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The context has no <c>runic-views</c> catalog.</exception>
    public static IDisposable FollowPlatformTexts(ITranslationContext context, Action<ArgumentException>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var manager = context.GetManager(RunicViewsTextCatalog.CatalogId);
        var follower = new PlatformTextsFollower(context, manager, onError);
        manager.SnapshotPublished += follower.OnPublished;
        follower.Apply();
        return follower;
    }

    // The application wrote or localized this message itself. ErrorMessage reads the attribute's English default where it
    // has one (EmailAddress, Phone, Url, …), so only another message counts.
    private static bool HasOwnMessage(ValidationAttribute attribute) =>
        attribute.ErrorMessageResourceType is not null || attribute.ErrorMessageResourceName is not null
        || attribute.ErrorMessage is { } message && !string.Equals(message, DefaultMessages.Of(attribute.GetType()), StringComparison.Ordinal);

    private static class DefaultMessages
    {
        private static readonly string? Email = new EmailAddressAttribute().ErrorMessage;
        private static readonly string? Phone = new PhoneAttribute().ErrorMessage;
        private static readonly string? Url = new UrlAttribute().ErrorMessage;
        private static readonly string? CreditCard = new CreditCardAttribute().ErrorMessage;
        private static readonly string? FileExtensions = new FileExtensionsAttribute().ErrorMessage;
        private static readonly string? Base64 = new Base64StringAttribute().ErrorMessage;
        private static readonly string? Allowed = new AllowedValuesAttribute().ErrorMessage;
        private static readonly string? Denied = new DeniedValuesAttribute().ErrorMessage;

        public static string? Of(Type type) =>
            type == typeof(EmailAddressAttribute) ? Email : type == typeof(PhoneAttribute) ? Phone : type == typeof(UrlAttribute) ? Url
            : type == typeof(CreditCardAttribute) ? CreditCard : type == typeof(FileExtensionsAttribute) ? FileExtensions
            : type == typeof(Base64StringAttribute) ? Base64 : type == typeof(AllowedValuesAttribute) ? Allowed
            : type == typeof(DeniedValuesAttribute) ? Denied : null;
    }

    private static TranslationReference? Range(string field, RangeAttribute range)
    {
        if (range.MinimumIsExclusive || range.MaximumIsExclusive) return null;
        // RangeAttribute converts text limits to its operand type when it first validates, and the Bridge passes an
        // evaluated attribute, so both forms arrive. Numbers are formatted in the window's locale.
        return (range.Minimum, range.Maximum) switch
        {
            (int minimum, int maximum) => RunicViewsText.References.validation_range_integer(field: field, min: minimum, max: maximum),
            (double minimum, double maximum) => Numbers(field, Decimal(minimum), Decimal(maximum)),
            (decimal minimum, decimal maximum) => Numbers(field, minimum, maximum),
            (string minimum, string maximum) when IsNumber(range.OperandType) => Numbers(field, Parse(minimum), Parse(maximum)),
            (DateOnly minimum, DateOnly maximum) => Text(field, Iso(minimum), Iso(maximum)),
            (DateTime minimum, DateTime maximum) when minimum.TimeOfDay == TimeSpan.Zero && maximum.TimeOfDay == TimeSpan.Zero =>
                Text(field, Iso(DateOnly.FromDateTime(minimum)), Iso(DateOnly.FromDateTime(maximum))),
            // Other limits given as text, such as DateTime values: shown as written.
            (string minimum, string maximum) => Text(field, minimum, maximum),
            _ => null,
        };

        static TranslationReference Text(string field, string minimum, string maximum) =>
            RunicViewsText.References.validation_range(field: field, min: minimum, max: maximum);

        static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // A range of numbers, whole when both limits are; null, keeping the attribute's message, for a limit that is no
    // decimal, such as double.MaxValue.
    private static TranslationReference? Numbers(string field, decimal? minimum, decimal? maximum)
    {
        if (minimum is not { } low || maximum is not { } high) return null;
        return low == decimal.Truncate(low) && high == decimal.Truncate(high) && low >= long.MinValue && high <= long.MaxValue
            ? RunicViewsText.References.validation_range_integer(field: field, min: (long)low, max: (long)high)
            : RunicViewsText.References.validation_range_number(field: field, min: low, max: high);
    }

    private static bool IsNumber(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint)
        || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte) || type == typeof(decimal) || type == typeof(double)
        || type == typeof(float);

    private static decimal? Decimal(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 7.9e28 ? (decimal)value : null;

    private static decimal? Parse(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Extensions(string extensions) =>
        string.Join(", ", extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed class PlatformTextsFollower(ITranslationContext context, ITranslationManager manager, Action<ArgumentException>? onError)
        : IDisposable
    {
        private int _disposed;

        public void OnPublished(object? sender, TranslationSnapshotPublishedEventArgs args)
        {
            if (Volatile.Read(ref _disposed) == 0) Apply();
        }

        public void Apply()
        {
            var texts = PlatformTexts(context);
            try { DesktopPlatformTexts.Current = texts; }
            catch (ArgumentException error) when (onError is not null) { onError(error); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) manager.SnapshotPublished -= OnPublished;
        }
    }
}
