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
/// context with <c>Runic.Translations.Hosting</c>. Without dependency injection, add <see cref="CatalogRegistration"/> to
/// the application's <see cref="TranslationContext"/> builder, set <see cref="ValidationText"/> as
/// <see cref="RunicValidationOptions.TextFor"/>, and call <see cref="FollowPlatformTexts"/>.
/// </para>
/// <para>
/// An application changes single messages, or adds a locale, in its own <c>runic.json</c> (<c>"overrides"</c> of the
/// <c>runic-views</c> catalog from this package); the text it overrides follows the same locale.
/// </para>
/// </remarks>
public static class RunicViewsTranslations
{
    /// <summary>The catalog's ID, <c>runic-views</c>: the <c>"catalog"</c> of an override in <c>runic.json</c>.</summary>
    public const string CatalogId = "runic-views";

    /// <summary>
    /// The <c>runic-views</c> catalog, for a <see cref="TranslationContext"/> builder:
    /// <c>TranslationContext.CreateBuilder().Add(AppTextCatalog.Registration).Add(RunicViewsTranslations.CatalogRegistration)</c>.
    /// <c>AddRunicViewsTranslations()</c> registers it with <c>Runic.Translations.Hosting</c>.
    /// </summary>
    public static TranslationCatalogRegistration CatalogRegistration => RunicViewsTextCatalog.Registration;

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
    /// the macOS Choose an application title, the Linux keyring label, shortcut key names, a browser window's error pages
    /// and the title of a Windows window while WebView2 starts.
    /// </summary>
    /// <param name="context">A Translations context with <see cref="CatalogRegistration"/>.</param>
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
            LoadingTitle = text.platform_loading_title,
        };
    }

    /// <summary>
    /// The titles of Runic Desktop's standard macOS menus in the context's current locale, with <c>{0}</c> for the
    /// application's name, which Runic inserts. Map them to <c>DesktopStandardMenuTitles</c>, whose members have the same
    /// names, and pass them to <c>DesktopHost.SetStandardMenuTitles</c> (the README shows how).
    /// </summary>
    /// <remarks>
    /// This package does not reference <c>Runic.Desktop</c>, so that CS-WebUI applications do not depend on it.
    /// </remarks>
    /// <param name="context">A Translations context with <see cref="CatalogRegistration"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The context has no <c>runic-views</c> catalog.</exception>
    public static RunicViewsMenuTitles MenuTitles(ITranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunicViewsMenuTitles.From(new RunicViewsText(context).Messages);
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
    /// <param name="context">A Translations context with <see cref="CatalogRegistration"/>.</param>
    /// <param name="onError">Receives a text the platform refused, or <see langword="null"/>.</param>
    /// <returns>A registration; dispose it to stop following. The current texts stay.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The context has no <c>runic-views</c> catalog.</exception>
    public static IDisposable FollowPlatformTexts(ITranslationContext context, Action<ArgumentException>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var manager = context.GetManager(CatalogId);
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
        // Before its first validation the attribute holds the limits it was given: int, double, or text that it parses
        // into its OperandType (in the current culture unless ParseLimitsInInvariantCulture). Validation replaces them with
        // the parsed values, and the Bridge passes an evaluated attribute, so both forms arrive and give the same text.
        var culture = range.ParseLimitsInInvariantCulture ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;
        var (minimum, maximum) = (Limit(range.Minimum, range.OperandType, culture), Limit(range.Maximum, range.OperandType, culture));
        return (minimum, maximum) switch
        {
            ({ Number: { } low }, { Number: { } high }) => Numbers(field, low, high),
            ({ Text: { } low }, { Text: { } high }) => RunicViewsText.References.validation_range(field: field, min: low, max: high),
            _ => null,
        };
    }

    // A limit as a number, formatted in the window's locale, or as text: ISO dates and times, and other limits as written.
    // Default for a number that is no decimal, such as double.MaxValue, and for a limit of the wrong kind.
    private readonly record struct RangeLimit(decimal? Number, string? Text);

    private static RangeLimit Limit(object? value, Type operandType, CultureInfo culture)
    {
        if (value is string text && operandType != typeof(string))
        {
            // A type without a parser here, such as an enum, or text validation would reject: the limit as written.
            if (Parse(text, operandType, culture) is not { } parsed) return new(null, text);
            value = parsed;
        }
        return value switch
        {
            sbyte or byte or short or ushort or int or uint or long or ulong => new(Convert.ToDecimal(value, CultureInfo.InvariantCulture), null),
            Int128 number => new(number >= (Int128)decimal.MinValue && number <= (Int128)decimal.MaxValue ? (decimal)number : null, null),
            UInt128 number => new(number <= (UInt128)decimal.MaxValue ? (decimal)number : null, null),
            Half number => new(Decimal((float)number), null),
            float number => new(Decimal(number), null),
            double number => new(Decimal(number), null),
            decimal number => new(number, null),
            DateOnly date => new(null, Iso(date)),
            DateTime date => new(null, date.TimeOfDay == TimeSpan.Zero ? Iso(DateOnly.FromDateTime(date)) : $"{Iso(DateOnly.FromDateTime(date))} {Clock(date.TimeOfDay)}"),
            DateTimeOffset date => new(null, $"{Iso(DateOnly.FromDateTime(date.DateTime))} {Clock(date.TimeOfDay)} {date.ToString("zzz", CultureInfo.InvariantCulture)}"),
            TimeOnly time => new(null, Clock(time.ToTimeSpan())),
            TimeSpan span => new(null, span.ToString("c", CultureInfo.InvariantCulture)),
            string written => new(null, written),
            IFormattable other => new(null, other.ToString(null, CultureInfo.InvariantCulture)),
            null => default,
            _ => new(null, value.ToString()),
        };
    }

    // The limit text parsed as RangeAttribute parses it (its TypeConverter for the operand type), for the types that have
    // a text form here; null for another type or text that does not parse, which validation rejects too.
    private static object? Parse(string text, Type type, CultureInfo culture)
    {
        text = text.Trim();
        const NumberStyles Integer = NumberStyles.Integer, Float = NumberStyles.Float;
        // The integer converters also take hexadecimal: #FF, 0xFF, &HFF.
        var hex = text.StartsWith('#') ? text[1..]
            : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("&h", StringComparison.OrdinalIgnoreCase) ? text[2..]
            : null;
        var (style, digits) = hex is null ? (Integer, text) : (NumberStyles.AllowHexSpecifier, hex);
        IFormatProvider provider = hex is null ? culture : CultureInfo.InvariantCulture;
        return type switch
        {
            _ when type == typeof(sbyte) => sbyte.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(byte) => byte.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(short) => short.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(ushort) => ushort.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(int) => int.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(uint) => uint.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(long) => long.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(ulong) => ulong.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(Int128) => Int128.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(UInt128) => UInt128.TryParse(digits, style, provider, out var number) ? number : null,
            _ when type == typeof(Half) => Half.TryParse(text, Float, culture, out var number) ? number : null,
            _ when type == typeof(float) => float.TryParse(text, Float, culture, out var number) ? number : null,
            _ when type == typeof(double) => double.TryParse(text, Float, culture, out var number) ? number : null,
            _ when type == typeof(decimal) => decimal.TryParse(text, Float, culture, out var number) ? number : null,
            _ when type == typeof(DateOnly) => DateOnly.TryParse(text, culture, DateTimeStyles.None, out var date) ? date : null,
            _ when type == typeof(DateTime) => DateTime.TryParse(text, culture, DateTimeStyles.None, out var date) ? date : null,
            _ when type == typeof(DateTimeOffset) => DateTimeOffset.TryParse(text, culture, DateTimeStyles.None, out var date) ? date : null,
            _ when type == typeof(TimeOnly) => TimeOnly.TryParse(text, culture, DateTimeStyles.None, out var time) ? time : null,
            _ when type == typeof(TimeSpan) => TimeSpan.TryParse(text, culture, out var span) ? span : null,
            _ => null,
        };
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // 24-hour ISO time: HH:mm, with seconds and their fraction only when there are any.
    private static string Clock(TimeSpan time) =>
        new DateTime(time.Ticks % TimeSpan.TicksPerDay).ToString(
            time.Ticks % TimeSpan.TicksPerSecond != 0 ? "HH:mm:ss.FFFFFFF" : time.Seconds != 0 ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);

    // A range of numbers, whole when both limits are.
    private static TranslationReference Numbers(string field, decimal low, decimal high) =>
        low == decimal.Truncate(low) && high == decimal.Truncate(high) && low >= long.MinValue && high <= long.MaxValue
            ? RunicViewsText.References.validation_range_integer(field: field, min: (long)low, max: (long)high)
            : RunicViewsText.References.validation_range_number(field: field, min: low, max: high);

    private static decimal? Decimal(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 7.9e28 ? (decimal)value : null;

    // Through decimal(float), which keeps the float's 7 significant digits: 0.1f is 0.1, not 0.100000001490116.
    private static decimal? Decimal(float value) =>
        float.IsFinite(value) && Math.Abs(value) < 7.9e28f ? (decimal)value : null;

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
