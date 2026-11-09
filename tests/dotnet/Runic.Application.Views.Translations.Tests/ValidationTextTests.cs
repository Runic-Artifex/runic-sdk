using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Runic.Translations;
using TUnit.Core;
using static Runic.Application.Views.Translations.Tests.CatalogTests;

namespace Runic.Application.Views.Translations.Tests;

// RunicViewsTranslations.ValidationText: the built-in attributes' messages as runic-views references, in English and German.
public sealed class ValidationTextTests
{
    public static IEnumerable<Func<(ValidationAttribute Attribute, string Key, string English, string German)>> Attributes()
    {
        yield return () => (new RequiredAttribute(), "validation_required", "Name is required.", "Name ist erforderlich.");
        yield return () => (new MaxLengthAttribute(8), "validation_max_length", "Name can have at most 8 characters.", "Name darf höchstens 8 Zeichen haben.");
        yield return () => (new MaxLengthAttribute(1), "validation_max_length", "Name can have at most 1 character.", "Name darf höchstens 1 Zeichen haben.");
        yield return () => (new MinLengthAttribute(3), "validation_min_length", "Name must have at least 3 characters.", "Name muss mindestens 3 Zeichen haben.");
        yield return () => (new StringLengthAttribute(1200), "validation_max_length", "Name can have at most 1,200 characters.", "Name darf höchstens 1.200 Zeichen haben.");
        yield return () => (new StringLengthAttribute(10) { MinimumLength = 2 }, "validation_length_between", "Name must have 2 to 10 characters.", "Name muss 2 bis 10 Zeichen haben.");
        yield return () => (new LengthAttribute(2, 10), "validation_length_between", "Name must have 2 to 10 characters.", "Name muss 2 bis 10 Zeichen haben.");
        yield return () => (new RangeAttribute(18, 120), "validation_range_integer", "Name must be between 18 and 120.", "Name muss zwischen 18 und 120 liegen.");
        yield return () => (new RangeAttribute(1.0, 10.0), "validation_range_integer", "Name must be between 1 and 10.", "Name muss zwischen 1 und 10 liegen.");
        yield return () => (new RangeAttribute(0.5, 9.5), "validation_range_number", "Name must be between 0.5 and 9.5.", "Name muss zwischen 0,5 und 9,5 liegen.");
        yield return () => (new EmailAddressAttribute(), "validation_email", "Name is not a valid email address.", "Name ist keine gültige E-Mail-Adresse.");
        yield return () => (new PhoneAttribute(), "validation_phone", "Name is not a valid phone number.", "Name ist keine gültige Telefonnummer.");
        yield return () => (new UrlAttribute(), "validation_url", "Name is not a valid web address (http, https or ftp).", "Name ist keine gültige Webadresse (http, https oder ftp).");
        yield return () => (new CreditCardAttribute(), "validation_credit_card", "Name is not a valid credit card number.", "Name ist keine gültige Kreditkartennummer.");
        yield return () => (new RegularExpressionAttribute("^[a-z]+$"), "validation_pattern", "Name is not in the expected format.", "Name hat nicht das erwartete Format.");
        yield return () => (new CompareAttribute("Password"), "validation_compare", "Name and Password do not match.", "Name und Password stimmen nicht überein.");
        yield return () => (new FileExtensionsAttribute { Extensions = "png,jpg" }, "validation_file_extensions", "Name must have one of these file extensions: png, jpg.", "Name muss eine dieser Dateiendungen haben: png, jpg.");
        yield return () => (new Base64StringAttribute(), "validation_base64", "Name is not valid Base64 text.", "Name ist kein gültiger Base64-Text.");
        yield return () => (new AllowedValuesAttribute("a", "b"), "validation_allowed_values", "Name is not one of the allowed values.", "Name hat keinen der zulässigen Werte.");
        yield return () => (new DeniedValuesAttribute("x"), "validation_denied_values", "Name is one of the values that are not allowed.", "Name hat einen Wert, der nicht zulässig ist.");
        yield return () => (new EnumDataTypeAttribute(typeof(DayOfWeek)), "validation_enum", "Name is not one of the available values.", "Name hat keinen der verfügbaren Werte.");
    }

    [Test]
    [MethodDataSource(nameof(Attributes))]
    public void TheBuiltInAttributesTakeTheCatalogsText(ValidationAttribute attribute, string key, string english, string german) =>
        RequireText(attribute, key, english, german);

    // Every operand type RangeAttribute converts its limits to: the limits as given, and after a validation replaced them
    // with the converted values (the Bridge passes evaluated attributes), give the same text.
    public static IEnumerable<Func<(ValidationAttribute Attribute, string Key, string English, string German)>> Ranges()
    {
        const string Integer = "validation_range_integer", Number = "validation_range_number", Text = "validation_range";
        var cases = new (Type Type, string Minimum, string Maximum, object Value, string Key, string English, string German)[]
        {
            (typeof(sbyte), "-5", "100", (sbyte)5, Integer, "-5 and 100", "-5 und 100"),
            (typeof(byte), "1", "200", (byte)5, Integer, "1 and 200", "1 und 200"),
            (typeof(short), "-1000", "1000", (short)5, Integer, "-1,000 and 1,000", "-1.000 und 1.000"),
            (typeof(ushort), "1", "60000", (ushort)5, Integer, "1 and 60,000", "1 und 60.000"),
            (typeof(int), "1", "100", 5, Integer, "1 and 100", "1 und 100"),
            (typeof(int), "0x10", "#FF", 20, Integer, "16 and 255", "16 und 255"),
            (typeof(uint), "1", "4000000000", 5u, Integer, "1 and 4,000,000,000", "1 und 4.000.000.000"),
            (typeof(long), "1", "3000000000", 5L, Integer, "1 and 3,000,000,000", "1 und 3.000.000.000"),
            (typeof(ulong), "0", "5000000000", 5UL, Integer, "0 and 5,000,000,000", "0 und 5.000.000.000"),
            (typeof(Int128), "-10", "10", (Int128)5, Integer, "-10 and 10", "-10 und 10"),
            (typeof(UInt128), "1", "10", (UInt128)5, Integer, "1 and 10", "1 und 10"),
            (typeof(Half), "0.5", "9.5", (Half)1, Number, "0.5 and 9.5", "0,5 und 9,5"),
            (typeof(float), "0.1", "2.5", 1f, Number, "0.1 and 2.5", "0,1 und 2,5"),
            (typeof(float), "1", "10", 5f, Integer, "1 and 10", "1 und 10"),
            (typeof(double), "0.5", "1000.5", 1d, Number, "0.5 and 1,000.5", "0,5 und 1.000,5"),
            (typeof(decimal), "0.25", "1000.5", 1m, Number, "0.25 and 1,000.5", "0,25 und 1.000,5"),
            (typeof(DateOnly), "2020-01-01", "2030-12-31", new DateOnly(2025, 1, 1), Text, "2020-01-01 and 2030-12-31", "2020-01-01 und 2030-12-31"),
            (typeof(DateTime), "2020-01-01", "2030-12-31", new DateTime(2025, 1, 1), Text, "2020-01-01 and 2030-12-31", "2020-01-01 und 2030-12-31"),
            (typeof(DateTime), "2020-01-01 08:00", "2030-12-31 17:30:15", new DateTime(2025, 1, 1), Text,
                "2020-01-01 08:00 and 2030-12-31 17:30:15", "2020-01-01 08:00 und 2030-12-31 17:30:15"),
            (typeof(DateTimeOffset), "2020-01-01 08:00 +01:00", "2030-12-31 17:30 +01:00", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)), Text,
                "2020-01-01 08:00 +01:00 and 2030-12-31 17:30 +01:00", "2020-01-01 08:00 +01:00 und 2030-12-31 17:30 +01:00"),
            (typeof(TimeOnly), "08:00", "17:30", new TimeOnly(12, 0), Text, "08:00 and 17:30", "08:00 und 17:30"),
            (typeof(TimeSpan), "00:30:00", "02:00:00", TimeSpan.FromHours(1), Text, "00:30:00 and 02:00:00", "00:30:00 und 02:00:00"),
            (typeof(string), "a", "m", "b", Text, "a and m", "a und m"),
        };
        foreach (var range in cases)
        {
            RangeAttribute Attribute() => new(range.Type, range.Minimum, range.Maximum) { ParseLimitsInInvariantCulture = true };
            var english = $"Name must be between {range.English}.";
            var german = $"Name muss zwischen {range.German} liegen.";
            yield return () => (Attribute(), range.Key, english, german);
            yield return () => (Evaluated(Attribute(), range.Value), range.Key, english, german);
        }
        // The int and double constructors.
        yield return () => (Evaluated(new RangeAttribute(18, 120), 20), Integer, "Name must be between 18 and 120.", "Name muss zwischen 18 und 120 liegen.");
        yield return () => (Evaluated(new RangeAttribute(0.5, 9.5), 1d), Number, "Name must be between 0.5 and 9.5.", "Name muss zwischen 0,5 und 9,5 liegen.");
    }

    [Test]
    [MethodDataSource(nameof(Ranges))]
    public void RangeLimitsOfEveryTypeTakeTheCatalogsText(ValidationAttribute attribute, string key, string english, string german) =>
        RequireText(attribute, key, english, german);

    // Limits given as text parse in the current culture unless ParseLimitsInInvariantCulture, as RangeAttribute parses them.
    [Test]
    public void RangeLimitsParseInTheCultureTheAttributeUses()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var range = new RangeAttribute(typeof(decimal), "0,5", "1000,5");
            const string English = "Name must be between 0.5 and 1,000.5.", German = "Name muss zwischen 0,5 und 1.000,5 liegen.";
            RequireText(range, "validation_range_number", English, German);
            RequireText(Evaluated(range, 1m), "validation_range_number", English, German);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void RequireText(ValidationAttribute attribute, string key, string english, string german)
    {
        var member = new RunicValidationMember(typeof(object), "Name", "Name", attribute.FormatErrorMessage("Name"));
        var text = RunicViewsTranslations.ValidationText(attribute, member)
            ?? throw new InvalidOperationException($"{attribute.GetType().Name} took no text.");
        Require(text is { Catalog: "runic-views", ContractFingerprint: RunicViewsTextCatalog.ContractFingerprint } && text.Key == key,
            $"{attribute.GetType().Name}: {text.ToJson()}");
        Require(text.FallbackText == english, $"{attribute.GetType().Name} fallback: {text.FallbackText}");
        var reference = TranslationReference.Parse(text.ToJson());
        Require(Context("en").Resolve(reference).Text == english, $"{attribute.GetType().Name} in English: {Context("en").Resolve(reference).Text}");
        Require(Context("de").Resolve(reference).Text == german, $"{attribute.GetType().Name} in German: {Context("de").Resolve(reference).Text}");
    }

    public static IEnumerable<Func<ValidationAttribute>> Kept()
    {
        yield return () => new RequiredAttribute { ErrorMessage = "Fill in your name." };
        yield return () => new MaxLengthAttribute(8) { ErrorMessageResourceType = typeof(ValidationTextTests), ErrorMessageResourceName = nameof(Message) };
        yield return () => new DerivedRequiredAttribute();
        yield return () => new MaxLengthAttribute();
        yield return () => new EmailAddressAttribute { ErrorMessage = "Use your work address." };
        yield return () => new RangeAttribute(0, 10) { MinimumIsExclusive = true };
        yield return () => new RangeAttribute(0, double.MaxValue);
        yield return () => Evaluated(new RangeAttribute(0, double.MaxValue), 1d);
        yield return () => Evaluated(new RangeAttribute(typeof(float), "0", "3e38") { ParseLimitsInInvariantCulture = true }, 1f);
        yield return () => new CustomValidationAttribute(typeof(ValidationTextTests), nameof(Validate));
    }

    public static string Message => "Too long.";

    public static ValidationResult? Validate(object value) => ValidationResult.Success;

    // An attribute with its own message, a derived attribute, and limits the catalog has no text for keep their message.
    [Test]
    [MethodDataSource(nameof(Kept))]
    public void OtherAttributesKeepTheirMessage(ValidationAttribute attribute)
    {
        var member = new RunicValidationMember(typeof(object), "Name", "Name", "message");
        Require(RunicViewsTranslations.ValidationText(attribute, member) is null && RunicViewsTranslations.ValidationReference(attribute, member) is null,
            $"{attribute.GetType().Name} took a text.");
    }

    [Test]
    public void NullArgumentsAreRefused()
    {
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.ValidationText(null!, new(typeof(object), "a", "a", "m")));
        RequireThrows<ArgumentNullException>(() => RunicViewsTranslations.ValidationText(new RequiredAttribute(), null!));
    }

    private static ValidationAttribute Evaluated(ValidationAttribute attribute, object value)
    {
        attribute.IsValid(value);
        return attribute;
    }

    private sealed class DerivedRequiredAttribute : RequiredAttribute;
}
